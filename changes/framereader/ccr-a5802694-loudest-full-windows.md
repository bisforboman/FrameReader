### Fixed

- `LoudnessMeter`: `MaxMomentary` and `MaxShortTerm` (and their times) come only from blocks whose 400 ms or 3 s
  window is full. Before, a block before the window filled counted at ebur128's starting 1e-12 (-120.691 LUFS), so
  samples shorter than the window gave -120.691 LUFS, and digital silence's time was the first block's. Now samples
  that never fill the window give negative infinity and no time, as with no block at all.
