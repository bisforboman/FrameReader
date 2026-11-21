# Backlog

What FrameReader does next, in priority order. Three sections: **Next** (open work, the first row first),
**Waiting** (rows that wait on the owner or an outside event) and **Maybe** (ideas not committed to). Update a status
when work starts or lands; add an item when a gap turns up, with why it matters. A row moves to
[backlog-done.md](backlog-done.md) when it is Done, Dropped or Won't do. Design questions go to
[decisions/](decisions/).

Status: **Open**, **In progress**, **Maybe**; finished rows (in backlog-done.md): **Released** (on nuget.org),
**Done** (on `main`, in the next release), **Dropped**, **Won't do**.

## Next

- The ShotDetector parity check on pull requests: pack the PR's FrameReader to a local feed, build ShotDetector's main against it, run its tests and its scenedetect comparison
  - Why: since the move a FrameReader change reaches ShotDetector's comparison only once released; this catches a change that moves ShotDetector's results before it merges (ShotDetector's docs/framereader-split.md, step 4). Needs ShotDetector to take FrameReader as a package with an overridable version (its step 6) first
  - Status: Open

- The mutation entries for FrameReader's quirks: Invoke-Mutations.ps1 and the entries of ShotDetector's tools/mutation/mutations.psd1 whose Project is tests/FrameReader.Tests, with a workflow
  - Why: each reproduced ffmpeg quirk keeps a test that fails when its guard is broken; in ShotDetector's repository those entries no longer find their files
  - Status: Open

- Windows and macOS: the tests on CI's Windows and macOS runners, informational, as ShotDetector's parity-os.yml ran them
  - Why: only Linux (x64, ARM, musl) runs the tests here; the Windows struct-layout gotcha (src/FrameReader/CLAUDE.md) shows platform-only bugs exist
  - Status: Open

- The approximate speed options in the README: `CodecOptions` `skip_loop_filter` = `all` (ffmpeg's `-skip_loop_filter all` before `-i`) next to `FrameDecoderOptions`' keyframes only, with a test that the frames equal ffmpeg's with the same option
  - Why: `CodecOptions` already passes any decoder option, so analysis that can take a slightly different picture (motion, cuts, thumbnails) can decode H.264 and HEVC faster today, but nothing says so; a test keeps it the same as the command line, as the rest is
  - Status: Open

- Wider output formats: `Gray16`, `Rgb48` and planar 32-bit float RGB (ffmpeg's `format=gray16le`, `rgb48le`, `gbrpf32le`) as `FrameFormat`s, checked byte for byte against `-f rawvideo`
  - Why: every `FrameFormat` is 8-bit, so a 10-bit ProRes or HDR source loses its low bits before the caller sees it, and ML input (the library's design doc names it) converts each 8-bit frame to float again in managed code
  - Status: Open

- ML input: a read that fills a caller's float tensor (planar RGB, NCHW, with a mean and scale per channel) on top of the planar float format above, and an example that feeds frames to ONNX Runtime and `AudioReader`'s 16 kHz mono to a speech model
  - Why: a model wants normalised planar floats, so every caller writes the same loop over a `Bgr24` frame; the planar float row gives the samples, this gives the shape models take and shows it working. The normalisation is FrameReader's own arithmetic, so it is checked against the float format's bytes, which are ffmpeg's (the owner chose it, 2026-11-21)
  - Status: Open (after the wider output formats)

- A read-once Stream of a moov-last MP4: fail with `FrameReaderException` (`InvalidInput`) where ffmpeg fails, instead of giving no frames
  - Why: since #11 a seekable Stream opens it, but one that can't seek (a network or request stream) still ends without a frame and without an error once the mdat is past the 64 KB buffer, where `ffmpeg -i -` fails (`partial file`, then no stream to read); a silent empty read looks like an empty video
  - Status: Open

- `FrameDecoder.CanReadAgain` for seekable Streams
  - Why: it's false for every Stream (`_io == null`), so `VideoFrameReader`'s look-ahead (`TryReadForwardTo`, `ReadAt`) skips ahead less on a FileStream or MemoryStream than on the same file's path, though since #11 such a stream seeks as the file does; needs the look-ahead's tests run on a seekable Stream too
  - Status: Open

- Video filter graphs on the reader: a `Filter` option on `VideoFrameReaderOptions` (any `-vf` chain before the scale and format), the frames `ffmpeg -vf GRAPH,scale=W:H:flags=bicubic,format=F -f rawvideo` gives, byte for byte, with crop, pad, overlay, thumbnail and tile in the native build
  - Why: `AudioFilter` runs any `-af` graph, but video gets only `fps`, `yadif` and `scale`: a crop, a letterbox or a contact sheet (`tile`) means managed code of the caller's own, or the ffmpeg executable. `ImageFilterOptions.Filter` already runs a chain for one image, so the graph code is there. The filters are an edit to tools/native/build-ffmpeg.sh, which rebuilds every platform's FFmpeg: one rebuild with the HLS, HDR, encoder and platform rows below (the owner chose it, 2026-11-21)
  - Status: Open

- Subtitles: a `SubtitleReader` that reads a subtitle stream's cues (start, end, text, and the ASS line as FFmpeg's decoder gives it), and a conversion in one call, the file `ffmpeg -i IN -map 0:s:N OUT.vtt` (or .srt, .ass) writes
  - Why: the native build has the srt, webvtt and ass muxers and `MediaProbe` lists subtitle streams, but nothing reads one: `Remux.Copy` can only copy it where the container takes its codec. Captions for a player (WebVTT from an mkv's SubRip or ASS, or an mp4's mov_text) are a common job beside thumbnails and DASH (the owner chose it, 2026-11-21)
  - Status: Open

- HDR to SDR: zimg in the native build, with the `zscale` and `tonemap` filters, and a `VideoFrameReaderOptions` option that tone-maps as `-vf zscale=t=linear,tonemap=...,zscale=t=bt709` does; first a test of what an HDR source gives today
  - Why: HDR (PQ, HLG) phone and streaming video converted to Bgr24 probably reads washed out today (not checked): swscale doesn't tone-map, and the build has only `scale` among the video filters, so a thumbnail of a phone's video is the first thing a new user sees wrong. zimg is a new linked library (its licence in THIRD-PARTY-NOTICES.md), and any edit to tools/native/build-ffmpeg.sh rebuilds every platform's FFmpeg: one rebuild with the filter, HLS, encoder and platform rows. Was a Maybe; the owner moved it here, 2026-11-21
  - Status: Open

- Quality metrics: PSNR and SSIM between two runs of frames, without FFmpeg, on planes from any decoder as `PictureMeter` takes them, each frame's and the run's figures those of ffmpeg's `psnr` and `ssim` filters; VMAF after them, if at all
  - Why: checking an encode against its source is the QC job the meters don't cover yet, and today it needs the ffmpeg executable and its log parsed. Both filters are integer sums and a little floating point, so the same figures are reachable as with signalstats and idet. VMAF needs libvmaf and its models (a new linked library, or a port): a question for the owner once the two are in (the owner chose it, 2026-11-21)
  - Status: Open

- HLS output: `Remux.Copy` to an .m3u8 with the `hls` muxer (fMP4 or MPEG-TS segments and the playlist), the files `ffmpeg -c copy -f hls` writes with the same options, with `Encryption` where the muxer has it
  - Why: `Remux.Copy` writes MPEG-DASH with CENC, but Apple's players take only HLS, so a service packaging for the web needs another tool for half its output. The native build has the hls protocol and demuxer but not the muxer: an edit to tools/native/build-ffmpeg.sh, one rebuild with the filter, HDR, encoder and platform rows (the owner chose it, 2026-11-21)
  - Status: Open

- A video encoder in FrameReader.Native that needs no GPL build: SVT-AV1 or libvpx (BSD), or OpenH264 (Cisco's binary; docs/encoding-package.md has its size and quality), with `VideoWriter` and `Video.Transcode` taking it, the file ffmpeg writes with the same encoder
  - Why: `VideoWriter` and `Video.Transcode` need libx264 from a GPL build the user brings, so with the packages alone writing video gives only `mpeg4`; it is the largest gap against FFMpegCore, which drives a full ffmpeg. Which encoder is the owner's choice (licence, patents, size, speed: ask before starting, and record it in docs/decisions/); a new linked library in THIRD-PARTY-NOTICES.md and the SBOM, and one rebuild with the filter, HLS, HDR and platform rows (the owner chose the row, 2026-11-21)
  - Status: Open (the encoder to be decided)

- More platforms: FrameReader.Native.win-arm64 and FrameReader.Native.osx-x64
  - Why: native.yml builds win-x64, linux-x64, linux-arm64, linux-musl-x64 and osx-arm64, so on a Windows ARM laptop or an Intel Mac the package finds no libraries and the user builds FFmpeg themselves. Two more jobs in native.yml (win-arm64 cross-compiled with llvm-mingw, osx-x64 on a macOS runner with `-arch x86_64`), the meta-package and release.yml; an edit to native.yml rebuilds every platform, so with the rows above. Tests there depend on the Windows and macOS row (the owner chose it, 2026-11-21)
  - Status: Open

- A cookbook: the README split into task pages under docs/ (thumbnails and contact sheets, QC measures, packaging for DASH, audio analysis, writing), the README kept as a short front page that links them
  - Why: src/FrameReader/README.md is nuget.org's front page and one page of some 750 lines, in which a reader looking for one job reads past every other; pages by task are found by search and can be linked from an answer. Its measured figures go at the same time (no benchmark numbers in docs: link frame-reader-bench.yml's runs) (the owner chose it, 2026-11-21)
  - Status: Open

## Waiting

- Move to FFmpeg 9's libraries (FFmpeg.AutoGen 9, Native packages on 9.0.x) when opencv-python's Linux wheels bundle FFmpeg 9
  - Why: ShotDetector's scenedetect parity needs OpenCV's FFmpeg; moving earlier risks bytes OpenCV doesn't give, and drops system FFmpeg 8 in-process. ShotDetector's backlog has the same row
  - Status: Waiting (2026-10-14: FFmpeg.AutoGen 9.0.1.1 is out; opencv-python still bundles libavcodec 62; the owner: wait for OpenCV)

- The owner files the FFmpeg yadif report (docs/ffmpeg-yadif-16bit-report.md)
  - Why: FFmpeg 8.1's x86 yadif at 9-16 bits races between slice threads, so every run gives another picture; FrameReader runs it on one thread there, which a fix upstream would make unneeded. A public post from the owner's account: theirs to file, never a session's
  - Status: Waiting (on the owner)

- The owner files the x264 AVX-512 report with VideoLAN
  - Why: CI's VideoWriter step needs DOTNET_TieredCompilation=0 because x264's AVX-512 code and .NET's tiered JIT differ about 1 run in 250 (docs/decisions/2026-10-10-videowriter-s-rare-ci-mismatch.md). A public post from the owner's account
  - Status: Waiting (on the owner)

- The tape splitter: .dv recordings from a .dv capture with FrameReader.Native, once a release carries its `dv` muxer
  - Why: the native build has the raw `dv` muxer since the rebuild with zlib 1.4 (#22), but examples/TapeSplitter's README, `TapeScan`'s doc comment and its test still say it has none and send a .dv capture's recordings into AVI (`--as avi`). Say .dv works, and test `--write` of a DV capture as .dv against ffmpeg's files. Left out of #22 because issue #23's session was editing the splitter then
  - Status: Open

## Maybe

- The tape splitter: a measure for analogue noise ("snow"), so a tape's unrecorded stretches count as gaps
  - Why: a VHS tape's unrecorded stretches show noise, not black or blue, and none of the detectors finds it (issue #23). No FFmpeg filter names it, so it would be FrameReader's own figure, outside the promise that every figure is checked against ffmpeg: the owner decides whether to make one (docs/decisions/2026-11-09-tape-splitter-example.md); not approved on 2026-11-15, when issue #23 closed with this row left (docs/decisions/2026-11-15-tape-splitter-cut-rules.md)
  - Status: Maybe

- Segment decoders inside `ReadAhead`: several decoders on different parts of the file
  - Why: ffmpeg caps frame threads at 16, so above that cores sit idle; issue #566's own run with 4 `ReadAt` readers shows the gain. Pays only above ~16 cores
  - Status: Maybe

- release.yml: a run waiting for approval and the pushes after it
  - Why: GitHub keeps one pending run per concurrency group and cancels the older pending one, whatever it would have released (2026-11-06, in ShotDetector's repository). The skip guard makes a lost version fail the next run, but a run that sits in approval for days could leave the queue
  - Status: Maybe

- swscale's slice threads in VideoFrameReader's and BgrConverter's conversion, opt-in or for the format pairs a stress test clears
  - Why: a scaled read (1080p to 1280x720 bicubic) runs swscale on one thread, which then costs more than the 4-thread decode; ReadPaths' scaled read takes about twice the plain one. But scale's slice threads varied ffmpeg's converted frames run to run on CI (1 run in 8, Encoding.cs and ImageEncoder.cs keep one thread), so it needs many runs per format pair on CI's runners first (docs/decisions/2026-11-07-performance-work.md)
  - Status: Maybe

- A buffer pool (`av_buffer_pool_init`) for FrameDecoder's row-deinterlaced and hardware-copied frames
  - Why: when the output isn't writable (a held frame: `ReadAhead`, `TakeFrame`), each frame gets a fresh `av_frame_get_buffer`, a malloc of the whole frame with its page faults (FrameDecoder.cs, the row deinterlacer and `FromHardware`); the rotation row covers `Orient`'s (docs/decisions/2026-11-07-performance-work.md)
  - Status: Maybe

- Packed output without the extra copy: Yuv420p, packed RGB whose rows swscale pads, and same-size Gray8
  - Why: VideoFrameReader hands out `_out`'s buffer only when its stride is the packed width; otherwise it packs every frame with `av_image_copy_to_buffer` into a managed array made once per reader (6 MB, on the large object heap, for 1080x1920 Bgr24). That is every Yuv420p read, and packed RGB of widths whose rows `av_frame_get_buffer` pads: a 1080-wide Bgr24 row is 3240 bytes, so a rotated 1920x1080 video copies each frame (the rotation session found it, 2026-11-07). An output frame with the packed stride where swscale's alignment allows it, or one contiguous buffer for Yuv420p's planes, saves a frame's copy (about 0.3 ms at 1080p); Gray8 from 8-bit limited-range YUV at the same size is the Y plane (docs/decisions/2026-11-07-performance-work.md)
  - Status: Maybe

- ReadAhead's handoff: pulse only when the other side waits, spin briefly before `Monitor.Wait`
  - Why: each frame takes the lock twice and pulses both sides with no waiter, a context switch per frame at depths 1 and 2; microseconds a frame (docs/decisions/2026-11-07-performance-work.md)
  - Status: Maybe

- The picture meters at 9-16 bits and one fused InterlaceDetector pass
  - Why: PictureMeter's `Luma16`, the wide `ChromaOutOfRange`, `RowSum` and `ColumnSums` are scalar (10-bit ProRes and HDR deliveries; many times the 8-bit cost in PictureMeasures); InterlaceDetector reads each row's neighbours several times and runs a `Parallel.For` per plane. Integers, so the same results (docs/decisions/2026-11-07-performance-work.md)
  - Status: Maybe

- LoudnessMeter's K-weighting with its state in locals or channel lanes, and one sample-peak pass
  - Why: the biquad state lives in arrays, and the sample peak is scanned in `Add`, in `TruePeakMeter.Add` and per block; each channel's order kept, no FMA, so the same doubles; maybe 1.5-2x (docs/decisions/2026-11-07-performance-work.md)
  - Status: Maybe

- AudioFilter's output nobody reads: Loudness drains its graph without the `aformat` tail, the copy and the metadata strings
  - Why: `Loudness.Measure` converts ebur128's doubles to floats, copies every frame and turns its metadata into strings, then drops them; ebur128's figures don't depend on what follows it. AudioAnalysis's loudness drains without the copy and the strings since `AudioFilter.Drain` (2026-11-07); both keep the `aformat` tail (docs/decisions/2026-11-07-performance-work.md)
  - Status: Maybe

- The 8-bit picture meters' leftovers: Luma8 and BlackPixels on Vector256, ContentLines from Luma8's row sums, ConstantColourDetector's flat frames histogrammed once, InterlaceDetector holding frame references instead of copies
  - Why: each reads the frame or a part of it again, or at half the width AVX2 gives; integers, so the same results. Small gains each (docs/decisions/2026-11-07-performance-work.md)
  - Status: Maybe

- The small per-frame allocations on the video path: `Take()` without ReadAhead references a taken frame (two references for one), the fps feed frame, `NextRows`' `av_frame_alloc`, `LeftOut`'s closures, `Convert`'s `av_frame_ref` (which copies side data)
  - Why: negligible next to decoding, but each is a malloc or a GC object a frame; trivial fixes, the same frames (docs/decisions/2026-11-07-performance-work.md)
  - Status: Maybe

- Vectorised start-code scans in UnreferencedPackets (Annex B, MPEG-1/2, MPEG-4 Part 2, DivX)
  - Why: they look for `00 00 01` one byte at a time, also on keyframes after the target (`Skips` reads their parameter sets); `IndexOf` on a span would be several times faster. Only decides what to skip, so the same frames; about 1-2% of a read in MPEG-TS and MPEG-1/2/4 (docs/decisions/2026-11-07-performance-work.md)
  - Status: Maybe

- The small audio loops: WaveformBuilder's conversion, mean and min/max on vectors; ReferenceToneFinder's Goertzel state and ChannelMeter's sums in locals or channel lanes; SilenceFinder's search for the next loud sample as a vector search; SpectralStats fed the decoded planar frame when the reader neither resamples nor remixes
  - Why: per-sample scalar code and loads and stores through arrays; each channel's order kept (and NaN, saturation and integer division as the scalar code has them), so the same results. Low gain: tone stops at its search duration, and SpectralStats' FFT dominates (docs/decisions/2026-11-07-performance-work.md)
  - Status: Maybe

- Hardware decoding: several frames in flight (decode on the GPU while the previous frame copies back), and GPU decoders beyond Windows (VAAPI on Linux, VideoToolbox on macOS)
  - Why: on Windows each frame waits for the GPU and its copy, so D3D11VA is slower in wall time than software decoding (FrameDecoderOptions.HardwareDecoding); overlapping them could make it pay where CPU is short. Other platforms need the native build with libva or VideoToolbox (licences, `--disable-autodetect`), and whether frames equal software decoding depends on each GPU's decoder, so it stays opt-in. Not measured (docs/decisions/2026-11-07-performance-work.md)
  - Status: Maybe

- The native build with link-time optimisation (`--enable-lto`, and for zlib, dav1d, LAME and libwebp)
  - Why: may shave a little off FFmpeg's C code (its hot loops are assembly already); not measured. Any edit to tools/native/build-ffmpeg.sh rebuilds every platform's FFmpeg, so it waits for a rebuild that's needed anyway, as the SBOM row does, and needs its own before-and-after run of frame-reader-bench.yml (docs/decisions/2026-11-07-performance-work.md)
  - Status: Maybe

- A performance check on pull requests: a short ReadPaths run timed against `ffmpeg -f null` in the same job, as an annotation when the ratio worsens, not a failing check
  - Why: frame-reader-bench.yml runs weekly, so a slower path shows only after it merged; the ratio to ffmpeg in one job evens out how fast the runner is, though shared runners are noisy, so it informs rather than gates
  - Status: Maybe
