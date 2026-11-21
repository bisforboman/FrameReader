---
name: ideas
description: Survey what FrameReader does and lacks, and propose directions that would improve it or make it more interesting, each checked against the code, for the owner to pick from; the picked ones become backlog rows (the `backlog` skill) or issues. Use when the user asks what could be improved, what to build next beyond the backlog, what's missing, or for ideas, e.g. "do you see any improvements or further development we can do to make FrameReader more interesting?"
---

# Ideas: survey, propose, let the owner pick

The `backlog` skill previews docs/backlog.md and suggests follow-ups from recent work. This one looks wider: at what
the library is, who uses it and what they can't do yet. It ends where `backlog` begins: the picked ideas are written
by that skill's rules. It first ran on 2026-11-21 (nine rows, the commit "Backlog: nine rows the owner chose").

## 1. Survey (read-only, in parallel)

- What it does: `src/FrameReader/README.md` (the package's front page: every type, and the lists of what the native
  build carries), the newest sections of `src/FrameReader/CHANGELOG.md`, `docs/frame-reader-library.md`.
- What is already wanted or settled: every row of `docs/backlog.md` (Next, Waiting, Maybe), the end of
  `docs/backlog-done.md` (Dropped and Won't do rows are answers: don't propose them again without something new),
  `docs/decisions/` by title, and the open issues and PRs.
- What the packages really contain: `tools/native/build-ffmpeg.sh` (`--enable-filter`, `--enable-muxer`,
  `--enable-encoder`, the linked libraries) and `.github/workflows/native.yml` (the platforms).
- Who it is compared with: `docs/coming-from-ffmpegcore.md`, `docs/encoding-package.md`.

## 2. Look for ideas in four places

- **Feature gaps**: what a user can't do yet. Asymmetries are the best source: audio has it and video doesn't (filter
  graphs), the build writes it but nothing reads it (subtitle muxers), one of a pair is there (DASH without HLS), a
  type needs libraries the packages don't carry (`VideoWriter` and x264).
- **New audiences**: uses the library is one step from serving (ML input, QC, packaging, captions) and what each lacks.
- **Reach and adoption**: platforms without a native package, the docs' shape, examples, the first thing a new user
  sees go wrong.
- **Internal quality**: performance, tests, CI, code health. Most of this is in the backlog already (the Maybe section,
  docs/decisions/2026-11-07-performance-work.md): propose only what isn't a row, and say when a row deserves to move
  up instead.

## 3. Check every idea before proposing it

An idea reaches the owner only after its claims hold against the code, so they pick from ideas that are true:

- Search `docs/backlog.md` and `docs/backlog-done.md` for it. Already a row: don't propose it; if it merits it,
  propose moving or extending the row, and say that is what it is.
- Grep the claim: "the build has no X" in build-ffmpeg.sh, "nothing reads Y" in `src/FrameReader/*.cs`, "no package
  for Z" in native.yml. What isn't checked is said to be unchecked ("probably", "not checked"), as the rows do.
- How it meets the promise: name the ffmpeg command line it would be checked against, byte for byte or figure for
  figure. An idea with nothing of ffmpeg's to compare with (FrameReader's own figure) is the owner's to approve: say so.
- What it costs: an edit to build-ffmpeg.sh or native.yml rebuilds every platform's FFmpeg, so such ideas share one
  rebuild; a new linked library needs its licence in THIRD-PARTY-NOTICES.md and the SBOM; a licence or patent question
  is the owner's decision, to be asked before the work starts and recorded in docs/decisions/.
- Drop what can't pay off, and what the owner has already declined.

## 4. Propose

One reply, short: the ideas grouped under the four headings (leave out an empty one), numbered, each with one or two
sentences: what it is, why it matters, what it costs. Mark which are already backlog rows. End with the three you
would pick first and why. No tables, no benchmark numbers.

## 5. Ask which to keep

With the multiple-choice prompt (AskUserQuestion), not at the end of the reply:

- one multiSelect question per group (a question holds at most four options: split a larger group), each option's
  description saying what the row would be and its cost;
- one question for where they go: backlog rows (recommended), GitHub issues, or both. Ask every time.

If the user asked only for ideas, the reply of step 4 is the answer: ask whether to pick from them, don't assume.

## 6. Write what was picked

- Backlog rows: by the `backlog` skill's step 4 (the row's shape, the file's line endings, `git diff --stat`, a
  docs-only PR from a branch off `origin/main`). Each row's Why ends with "(the owner chose it, YYYY-MM-DD)". Place
  them in Next in the order you ranked them, a row that builds on another right after it (and "after ..." in its
  status); say in the reply that the order is yours to change. A row whose design is the owner's to decide says so in
  its status ("Open (the encoder to be decided)"). A Maybe row promoted moves, it isn't copied.
- Issues: one per idea, the row's what and why as its body, signed as the session's comments are.
- If GitHub isn't reachable from the session, push the branch and say plainly that the PR (or the issues) is still to
  open; don't report it as done.

Then tell the owner what changed: where each row went, which decisions are theirs before work starts, and what you
left out and why.
