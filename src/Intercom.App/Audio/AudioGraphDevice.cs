using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Devices;
using Windows.Devices.Enumeration;
using Intercom.Diagnostics;

namespace Intercom.App.Audio;

/// <summary>Windows shared-mode communications AudioGraph boundary. Graph
/// callbacks only copy PCM to/from bounded queues; codec/network work stays
/// off the real-time audio callback.</summary>
public sealed class AudioGraphDevice : Intercom.Audio.IAudioDevice
{
    const int MaxQueuedFrames = 12;
    readonly Intercom.Audio.PcmPlaybackBuffer _playback = new(
        MaxQueuedFrames * Intercom.Audio.AudioFormat.SamplesPerFrame);
    AudioGraph? _graph;
    AudioDeviceInputNode? _input;
    AudioDeviceOutputNode? _output;
    AudioFrameOutputNode? _capture;
    AudioFrameInputNode? _render;
    bool _disposed;
    int _callbackFailed;
    readonly string? _inputDeviceId;
    readonly string? _outputDeviceId;

    public AudioGraphDevice(string? inputDeviceId = null, string? outputDeviceId = null)
    {
        _inputDeviceId = inputDeviceId;
        _outputDeviceId = outputDeviceId;
    }

    public event Action<short[]>? Captured;
    public event Action<Exception>? DeviceFailed;
    public string InputDeviceName { get; private set; } = "Not initialized";
    public string OutputDeviceName { get; private set; } = "Not initialized";
    public bool CanCapture => _input is not null;
    public bool CanRender => _output is not null;

    public static async Task<(IReadOnlyList<AudioDeviceChoice> Inputs, IReadOnlyList<AudioDeviceChoice> Outputs)> GetDevicesAsync()
    {
        var captures = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());
        var renders = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
        return (
            captures.Select(device => new AudioDeviceChoice(device.Id, device.Name)).ToList(),
            renders.Select(device => new AudioDeviceChoice(device.Id, device.Name)).ToList());
    }

    public async Task StartAsync()
    {
        if (_graph is not null) throw new InvalidOperationException("Audio graph is already started.");
        var frameEncoding = AudioEncodingProperties.CreatePcm(
            Intercom.Audio.AudioFormat.SampleRate,
            Intercom.Audio.AudioFormat.Channels,
            32);
        frameEncoding.Subtype = MediaEncodingSubtypes.Float;
        var settings = new AudioGraphSettings(Windows.Media.Render.AudioRenderCategory.Communications)
        {
            QuantumSizeSelectionMode = QuantumSizeSelectionMode.SystemDefault,
            EncodingProperties = frameEncoding,
        };
        DeviceInformation? outputDevice = null;
        if (!string.IsNullOrWhiteSpace(_outputDeviceId))
        {
            outputDevice = await DeviceInformation.CreateFromIdAsync(_outputDeviceId);
            settings.PrimaryRenderDevice = outputDevice;
        }
        var graphResult = await AudioGraph.CreateAsync(settings);
        if (graphResult.Status != AudioGraphCreationStatus.Success)
            throw new InvalidOperationException($"AudioGraph creation failed: {graphResult.Status}.");

        _graph = graphResult.Graph;
        _graph.UnrecoverableErrorOccurred += OnUnrecoverableError;

        try
        {
            var outputResult = await _graph.CreateDeviceOutputNodeAsync();
            if (outputResult.Status == AudioDeviceNodeCreationStatus.Success)
            {
                _output = outputResult.DeviceOutputNode;
                OutputDeviceName = _output.Device?.Name ?? "Windows default communications speaker";
            }
            else
            {
                OutputDeviceName = "No speaker (transmit only)";
                DiagnosticLog.Current.Warning("audio.playback-unavailable", $"status={outputResult.Status}");
            }
        }
        catch (Exception ex)
        {
            OutputDeviceName = "No speaker (transmit only)";
            DiagnosticLog.Current.Warning("audio.playback-unavailable", "Speaker initialization failed; capture remains available.", ex);
        }

        if (_inputDeviceId != "")
        {
            try
            {
                CreateAudioDeviceInputNodeResult inputResult;
                if (string.IsNullOrWhiteSpace(_inputDeviceId))
                    inputResult = await _graph.CreateDeviceInputNodeAsync(Windows.Media.Capture.MediaCategory.Communications);
                else
                {
                    var inputDevice = await DeviceInformation.CreateFromIdAsync(_inputDeviceId);
                    inputResult = await _graph.CreateDeviceInputNodeAsync(Windows.Media.Capture.MediaCategory.Communications, frameEncoding, inputDevice);
                }
                if (inputResult.Status == AudioDeviceNodeCreationStatus.Success)
                {
                    _input = inputResult.DeviceInputNode;
                    InputDeviceName = _input.Device?.Name ?? "Windows default communications microphone";
                }
                else
                {
                    InputDeviceName = "No microphone (receive only)";
                    DiagnosticLog.Current.Warning("audio.capture-unavailable", $"status={inputResult.Status}");
                }
            }
            catch (Exception ex)
            {
                InputDeviceName = "No microphone (receive only)";
                DiagnosticLog.Current.Warning("audio.capture-unavailable", "Microphone initialization failed; playback remains available.", ex);
            }
        }
        else InputDeviceName = "No microphone (receive only)";
        if (!CanCapture && !CanRender)
            throw new InvalidOperationException("No microphone or speaker is available.");

        // AudioFrame memory is float32. Convert explicitly at this boundary so
        // the codec/network pipeline remains mono PCM16.
        if (CanCapture) _capture = _graph.CreateFrameOutputNode(frameEncoding);
        if (CanRender) _render = _graph.CreateFrameInputNode(frameEncoding);
        DiagnosticLog.Current.Info(
            "audio.frame-format",
            $"capture={(CanCapture ? "available" : "unavailable")} " +
            $"render={(CanRender ? "available" : "unavailable")} " +
            $"graph={_graph.EncodingProperties.Subtype}/{_graph.EncodingProperties.SampleRate}Hz/{_graph.EncodingProperties.ChannelCount}ch/{_graph.EncodingProperties.BitsPerSample}bit quantum={_graph.SamplesPerQuantum}");
        if (_input is not null && _capture is not null)
        {
            _input.AddOutgoingConnection(_capture);
            _graph.QuantumStarted += OnQuantumStarted;
        }
        if (_render is not null && _output is not null)
        {
            _render.AddOutgoingConnection(_output);
            _render.QuantumStarted += OnRenderQuantumStarted;
        }
        _graph.Start();
    }

    public void QueuePlayback(short[] pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (!CanRender) return;
        _playback.Enqueue(pcm);
    }

    void OnQuantumStarted(AudioGraph sender, object args)
    {
        if (Volatile.Read(ref _callbackFailed) != 0) return;
        try
        {
            using var frame = _capture?.GetFrame();
            if (frame is null) return;
            var samples = ReadSamples(frame, checked((uint)sender.SamplesPerQuantum));
            if (samples.Length > 0) Captured?.Invoke(samples);
        }
        catch (Exception ex) { FailCallback(ex); }
    }

    void OnRenderQuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
    {
        if (args.RequiredSamples <= 0 || Volatile.Read(ref _callbackFailed) != 0) return;
        try
        {
            var source = new short[args.RequiredSamples];
            _playback.Dequeue(source);
            var frame = new AudioFrame((uint)(args.RequiredSamples * sizeof(float)));
            WriteSamples(frame, source, args.RequiredSamples);
            sender.AddFrame(frame);
        }
        catch (Exception ex) { FailCallback(ex); }
    }

    void FailCallback(Exception exception)
    {
        if (Interlocked.Exchange(ref _callbackFailed, 1) != 0) return;
        DiagnosticLog.Current.Error("audio.callback-failed", "AudioGraph real-time callback failed; session will be stopped outside the callback thread.", exception);
        DeviceFailed?.Invoke(exception);
    }

    void OnUnrecoverableError(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args) =>
        DeviceFailed?.Invoke(new InvalidOperationException($"Audio device failed: {args.Error}."));

    static unsafe short[] ReadSamples(AudioFrame frame, uint samplesPerQuantum)
    {
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        GetBuffer(reference, out var data, out var capacity);
        // Capacity is the allocation size and can be much larger than the
        // valid quantum. Encoding it produced stale audio/static and silence.
        var count = Intercom.Audio.PcmFrameSizing.ValidCaptureSamples(
            buffer.Length,
            samplesPerQuantum,
            Intercom.Audio.AudioFormat.Channels,
            sizeof(float));
        return Intercom.Audio.FloatPcmConverter.ToPcm16(new ReadOnlySpan<float>(data, count));
    }

    static unsafe void WriteSamples(AudioFrame frame, short[]? source, int requiredSamples)
    {
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Write);
        using var reference = buffer.CreateReference();
        GetBuffer(reference, out var data, out var capacity);
        var destination = new Span<float>(data, Math.Min(requiredSamples, checked((int)capacity / sizeof(float))));
        destination.Clear();
        if (source is not null)
        {
            var count = Math.Min(source.Length, destination.Length);
            Intercom.Audio.FloatPcmConverter.ToFloat(source.AsSpan(0, count), destination[..count]);
        }
        buffer.Length = checked((uint)(destination.Length * sizeof(float)));
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        if (_graph is not null)
        {
            _graph.Stop();
            _graph.QuantumStarted -= OnQuantumStarted;
            _graph.UnrecoverableErrorOccurred -= OnUnrecoverableError;
        }
        if (_render is not null) _render.QuantumStarted -= OnRenderQuantumStarted;
        _capture?.Dispose();
        _render?.Dispose();
        _input?.Dispose();
        _output?.Dispose();
        _graph?.Dispose();
        _playback.Clear();
        return ValueTask.CompletedTask;
    }

    // C#/WinRT's object.As<T>() treats T as a projected WinRT interface. This
    // is instead a private COM interface exposed by IMemoryBufferReference,
    // so using As<T> throws InvalidCastException even though QueryInterface
    // succeeds. Query the COM ABI directly and call its sole method.
    static unsafe void GetBuffer(IMemoryBufferReference reference, out byte* data, out uint capacity)
    {
        // Marshal.GetIUnknownForObject returns the identity of the managed
        // C#/WinRT wrapper. Unwrap it so QueryInterface reaches the native
        // IMemoryBufferReference implementation that exposes byte access.
        var unknown = WinRT.MarshalInspectable<object>.FromManaged(reference);
        var iid = new Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
        IntPtr access = IntPtr.Zero;
        try
        {
            var queryResult = Marshal.QueryInterface(unknown, in iid, out access);
            if (queryResult < 0)
                throw new InvalidOperationException(
                    $"Audio buffer byte access is unavailable (HRESULT 0x{queryResult:X8}).",
                    Marshal.GetExceptionForHR(queryResult));
            var vtable = *(IntPtr**)access;
            var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)vtable[3];
            byte* bytes = null;
            uint byteCapacity = 0;
            Marshal.ThrowExceptionForHR(getBuffer(access, &bytes, &byteCapacity));
            data = bytes;
            capacity = byteCapacity;
        }
        finally
        {
            if (access != IntPtr.Zero) Marshal.Release(access);
            WinRT.MarshalInspectable<object>.DisposeAbi(unknown);
        }
    }
}
