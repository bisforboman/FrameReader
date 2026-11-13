using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FrameReader;
using TapeSplitter;

/// <summary>
/// The tape splitter's report against FFmpeg 8.1's own filters on a tape made with lavfi: black leader, a recording, a
/// blue "no signal" screen, a recording that freezes on its last frame, a recording, black to the end, the sound silent
/// wherever the picture is blank. Its black, frozen and silent spans print as blackdetect's, freezedetect's and
/// silencedetect's (on the samples as floats, <c>aformat=flt</c>, as <see cref="SilenceFinder"/> takes them) with
/// <c>-copyts</c>; the flat colours (no FFmpeg filter) and the gaps, cut points and recordings are what the tape was
/// made of. As a lossless capture (FFV1, PCM), as DV (the codec of DV tapes and their captures) and at 10 bits 4:2:2
/// without sound. Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg); without it these do
/// nothing.
/// </summary>
public partial class TapeScanTests
{
    static readonly string? Libs = Load();

    // Inputs 0 to 5: black 2 s, a recording 4 s, blue 3 s, a recording 4 s (frozen on its last frame for 3 s), a
    // recording 4 s (testsrc2 from 3 s, so it differs from the frozen frame), black 3 s; 6: the sound.
    const string Tape = "-f lavfi -i color=black:{0}:d=2 -f lavfi -i testsrc2={0}:d=4 -f lavfi -i color=0x1010d0:{0}:d=3 "
        + "-f lavfi -i testsrc2={0}:d=4 -f lavfi -i testsrc2={0}:d=4 -f lavfi -i color=black:{0}:d=3 "
        + "-f lavfi -i aevalsrc=0.3*sin(2*PI*440*t)*(between(t\\,2\\,6)+between(t\\,9\\,13)+between(t\\,16\\,20)):s=48000:c=stereo:d=23 "
        + "-filter_complex [3]tpad=stop_mode=clone:stop_duration=3[b];[4]setpts=PTS+3/TB,trim=start=3,setpts=PTS-STARTPTS[c];"
        + "[0][1][2][b][c][5]concat=n=6:v=1:a=0,format={1}[v] -map [v]";

    [Theory]
    [InlineData("tape.mkv", "s=320x240:r=25", "yuv420p", "-map 6:a -c:v ffv1 -c:a pcm_s16le")]
    [InlineData("tape.dv", "s=720x576:r=25", "yuv420p", "-map 6:a -c:v dvvideo -c:a pcm_s16le")]
    [InlineData("silent.mkv", "s=320x240:r=25", "yuv422p10le", "-c:v ffv1")]
    public void IsFfmpegsFilters(string name, string size, string format, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-tape-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Ffmpeg(["-v", "error", "-y", .. string.Format(CultureInfo.InvariantCulture, Tape, size, format).Split(' '), .. encode.Split(' '), path]);
            bool sound = encode.Contains("-c:a", StringComparison.Ordinal);
            var report = TapeScan.Run(path);

            // blackdetect, freezedetect and silencedetect in one pass, on the stream's own timestamps.
            string log = Ffmpeg(["-hide_banner", "-nostats", "-nostdin", "-copyts", "-i", path, "-vf", "blackdetect,freezedetect",
                .. sound ? ["-af", "aformat=sample_fmts=flt,silencedetect"] : Array.Empty<string>(), "-f", "null", "-"]);
            Same("black", report, [.. Black().Matches(log).Select(m => (m.Groups[1].Value, (string?)m.Groups[2].Value))]);
            Same("frozen", report, Frozen(log));
            Same("silence", report, [.. Silence(log).Select(s => (s.Item1, (string?)s.Item2))]);
            Assert.Equal(sound, report.Spans.Any(s => s.Kind == "silence"));

            // The flat colours: black (16, 128, 128) at either end, the blue screen between the first two recordings.
            var flat = report.Spans.Where(s => s.Kind == "flat").ToList();
            Assert.Equal([(0.0, 2.0), (6.0, 9.0), (20.0, 23.0)], flat.Select(s => (s.Start, s.End!.Value)));
            Assert.All(new[] { flat[0], flat[2] }, s => Assert.Equal([16, 128, 128], s.Colour!.Select(c => Math.Round(c))));
            Assert.True(flat[1].Colour![1] > 200, $"the blue screen's Cb {flat[1].Colour![1]}");

            // The gaps, cut points and recordings: what the tape was made of. The frozen picture's gap starts at its
            // first frame, the recording's last (freezedetect's start).
            Assert.Equal(23, report.Duration);
            Assert.Equal([(0.0, 2.0), (6.0, 9.0), (12.96, 16.0), (20.0, 23.0)], report.Gaps.Select(g => (g.Start, g.End)));
            Assert.Equal(["black", "flat", "frozen"], report.Gaps[0].Kinds);
            Assert.Equal(["flat", "frozen"], report.Gaps[1].Kinds);
            Assert.Equal(["frozen"], report.Gaps[2].Kinds);
            Assert.Equal(sound ? [2, 3, 3, 3] : new double[] { 0, 0, 0, 0 }, report.Gaps.Select(g => Math.Round(g.Silence)));
            Assert.Equal([2, 6, 9, 12.96, 16, 20], report.Cuts);
            Assert.Equal([new Recording(2, 6), new Recording(9, 12.96), new Recording(16, 20)], report.Recordings);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // One detector's spans in the report against FFmpeg's printed times (null: no end printed).
    static void Same(string kind, TapeReport report, List<(string Start, string? End)> theirs)
    {
        var ours = report.Spans.Where(s => s.Kind == kind).ToList();
        Assert.True(ours.Count == theirs.Count, $"{kind}: {ours.Count} spans, FFmpeg's {theirs.Count}: {string.Join(", ", theirs)}");
        foreach (var (o, t) in ours.Zip(theirs))
        {
            Assert.True(Printed(t.Start, o.Start), $"{kind}: {o}, FFmpeg's {t}");
            Assert.True(t.End is null ? o.End is null : o.End is { } end && Printed(t.End, end), $"{kind}: {o}, FFmpeg's {t}");
        }
    }

    // freezedetect's start, and its end where it printed one (none for a picture still frozen at the end).
    static List<(string, string?)> Frozen(string log)
    {
        var spans = new List<(string, string?)>();
        foreach (Match m in Freeze().Matches(log))
        {
            if (m.Groups[1].Value == "start")
                spans.Add((m.Groups[2].Value, null));
            else if (m.Groups[1].Value == "end")
                spans[^1] = (spans[^1].Item1, m.Groups[2].Value);
        }

        return spans;
    }

    static List<(string, string)> Silence(string log) =>
        [.. SilenceStart().Matches(log).Zip(SilenceEnd().Matches(log), (s, e) => (s.Groups[1].Value, e.Groups[1].Value))];

    /// <summary>Whether <paramref name="value"/> prints as <paramref name="text"/>: within half its last digit.</summary>
    static bool Printed(string text, double value)
    {
        int dot = text.IndexOf('.', StringComparison.Ordinal);
        int decimals = dot < 0 ? 0 : text.Length - dot - 1;
        return Math.Abs(double.Parse(text, CultureInfo.InvariantCulture) - value) <= Math.Pow(10, -decimals) / 2 * (1 + 1e-9);
    }

    static string Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
        return err + stdout.Result;
    }

    static string? Load()
    {
        string? dir = Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS");
        if (dir is not null)
            FFmpegLibraries.Load(dir);
        return dir;
    }

    [GeneratedRegex(@"black_start:(\S+) black_end:(\S+)")]
    private static partial Regex Black();

    [GeneratedRegex(@"lavfi\.freezedetect\.freeze_(start|end): (\S+)")]
    private static partial Regex Freeze();

    [GeneratedRegex(@"silence_start: (\S+)")]
    private static partial Regex SilenceStart();

    [GeneratedRegex(@"silence_end: (\S+)")]
    private static partial Regex SilenceEnd();
}
