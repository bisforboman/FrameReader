using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FrameReader;

/// <summary>
/// A Stream that can seek (a FileStream, a MemoryStream, one positioned after other bytes) is read as ffmpeg reads the
/// file: an MP4 with its moov last (ffmpeg's default, and most cameras') opens, and its probe, frames, seeks and audio
/// are the file path's; a stream that can't seek still can't open it, as ffmpeg can't on a pipe. Needs
/// SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg); without it these do nothing.
/// </summary>
public class SeekableStreamTests
{
    static readonly string? Libs = TestLibraries.Load();

    // How the test opens the file's bytes as a Stream: the file, its bytes in memory, and after 1000 other bytes.
    public static TheoryData<string> Kinds => new() { "file", "memory", "offset" };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void MoovLastMp4IsTheFiles(string kind) => WithClip("moov-last.mp4", "-c:v libx264 -crf 12 -g 12 -bf 2 -c:a aac", (dir, path) =>
    {
        Assert.True(MoovLast(path));

        // The probe: the headers and every video packet's timestamps, sizes and positions.
        var options = new ProbeOptions { PacketTimestamps = true, FirstVideoPacket = true };
        var file = MediaProbe.Probe(path, options);
        MediaInfo streamed;
        using (var stream = Open(path, kind))
            streamed = MediaProbe.Probe(stream, options);
        Assert.Equal(file.Duration, streamed.Duration);
        Assert.Equal(file.Video! with { ExtraData = default }, streamed.Video! with { ExtraData = default });
        Assert.Equal(file.Video.ExtraData.ToArray(), streamed.Video.ExtraData.ToArray());
        Assert.Equal(file.Streams, streamed.Streams);
        Assert.Equal(file.HasAudio, streamed.HasAudio);
        Assert.Equal(file.PacketTimestamps, streamed.PacketTimestamps);
        Assert.Equal(file.PacketDecodeTimestamps, streamed.PacketDecodeTimestamps);
        Assert.Equal(file.PacketSizes, streamed.PacketSizes);
        Assert.Equal(file.PacketPositions, streamed.PacketPositions);
        Assert.Equal(file.FirstVideoPacket!.Value.ToArray(), streamed.FirstVideoPacket!.Value.ToArray());

        // Every frame, then seeks back and forth, then a picture a second.
        var times = new[] { 1.3, 0.2, 2.6, 0.0, 1.96 }.Select(TimeSpan.FromSeconds).ToArray();
        static List<string> Read(VideoFrameReader reader, TimeSpan[] times)
        {
            var frames = new List<string>();
            while (reader.TryRead(out var frame))
                frames.Add(Describe(frame));
            foreach (var t in times)
                frames.Add(reader.TryReadAt(t, out var frame) ? Describe(frame) : "none");
            reader.Seek(TimeSpan.Zero);
            for (int s = 0; reader.TryReadForwardTo(TimeSpan.FromSeconds(s), out var frame); s++)
                frames.Add(Describe(frame));
            return frames;
        }

        var readerOptions = new VideoFrameReaderOptions { Width = 96, Height = 54, FrameFormat = FrameFormat.Bgr24 };
        List<string> expected;
        using (var reader = new VideoFrameReader(path, readerOptions))
            expected = Read(reader, times);
        using (var stream = Open(path, kind))
        using (var reader = new VideoFrameReader(stream, readerOptions))
            Assert.Equal(expected, Read(reader, times));
        Assert.True(expected.Count > 75);

        // The audio, whole and after a seek.
        static byte[] Samples(AudioReader reader, TimeSpan? seek)
        {
            if (seek is { } s)
                reader.Seek(s);
            var all = new MemoryStream();
            while (reader.TryRead(out var chunk))
                all.Write(MemoryMarshal.AsBytes(chunk.Samples));
            return all.ToArray();
        }

        foreach (var seek in new TimeSpan?[] { null, TimeSpan.FromSeconds(1.7) })
        {
            byte[] fromFile;
            using (var reader = new AudioReader(path))
                fromFile = Samples(reader, seek);
            using var stream = Open(path, kind);
            using var piped = new AudioReader(stream);
            Assert.True(fromFile.Length > 0);
            Assert.True(fromFile.AsSpan().SequenceEqual(Samples(piped, seek)), $"{kind}, seek {seek}: the audio differs from the file's");
        }
    });

    // A stream that can't seek is a pipe: past the read buffer (64 KB) the mdat before the moov is gone once the moov
    // is found, as with `cat moov-last.mp4 | ffmpeg -i -`, which fails; the same bytes in a seekable stream are read whole.
    [Fact]
    public void MoovLastMp4ReadOnceLosesTheFrames() => WithClip("moov-last.mp4", "-c:v libx264 -crf 12 -g 12", (dir, path) =>
    {
        Assert.True(new FileInfo(path).Length > 4 << 16);
        static int Count(VideoFrameReader reader)
        {
            int n = 0;
            while (reader.TryRead(out _))
                n++;
            return n;
        }

        int all;
        using (var reader = new VideoFrameReader(path, new() { Width = 64, Height = 36 }))
            all = Count(reader);
        Assert.Equal(75, all);
        using (var stream = Open(path, "memory"))
        using (var reader = new VideoFrameReader(stream, new() { Width = 64, Height = 36 }))
            Assert.Equal(all, Count(reader));
        using (var pipe = new Pipe(path))
        {
            int frames;
            try
            {
                using var reader = new VideoFrameReader(pipe, new() { Width = 64, Height = 36 });
                frames = Count(reader);
            }
            catch (FrameReaderException)
            {
                frames = 0;
            }

            Assert.True(frames < all, $"{frames} frames read once");
        }
    });

    // An MP3 in memory is the file: its size known, so mp3dec trims the encoder's padding as it does from a path (from
    // a pipe it can't tell the end).
    [Theory]
    [MemberData(nameof(Kinds))]
    public void Mp3IsTheFiles(string kind) => WithClip("tone.mp3", "-vn -c:a libmp3lame", (dir, path) =>
    {
        static (byte[] Samples, TimeSpan? Duration) Read(AudioReader reader)
        {
            var all = new MemoryStream();
            while (reader.TryRead(out var chunk))
                all.Write(MemoryMarshal.AsBytes(chunk.Samples));
            return (all.ToArray(), reader.Duration);
        }

        (byte[] Samples, TimeSpan? Duration) expected;
        using (var reader = new AudioReader(path))
            expected = Read(reader);
        using var stream = Open(path, kind);
        using var streamed = new AudioReader(stream);
        var actual = Read(streamed);
        Assert.Equal(expected.Duration, actual.Duration);
        Assert.True(expected.Samples.AsSpan().SequenceEqual(actual.Samples), $"{kind}: {expected.Samples.Length} bytes from the file, {actual.Samples.Length} from the stream");
    });

    static string Describe(VideoFrame frame) => $"{frame.Pts} {frame.Time.Ticks} {frame.Width}x{frame.Height} {Convert.ToHexString(SHA256.HashData(frame.Data))}";

    static Stream Open(string path, string kind)
    {
        switch (kind)
        {
            case "file":
                return File.OpenRead(path);
            case "memory":
                return new MemoryStream(File.ReadAllBytes(path), writable: false);
            default:
                var bytes = File.ReadAllBytes(path);
                var stream = new MemoryStream();
                stream.Write(new byte[1000]);
                stream.Write(bytes);
                stream.Position = 1000;
                return stream;
        }
    }

    // Whether the moov box comes after the mdat box (the top-level boxes in order).
    static bool MoovLast(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var order = new List<string>();
        for (long at = 0; at + 8 <= bytes.Length;)
        {
            long size = (uint)((bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3]);
            order.Add(System.Text.Encoding.ASCII.GetString(bytes, (int)at + 4, 4));
            if (size == 1)
                size = (long)BitConverter.ToUInt64(bytes.AsSpan((int)at + 8, 8).ToArray().Reverse().ToArray());
            if (size < 8)
                break;
            at += size;
        }

        return order.IndexOf("moov") > order.IndexOf("mdat") && order.Contains("mdat");
    }

    static void WithClip(string name, string encode, Action<string, string> test)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-seekable-").FullName;
        try
        {
            string path = Path.Combine(dir, name);

            // 640x360 at a high quality: the mdat is several times the 64 KB read buffer, so the moov can't be reached
            // without seeking past it and back.
            Ffmpeg(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=25:d=3", "-f", "lavfi", "-i",
                "aevalsrc='0.4*sin(440*2*PI*t)+0.1*random(0)':s=48000:d=3", .. encode.Split(' '), "-shortest", path]);
            test(dir, path);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

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

    /// <summary>A file's bytes as a stream that can't seek or tell its length, like a pipe.</summary>
    sealed class Pipe(string path) : Stream
    {
        readonly FileStream _file = File.OpenRead(path);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _file.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _file.Dispose();
            base.Dispose(disposing);
        }
    }
}
