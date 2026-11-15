### New

- `Remux.Copy` trims: `RemuxOptions.Start` and `End` are ffmpeg's input `-ss` and `-to`, the file
  `ffmpeg -ss S -to E -i IN -c copy OUT` writes (FFMpegCore's `SubVideo`). The copy starts at the keyframe at or before
  `Start`: exact where every frame is a keyframe (DV, MJPEG, FFV1 with `-g 1`, PCM), from the keyframe before for H.264,
  HEVC and MPEG-2.
- `MediaInfo.StartTime`: the container's start time (ffprobe's `format=start_time`), which `Start` and `End` count from.
