# FrameReader

Fast in-process video and audio for .NET through FFmpeg 8's libraries (FFmpeg.AutoGen): frames at a size and pixel
format with the bytes ffmpeg's filters give, exact seeking, audio, measures, writing, remuxing and probing. Exactness
against ffmpeg's command line is the promise: every test compares with ffmpeg's output, byte for byte where the
format allows. Repo: `github.com/bisforboman/FrameReader`. NuGet: `FrameReader` (MIT) and `FrameReader.Native.<rid>`
plus the `FrameReader.Native` meta-package (our FFmpeg build, LGPL).

It was part of [ShotDetector](https://github.com/bisforboman/shotdetector) until 2026-11-07
(docs/decisions/2026-11-07-framereader-own-repository.md); its history came with it. ShotDetector depends on the
package, through its public API only, so a change here reaches ShotDetector's scenedetect comparison once it is
released, and a breaking change is a major version.

## Where things are

| Topic | Where |
|---|---|
| Releases and what each brought | src/FrameReader/CHANGELOG.md |
| The types and what they handle | src/FrameReader/README.md (the package's front page), docs/frame-reader-library.md |
| The FFmpeg interop gotchas | src/FrameReader/CLAUDE.md |
| Our FFmpeg build | tools/native/build-ffmpeg.sh, .github/workflows/native.yml, src/FrameReader.Native/README.md |
| The licences of what that build links in | THIRD-PARTY-NOTICES.md |
| What's next | docs/backlog.md |
| What the owner decided and why | docs/decisions/ (its README; the decisions before the move are in ShotDetector's) |

## Release policy

- The owner says when to release.
- A release PR (`python tools/changelog.py X.Y.Z "summary"` folds the fragments) opens once every PR going into it has
  merged, never stacked. Release notes are the version's CHANGELOG section: keep sections grouped (breaking changes,
  new, faster, fixed). A change to the native build is a release too.
- Merging it releases: release.yml (every push to main) releases the newest version if it isn't tagged (`vX.Y.Z`; the
  releases made from ShotDetector's repository, `framereader-vX.Y.Z` there, count as released here:
  .github/imported-tags.txt, which also says how to push them as tags). A hand-pushed `v*` tag still works. Pass `-p:FrameReaderVersion`.
- Prereleases (`-`) publish without approval (environment `prerelease`); stable ones wait for the owner's approval
  (environment `release`).
- Once a stable release is on nuget.org, set `FrameReaderApiBaselineVersion` in Directory.Build.props (release.yml
  opens a reminder issue): packing then fails on a breaking change.
- Each release lets ShotDetector move: Dependabot opens the bump there, and its checks (the scenedetect comparison
  included) run on it.

## Working on this repository

- **PR pipeline**: `main` is protected; work on a branch, push, open a PR, and turn auto-merge on at once (drafts
  excepted, and a PR the owner must decide on says so in its body and waits for the answer). Don't push to main.
- **Required checks**: `build` and `CI passed` (ci.yml). `CI passed` is a gate that needs every other job in ci.yml,
  passed or skipped; a new job goes in its `needs`.
- **Files an open PR changes**: before editing a file, check the open PRs; if one changes it, wait for it to merge and
  branch from main after it.
- **Worktrees**: `git worktree add .claude/worktrees/<name> -b <branch> origin/main` (ignored). Sessions working at
  the same time each use their own worktree.
- **Changelog**: a PR with a change users of the packages notice adds `changes/framereader/<branch>.md`
  (changes/README.md), never the CHANGELOG itself.
- **Backlog**: docs/backlog.md (the `backlog` skill previews and extends it); set a row's status when work starts; when it is Done (Dropped, Won't do) move it to the
  end of docs/backlog-done.md. Add items when something turns up, with why.
- **Asking the owner**: whenever you need input (a choice, an approval, whether to go on), ask with the
  multiple-choice prompt (AskUserQuestion), not at the end of a reply. Record a design decision's question, choices
  and answer as `docs/decisions/YYYY-MM-DD-what.md`.
- **Where work runs**: long work runs as cloud sessions; the owner's computer must stay free. Work that needs it is
  asked for, never started unasked.
- **Docs first**: every push reruns everything, so docs go in before the PR opens, not as follow-up pushes (a
  docs-only PR skips the heavy steps).
- **No benchmark numbers in docs**: they go stale; link frame-reader-bench.yml's runs.
- **Code style**: StyleBro (rules in `.editorconfig`). The pre-commit hook formats the staged .cs files (`git config
  core.hooksPath .githooks` once per clone). By hand: `dotnet tool run bro format FrameReader.slnx`; CI's `format`
  job runs it with `--verify-no-changes` on windows-2025. The tool (.config/dotnet-tools.json) and the analyzers
  (Directory.Packages.props) move together, by hand.
- **Security analyzers**: the SDK's Security rules (CA2xxx, CA3xxx, CA5xxx) are errors on every build
  (Directory.Build.props, docs/decisions/2026-11-06-security-analyzers.md). Fix a real finding (the owner decides
  how); suppress a false positive where it is, with a justification.
- **Dependabot**: patch and minor PRs auto-merge; majors wait for a look. FFmpeg.AutoGen stays on 8.x until the
  native build moves to FFmpeg 9.
- **Commit messages**: what and why, like the history. Small commits.

## Before pushing

- Cloud sessions: `.claude/hooks/session-start.sh` installs the .NET 10 SDK if missing, restores the tool and the
  solution, sets `core.hooksPath`, and puts BtbN's pinned FFmpeg 8.1 gpl-shared (in `~/.cache/framereader`) on PATH
  and in `FRAMEREADER_FFMPEG_LIBS`.
- `dotnet test FrameReader.slnx` (~5 min). One class: `--filter "FullyQualifiedName~SeekTests"`.
- `FRAMEREADER_FFMPEG_LIBS`: a folder of FFmpeg 8.1's shared libraries;
  without it the in-process tests pass without checking. Our build: a ci.yml or native.yml run's `native-linux-x64`
  artifact, or BtbN's `gpl-shared` 8.1 archive from the release pinned in `.github/actions/btbn-ffmpeg/action.yml`.
- The VideoWriter tests against x264 need `DOTNET_TieredCompilation=0` on AVX-512 CPUs (src/FrameReader/CLAUDE.md).

## Layout

- `src/FrameReader`: the library. `src/FrameReader.Native`: the native packages (`dotnet pack -p:Rid=<rid>` from
  `native/<rid>/`).
- `tests/FrameReader.Tests`: xunit. `tests/AudioWithoutFfmpeg`: a process of its own that fails if the audio measures
  on samples load anything of FFmpeg. `tests/TapeSplitter.Tests`: examples/TapeSplitter against ffmpeg's filters.
- `examples/`: `ThumbnailApi` (ASP.NET Core) and `TapeSplitter` (console), on FrameReader's source; in the solution, so CI
  builds, formats and tests them.
- `benchmarks/FrameReader.Benchmarks`: against FFMpegCore and the ffmpeg executable (frame-reader-bench.yml).
- `tools/`: `native/build-ffmpeg.sh`, `changelog.py`, `media.py` (pinned real-world media), `nightly-issue.sh`,
  `vulnerable-packages.py`, `true_peak_reference.py` (LoudnessMeterTests' reference true peaks).
- `.github/workflows`: `ci.yml`, `release.yml`, `native.yml` (our FFmpeg build, which the others call),
  `realworld-audio.yml` (nightly), `frame-reader-bench.yml` (weekly), `upstream.yml` (weekly: an issue per newer
  FFmpeg, linked library, BtbN build or StyleBro), `codeql.yml`, `dependabot-auto-merge.yml`,
  `vulnerable-packages.yml` (weekly). Each header says the rest.

## Gotchas

- Line endings are per file (most CRLF, some LF; no .gitattributes): keep each file's own. Write Python edit scripts
  with the Write tool, not bash heredocs. CI's `line-endings` job fails a PR in which more than 20 of a file's lines
  changed only their ending; label the PR `line-endings` for a conversion on purpose.
- An edit to tools/native/build-ffmpeg.sh or native.yml changes the native cache key: every platform's FFmpeg is
  rebuilt (long). Batch such changes.
- Workflows: pin external actions by commit SHA with the tag in a comment; tokens default to `contents: read`, so give
  a job that needs more its own `permissions`. BtbN's FFmpeg only through `.github/actions/btbn-ffmpeg`.
- Runners are pinned to an image version (ubuntu-26.04, windows-2025, macos-15); native.yml's glibc and Windows builds
  stay on ubuntu-22.04 (glibc baseline), musl in Alpine, osx-arm64 on macos-15.
- Failed tests are check-run annotations, readable where job logs aren't (GitHubActions logger).
- NuGet trusted publishing: the policy's workflow must be `release.yml` in this repository, package glob
  `FrameReader*`.
