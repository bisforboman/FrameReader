using FrameReader;

/// <summary>
/// TruePeakMeter's vectors against its scalar arithmetic (the meter as it was before them, below): the same peak, to the
/// bit, on noise, a crescendo and tones near Nyquist, in chunks of every size, at 44.1, 48 and 192 kHz; and NaN and
/// infinite samples as Math.Max takes them (a NaN anywhere makes the true peak NaN, an infinity infinite), where the
/// sample peak (ebur128's FFMAX) drops a NaN.
/// </summary>
public class TruePeakMeterTests
{
    [Theory]
    [InlineData(48000, 4096)]
    [InlineData(48000, 1)]
    [InlineData(44100, 7)]
    [InlineData(44100, 1000)]
    [InlineData(192000, 333)]
    public void IsTheScalarArithmetic(int rate, int chunk)
    {
        var random = new Random(rate + chunk);
        foreach (var signal in Signals(random, 20011))
            AssertSame(signal, rate, chunk);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void TakesNanAndInfinityAsMathMax(float special)
    {
        var random = new Random(3);
        var signal = Signals(random, 5003).First();

        // At the start, within and at the end of a vector of points, in a stretch the peak so far would skip, and last.
        foreach (int at in new[] { 0, 1, 70, 71, 77, 1000, 2500, 5002 })
        {
            var s = (float[])signal.Clone();
            if (at == 2500)
            {
                for (int i = 2000; i < 3000; i++)
                    s[i] *= 0.01f;
            }

            s[at] = special;
            foreach (int chunk in new[] { 1, 64, 5003 })
            {
                double peak = AssertSame(s, 48000, chunk);
                Assert.Equal(float.IsNaN(special) ? double.NaN : double.PositiveInfinity, peak);
            }
        }

        // Through LoudnessMeter: the sample peak without the NaN (FFMAX), the true peak NaN or infinite.
        var meter = new LoudnessMeter(48000, 1);
        var t = (float[])signal.Clone();
        double samplePeak = t.Max(v => Math.Abs((double)v));
        t[100] = special;
        meter.Add(t);
        var channel = meter.Finish().Channels[0];
        Assert.Equal(float.IsNaN(special) ? 20 * Math.Log10(samplePeak) : double.PositiveInfinity, channel.SamplePeak, 1e-9);
        Assert.Equal(float.IsNaN(special) ? double.NaN : double.PositiveInfinity, channel.TruePeak);
    }

    static IEnumerable<float[]> Signals(Random random, int n)
    {
        yield return [.. Enumerable.Range(0, n).Select(_ => (float)((random.NextDouble() * 2) - 1))];
        yield return [.. Enumerable.Range(0, n).Select(i => (float)(((random.NextDouble() * 2) - 1) * (0.01 + (0.99 * i / n))))];
        yield return [.. Enumerable.Range(0, n).Select(i => (float)Math.Sin((i * Math.PI * 0.97) + 0.3))];
        yield return [.. Enumerable.Range(0, n).Select(i => (float)(0.5 * Math.Sin(i * Math.PI * 0.49)) + (float)(0.5 * Math.Sin(i * Math.PI * 0.51)))];
    }

    // Both meters on the samples in chunks: the same peak (bit for bit, NaN for NaN).
    static double AssertSame(float[] samples, int rate, int chunk)
    {
        var meter = new TruePeakMeter(rate);
        var scalar = new ScalarTruePeakMeter(rate);
        for (int o = 0; o < samples.Length; o += chunk)
        {
            var part = samples.AsSpan(o, Math.Min(chunk, samples.Length - o));
            double largest = 0;
            foreach (float s in part)
                largest = Math.Max(largest, Math.Abs((double)s));
            meter.Add(part, largest);
            scalar.Add(part);
            Assert.Equal(scalar.Peak, meter.Peak);
        }

        meter.Finish();
        scalar.Finish();
        Assert.Equal(scalar.Peak, meter.Peak);
        return meter.Peak;
    }

    // TruePeakMeter before its vectors, as it was.
    sealed class ScalarTruePeakMeter
    {
        const int Half = 8;
        const int Block = 64;

        readonly int _factor;
        readonly float[][] _phases;
        readonly double _gain;

        // The samples not yet interpolated past, after the Half - 1 before them that their interpolation needs.
        float[] _buffer = new float[4096];
        int _count = Half - 1;

        public ScalarTruePeakMeter(int sampleRate)
        {
            _factor = sampleRate < 192000 ? 4 : 1;
            _phases = new float[_factor - 1][];
            for (int p = 1; p < _factor; p++)
            {
                // Phase p is the point p / factor of the way from a sample to the next: the sinc at its distance from each of
                // the 2 × Half samples around it, under a Kaiser window, scaled so that the taps add up to 1.
                var taps = new double[2 * Half];
                double t = p / (double)_factor;
                for (int j = 0; j < taps.Length; j++)
                {
                    double x = t + Half - 1 - j;
                    taps[j] = Sinc(x) * Kaiser(x / Half, 8);
                }

                double sum = taps.Sum();
                _phases[p - 1] = [.. taps.Select(v => (float)(v / sum))];
                _gain = Math.Max(_gain, _phases[p - 1].Sum(v => Math.Abs((double)v)));
            }
        }

        /// <summary>The true peak so far, a linear amplitude.</summary>
        public double Peak { get; private set; }

        public void Add(ReadOnlySpan<float> samples)
        {
            foreach (float s in samples)
                Peak = Math.Max(Peak, Math.Abs(s));
            if (_factor == 1)
                return;
            if (_count + samples.Length > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _count + samples.Length));
            samples.CopyTo(_buffer.AsSpan(_count));
            _count += samples.Length;
            Interpolate();
        }

        /// <summary>The last samples' points, against silence after them.</summary>
        public void Finish()
        {
            if (_factor == 1)
                return;
            Add(new float[Half]);
        }

        /// <summary>Every point between the samples that have Half samples after them; keeps the Half - 1 before the next.</summary>
        void Interpolate()
        {
            // Point between sample i and i + 1 uses samples i - Half + 1 to i + Half.
            int first = Half - 1;
            int end = _count - Half;
            for (int start = first; start < end; start += Block)
            {
                int stop = Math.Min(start + Block, end);
                var window = _buffer.AsSpan(start - Half + 1, stop - start + 2 * Half - 1);
                float largest = 0;
                foreach (float s in window)
                    largest = Math.Max(largest, Math.Abs(s));
                if (largest * _gain <= Peak)
                    continue;
                for (int i = start; i < stop; i++)
                {
                    var around = _buffer.AsSpan(i - Half + 1, 2 * Half);
                    foreach (var taps in _phases)
                    {
                        float y = 0;
                        for (int j = 0; j < taps.Length; j++)
                            y += taps[j] * around[j];
                        Peak = Math.Max(Peak, Math.Abs(y));
                    }
                }
            }

            if (end > first)
            {
                int keep = _count - (end - first);
                _buffer.AsSpan(end - first, keep).CopyTo(_buffer);
                _count = keep;
            }
        }

        static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

        /// <summary>The Kaiser window at x in [-1, 1].</summary>
        static double Kaiser(double x, double beta) =>
            Math.Abs(x) >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - x * x)) / BesselI0(beta);

        static double BesselI0(double x)
        {
            double sum = 1, term = 1;
            for (int k = 1; k < 50; k++)
            {
                term *= x / (2 * k) * (x / (2 * k));
                sum += term;
            }

            return sum;
        }
    }
}
