# ThumbnailApi: an upload in, a thumbnail and its duration out

An ASP.NET Core minimal API around FrameReader ([Program.cs](Program.cs)). `POST /thumbnail` takes a video as the
request body and answers with its duration, size, rotation and codec, and a JPEG thumbnail a tenth of the way in. It
all happens in memory and in-process: no temporary file, no ffmpeg process. The JPEG is the file
`ffmpeg -ss T -i upload -vf scale='min(640,iw)':-2 -frames:v 1 -q:v 3 out.jpg` writes, byte for byte, upright for phone
video.

```bash
FrameReader__Libraries=/path/to/ffmpeg-8-shared/lib dotnet run --project examples/ThumbnailApi
```

```bash
curl --data-binary @phone.mp4 -H "Content-Type: video/mp4" http://localhost:5000/thumbnail
```

```json
{"duration":20,"width":1280,"height":720,"rotation":90,"codec":"h264","thumbnailTime":2,"jpeg":"data:image/jpeg;base64,/9j/4AAQ..."}
```

In this repository the project references FrameReader's source, and `FrameReader__Libraries` (the configuration's
`FrameReader:Libraries`) names a folder of FFmpeg 8's shared libraries. In your own app, reference the packages:

```bash
dotnet add package FrameReader
dotnet add package FrameReader.Native.linux-x64   # or FrameReader.Native: every platform
```

Then the libraries are found next to the app and `FrameReader:Libraries` can stay unset.

## How it reads the upload

- The body is copied into a `MemoryStream` (up to 512 MB). A stream that can seek is read as ffmpeg reads a file, so
  every MP4 opens, including one with its moov at the end, as phones and cameras write them. The same stream is probed
  (`MediaProbe.Probe`) and then read (`VideoFrameReader`), rewound in between.
- The body is the video, not a multipart form: ASP.NET Core buffers a form's large files to a temporary file.
- Formats with their headers first (mkv, webm, ts, a faststart mp4) could be read straight from `request.Body`, without
  the copy, as ffmpeg reads a pipe; but a body can be read only once, so the probe and the reader can't both have it.
- The upload is untrusted: `format_whitelist` allows only mov/mp4, matroska/webm and MPEG-TS, and `protocol_whitelist`
  only the stream itself, so a playlist (HLS, concat) can't make FFmpeg open URLs or local files. Frames larger than
  `FrameDecoderOptions.MaxPixels` (8192 x 8192) are refused. Anything FFmpeg can't read is a 422 with FFmpeg's reason.
- Decoding runs FFmpeg's code in the server's process. Keep the libraries current: FrameReader.Native follows FFmpeg's
  point releases.

From FFMpegCore? [docs/coming-from-ffmpegcore.md](../../docs/coming-from-ffmpegcore.md) puts the two side by side.
