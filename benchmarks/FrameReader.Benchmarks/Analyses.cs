using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using FrameReader;

/// <summary>
/// The audio of the first video (the 1080p film, 52 s), on one decode: only reading its samples (the baseline), loudness
/// alone (ebur128 with true peaks), waveform alone, and every analysis at once (loudness, waveform, silence and
/// reference tone), as <see cref="AudioAnalysis.Run"/> does them.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(Config))]
public class AudioAnalyses
{
    sealed class Config : ManualConfig
    {
        public Config() => AddJob(Job.Default.WithLaunchCount(1).WithWarmupCount(1).WithIterationCount(5).WithInvocationCount(1).WithUnrollFactor(1));
    }

    string _video = "";

    [GlobalSetup]
    public void Setup()
    {
        FFmpegLibraries.Load(Environment.GetEnvironmentVariable("FRAMEREADER_FFMPEG_LIBS"));
        _video = ReadFrames.Videos().First();
    }

    [Benchmark(Baseline = true)]
    public long Read()
    {
        using var audio = new AudioReader(_video);
        long samples = 0;
        while (audio.TryRead(out var chunk))
            samples += chunk.Samples.Length;
        return samples;
    }

    [Benchmark]
    public object? Loudness() => Run(new AudioAnalysisOptions { Loudness = true }).Loudness;

    [Benchmark]
    public object? Waveform() => Run(new AudioAnalysisOptions { Waveform = new() }).Waveform;

    [Benchmark]
    public object All() => Run(new AudioAnalysisOptions { Loudness = true, Waveform = new(), Silence = new(), ReferenceTone = new() });

    AudioAnalysisResult Run(AudioAnalysisOptions options)
    {
        using var audio = new AudioReader(_video);
        return AudioAnalysis.Run(audio, options);
    }
}

/// <summary>
/// The picture measures on generated frames (no decoding): one 1920x1080 4:2:0 frame through each meter at 8, 10 and 12
/// bits (two frames that differ in turn, as moving video does, so FreezeDetector never sees a freeze), and a GamutMeter
/// made (its table).
/// </summary>
[MemoryDiagnoser]
[Config(typeof(Config))]
public class PictureMeasures
{
    const int Width = 1920, Height = 1080;

    sealed class Config : ManualConfig
    {
        public Config() => AddJob(Job.Default.WithLaunchCount(1).WithWarmupCount(3).WithIterationCount(10));
    }

    [Params(8, 10, 12)]
    public int Depth { get; set; }

    readonly byte[][] _y = new byte[2][], _u = new byte[2][], _v = new byte[2][];
    int _bytes, _turn;
    long _pts;
    PictureMeter _picture = null!;
    FreezeDetector _freeze = null!;
    InterlaceDetector _interlace = null!;
    GamutMeter _gamut = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bytes = Depth > 8 ? 2 : 1;
        for (int f = 0; f < 2; f++)
        {
            _y[f] = Plane(f, Width, Height);
            _u[f] = Plane(f + 2, Width / 2, Height / 2);
            _v[f] = Plane(f + 4, Width / 2, Height / 2);
        }

        (int, int)[] planes = [(Width, Height), (Width / 2, Height / 2), (Width / 2, Height / 2)];
        var format = new PictureFormat(Width, Height, false, new Rational(1, 25), BitDepth: Depth);
        _picture = new PictureMeter(format);
        _freeze = new FreezeDetector(planes, Depth, new Rational(1, 25));
        _interlace = new InterlaceDetector(planes, Depth);
        _gamut = GamutMeter.For(format);
    }

    // Values of a smooth picture with noise, within the depth's limited range.
    byte[] Plane(int seed, int width, int height)
    {
        var plane = new byte[width * height * _bytes];
        int scale = 1 << (Depth - 8);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int value = ((16 + (((x + y) * 219) / (width + height))) * scale) + (Noise.Next((seed * width * height) + (y * width) + x) % (scale * 8));
                int i = ((y * width) + x) * _bytes;
                if (_bytes == 1)
                {
                    plane[i] = (byte)Math.Min(value, 235);
                }
                else
                {
                    plane[i] = (byte)value;
                    plane[i + 1] = (byte)(value >> 8);
                }
            }
        }

        return plane;
    }

    int Next() => _turn ^= 1;

    [Benchmark]
    public void PictureMeterAdd()
    {
        int f = Next();
        _picture.Add(_y[f], Width * _bytes, _u[f], Width / 2 * _bytes, _v[f], Width / 2 * _bytes, _pts++);
    }

    [Benchmark]
    public void FreezeDetectorAdd()
    {
        int f = Next();
        _freeze.Add(_pts++, _y[f], Width * _bytes, _u[f], Width / 2 * _bytes, _v[f], Width / 2 * _bytes);
    }

    [Benchmark]
    public void InterlaceDetectorAdd()
    {
        int f = Next();
        _interlace.Add(_y[f], Width * _bytes, _u[f], Width / 2 * _bytes, _v[f], Width / 2 * _bytes);
    }

    [Benchmark]
    public void GamutMeterAdd()
    {
        int f = Next();
        _gamut.Add(_pts++, _y[f], Width * _bytes, _u[f], Width / 2 * _bytes, _v[f], Width / 2 * _bytes);
    }

    [Benchmark]
    public GamutMeter GamutMeterNew() => new(Width, Height, ChromaSubsampling.Yuv420, Depth, false);
}

/// <summary>The audio measures on generated samples (no decoding): 10 s of a loud 48 kHz stereo tone with noise through LoudnessMeter (its K-weighting, sample and true peaks).</summary>
[MemoryDiagnoser]
[Config(typeof(Config))]
public class AudioMeasures
{
    const int Rate = 48000;

    sealed class Config : ManualConfig
    {
        public Config() => AddJob(Job.Default.WithLaunchCount(1).WithWarmupCount(3).WithIterationCount(10));
    }

    float[] _stereo = [];

    [GlobalSetup]
    public void Setup()
    {
        // Loud, so that no block is quiet enough for the meters to skip.
        _stereo = new float[Rate * 10 * 2];
        for (int i = 0; i < _stereo.Length; i++)
            _stereo[i] = (float)((0.8 * Math.Sin((i / 2) * 2 * Math.PI * 997 / Rate)) + (0.15 * ((Noise.Next(i) / 256.0) - 0.5)));
    }

    [Benchmark]
    public LoudnessMeasures LoudnessMeter10s()
    {
        var meter = new LoudnessMeter(Rate, 2);
        meter.Add(_stereo);
        return meter.Finish();
    }
}

/// <summary>Reproducible noise for generated content (not for security): a hash of the index, 0 to 255.</summary>
static class Noise
{
    public static int Next(int index)
    {
        uint h = (uint)index * 2654435761U;
        h ^= h >> 15;
        h *= 2246822519U;
        h ^= h >> 13;
        return (int)(h >> 24);
    }
}
