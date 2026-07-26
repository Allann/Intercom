# Real-time audio pipeline for the MVP

## Decision

Use `Windows.Media.Audio.AudioGraph` from the WinUI 3/.NET app for microphone capture and speaker playback. Keep the graph in Windows shared mode, select the communications media category, and use the graph's normal signal processing rather than raw mode. Encode network audio as mono 48 kHz Opus using the VoIP application mode, one 20 ms Opus frame per datagram.

Use a small application packet header rather than implementing the full RTP/RTCP stack. Each audio datagram needs, at minimum, a protocol version, conversation/session ID, stream ID, monotonically increasing sequence number, 48 kHz sample timestamp, flags, and the Opus packet. The sequence number distinguishes loss from Opus discontinuous transmission; the timestamp drives playout. Authentication/encryption belongs to the transport/security decision and must cover the header and payload.

Start with a 60 ms target jitter buffer (three 20 ms frames), permitted to adapt within a bounded range after measurement. A late or missing packet is not retransmitted: invoke Opus packet-loss concealment for the missing frame and continue. This preserves conversational latency and gives push-to-talk a predictable start.

Use the same pipeline for push-to-talk and one-to-one hands-free sessions:

- Push-to-talk starts/stops capture and transmission while leaving playback ready.
- A hands-free session keeps both capture and playback active until either peer ends it.
- Group voice encodes once and fans the same packet out directly to every receiving peer. The voice-floor protocol ensures only one peer transmits, so the receiver still decodes one stream and does not need a mixer for the MVP.

Do not record voice or write PCM/Opus frames to disk.

## Why AudioGraph

Microsoft identifies both AudioGraph and WASAPI as Windows low-latency APIs and explicitly says to favour AudioGraph for new development unless an application needs more control or lower latency than AudioGraph provides. AudioGraph adds buffering compared with WASAPI, but simplifies synchronized capture and render. That trade is appropriate for household speech over a LAN, where network jitter already requires buffering and MVP implementation risk matters more than shaving the final audio-engine quantum. [Microsoft: Low Latency Audio](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio)

The graph can connect a device-input node to an app-controlled frame-output node for capture, and an app-controlled frame-input node to a device-output node for playback. `AudioFrameOutputNode.GetFrame()` is intended to retrieve graph-synchronized PCM, while `AudioFrameInputNode.QuantumStarted` reports the exact samples required for the next render quantum. Supplying only the requested samples avoids adding latency. Only PCM and float formats are accepted by the frame-input boundary. [Microsoft: AudioGraph API](https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audiograph?view=winrt-26100), [Microsoft: Audio graphs](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/audio-graphs), [Microsoft: `AudioFrameInputNode.QuantumStarted`](https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioframeinputnode.quantumstarted?view=winrt-26100)

Recommended graph settings:

- `AudioRenderCategory.Communications` and `MediaCategory.Communications`.
- Shared mode; do not take exclusive control of household audio endpoints.
- Default Windows audio processing, not raw processing. Microsoft cautions that raw mode bypasses OEM-selected processing and can make capture formats or output quality unsuitable. This also preserves the best chance of endpoint-provided communications processing. [Microsoft: Low Latency Audio](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio)
- Prefer the default communications capture/render endpoints in the MVP; allow explicit device selection later if needed.
- Begin with the normal graph quantum rather than forcing the smallest supported quantum. Microsoft's documentation says the usual quantum is about 10 ms and lower sizes depend on drivers. Measure end-to-end latency before enabling `QuantumSizeSelectionMode.LowestLatency`. [Microsoft: Audio graphs](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/audio-graphs)

Accessing the raw bytes of an `AudioFrame` from C# requires locking its `AudioBuffer`, obtaining `IMemoryBufferByteAccess`, and compiling that narrow adapter as unsafe code. Isolate this interop in the audio module rather than exposing buffers to UI or conversation code. [Microsoft: Audio graphs](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/audio-graphs)

WASAPI/`IAudioClient3` remains the fallback only if an AudioGraph prototype cannot meet measured push-to-talk latency or stable teardown requirements. Exclusive WASAPI is specifically unsuitable as the default because it prevents other applications using the endpoint. [Microsoft: Low Latency Audio](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio)

## Why Opus and these initial settings

Opus is standardized for interactive speech and audio, supports 48 kHz mono input, and has a VoIP mode designed for speech intelligibility. Its API accepts 2.5, 5, 10, 20, 40, or 60 ms frames; the official tools use 20 ms by default and state that smaller frames trade quality for latency. Use:

- 48 kHz, mono, signed 16-bit PCM at the codec boundary (960 samples per 20 ms frame).
- `OPUS_APPLICATION_VOIP` and the voice signal hint.
- 20 ms frames.
- VBR at an initial target of 24 kbit/s; make the bitrate a protocol/session parameter so testing can move it without a wire-format change.
- Decoder packet-loss concealment for isolated loss.
- No in-band FEC or DTX in the first implementation; add only after loss/bandwidth measurements establish a need.

The standard says an Opus packet is already a transport unit and relies on a lower layer such as UDP or RTP to convey packet length. The RTP payload specification explains that sequence gaps distinguish loss from DTX and that missing frames are handled by the decoder's loss-concealment unit. [RFC 6716: Opus codec](https://www.rfc-editor.org/rfc/rfc6716), [RFC 7587: RTP payload for Opus](https://www.rfc-editor.org/rfc/rfc7587), [Xiph: Opus 1.6 API](https://opus-codec.org/docs/opus_api-1.6.pdf)

For .NET, begin with the managed `Concentus` package behind an internal codec interface. Its package is a portable C# Opus implementation and explicitly does not provide RTP, which fits the separate packetizer above. Benchmark CPU use on the minimum target PC. If it is material, substitute its native acceleration package or the official Xiph `libopus` behind the same interface; Xiph documents Windows builds through CMake. Pin the selected package version and license notices during implementation. [NuGet: Concentus](https://www.nuget.org/packages/Concentus), [Xiph: reference Opus implementation](https://github.com/xiph/opus)

## Buffering and real-time rules

- The AudioGraph event handler must only copy/convert a quantum into a preallocated bounded queue. Encoding, network I/O, decoding, and UI updates run elsewhere.
- Accumulate graph quanta until exactly 960 mono samples are available, then encode one packet. Do not expose graph quantum size as the network frame size.
- Decode into a bounded jitter buffer. At playout, request PLC when the next sequence is missing; never wait indefinitely for a late packet.
- On queue overflow, discard old audio rather than growing latency. On underflow, use PLC/silence and report degraded voice state.
- Pool PCM and packet buffers. The render callback must provide exactly the requested samples; `AudioFrameInputNode` permits at most 64 queued frames, so unbounded prefeeding is invalid as well as latent. [Microsoft: `AudioFrameInputNode.AddFrame`](https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audioframeinputnode.addframe?view=winrt-26100)

## Failure detection and teardown

Treat the audio pipeline as an explicit per-session state machine (`Stopped`, `Starting`, `Running`, `Degraded`, `Stopping`, `Failed`) with idempotent stop:

1. Stop accepting new capture or network frames.
2. Stop the relevant graph nodes and unsubscribe quantum/error callbacks.
3. Cancel and await encoder, sender, receiver, and decoder loops.
4. Drain/discard jitter and PCM queues; reset graph nodes so buffered audio cannot play in the next session.
5. Dispose nodes, graph, buffers, encoder, and decoder once.
6. Publish the final session state only after resources have stopped.

Subscribe to `AudioGraph.UnrecoverableErrorOccurred`. `AudioDeviceLost` and `AudioSessionDisconnected` are explicit error values; either should terminate the current voice attempt, notify the remote peer, and offer text fallback. Recreate the graph against the current default communications endpoint for a later attempt rather than trying to repair a running graph. Windows also documents endpoint invalidation on unplug, reconfiguration, disablement, removal, or audio-service shutdown. [Microsoft: `AudioGraphUnrecoverableError`](https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.audiographunrecoverableerror?view=winrt-26100), [Microsoft: Recovering from an invalid-device error](https://learn.microsoft.com/en-us/windows/win32/coreaudio/recovering-from-an-invalid-device-error)

Network liveness is separate from device health. Session control should exchange a lightweight heartbeat while hands-free or group voice is active. After a small number of missed heartbeat intervals, stop playout/capture cleanly and present text fallback; do not infer peer failure from a single absent audio packet because silence/DTX and packet loss are valid.

## Validation gates

The implementation ticket should include a two-PC audio harness and measure:

- press-to-first-audible-speech and release-to-silence;
- steady-state one-way latency and jitter-buffer occupancy;
- loss behavior at 1%, 5%, and short bursts;
- microphone/speaker unplug and default-device changes during each voice mode;
- repeated start/stop (at least 100 cycles) without stale audio, exceptions, or growing memory/handles;
- simultaneous use of the audio endpoint by another normal Windows application;
- CPU and allocation rate while encoding once and fanning out to several loopback receivers.

The acceptance target should be set by the prototype after measurement rather than asserted from API documentation, because endpoint drivers determine part of Windows' latency.

## New decisions and follow-up work

1. **Hands-free acoustic echo is a prototype gate.** Communications-category processing may benefit from endpoint/OEM processing, but the cited APIs do not guarantee adequate acoustic echo cancellation for every PC/speaker/microphone combination. Test the actual kitchen-style hardware with speakers before accepting hands-free mode. If echo is unacceptable, investigate a dedicated local AEC stage or require a headset for MVP hands-free operation.
2. **Transport security owns replay protection.** The packet sequence and session identifiers provide the inputs, but the security design must specify authenticated encryption, nonce construction, and replay windows before audio packets are accepted.
3. **Benchmark managed versus native Opus.** Concentus minimizes packaging friction; CPU and allocation measurements decide whether native `libopus` is warranted.
4. **Tune, do not assume, the jitter window.** The proposed 60 ms starting target and 24 kbit/s bitrate are implementation defaults, not immutable product decisions.

