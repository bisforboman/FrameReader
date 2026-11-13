using System.Diagnostics;
using FrameReader;

/// <summary>
/// <see cref="AudioAnalysis"/>: every result it gives is the one its standalone call gives on a reader opened with the
/// same options, to the bit (tone segments, the waveform's points and .dat bytes, loudness's doubles, silences, the
/// callback's chunks), alone and all together, on integer, float and lossy audio, 5.1, Matroska's millisecond
/// timestamps, a second stream, a resampling and downmixing reader, after a seek and with a tone search shorter than
/// the clip; and all of them read the input once. Needs FRAMEREADER_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder
/// with ffmpeg); without it these do nothing.
/// </summary>
public class AudioAnalysisTests
{
    static readonly string? Libs = TestLibraries.Load();

    static void Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")) { RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, error);
    }

    // 4 s of -18 dBFS line-up tone, 2.5 s of silence, then programme (a swelling chord over noise), 12 s.
    const string Channel = "if(lt(t,4),0.1259*sin(2*PI*1000*t),if(lt(t,6.5),0,(0.2*sin(2*PI*220*t)+0.1*sin(2*PI*660*t))*(0.6+0.4*sin(2*PI*0.3*t))+0.02*(random(0)-0.5)))";
    const string Stereo = $"aevalsrc='{Channel}|{Channel}':s=48000:d=12";

    static string Make(string dir, string name, string args)
    {
        string path = Path.Combine(dir, name);
        Ffmpeg(["-v", "error", "-y", .. args.Split(' '), path]);
        return path;
    }

    static readonly ReferenceToneOptions Tone = new() { MinDuration = TimeSpan.FromSeconds(2) };
    static readonly WaveformOptions Waveform = new() { PixelsPerSecond = 100 };
    static readonly SilenceOptions Silence = new() { Level = -50, Duration = TimeSpan.FromSeconds(1) };

    public static TheoryData<string, string> Clips => new()
    {
        { "s16.wav", "-c:a pcm_s16le" },
        { "s24.wav", "-c:a pcm_s24le" },
        { "f32.wav", "-c:a pcm_f32le" },
        { "aac.m4a", "-c:a aac" },
        { "mp3.mp3", "-c:a libmp3lame" },
        { "opus.ogg", "-c:a libopus" },
        { "aac.mkv", "-c:a aac" }, // millisecond timestamps
    };

    [Theory]
    [MemberData(nameof(Clips))]
    public void EachResultIsItsOwnCalls(string name, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-analysis-").FullName;
        try
        {
            string path = Make(dir, name, $"-f lavfi -i {Stereo} {encode}");
            Check(path, new AudioReaderOptions());
            Check(path, new AudioReaderOptions { SampleRate = 16000, Channels = 1 }); // resampled and downmixed
            Check(path, new AudioReaderOptions(), seek: TimeSpan.FromSeconds(2.5123)); // inside a frame: the trim
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SurroundSecondStreamAndSplitChannels()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-analysis-").FullName;
        try
        {
            string surround = Make(dir, "surround.wav", $"-f lavfi -i aevalsrc='{Channel}|{Channel}|{Channel}|{Channel}|{Channel}|{Channel}':s=48000:d=12:c=5.1 -c:a pcm_s16le");
            Check(surround, new AudioReaderOptions(), split: true);

            // Two tracks, the second one read: a 44.1 kHz mono one of its own.
            string tracks = Make(dir, "tracks.mkv", $"-f lavfi -i {Stereo} -f lavfi -i aevalsrc='{Channel}':s=44100:d=9 -map 0 -map 1 -c:a flac");
            Check(tracks, new AudioReaderOptions { Stream = 1 }, split: true);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Every analysis alone, then all four with the callback, each against its own call on a fresh reader.</summary>
    static void Check(string path, AudioReaderOptions readerOptions, TimeSpan? seek = null, bool split = false)
    {
        AudioReader Open()
        {
            var r = new AudioReader(path, readerOptions);
            if (seek is { } s)
                r.Seek(s);
            return r;
        }

        var waveformOptions = Waveform with { SplitChannels = split };
        IReadOnlyList<ToneSegment> tone;
        WaveformData waveform;
        LoudnessResult loudness;
        var silenceFinder = default(SilenceFinder);
        var chunks = new List<(TimeSpan Time, float[] Samples)>();
        long onePass;
        using (var a = Open())
            tone = ReferenceTone.Find(a, Tone);
        using (var a = Open())
        {
            waveform = WaveformData.Read(a, waveformOptions);
            onePass = a.PacketsRead;
        }

        using (var a = Open())
            loudness = Loudness.Measure(a);
        using (var a = Open())
        {
            silenceFinder = new SilenceFinder(a.SampleRate, a.Channels, Silence.Level, Silence.Duration);
            while (a.TryRead(out var chunk))
            {
                silenceFinder.Add(chunk.Samples);
                chunks.Add((chunk.Time, chunk.Samples.ToArray()));
            }
        }

        var silences = silenceFinder.Finish();
        Assert.NotEmpty(silences);
        if (seek is null && readerOptions.Stream is null)
            Assert.NotEmpty(tone);

        string what = $"{Path.GetFileName(path)} {readerOptions} seek {seek}";
        using (var a = Open())
            Assert.Equal(tone, AudioAnalysis.Run(a, new() { ReferenceTone = Tone }).ReferenceTone);
        using (var a = Open())
            SameWaveform(waveform, AudioAnalysis.Run(a, new() { Waveform = waveformOptions }).Waveform!, what);
        using (var a = Open())
            Assert.Equal(loudness, AudioAnalysis.Run(a, new() { Loudness = true }).Loudness);
        using (var a = Open())
            Assert.Equal(silences, AudioAnalysis.Run(a, new() { Silence = Silence }).Silences);

        using (var a = Open())
        {
            var given = new List<(TimeSpan Time, float[] Samples)>();
            var r = AudioAnalysis.Run(a,
                new()
                {
                    ReferenceTone = Tone,
                    Waveform = waveformOptions,
                    Loudness = true,
                    Silence = Silence,
                    Chunks = chunk => given.Add((chunk.Time, chunk.Samples.ToArray())),
                });
            Assert.Equal(tone, r.ReferenceTone);
            SameWaveform(waveform, r.Waveform!, what);
            Assert.Equal(loudness, r.Loudness);
            Assert.Equal(silences, r.Silences);
            Assert.Equal(a.SampleRate, r.SampleRate);
            Assert.Equal(chunks.Count, given.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                Assert.Equal(chunks[i].Time, given[i].Time);
                Assert.True(chunks[i].Samples.AsSpan().SequenceEqual(given[i].Samples), $"{what}: chunk {i} differs");
            }

            Assert.Equal(onePass, a.PacketsRead); // four analyses, one pass
        }
    }

    static void SameWaveform(WaveformData expected, WaveformData actual, string what)
    {
        foreach (var bits in new[] { WaveformBits.Sixteen, WaveformBits.Eight })
        {
            using MemoryStream e = new(), a = new();
            expected.Save(e, bits);
            actual.Save(a, bits);
            Assert.True(e.ToArray().AsSpan().SequenceEqual(a.ToArray()), $"{what}: the {bits}-bit .dat differs");
        }
    }

    [Fact]
    public void ToneAloneStopsWhereItsSearchEnds()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-analysis-").FullName;
        try
        {
            string path = Make(dir, "s16.wav", $"-f lavfi -i {Stereo} -c:a pcm_s16le");
            foreach (double seconds in new[] { 3, 60 })
            {
                // Shorter and longer than the clip.
                var options = Tone with { SearchDuration = TimeSpan.FromSeconds(seconds) };
                IReadOnlyList<ToneSegment> expected;
                long packets;
                using (var a = new AudioReader(path))
                {
                    expected = ReferenceTone.Find(a, options);
                    packets = a.PacketsRead;
                }

                using (var a = new AudioReader(path))
                {
                    Assert.Equal(expected, AudioAnalysis.Run(a, new() { ReferenceTone = options }).ReferenceTone);
                    Assert.Equal(packets, a.PacketsRead); // decoding stops where Find's does
                }

                // With loudness too: the tone the same, the pass to the end, loudness the whole clip's.
                using (var a = new AudioReader(path))
                {
                    var r = AudioAnalysis.Run(a, new() { ReferenceTone = options, Loudness = true });
                    Assert.Equal(expected, r.ReferenceTone);
                    using var b = new AudioReader(path);
                    Assert.Equal(Loudness.Measure(b), r.Loudness);
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NoSamples()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-analysis-").FullName;
        try
        {
            string path = Make(dir, "empty.wav", "-f lavfi -i anullsrc=r=48000:cl=stereo -t 0 -c:a pcm_s16le");
            using (var a = new AudioReader(path))
            {
                var r = AudioAnalysis.Run(a, new() { ReferenceTone = Tone, Waveform = Waveform, Silence = Silence });
                Assert.Empty(r.ReferenceTone!);
                Assert.Equal(0, r.Waveform!.Length);
                Assert.Empty(r.Silences!);
                Assert.Null(r.Loudness);
            }

            // Loudness throws as Loudness.Measure does, so the whole call does.
            using (var a = new AudioReader(path))
                Assert.Equal(FrameReaderError.InvalidInput, Assert.Throws<FrameReaderException>(() => AudioAnalysis.Run(a, new() { Loudness = true, Waveform = Waveform })).Reason);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void OptionsFailBeforeReading()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-analysis-").FullName;
        try
        {
            string path = Make(dir, "s16.wav", $"-f lavfi -i {Stereo} -c:a pcm_s16le");
            using var a = new AudioReader(path);
            Assert.Throws<ArgumentException>(() => AudioAnalysis.Run(a, new()));
            Assert.ThrowsAny<ArgumentException>(() => AudioAnalysis.Run(a, new() { Loudness = true, Waveform = new() { SamplesPerPixel = 1 } }));
            Assert.ThrowsAny<ArgumentException>(() => AudioAnalysis.Run(a, new() { Loudness = true, Silence = new() { Duration = TimeSpan.FromSeconds(-1) } }));
            Assert.Equal(0, a.PacketsRead);

            // The callback's exception ends the pass, unchanged; cancellation is an OperationCanceledException.
            var thrown = new InvalidOperationException("the host's");
            Assert.Same(thrown, Assert.Throws<InvalidOperationException>(() => AudioAnalysis.Run(a, new() { Chunks = _ => throw thrown })));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => AudioAnalysis.Run(a, new() { Loudness = true }, cts.Token));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ProgressReachesTheEnd()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-analysis-").FullName;
        try
        {
            string path = Make(dir, "s16.wav", $"-f lavfi -i {Stereo} -c:a pcm_s16le");
            var reports = new List<MediaProgress>();
            using var a = new AudioReader(path);
            var r = await AudioAnalysis.RunAsync(a, new() { Loudness = true, Progress = new Sync(reports.Add) });
            Assert.NotNull(r.Loudness);
            Assert.Equal(1.0, reports[^1].Fraction);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // With loudness and another analysis the loudness graph runs on a thread of its own: what fails there is thrown on
    // the caller's thread, as the same exception the one-thread pass throws, and nothing is left behind.
    [Fact]
    public void LoudnessThreadFailureIsTheCallersException()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-analysis-").FullName;
        try
        {
            string path = Make(dir, "s16.wav", $"-f lavfi -i {Stereo} -c:a pcm_s16le");
            const string Bad = "ebur128=peak=true+sample,nosuchfilter";

            // The graph doesn't build (on the first frame): FrameReaderException, InvalidInput, alone and with others.
            foreach (var options in new AudioAnalysisOptions[] { new() { Loudness = true }, new() { Loudness = true, Waveform = Waveform } })
            {
                using var a = new AudioReader(path);
                var e = Assert.Throws<FrameReaderException>(() => AudioAnalysis.RunCore(a, options, default, Bad, null));
                Assert.Equal(FrameReaderError.InvalidInput, e.Reason);
            }

            // A failure mid-run on the loudness thread: that very exception, and the pass stops.
            using (var a = new AudioReader(path))
            {
                var thrown = new FrameReaderException(FrameReaderError.DecodeFailed, "mid-run");
                int frames = 0;
                Assert.Same(thrown,
                    Assert.Throws<FrameReaderException>(() => AudioAnalysis.RunCore(a,
                        new() { Loudness = true, Waveform = Waveform, Silence = Silence },
                        default,
                        Loudness.Graph,
                        () =>
                        {
                            if (++frames == 20)
                                throw thrown;
                        })));
                Assert.Equal(20, frames);
            }

            Assert.Equal(0, LoudnessThread.Held);
            Assert.Equal(0, LoudnessThread.Running);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CancelledMidRunOnTheLoudnessThread()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-analysis-").FullName;
        try
        {
            string path = Make(dir, "s16.wav", $"-f lavfi -i {Stereo} -c:a pcm_s16le");
            long all;
            using (var a = new AudioReader(path))
            {
                AudioAnalysis.Run(a, new() { Waveform = Waveform });
                all = a.PacketsRead;
            }

            // Cancelled from the loudness thread while the caller's thread decodes: both stop, within a queue's length.
            using (var a = new AudioReader(path))
            using (var cts = new CancellationTokenSource())
            {
                int frames = 0, chunks = 0;
                Assert.ThrowsAny<OperationCanceledException>(() => AudioAnalysis.RunCore(a,
                    new() { Loudness = true, Waveform = Waveform, Chunks = _ => chunks++ },
                    cts.Token,
                    Loudness.Graph,
                    () =>
                    {
                        if (++frames == 10)
                            cts.Cancel();
                    }));
                Assert.Equal(10, frames);
                Assert.InRange(a.PacketsRead, 10, 10 + LoudnessThread.Depth + 2);
                Assert.True(a.PacketsRead < all);
            }

            // Cancelled from the caller's thread (the callback), while the loudness thread works.
            using (var a = new AudioReader(path))
            using (var cts = new CancellationTokenSource())
            {
                int chunks = 0;
                Assert.ThrowsAny<OperationCanceledException>(() => AudioAnalysis.Run(a,
                    new()
                    {
                        Loudness = true,
                        Chunks = _ =>
                        {
                            if (++chunks == 10)
                                cts.Cancel();
                        },
                    },
                    cts.Token));
                Assert.Equal(10, chunks);
            }

            Assert.Equal(0, LoudnessThread.Held);
            Assert.Equal(0, LoudnessThread.Running);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    sealed class Sync(Action<MediaProgress> report) : IProgress<MediaProgress>
    {
        public void Report(MediaProgress value) => report(value);
    }
}
