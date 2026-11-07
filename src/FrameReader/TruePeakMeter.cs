using System.Numerics;
using System.Runtime.InteropServices;

namespace FrameReader;

/// <summary>
/// One channel's true peak: the largest magnitude of its samples and of the points between them, interpolated with a
/// windowed sinc (16 taps a phase) at 4 times the rate below 192 kHz (BS.1770-4 Annex 2; from 192 kHz the samples alone). Only the stretches that could
/// beat the peak so far are interpolated (a stretch's largest sample times the filter's gain bounds what it can give).
/// Neighbouring points are interpolated together in vectors, each point's products added in the same order as alone
/// (no fused multiply-adds), and a NaN among the samples or points makes the peak NaN, as Math.Max does.
/// </summary>
internal sealed class TruePeakMeter
{
    const int Half = 8;
    const int Block = 64;

    readonly int _factor;
    readonly float[][] _phases;
    readonly double _gain;

    // The samples not yet interpolated past, after the Half - 1 before them that their interpolation needs.
    float[] _buffer = new float[4096];
    int _count = Half - 1;

    public TruePeakMeter(int sampleRate)
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

    /// <summary>Adds the next samples, the largest of whose magnitudes by Math.Max (NaN if any is) is <paramref name="samplePeak"/>.</summary>
    public void Add(ReadOnlySpan<float> samples, double samplePeak)
    {
        Peak = Math.Max(Peak, samplePeak);
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
        Add(new float[Half], 0);
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
            if (Largest(window) * _gain <= Peak)
                continue;
            int i = start;
            if (Vector.IsHardwareAccelerated)
            {
                // Vector<float>.Count neighbouring points at a time, lane k the point after sample i + k: its taps times
                // the samples around it, added in the order the point alone adds them.
                ref float buffer = ref MemoryMarshal.GetArrayDataReference(_buffer);
                var top = Vector<float>.Zero;
                var nan = Vector<int>.Zero;
                for (; i + Vector<float>.Count <= stop; i += Vector<float>.Count)
                {
                    foreach (var taps in _phases)
                    {
                        var y = Vector<float>.Zero;
                        for (int j = 0; j < taps.Length; j++)
                            y += new Vector<float>(taps[j]) * Vector.LoadUnsafe(ref buffer, (nuint)(i - Half + 1 + j));
                        top = Vector.Max(top, Vector.Abs(y));
                        nan |= ~Vector.Equals(y, y);
                    }
                }

                Peak = Max(Peak, top, nan);
            }

            for (; i < stop; i++)
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

    /// <summary>The largest magnitude of the samples by Math.Max: NaN if any is NaN.</summary>
    static float Largest(ReadOnlySpan<float> samples)
    {
        float largest = 0;
        int k = 0;
        if (Vector.IsHardwareAccelerated)
        {
            ref float s = ref MemoryMarshal.GetReference(samples);
            var top = Vector<float>.Zero;
            var nan = Vector<int>.Zero;
            for (; k + Vector<float>.Count <= samples.Length; k += Vector<float>.Count)
            {
                var v = Vector.LoadUnsafe(ref s, (nuint)k);
                top = Vector.Max(top, Vector.Abs(v));
                nan |= ~Vector.Equals(v, v);
            }

            largest = (float)Max(0, top, nan);
        }

        for (; k < samples.Length; k++)
            largest = Math.Max(largest, Math.Abs(samples[k]));
        return largest;
    }

    /// <summary>
    /// Math.Max of <paramref name="peak"/> and the lanes of <paramref name="top"/> (magnitudes), NaN where any lane of
    /// <paramref name="nan"/> is set (a NaN that the vector's max may have dropped).
    /// </summary>
    static double Max(double peak, Vector<float> top, Vector<int> nan)
    {
        if (nan != Vector<int>.Zero)
            return double.NaN;
        for (int k = 0; k < Vector<float>.Count; k++)
            peak = Math.Max(peak, top[k]);
        return peak;
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
