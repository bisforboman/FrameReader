### Faster

- `AudioAnalysis.Run`: with loudness and any other analysis, the loudness graph (ebur128 with its true peak, the
  heaviest stage) runs on a thread of its own while the calling thread converts and runs the others, so the pass takes
  about as long as the slower of the two instead of both together. Every result is the same as before, to the bit.
