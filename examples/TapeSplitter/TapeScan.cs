using System.Text.Json.Serialization;
using FrameReader;

namespace TapeSplitter;

/// <summary>Whether the sound has a say in what counts as a gap.</summary>
public enum SoundRule
{
    /// <summary>The picture alone decides; the sound's silence is reported beside each gap.</summary>
    Ignore,

    /// <summary>A gap is only where the picture is blank and the sound silent (a capture without sound: the picture decides).</summary>
    Agree,
}

/// <summary>What <see cref="TapeScan"/> counts as blank, and its rules from blank spans to gaps and cut points.</summary>
public sealed record TapeScanOptions
{
    /// <summary>
    /// The shortest gap: blank spans that overlap or touch, merged, count when they last this together. 2 s by default
    /// (blackdetect's, freezedetect's and silencedetect's default d).
    /// </summary>
    public TimeSpan MinGap { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The shortest span a detector reports: each one's own minimum (blackdetect's, freezedetect's and silencedetect's
    /// d, the flat colour's duration). Shorter than <see cref="MinGap"/>, so a second of black before a blue screen
    /// and the blue make one gap. 0.5 s by default; never over <see cref="MinGap"/>.
    /// </summary>
    public TimeSpan MinSpan { get; init; } = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// The shortest recording: a stretch between two gaps (or a gap and the tape's start or end) shorter than this is
    /// a glitch, and part of the gap. 1 s by default; zero keeps every stretch.
    /// </summary>
    public TimeSpan MinRecording { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether the sound must be silent too. <see cref="SoundRule.Ignore"/> by default (the owner, 2026-11-15).</summary>
    public SoundRule Sound { get; init; } = SoundRule.Ignore;

    /// <summary>Which of the picture's detectors make gaps (and run): <c>black</c>, <c>flat</c>, <c>frozen</c>. All by default.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = ["black", "flat", "frozen"];

    /// <summary>silencedetect's n in dB: samples below it are silent. -60 by default, its default.</summary>
    public double SilenceLevel { get; init; } = -60;

    /// <summary>blackdetect's pix_th: the luma at or under which a pixel is black, as a share of the range above black. 0.10 by default, its default.</summary>
    public double BlackPixel { get; init; } = 0.10;

    /// <summary>blackdetect's pic_th: the share of black pixels that makes a frame black. 0.98 by default, its default.</summary>
    public double BlackPicture { get; init; } = 0.98;

    /// <summary>freezedetect's n: the largest mean absolute difference, as a share of the range, of a frame that is still the same picture. 0.001 by default, its default.</summary>
    public double FreezeNoise { get; init; } = 0.001;

    /// <summary>How flat a flat colour is (<see cref="ConstantColourOptions.Spread"/>, in 8-bit codes). 4 by default, its default.</summary>
    public double FlatSpread { get; init; } = 4;
}

/// <summary>A blank stretch one detector found, in seconds on the stream's clock (ffmpeg's <c>-copyts</c> times).</summary>
/// <param name="Kind"><c>black</c> (blackdetect), <c>flat</c> (one flat colour: a blue "no signal" screen, black too),
/// <c>frozen</c> (freezedetect) or <c>silence</c> (silencedetect).</param>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends, as the detector reports it; null for a frozen picture still frozen at the end
/// (freezedetect prints no end then).</param>
/// <param name="Colour">For <c>flat</c>: the run's mean Y, Cb and Cr in 8-bit codes.</param>
public sealed record BlankSpan(string Kind, double Start, double? End, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double[]? Colour = null);

/// <summary>
/// A stretch of the tape where the picture is blank: the detectors' picture spans that overlap or touch, merged, by
/// <see cref="TapeScanOptions"/>' rules (long enough, the sound silent too if asked, glitches between gaps taken in).
/// </summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends (the video's end for one that runs to it).</param>
/// <param name="Kinds">Which picture detectors found it.</param>
/// <param name="Silence">How many of its seconds the sound is silent too (0 without audio).</param>
public sealed record Gap(double Start, double End, IReadOnlyList<string> Kinds, double Silence);

/// <summary>A stretch between gaps: a recording to keep.</summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends.</param>
public sealed record Recording(double Start, double End);

/// <summary>What <see cref="TapeScan.Run"/> found.</summary>
/// <param name="Duration">The video's end: its last frame's time plus the step between the last two.</param>
/// <param name="Spans">Every detector's spans, as the detectors report them, by start.</param>
/// <param name="Gaps">Where the picture is blank.</param>
/// <param name="Cuts">The proposed cut points: each gap's edges, but for the tape's start and end.</param>
/// <param name="Recordings">What lies between the gaps.</param>
public sealed record TapeReport(double Duration, IReadOnlyList<BlankSpan> Spans, IReadOnlyList<Gap> Gaps, IReadOnlyList<double> Cuts, IReadOnlyList<Recording> Recordings);

/// <summary>
/// Finds the blank stretches of a digitised tape with FrameReader's detectors, each checked against the FFmpeg filter
/// it reproduces: black (<see cref="PictureMeter"/>, blackdetect), one flat colour (<see cref="ConstantColourDetector"/>:
/// a VCR's blue screen; no FFmpeg filter), a frozen picture (<see cref="FreezeDetector"/>, freezedetect) and silence
/// (<see cref="SilenceFinder"/>, silencedetect). The picture is decoded once for the three picture detectors, the
/// sound once. A gap is where the picture is blank for long enough (<see cref="TapeScanOptions.MinGap"/>); the
/// sound's silence is reported beside it, and required only with <see cref="SoundRule.Agree"/>. It finds no cut
/// without a blank stretch: that is a cut detector's work (ShotDetector).
/// </summary>
public static class TapeScan
{
    /// <summary>Scans <paramref name="path"/>.</summary>
    /// <param name="path">The capture.</param>
    /// <param name="options">What counts as blank; null: the defaults.</param>
    /// <exception cref="FrameReaderException">The file can't be read or has no video.</exception>
    /// <exception cref="NotSupportedException">The decoded picture isn't planar YUV of 8 to 16 bits.</exception>
    /// <exception cref="ArgumentException">An option is out of range, or <see cref="TapeScanOptions.Kinds"/> names no detector.</exception>
    public static TapeReport Run(string path, TapeScanOptions? options = null)
    {
        var o = options ?? new TapeScanOptions();
        if (o.MinGap <= TimeSpan.Zero || o.MinSpan <= TimeSpan.Zero || o.MinSpan > o.MinGap || o.MinRecording < TimeSpan.Zero)
            throw new ArgumentException("MinGap and MinSpan are positive, MinSpan at most MinGap, MinRecording not negative.", nameof(options));
        if (o.Kinds.Count == 0 || o.Kinds.Any(k => k is not ("black" or "flat" or "frozen")))
            throw new ArgumentException("Kinds are black, flat and frozen, one at least.", nameof(options));
        var (picture, duration, step) = Picture(path, o);
        bool sound = MediaProbe.Probe(path).HasAudio;
        var silences = sound ? Silences(path, o) : [];
        var gaps = Gaps(picture, duration, silences, o.MinGap.TotalSeconds);
        if (sound && o.Sound == SoundRule.Agree)
            gaps = Agreed(gaps, duration, silences, o.MinGap.TotalSeconds - step);
        gaps = WithoutGlitches(gaps, duration, silences, o.MinRecording.TotalSeconds);
        var cuts = gaps.SelectMany(g => new[] { g.Start, g.End }).Where(t => t > 0 && t < duration).ToList();
        var recordings = new List<Recording>();
        double at = 0;
        foreach (var g in gaps)
        {
            if (g.Start > at)
                recordings.Add(new Recording(at, g.Start));
            at = Math.Max(at, g.End);
        }

        if (at < duration)
            recordings.Add(new Recording(at, duration));
        var spans = picture.Select(p => p.Span).Concat(silences).OrderBy(s => s.Start).ThenBy(s => s.Kind, StringComparer.Ordinal).ToList();
        return new TapeReport(duration, spans, gaps, cuts, recordings);
    }

    /// <summary>
    /// Writes each of <paramref name="report"/>'s recordings as a file of its own, by stream copy (<see cref="Remux.Copy"/>
    /// with a start and an end: ffmpeg's <c>-ss S -to E -i IN -c copy OUT</c>): <c>recording-01</c> and on, with the
    /// capture's extension or <paramref name="extension"/>. FrameReader.Native's FFmpeg has no raw <c>dv</c> muxer, so
    /// with it a .dv capture's recordings go into another container (<c>.avi</c>, <c>.mkv</c>), the DV frames unchanged. Exact for a capture whose every frame is a keyframe (DV, MJPEG, FFV1 with <c>-g 1</c>); from the
    /// keyframe before the start for MPEG-2 and H.264.
    /// </summary>
    /// <param name="path">The capture <paramref name="report"/> is of.</param>
    /// <param name="report">What <see cref="Run"/> found.</param>
    /// <param name="folder">Where the files go (created).</param>
    /// <param name="extension">The files' extension, which picks their container (<c>.avi</c>); null: the capture's.</param>
    /// <param name="bitexact">Leave out what differs between FFmpeg versions (ffmpeg's <c>-fflags +bitexact</c>).</param>
    /// <returns>The files, in the recordings' order.</returns>
    /// <exception cref="FrameReaderException">The capture can't be read or the files can't be written.</exception>
    public static IReadOnlyList<string> Write(string path, TapeReport report, string folder, string? extension = null, bool bitexact = false)
    {
        Directory.CreateDirectory(folder);

        // The report's times are on the stream's clock; -ss and -to count from the container's start time.
        long from = Microseconds(MediaProbe.Probe(path).StartTime?.TotalSeconds ?? 0);
        var files = new List<string>();
        foreach (var r in report.Recordings)
        {
            string file = Path.Combine(folder, $"recording-{files.Count + 1:D2}{extension ?? Path.GetExtension(path)}");
            Remux.Copy(path, file, new RemuxOptions
            {
                Start = TimeSpan.FromTicks(Math.Max(0, Microseconds(r.Start) - from) * 10),
                End = TimeSpan.FromTicks((Microseconds(r.End) - from) * 10),
                Bitexact = bitexact,
            });
            files.Add(file);
        }

        return files;
    }

    /// <summary>Seconds as whole microseconds, ffmpeg's unit for <c>-ss</c> and <c>-to</c>.</summary>
    public static long Microseconds(double seconds) => (long)Math.Round(seconds * 1e6);

    // The picture detectors' spans, each with where it ends for the gaps (the video's end for one that runs to it).
    // Step: the last frame's duration in seconds.
    static (List<(BlankSpan Span, double End)> Spans, double Duration, double Step) Picture(string path, TapeScanOptions o)
    {
        using var reader = new VideoFrameReader(path, new() { Decoder = new() { Autorotate = false } });
        double tb = reader.TimeBase.Value;
        PictureMeter? meter = null;
        ConstantColourDetector? flat = null;
        FreezeDetector? freeze = null;
        long last = 0, step = 0;
        int frames = 0;
        while (reader.TryTake(out var frame))
        {
            using (frame)
            {
                if (meter is null)
                {
                    var format = PictureFormat.Of(frame, reader.TimeBase) ?? throw new NotSupportedException($"The picture is {frame.PixelFormat}: planar YUV of 8 to 16 bits only.");
                    meter = new PictureMeter(format, 0, int.MaxValue, o.BlackPixel);
                    if (o.Kinds.Contains("flat"))
                        flat = ConstantColourDetector.For(format, new() { MinDuration = o.MinSpan, Spread = o.FlatSpread });
                    if (o.Kinds.Contains("frozen"))
                        freeze = FreezeDetector.For(frame, reader.TimeBase, new() { MinDuration = o.MinSpan, Noise = o.FreezeNoise });
                }

                if (o.Kinds.Contains("black"))
                    meter.Add(frame);
                flat?.Add(frame);
                freeze?.Add(frame);
                if (frames > 0)
                    step = frame.Pts - last;
                last = frame.Pts;
                frames++;
            }
        }

        if (meter is null)
            return ([], 0, 0);
        double end = Seconds(last + step, tb);
        var spans = new List<(BlankSpan, double)>();

        // blackdetect ends a span that runs to the end at the last frame's pts (a FIXME in FFmpeg): the gap runs on.
        var m = meter.Measures;
        foreach (var s in o.Kinds.Contains("black") ? m.BlackSpans(o.MinSpan.TotalSeconds, o.BlackPicture) : [])
        {
            bool toEnd = s.EndFrame == m.Count - 1 && m.IsBlack(m.Count - 1, o.BlackPicture);
            spans.Add((new BlankSpan("black", Seconds(s.StartPts, tb), Seconds(s.EndPts, tb)), toEnd ? end : Seconds(s.EndPts, tb)));
        }

        foreach (var s in flat?.Finish() ?? [])
            spans.Add((new BlankSpan("flat", Seconds(s.StartPts, tb), Seconds(s.EndPts, tb), [Math.Round(s.Y, 2), Math.Round(s.Cb, 2), Math.Round(s.Cr, 2)]), Seconds(s.EndPts, tb)));

        // A format freezedetect doesn't take as it is (ffmpeg would convert it first): no frozen spans.
        foreach (var s in freeze?.Finish() ?? [])
            spans.Add((new BlankSpan("frozen", Seconds(s.StartPts, tb), s.EndPts is { } e ? Seconds(e, tb) : null), s.EndPts is { } f ? Seconds(f, tb) : end));
        return (spans, end, Seconds(step, tb));
    }

    // silencedetect's spans on the sound as ffmpeg's -f f32le gives it, timed from the first sample's time.
    static List<BlankSpan> Silences(string path, TapeScanOptions o)
    {
        using var audio = new AudioReader(path);
        var finder = new SilenceFinder(audio.SampleRate, audio.Channels, o.SilenceLevel, o.MinSpan);
        double? first = null;
        while (audio.TryRead(out var chunk))
        {
            first ??= chunk.Time.TotalSeconds;
            finder.Add(chunk.Samples);
        }

        return [.. finder.Finish().Select(s => new BlankSpan("silence", Seconds(s.StartSample, audio.SampleRate, first ?? 0), Seconds(s.EndSample, audio.SampleRate, first ?? 0)))];
    }

    // The picture spans merged where they overlap or touch, kept when that lasts minGap, each with the seconds of
    // silence within it.
    static List<Gap> Gaps(List<(BlankSpan Span, double End)> picture, double duration, List<BlankSpan> silences, double minGap)
    {
        var gaps = new List<Gap>();
        foreach (var (span, end) in picture.OrderBy(p => p.Span.Start))
        {
            if (gaps.Count > 0 && span.Start <= gaps[^1].End)
            {
                var g = gaps[^1];
                gaps[^1] = g with { End = Math.Max(g.End, end), Kinds = g.Kinds.Contains(span.Kind) ? g.Kinds : [.. g.Kinds, span.Kind] };
            }
            else
            {
                gaps.Add(new Gap(span.Start, end, [span.Kind], 0));
            }
        }

        return [.. gaps.Where(g => Micro(g.End - g.Start) >= Micro(minGap)).Select(g => g with { Silence = Silent(g.Start, g.End, duration, silences) })];
    }

    // SoundRule.Agree: of each gap, the parts where the sound is silent too, kept when they last atLeast (the shortest
    // gap less a frame: the picture's clock ticks in frames, the sound's in samples).
    static List<Gap> Agreed(List<Gap> gaps, double duration, List<BlankSpan> silences, double atLeast)
    {
        var agreed = new List<Gap>();
        foreach (var g in gaps)
        {
            foreach (var s in silences)
            {
                double start = Math.Max(g.Start, s.Start), end = Math.Min(g.End, s.End ?? duration);
                if (end > start && Micro(end - start) >= Micro(atLeast))
                    agreed.Add(g with { Start = start, End = end, Silence = Math.Round(end - start, 6) });
            }
        }

        return agreed;
    }

    // A stretch between two gaps, or a gap and the tape's start or end, shorter than minRecording is part of the gap.
    static List<Gap> WithoutGlitches(List<Gap> gaps, double duration, List<BlankSpan> silences, double minRecording)
    {
        var joined = new List<Gap>();
        foreach (var g in gaps)
        {
            if (joined.Count > 0 && Micro(g.Start - joined[^1].End) < Micro(minRecording))
            {
                var j = joined[^1];
                joined[^1] = j with { End = g.End, Kinds = [.. j.Kinds.Union(g.Kinds)] };
            }
            else
            {
                joined.Add(g);
            }
        }

        if (joined.Count > 0 && joined[0].Start > 0 && Micro(joined[0].Start) < Micro(minRecording))
            joined[0] = joined[0] with { Start = 0 };
        if (joined.Count > 0 && joined[^1].End < duration && Micro(duration - joined[^1].End) < Micro(minRecording))
            joined[^1] = joined[^1] with { End = duration };
        return [.. joined.Select(g => g with { Silence = Silent(g.Start, g.End, duration, silences) })];
    }

    static double Silent(double start, double end, double duration, List<BlankSpan> silences) =>
        Math.Round(silences.Sum(s => Math.Max(0, Math.Min(end, s.End ?? duration) - Math.Max(start, s.Start))), 6);

    static long Micro(double seconds) => (long)Math.Round(seconds * 1e6);

    static double Seconds(long pts, double timeBase) => Math.Round(pts * timeBase, 6);

    static double Seconds(long sample, int rate, double from) => Math.Round(from + (sample / (double)rate), 6);
}
