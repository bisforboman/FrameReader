# Backlog: done

Rows moved from [backlog.md](backlog.md) when Done, Dropped or Won't do, oldest first. Those from before 2026-11-07
are in [ShotDetector's backlog-done.md](https://github.com/bisforboman/shotdetector/blob/main/docs/backlog-done.md).

- Benchmarks for FrameReader's own paths: rotated, scaled and Yuv420p reads (ReadPaths), a picture a second (Sampling), the audio analyses (AudioAnalyses) and the managed measures (PictureMeasures, AudioMeasures), in frame-reader-bench.yml's weekly run
  - Why: the benchmark timed only reading frames against ffmpeg's pipe, so the performance work of 2026-11-07 couldn't be measured (docs/decisions/2026-11-07-performance-work.md)
  - Status: Done (2026-11-07)

- Vectorise FrameDecoder's rotation: `Orient`'s hflip and transpose with Vector128 shuffles and blocks, and a buffer pool for its output
  - Why: a display rotation of 90 or 270 degrees (phone video) moves one sample at a time; at 1080p that costs more than decoding the frame, and the rotated read of ReadPaths takes about twice the plain one (docs/decisions/2026-11-07-performance-work.md)
  - Status: Done (2026-11-07)

- Faster measures: GamutMeter's table, FreezeDetector's SAD, TruePeakMeter's interpolation
  - Why: GamutMeter rebuilds its table for every meter (64 MB and most of a second at 12 bits); FreezeDetector's sum of differences widens bytes three times and is scalar above 8 bits (much slower at 10 bits than at 8); TruePeakMeter interpolates one point at a time. Integer sums, a cache, and vectors that keep each point's order: the same results (docs/decisions/2026-11-07-performance-work.md)
  - Status: Done (2026-11-07)

- AudioAnalysis: the loudness graph on its own thread, fed references of the decoded frames, while the caller's thread converts for the other analyses
  - Why: ebur128 with true peaks (192 kHz inside the filter) and the conversion for tone, waveform and silences run one after the other on one thread; up to twice as fast when loudness and another analysis are asked for (docs/decisions/2026-11-07-performance-work.md)
  - Status: Done (2026-11-07): the pass takes about as long as loudness alone (docs/decisions/2026-11-07-audio-analysis-loudness-thread.md)

- The first release from this repository: 2.26.0 (the owner, 2026-11-09: stable, without the planned 2.26.0-pre)
  - Why: checks the nuget.org trusted publishing policy for this repository, the environments and the native packages before a stable release depends on them (ShotDetector's docs/framereader-split.md, step 5)
  - Status: Released (2026-11-09). The first run stopped at the NuGet login (no `NUGET_USER` variable, and `release` didn't wait for approval); the owner set the variable, the policy and a reviewer, and the re-run published every package

- The owner sets up the repository: branch protection on main (required checks `build` and `CI passed`), auto-merge, the environments `release` (with approval) and `prerelease`, the `NUGET_USER` variable, the nuget.org trusted publishing policy (workflow `release.yml`, package glob `FrameReader*`), and the transfer of ShotDetector's open `framereader` issues
  - Why: a session can't change repository settings, nuget.org or another repository's issues
  - Status: Done (2026-11-12, the owner)

- Read-ahead: convert ahead too, for TryRead
  - Why: `VideoFrameReaderOptions.ReadAhead` (#568, ShotDetector issue #566) decodes, deinterlaces and rotates frames ahead on a background thread but converts each frame in the read, because `TryReadForwardTo` only knows which frame it wants when asked. Plain `TryRead` wants every frame, so its conversion could run ahead too. The same swscale context on another thread, so the same bytes, unlike swscale's slice threads (Maybe, below); at whole size the conversion is most of what's left on the caller's thread. Moved from Maybe after the 2026-11-07 review of what's next
  - Status: Done (2026-11-12): while `TryRead` reads frame after frame, the decoding thread converts with a FrameConverter of its own; the other reads convert in the read

- The tape splitter example's first slice: examples/TapeSplitter reports a digitised tape's blank stretches (black, a blue "no signal" screen, a frozen picture, silence) and proposed cut points as JSON, with tests against blackdetect, freezedetect and silencedetect on tapes made with lavfi (issue #23)
  - Why: people and digitisation services capture a whole VHS, DV or DVD tape as one file, its ends and the gaps between recordings blank; FrameReader's detectors find all of these in one decode. A report only: it writes nothing (docs/decisions/2026-11-09-tape-splitter-example.md)
  - Status: Done (2026-11-13): PR #24, merged by the owner's word; `--write` came with the trim (below)

- Trimming with stream copy: `Remux.Copy` with a start and an end, as ffmpeg's `-ss S -to E -i in -c copy` (keyframe-aligned start as there), checked against ffmpeg's output packet for packet
  - Why: the one FFMpegCore job (`FFMpeg.SubVideo`) FrameReader lacks; docs/coming-from-ffmpegcore.md says "not yet" (#11). The tape splitter (issue #23) needs it to write each recording as a file of its own: exact for intra-only captures (DV, MJPEG, FFV1 with -g 1), keyframe-aligned for MPEG-2 and H.264
  - Status: Done (2026-11-13): `RemuxOptions.Start` and `End`, byte for byte against `ffmpeg -ss S -to E -i in -c copy` on H.264 (mp4, mkv), MPEG-2 in MPEG-TS (its start time not 0), DV and FFV1; `MediaInfo.StartTime` for the time they count from; examples/TapeSplitter writes each recording with them (`--write`)

- Rename the test environment variables from `SHOTDETECTOR_*` to `FRAMEREADER_*` (FFMPEG_LIBS, REQUIRE_X264, REQUIRE_SAME_BUILD, KEEP_DIFFERING, WIDEVINE_PSSH*, REALWORLD_AUDIO)
  - Why: names from ShotDetector's repository; kept for the move so nothing else changed. Rename in the tests, the workflows, the session-start hook and CLAUDE.md in one PR
  - Status: Done (2026-11-14)

- At the next change to tools/native/build-ffmpeg.sh: its SBOM's documentNamespace to this repository's URL
  - Why: it still names github.com/bisforboman/shotdetector; any edit to the script rebuilds every platform's FFmpeg (the cache key), so it waits for a change that needs a rebuild anyway
  - Status: Done (2026-11-12), with the move to zlib 1.4 (#22)

- The raw `dv` muxer in the native build, so `Remux.Copy` writes .dv files with FrameReader.Native
  - Why: the build enables only the muxers FrameReader uses (tools/native/build-ffmpeg.sh), and `dv` isn't one, so the tape splitter writes a .dv capture's recordings as AVI or Matroska (`--as`; found by PR #26's arm job). An edit to the script rebuilds every platform's FFmpeg, so it waits for a change that needs a rebuild anyway
  - Status: Done (2026-11-15), with the move to zlib 1.4 (#22), the owner's choice (docs/decisions/2026-11-15-dv-muxer-with-zlib-rebuild.md): `--enable-muxer=dv`, `Remux.Copy` to .dv byte for byte against `ffmpeg -c copy` (trimmed, whole, and from AVI). The splitter's docs and test follow in a row of their own (docs/backlog.md)

- The tape splitter: cut points for recordings that start with no gap (a hard cut), and the rules from spans to cut points (how long a blank counts, whether picture and sound must agree, the thresholds)
  - Why: the first slice cuts only at blank stretches the picture shows, silence reported beside them. A recording that starts straight after another needs a cut detector (ShotDetector's territory) or DV's recording date where the stream has it (dvrescue's dvpackager splits DV that way) (issue #23)
  - Status: Done (2026-11-15), the rules only (docs/decisions/2026-11-15-tape-splitter-cut-rules.md): spans from 0.5 s merged into gaps from 2 s, `--sound agree` (the picture alone by default), glitches under 1 s taken into the gap, `--kinds` and the filters' thresholds as options, each compared with blackdetect, freezedetect and silencedetect given the same options. No cuts at DV's recording date: FFmpeg 8.1 doesn't read it, so nothing of ffmpeg's to check it against; left out by the owner, as is a cut detector (ShotDetector's)

- The ShotDetector parity check on pull requests: pack the PR's FrameReader to a local feed, build ShotDetector's main against it, run its tests and its scenedetect comparison
  - Why: since the move a FrameReader change reaches ShotDetector's comparison only once released; this catches a change that moves ShotDetector's results before it merges (ShotDetector's docs/framereader-split.md, step 4). Needs ShotDetector to take FrameReader as a package with an overridable version (its step 6) first
  - Status: Done (2026-11-22): ci.yml's `shotdetector` job packs the PR's FrameReader as 0.0.0-pr.<n>, builds ShotDetector's main against it with the PR's FFmpeg build, runs its tests and compare.py (scenedetect's current release; core in-process and through the executable, FastYuv in-process). Required when the PR changes decoding (the `changes` job's list), informational otherwise (`CI passed` ignores its result)

- A weekly check for CVEs against what FrameReader.Native links in: the versions and CPEs in the native build's sbom.spdx.json (FFmpeg, zlib, dav1d, LAME, libwebp; OpenSSL on Linux) looked up in the NVD (or OSV), an issue per new CVE that affects the pinned version (tools/nightly-issue.sh), in upstream.yml or a workflow of its own
  - Why: NuGet's audit, vulnerable-packages.yml and Dependabot only see NuGet advisories, so they cover the FFmpeg.AutoGen bindings but not the native libraries, where a media library's CVEs land; upstream.yml notices a newer release, not whether the version we ship has a known CVE (a fix backported to an older point release, or a CVE with no release yet, goes unseen)
  - Status: Done (2026-11-27): native-cves.yml with tools/native_cves.py, the versions and CPEs from build-ffmpeg.sh's `pkg` lines (the SBOM's source), every NVD CVE for each product matched locally against its version ranges, an issue per new one (open or closed counts as filed), at most 10 a run; a dry run on PRs that change it (docs/decisions/2026-11-27-security-policy.md)
