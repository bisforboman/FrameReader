using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using FrameReader;

/// <summary>
/// FrameReader's own paths beside the plain read, on the first video (the 1080p film): the first
/// <see cref="Frames"/> frames as whole Bgr24 (the baseline), turned by a display rotation of 90 degrees (a phone's
/// portrait video: FrameDecoder's own rotation), scaled to 1280x720 (swscale bound) and as Yuv420p (the planar copy).
/// </summary>
[MemoryDiagnoser]
[Config(typeof(Config))]
public class ReadPaths
{
    const int Frames = 500;

    sealed class Config : ManualConfig
    {
        public Config() => AddJob(Job.Default.WithLaunchCount(1).WithWarmupCount(1).WithIterationCount(5).WithInvocationCount(1).WithUnrollFactor(1));
    }

    string _video = "";
    string _rotated = "";

    [Params("Bgr24", "Bgr24-rotated90", "Bgr24-1280x720", "Yuv420p")]
    public string Shape { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        FFmpegLibraries.Load(Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS"));
        _video = ReadFrames.Videos().First();
        _rotated = Path.Combine(Path.GetTempPath(), $"framereader-bench-rotated-{Environment.ProcessId}{Path.GetExtension(_video)}");
        if (Shape == "Bgr24-rotated90")
            Remux.Copy(_video, _rotated, new RemuxOptions { DisplayRotation = 90 });
    }

    [GlobalCleanup]
    public void Cleanup() => File.Delete(_rotated);

    [Benchmark]
    public long FrameReader()
    {
        using var reader = new VideoFrameReader(Shape == "Bgr24-rotated90" ? _rotated : _video, new VideoFrameReaderOptions
        {
            Width = Shape == "Bgr24-1280x720" ? 1280 : null, Height = Shape == "Bgr24-1280x720" ? 720 : null,
            FrameFormat = Shape == "Yuv420p" ? FrameFormat.Yuv420p : FrameFormat.Bgr24,
        });
        long sum = 0;
        for (int n = 0; n < Frames && reader.TryRead(out var frame); n++)
            sum += frame.Data[frame.Data.Length / 2];
        return sum;
    }
}

/// <summary>
/// A picture a second of each video at 320x180 Bgr24: <see cref="VideoFrameReader.ReadAt"/> (parallel readers, each told
/// its next time) and one reader's <see cref="VideoFrameReader.TryReadForwardTo"/> with <c>ReadAhead = 4</c> (keyframe
/// jumps and the frames nothing references left out).
/// </summary>
[MemoryDiagnoser]
[Config(typeof(Config))]
public class Sampling
{
    sealed class Config : ManualConfig
    {
        public Config() => AddJob(Job.Default.WithLaunchCount(1).WithWarmupCount(1).WithIterationCount(5).WithInvocationCount(1).WithUnrollFactor(1));
    }

    TimeSpan[] _times = [];

    public static IEnumerable<string> Videos() => ReadFrames.Videos();

    [ParamsSource(nameof(Videos))]
    public string Video { get; set; } = "";

    static VideoFrameReaderOptions Options(int readAhead) => new() { Width = 320, Height = 180, FrameFormat = FrameFormat.Bgr24, ReadAhead = readAhead };

    [GlobalSetup]
    public void Setup()
    {
        FFmpegLibraries.Load(Environment.GetEnvironmentVariable("SHOTDETECTOR_FFMPEG_LIBS"));
        var duration = MediaProbe.Probe(Video).Duration ?? TimeSpan.FromMinutes(1);
        _times = [.. Enumerable.Range(0, (int)duration.TotalSeconds).Select(s => TimeSpan.FromSeconds(s))];
    }

    [Benchmark(Baseline = true)]
    public long ReadAt()
    {
        long sum = 0;
        VideoFrameReader.ReadAt(Video, _times, Options(0), null, (_, frame) => Interlocked.Add(ref sum, frame.Data[frame.Data.Length / 2]));
        return sum;
    }

    [Benchmark]
    public long TryReadForwardTo()
    {
        using var reader = new VideoFrameReader(Video, Options(4));
        long sum = 0;
        foreach (var time in _times)
        {
            if (!reader.TryReadForwardTo(time, out var frame))
                break;
            sum += frame.Data[frame.Data.Length / 2];
        }

        return sum;
    }
}
