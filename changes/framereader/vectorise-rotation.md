### Faster

- `FrameDecoder` (and the readers on it): rotated video (a display rotation of 90, 180 or 270 degrees, or a flip, as
  phone video has) turns with vector moves instead of one sample at a time, several times faster, the same bytes. A
  frame held while the next is turned (`TakeFrame`, `ReadAhead`) gets its buffer from a pool rather than a fresh
  allocation.
