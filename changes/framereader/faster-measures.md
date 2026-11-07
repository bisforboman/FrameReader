### Faster

- `GamutMeter`: the table of runs is built once for each depth, range, limits and matrix and shared by the meters made
  with them (a file split into parallel ranges, a meter per file; the last few kept, up to 128 MB), and built on all
  cores: a 12-bit meter no longer spends most of a second on it each time.
- `FreezeDetector`: scene_sad by psadbw on x86 (pairwise widening adds on ARM) for 8-bit samples, and in vectors for
  16-bit ones (formerly one sample at a time). The same sums.
- `LoudnessMeter`: the true peak's interpolation in vectors across neighbouring points, each point's sum in the same
  order, and the samples scanned once for both peaks. The same figures, NaN and infinite samples included.
