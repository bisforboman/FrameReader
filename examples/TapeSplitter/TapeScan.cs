using System.Text.Json.Serialization;
using FrameReader;

namespace TapeSplitter;

/// <summary>What <see cref="TapeScan"/> counts as blank.</summary>
public sealed record TapeScanOptions
{
    /// <summary>
    /// The shortest blank stretch that counts: each detector's own minimum (blackdetect's and freezedetect's d,
    /// silencedetect's d, the flat colour's duration). 2 s by default, their defaults.
    /// </summary>
    public TimeSpan MinGap { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>silencedetect's n in dB: samples below it are silent. -60 by default, its default.</summary>
    public double SilenceLevel { get; init; } = -60;
}

/// <summary>A blank stretch one detector found, in seconds on the stream's clock (ffmpeg's <c>-copyts</c> times).</summary>
/// <param name="Kind"><c>black</c> (blackdetect), <c>flat</c> (one flat colour: a blue "no signal" screen, black too),
/// <c>frozen</c> (freezedetect) or <c>silence</c> (silencedetect).</param>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends, as the detector reports it; null for a frozen picture still frozen at the end
/// (freezedetect prints no end then).</param>
/// <param name="Colour">For <c>flat</c>: the run's mean Y, Cb and Cr in 8-bit codes.</param>
public sealed record BlankSpan(string Kind, double Start, double? End, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double[]? Colour = null);

/// <summary>A stretch of the tape where the picture is blank: the detectors' picture spans that overlap or touch, merged.</summary>
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
/// sound once. A gap is where the picture is blank; the sound's silence is reported beside it, not required.
/// </summary>
public static class TapeScan
{
    /// <summary>Scans <paramref name="path"/>.</summary>
    /// <param name="path">The capture.</param>
    /// <param name="options">What counts as blank; null: the defaults.</param>
    /// <exception cref="FrameReaderException">The file can't be read or has no video.</exception>
    /// <exception cref="NotSupportedException">The decoded picture isn't planar YUV of 8 to 16 bits.</exception>
    public static TapeReport Run(string path, TapeScanOptions? options = null)
    {
        var o = options ?? new TapeScanOptions();
        var (picture, duration) = Picture(path, o);
        var silences = MediaProbe.Probe(path).HasAudio ? Silences(path, o) : [];
        var gaps = Gaps(picture, duration, silences);
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
    /// capture's extension. Exact for a capture whose every frame is a keyframe (DV, MJPEG, FFV1 with <c>-g 1</c>); from the
    /// keyframe before the start for MPEG-2 and H.264.
    /// </summary>
    /// <param name="path">The capture <paramref name="report"/> is of.</param>
    /// <param name="report">What <see cref="Run"/> found.</param>
    /// <param name="folder">Where the files go (created).</param>
    /// <param name="bitexact">Leave out what differs between FFmpeg versions (ffmpeg's <c>-fflags +bitexact</c>).</param>
    /// <returns>The files, in the recordings' order.</returns>
    /// <exception cref="FrameReaderException">The capture can't be read or the files can't be written.</exception>
    public static IReadOnlyList<string> Write(string path, TapeReport report, string folder, bool bitexact = false)
    {
        Directory.CreateDirectory(folder);

        // The report's times are on the stream's clock; -ss and -to count from the container's start time.
        long from = Microseconds(MediaProbe.Probe(path).StartTime?.TotalSeconds ?? 0);
        var files = new List<string>();
        foreach (var r in report.Recordings)
        {
            string file = Path.Combine(folder, $"recording-{files.Count + 1:D2}{Path.GetExtension(path)}");
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
    static (List<(BlankSpan Span, double End)> Spans, double Duration) Picture(string path, TapeScanOptions o)
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
                    meter = new PictureMeter(format);
                    flat = ConstantColourDetector.For(format, new() { MinDuration = o.MinGap });
                    freeze = FreezeDetector.For(frame, reader.TimeBase, new() { MinDuration = o.MinGap });
                }

                meter.Add(frame);
                flat!.Add(frame);
                freeze?.Add(frame);
                if (frames > 0)
                    step = frame.Pts - last;
                last = frame.Pts;
                frames++;
            }
        }

        if (meter is null)
            return ([], 0);
        double end = Seconds(last + step, tb);
        var spans = new List<(BlankSpan, double)>();

        // blackdetect ends a span that runs to the end at the last frame's pts (a FIXME in FFmpeg): the gap runs on.
        var m = meter.Measures;
        foreach (var s in m.BlackSpans(o.MinGap.TotalSeconds))
        {
            bool toEnd = s.EndFrame == m.Count - 1 && m.IsBlack(m.Count - 1);
            spans.Add((new BlankSpan("black", Seconds(s.StartPts, tb), Seconds(s.EndPts, tb)), toEnd ? end : Seconds(s.EndPts, tb)));
        }

        foreach (var s in flat!.Finish())
            spans.Add((new BlankSpan("flat", Seconds(s.StartPts, tb), Seconds(s.EndPts, tb), [Math.Round(s.Y, 2), Math.Round(s.Cb, 2), Math.Round(s.Cr, 2)]), Seconds(s.EndPts, tb)));

        // A format freezedetect doesn't take as it is (ffmpeg would convert it first): no frozen spans.
        foreach (var s in freeze?.Finish() ?? [])
            spans.Add((new BlankSpan("frozen", Seconds(s.StartPts, tb), s.EndPts is { } e ? Seconds(e, tb) : null), s.EndPts is { } f ? Seconds(f, tb) : end));
        return (spans, end);
    }

    // silencedetect's spans on the sound as ffmpeg's -f f32le gives it, timed from the first sample's time.
    static List<BlankSpan> Silences(string path, TapeScanOptions o)
    {
        using var audio = new AudioReader(path);
        var finder = new SilenceFinder(audio.SampleRate, audio.Channels, o.SilenceLevel, o.MinGap);
        double? first = null;
        while (audio.TryRead(out var chunk))
        {
            first ??= chunk.Time.TotalSeconds;
            finder.Add(chunk.Samples);
        }

        return [.. finder.Finish().Select(s => new BlankSpan("silence", Seconds(s.StartSample, audio.SampleRate, first ?? 0), Seconds(s.EndSample, audio.SampleRate, first ?? 0)))];
    }

    // The picture spans merged where they overlap or touch, each with the seconds of silence within it.
    static List<Gap> Gaps(List<(BlankSpan Span, double End)> picture, double duration, List<BlankSpan> silences)
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

        return [.. gaps.Select(g => g with
        {
            Silence = Math.Round(silences.Sum(s => Math.Max(0, Math.Min(g.End, s.End ?? duration) - Math.Max(g.Start, s.Start))), 6),
        })];
    }

    static double Seconds(long pts, double timeBase) => Math.Round(pts * timeBase, 6);

    static double Seconds(long sample, int rate, double from) => Math.Round(from + (sample / (double)rate), 6);
}
