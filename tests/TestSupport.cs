using NAudio.Wave;

namespace EqualizerPlugin.Tests;

/// <summary>Host mixer format: 48 kHz IEEE-float stereo.</summary>
internal static class Fmt
{
    public static readonly WaveFormat Stereo = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public const int SampleRate = 48000;
}

/// <summary>
/// Deterministic <see cref="ISampleProvider"/> driven by a pure function of
/// the (interleaved) sample index. Returns exactly <c>count</c> samples on
/// every call — never EOF — so tests can pull arbitrarily long.
/// </summary>
internal sealed class SignalProvider(WaveFormat fmt, Func<long, float> fn) : ISampleProvider
{
    private long _n;
    public WaveFormat WaveFormat => fmt;

    public int Read(float[] buf, int off, int count)
    {
        for (int i = 0; i < count; i++) buf[off + i] = fn(_n++);
        return count;
    }
}

internal static class Signals
{
    /// <summary>Stereo sine at <paramref name="hz"/>; same value on both
    /// channels (interleaved sample index advances per channel-sample).</summary>
    public static Func<long, float> Sine(double hz, int sampleRate = Fmt.SampleRate, float amp = 0.5f)
    {
        // Interleaved stereo: frame index = n / 2.
        double w = 2.0 * Math.PI * hz / sampleRate;
        return n => (float)(amp * Math.Sin(w * (n / 2)));
    }

    /// <summary>RMS over a buffer slice, skipping a lead-in so filter
    /// transients don't pollute the steady-state measurement.</summary>
    public static double Rms(float[] buf, int skip = 0)
    {
        double acc = 0;
        int count = 0;
        for (int i = skip; i < buf.Length; i++) { acc += (double)buf[i] * buf[i]; count++; }
        return count == 0 ? 0 : Math.Sqrt(acc / count);
    }

    /// <summary>Pull <paramref name="frames"/> stereo frames through a
    /// provider into one contiguous buffer.</summary>
    public static float[] Pull(ISampleProvider fx, int frames)
    {
        int samples = frames * fx.WaveFormat.Channels;
        var buf = new float[samples];
        int read = 0;
        while (read < samples)
            read += fx.Read(buf, read, samples - read);
        return buf;
    }
}
