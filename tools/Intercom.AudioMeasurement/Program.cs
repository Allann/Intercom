using System.Globalization;
using System.Text.RegularExpressions;
using Intercom.Audio;

if (args.Length == 2 && args[0].Equals("configure", StringComparison.OrdinalIgnoreCase))
{
    var profile = AudioMeasurementProfileParser.Parse(args[1]);
    AudioMeasurementSettings.Save(profile);
    Console.WriteLine($"Audio measurement profile: {profile.ToConfigValue()}");
    Console.WriteLine($"Settings: {AudioMeasurementSettings.DefaultPath}");
    return;
}

if (args.Length is 4 or 5 && args[0].Equals("report", StringComparison.OrdinalIgnoreCase))
{
    var report = MeasurementReport.Create(args[1], args[2], args[3]);
    var output = args.Length == 5 ? args[4] : Path.GetFullPath($"audio-measurement-{args[3]}.md");
    File.WriteAllText(output, report);
    Console.WriteLine(output);
    return;
}

Console.Error.WriteLine("Usage:");
Console.Error.WriteLine("  Intercom.AudioMeasurement configure disabled|clean|loss1|loss5|burst");
Console.Error.WriteLine("  Intercom.AudioMeasurement report <sender-intercom.log> <receiver-intercom.log> <clean|loss1|loss5|burst> [report.md]");
Environment.ExitCode = 2;

static class MeasurementReport
{
    static readonly Regex LinePattern = new(
        @"^(?<timestamp>\S+) \[\S+\] (?<event>audio\.measurement-[^ ]+) \| (?<message>.*)$",
        RegexOptions.Compiled);
    static readonly Regex FieldPattern = new(@"(?<key>\w+)=(?<value>\S+)", RegexOptions.Compiled);

    public static string Create(string senderPath, string receiverPath, string profile)
    {
        var allSender = Read(senderPath);
        var allReceiver = Read(receiverPath);
        var receiverSessionIds = allReceiver.Select(e => e.Text("session")).Where(value => value.Length > 0).ToHashSet();
        var sessionIds = allSender.Where(e => e.Name == "audio.measurement-talk-start"
                && e.Text("profile") == profile && receiverSessionIds.Contains(e.Text("session")))
            .Select(e => e.Text("session")).ToHashSet();
        var sender = allSender.Where(e => sessionIds.Contains(e.Text("session"))).ToList();
        var receiver = allReceiver.Where(e => sessionIds.Contains(e.Text("session"))).ToList();
        var starts = sender.Where(e => e.Name == "audio.measurement-talk-start").ToList();
        var frames = receiver.Where(e => e.Name == "audio.measurement-frame-to-speaker-queue").ToList();
        var releases = receiver.Where(e => e.Name == "audio.measurement-release-to-speaker-queue").ToList();
        var dropped = sender.Count(e => e.Name == "audio.measurement-packet-dropped");
        var latencies = frames.Select(e => e.Long("estimatedMs")).Where(v => v is >= -5000 and <= 30000).ToList();
        var releaseLatencies = releases.Select(e => e.Long("estimatedMs")).Where(v => v is >= -5000 and <= 30000).ToList();
        var pressLatencies = starts.Select(start =>
        {
            var session = start.Text("session");
            var source = start.Long("sourceUnixMs");
            return frames.FirstOrDefault(frame => frame.Text("session") == session && frame.Long("sourceUnixMs") >= source) is { } first
                ? first.Long("observedUnixMs") - source
                : (long?)null;
        }).Where(value => value.HasValue).Select(value => value!.Value).ToList();
        var firstReceived = receiver.Where(e => e.Name == "audio.measurement-first-received").ToList();
        var arrivalToQueue = firstReceived.Select(received =>
        {
            var played = frames.FirstOrDefault(frame => frame.Text("session") == received.Text("session")
                && frame.Long("sequence") == received.Long("sequence"));
            return played is null ? (long?)null : played.Long("observedUnixMs") - received.Long("observedUnixMs");
        }).Where(value => value.HasValue).Select(value => value!.Value).ToList();
        var clockReliable = latencies.Count > 0 && Percentile(latencies.Order().ToArray(), 0.5) is >= 0 and <= 2000;
        var maxTarget = receiver.Select(e => e.Long("targetFrames")).DefaultIfEmpty(0).Max();
        var concealed = SessionCounterTotal(receiver, "concealed");
        var discarded = SessionCounterTotal(receiver, "discarded");

        return $"""
            # Intercom two-PC audio measurement

            Generated: {DateTimeOffset.Now:O}

            | Measurement | Result |
            |---|---:|
            | Profile | {profile} |
            | Talk spurts | {starts.Count} |
            | Audio frames measured | {frames.Count} |
            | Deliberately dropped frames | {dropped} |
            | Press to first speaker queue | {(clockReliable ? Summary(pressLatencies) : "invalid: PC clocks are not sufficiently aligned")} |
            | Steady-state capture to speaker queue | {(clockReliable ? Summary(latencies) : "invalid: PC clocks are not sufficiently aligned")} |
            | Release to speaker queue drained | {(clockReliable ? Summary(releaseLatencies) : "invalid: PC clocks are not sufficiently aligned")} |
            | Receiver packet-arrival to speaker queue | {Summary(arrivalToQueue)} |
            | Cross-PC timing variation (p95-p5) | {Spread(latencies)} |
            | Maximum jitter target | {maxTarget} frames ({maxTarget * AudioFormat.FrameMilliseconds} ms) |
            | Concealed frames | {concealed} |
            | Discarded frames | {discarded} |

            ## Interpretation

            Times use UTC timestamps carried inside the real encrypted audio packets. Keep both PCs synchronised with Windows Time; clock offset is included in cross-PC latency. Absolute cross-PC values are rejected when their median is negative or implausibly high. Receiver arrival-to-queue delay and timing variation remain useful with a fixed clock offset. “Speaker queue” is the software render boundary and does not claim to detect the physical instant a loudspeaker becomes audible.

            ## Inputs

            - Sender: `{Path.GetFullPath(senderPath)}`
            - Receiver: `{Path.GetFullPath(receiverPath)}`
            """;
    }

    static string Summary(IReadOnlyList<long> values)
    {
        if (values.Count == 0) return "no samples";
        var ordered = values.Order().ToArray();
        return $"min {ordered[0]} ms; median {Percentile(ordered, 0.5)} ms; p95 {Percentile(ordered, 0.95)} ms; max {ordered[^1]} ms";
    }

    static long Percentile(long[] ordered, double percentile) => ordered[(int)Math.Ceiling((ordered.Length - 1) * percentile)];

    static string Spread(IReadOnlyList<long> values)
    {
        if (values.Count < 2) return "no samples";
        var ordered = values.Order().ToArray();
        return $"{Percentile(ordered, 0.95) - Percentile(ordered, 0.05)} ms";
    }

    static long SessionCounterTotal(IEnumerable<Event> events, string field) => events
        .GroupBy(e => e.Text("session"))
        .Sum(group => group.Select(e => e.Long(field)).DefaultIfEmpty(0).Max());

    static List<Event> Read(string path)
    {
        var files = Directory.Exists(path)
            ? Directory.GetFiles(path, "intercom*.log")
            : [path];
        return files.SelectMany(File.ReadLines).Select(Parse).Where(e => e is not null).Cast<Event>().ToList();
    }

    static Event? Parse(string line)
    {
        var match = LinePattern.Match(line);
        if (!match.Success) return null;
        var fields = FieldPattern.Matches(match.Groups["message"].Value)
            .ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value);
        return new Event(match.Groups["event"].Value, fields);
    }

    sealed record Event(string Name, IReadOnlyDictionary<string, string> Fields)
    {
        public string Text(string key) => Fields.TryGetValue(key, out var value) ? value : "";
        public long Long(string key) => long.TryParse(Text(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
