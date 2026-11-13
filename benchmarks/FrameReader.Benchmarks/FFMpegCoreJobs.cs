using System.Drawing;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using FFMpegCore;
using FrameReader;

/// <summary>
/// The jobs people use FFMpegCore for, each done both ways on the same file (docs/coming-from-ffmpegcore.md): probing
/// (<c>FFProbe.Analyse</c>, an ffprobe process, against <see cref="MediaProbe.Probe(string, ProbeOptions?)"/>), a
/// 320x180 JPEG thumbnail at 10 s (<c>FFMpeg.Snapshot</c>, an ffmpeg process, against <see cref="VideoFrameReader.TryTakeAt"/>
/// and <see cref="ImageEncoder"/>, the file <c>ffmpeg -ss 10 -i X -vf scale=320:180 -frames:v 1 out.jpg</c> writes), and a
/// stream copy into a new MP4 (<c>-c copy</c> through <c>FFMpegArguments</c>, against <see cref="Remux.Copy"/>). Frames
/// into memory are <see cref="ReadFrames"/>. Each category's FrameReader method is its baseline.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(Config))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class FFMpegCoreJobs
{
    static readonly TimeSpan At = TimeSpan.FromSeconds(10);

    sealed class Config : ManualConfig
    {
        // Milliseconds to seconds each (a process start, a seek and decode, a whole file copied): a few iterations.
        public Config() => AddJob(Job.Default.WithLaunchCount(1).WithWarmupCount(2).WithIterationCount(8).WithInvocationCount(1).WithUnrollFactor(1));
    }

    static readonly string? Libs = Environment.GetEnvironmentVariable("FRAMEREADER_FFMPEG_LIBS");
    static readonly string FfmpegDir = Environment.GetEnvironmentVariable("BENCH_FFMPEG") ?? "";

    public static IEnumerable<string> Videos() => ReadFrames.Videos();

    [ParamsSource(nameof(Videos))]
    public string Video { get; set; } = "";

    string _out = "";
    ImageEncoder _jpeg = null!;

    [GlobalSetup]
    public void Setup()
    {
        if (FfmpegDir.Length > 0)
            GlobalFFOptions.Configure(new FFOptions { BinaryFolder = FfmpegDir });
        FFmpegLibraries.Load(Libs);
        _jpeg = new ImageEncoder(new() { Format = ImageFormat.Jpg });
        _out = Path.Combine(Path.GetTempPath(), $"framereader-bench-{Environment.ProcessId}");
        Directory.CreateDirectory(_out);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _jpeg.Dispose();
        Directory.Delete(_out, true);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Probe")]
    public object? ProbeFrameReader()
    {
        var info = MediaProbe.Probe(Video);
        return (info.Duration, info.Video?.Width, info.Video?.Height, info.Video?.Codec, info.Video?.Rotation);
    }

    [Benchmark]
    [BenchmarkCategory("Probe")]
    public object? ProbeFFMpegCore()
    {
        var info = FFProbe.Analyse(Video);
        return (info.Duration, info.PrimaryVideoStream?.Width, info.PrimaryVideoStream?.Height, info.PrimaryVideoStream?.CodecName, info.PrimaryVideoStream?.Rotation);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Thumbnail")]
    public long ThumbnailFrameReader()
    {
        string path = Path.Combine(_out, "fr.jpg");
        using var reader = new VideoFrameReader(Video);
        if (!reader.TryTakeAt(At, out var frame))
            return 0;
        using (frame)
            File.WriteAllBytes(path, _jpeg.Encode(frame, new ImageFilterOptions { Width = 320, Height = 180 }));
        return new FileInfo(path).Length;
    }

    [Benchmark]
    [BenchmarkCategory("Thumbnail")]
    public long ThumbnailFFMpegCore()
    {
        string path = Path.Combine(_out, "core.jpg");
        FFMpeg.Snapshot(Video, path, new Size(320, 180), At);
        return new FileInfo(path).Length;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("StreamCopy")]
    public long StreamCopyFrameReader()
    {
        string path = Path.Combine(_out, "fr.mp4");
        Remux.Copy(Video, path);
        return new FileInfo(path).Length;
    }

    [Benchmark]
    [BenchmarkCategory("StreamCopy")]
    public long StreamCopyFFMpegCore()
    {
        string path = Path.Combine(_out, "core.mp4");
        FFMpegArguments.FromFileInput(Video).OutputToFile(path, true, o => o.CopyChannel()).ProcessSynchronously();
        return new FileInfo(path).Length;
    }
}
