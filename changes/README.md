# Changelog fragments

What's changed since the last release, one file per pull request in `changes/framereader/`, so parallel PRs never
edit the same lines of src/FrameReader/CHANGELOG.md (FrameReader and FrameReader.Native). Name it after the branch
(`changes/framereader/fix-videowriter-filters.md`); write it as the release notes' reader will read it.

```markdown
### Fixed

- `VideoWriter`: every writer failed with "Filter not found" (issue #127).
```

Sections, in the release notes' order: `### Breaking changes`, `### New`, `### Faster`, `### Fixed`. A fragment can
have several. Changes nobody using the packages would notice (CI, tests, docs) need none.

A release PR folds the fragments into the CHANGELOG and deletes them:

```bash
python tools/changelog.py 2.26.0 "One line on what the version is."
```
