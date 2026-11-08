using FrameReader;

// An upload in, a JPEG thumbnail and the video's duration out: the request body is copied into memory and read from
// there by FFmpeg's libraries in this process. No temporary file, no ffmpeg process.
//
//   curl --data-binary @phone.mp4 -H "Content-Type: video/mp4" http://localhost:5000/thumbnail
//
// The body is the video itself, not a form: ASP.NET Core buffers a multipart form's large files to disk.
const long MaxUpload = 512L << 20;
const int ThumbnailWidth = 640;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = MaxUpload);
var app = builder.Build();

// FFmpeg's libraries, once: FrameReader.Native's next to the app, or the folder in FrameReader:Libraries (the
// configuration, e.g. FrameReader__Libraries in the environment). A missing library fails here, not on a request.
FFmpegLibraries.Load(app.Configuration["FrameReader:Libraries"]);

// An upload is untrusted: only the containers a video upload comes in, and no URLs or files opened on its behalf (a
// playlist would name some). FrameDecoderOptions.MaxPixels (8192 x 8192 by default) refuses frames that would exhaust
// memory.
var inputOptions = new Dictionary<string, string>
{
    ["format_whitelist"] = "mov,mp4,matroska,webm,mpegts",
    ["protocol_whitelist"] = "pipe",
};

app.MapPost("/thumbnail", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    // In memory: a MemoryStream can seek, so FrameReader reads it as ffmpeg reads a file, and any MP4 opens (also one
    // with its moov last, as phones and cameras write them). Headers-first formats (mkv, webm, ts, a faststart mp4)
    // could be read straight from request.Body instead, without the copy.
    using var video = new MemoryStream();
    await request.Body.CopyToAsync(video, cancellationToken);
    if (video.Length == 0)
        return Results.BadRequest("Send the video as the request body.");

    try
    {
        video.Position = 0;
        var info = MediaProbe.Probe(video, new ProbeOptions { InputOptions = inputOptions });
        if (info.Video is null)
            return Results.UnprocessableEntity("The upload has no video.");

        // A tenth of the way in (past fades from black), the frame `ffmpeg -ss T -i X` gives there, upright as a
        // phone's display rotation says, scaled as `-vf scale='min(640,iw)':-2`. The file is the one
        // `ffmpeg -ss T -i X -vf scale='min(640,iw)':-2 -frames:v 1 out.jpg` writes.
        var at = (info.Duration ?? TimeSpan.Zero) / 10;
        video.Position = 0;
        using var reader = new VideoFrameReader(video, new VideoFrameReaderOptions { Decoder = new FrameDecoderOptions { InputOptions = inputOptions } });
        if (!reader.TryTakeAt(at, out var frame) && !reader.TryTakeAt(TimeSpan.Zero, out frame))
            return Results.UnprocessableEntity("The video has no frames.");
        using (frame)
        {
            using var jpeg = new ImageEncoder(new ImageEncoderOptions { Format = ImageFormat.Jpg, JpegQscale = 3 });
            byte[] thumbnail = jpeg.Encode(frame, new ImageFilterOptions { MaxWidth = ThumbnailWidth });
            return Results.Ok(new Thumbnail(
                info.Duration?.TotalSeconds,
                info.Video.Width,
                info.Video.Height,
                info.Video.Rotation,
                info.Video.Codec,
                frame.Time?.TotalSeconds,
                $"data:image/jpeg;base64,{Convert.ToBase64String(thumbnail)}"));
        }
    }
    catch (FrameReaderException e) when (e.Reason is FrameReaderError.InvalidInput or FrameReaderError.DecodeFailed)
    {
        return Results.UnprocessableEntity($"Not a video FFmpeg can read: {e.Message}");
    }
});

app.Run();

/// <summary>What the endpoint answers: the probe's figures (seconds, pixels as stored, degrees) and the JPEG as a data URL.</summary>
internal sealed record Thumbnail(double? Duration, int Width, int Height, double? Rotation, string Codec, double? ThumbnailTime, string Jpeg);
