using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NAudio.Wave;
using SoundBoard.PluginApi;

namespace EqualizerPlugin;

/// <summary>
/// A parametric equalizer FX plugin for Game Master Sound Board. Cascade of
/// biquad filters — one peaking-EQ biquad per middle band, low-shelf at the
/// bottom band, high-shelf at the top. User-selectable 3-band or 7-band
/// layout via the editor's mode toggle.
///
/// <para>Coefficients are recomputed off the audio thread on any knob
/// change and published atomically via <c>Volatile.Write</c>; the audio
/// thread snapshots them once per buffer.</para>
/// </summary>
public sealed class EqualizerPlugin : IAudioSamplerPlugin
{
    public string Id => "sampler.equalizer";
    public string Name => "Equalizer";
    public string Description => "Parametric EQ — choose 3-band or 7-band layout.";
    public string Version => PluginVersion.OfAssembly(typeof(EqualizerPlugin));
    public string Author => "Devin Sanders";

    public SamplerAttachmentPoints SupportedAttachments => SamplerAttachmentPoints.All;

    public void Initialize(IPluginContext context) { }
    public void Shutdown() { }

    public ISamplerInstance CreateInstance() => new EqualizerInstance();
}

internal sealed class EqualizerInstance : ISamplerInstance
{
    // ── Fixed band layouts (v1: no parametric center/Q) ───────────────
    internal static readonly float[] Bands3 = { 100f, 1000f, 8000f };
    internal static readonly float[] Bands7 = { 60f, 170f, 350f, 1000f, 3500f, 10000f, 16000f };

    internal const float MinGainDb = -18f;
    internal const float MaxGainDb = +18f;
    private const float ShelfQ = 0.7f;
    private const float PeakQ = 1.4f;

    // ── Shared mutable state ──────────────────────────────────────────
    // Mode: 0 = 3-band, 1 = 7-band. Plain int — atomic on every supported arch.
    private int _modeBits;

    // Per-band gain in dB, stored as float bit-patterns. 7 slots cover both
    // modes (3-band uses the first 3). Written from UI / DeserializeConfig
    // thread, read by RebuildCoefficients. Volatile.Read/Write keeps the
    // values coherent without locks.
    private readonly int[] _gainBits = new int[7];

    // Sample rate captured in CreateEffect. Defaults to host mixer rate
    // so the constructor's initial coefficient build is meaningful even
    // before any audio flows.
    private int _sampleRate = 48000;

    // The atomically-published coefficient set. Built off-thread, swapped
    // by reference; audio thread reads it via Volatile.Read once per buffer.
    private Coefficients _coeffs = Coefficients.Empty;

    public EqualizerInstance()
    {
        for (int i = 0; i < _gainBits.Length; i++)
            WriteFloatBits(ref _gainBits[i], 0f);
        Volatile.Write(ref _modeBits, 0);
        RebuildCoefficients();
    }

    // ── Knob accessors (thread-safe) ──────────────────────────────────
    internal int Mode => Volatile.Read(ref _modeBits);

    internal void SetMode(int v)
    {
        Volatile.Write(ref _modeBits, v == 1 ? 1 : 0);
        RebuildCoefficients();
    }

    internal float GetGainDb(int bandIndex) => ReadFloatBits(ref _gainBits[bandIndex]);

    internal void SetGainDb(int bandIndex, float db)
    {
        db = Math.Clamp(db, MinGainDb, MaxGainDb);
        WriteFloatBits(ref _gainBits[bandIndex], db);
        RebuildCoefficients();
    }

    private static float ReadFloatBits(ref int slot) =>
        BitConverter.Int32BitsToSingle(Volatile.Read(ref slot));

    private static void WriteFloatBits(ref int slot, float v) =>
        Volatile.Write(ref slot, BitConverter.SingleToInt32Bits(v));

    // ── ISamplerInstance ──────────────────────────────────────────────
    public ISampleProvider CreateEffect(ISampleProvider source)
    {
        _sampleRate = source.WaveFormat.SampleRate;
        RebuildCoefficients();
        return new BiquadProvider(source, this);
    }

    public string SerializeConfig()
    {
        var dto = new ConfigDto
        {
            Mode = Mode == 1 ? "7band" : "3band",
            Gains = new float[7]
        };
        for (int i = 0; i < 7; i++) dto.Gains[i] = GetGainDb(i);
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    public void DeserializeConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        ConfigDto? dto;
        try { dto = JsonSerializer.Deserialize<ConfigDto>(json, JsonOpts); }
        catch { return; }
        if (dto == null) return;

        // Update the scalar knobs first, then publish a fresh coefficient
        // set in a single Volatile.Write. The audio thread sees either the
        // previous snapshot or the new one — never an in-between.
        if (dto.Gains != null)
        {
            int n = Math.Min(dto.Gains.Length, 7);
            for (int i = 0; i < n; i++)
                WriteFloatBits(ref _gainBits[i], Math.Clamp(dto.Gains[i], MinGainDb, MaxGainDb));
        }
        Volatile.Write(ref _modeBits, dto.Mode == "7band" ? 1 : 0);
        RebuildCoefficients();
    }

    public object? CreateControl() => new EqualizerControl(this);

    public void Dispose() { }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class ConfigDto
    {
        public string? Mode { get; set; }
        public float[]? Gains { get; set; }
    }

    // ── Coefficient build ─────────────────────────────────────────────
    private void RebuildCoefficients()
    {
        int mode = Mode;
        var freqs = mode == 1 ? Bands7 : Bands3;
        int n = freqs.Length;
        var bands = new Biquad[n];
        for (int i = 0; i < n; i++)
        {
            float dB = GetGainDb(i);
            float fc = freqs[i];
            if (n == 1)
                bands[i] = Biquad.Peaking(_sampleRate, fc, PeakQ, dB);
            else if (i == 0)
                bands[i] = Biquad.LowShelf(_sampleRate, fc, ShelfQ, dB);
            else if (i == n - 1)
                bands[i] = Biquad.HighShelf(_sampleRate, fc, ShelfQ, dB);
            else
                bands[i] = Biquad.Peaking(_sampleRate, fc, PeakQ, dB);
        }
        Volatile.Write(ref _coeffs, new Coefficients(bands));
    }

    internal Coefficients ReadCoefficients() => Volatile.Read(ref _coeffs);

    // ── Immutable coefficient snapshot ────────────────────────────────
    internal sealed class Coefficients
    {
        public Biquad[] Bands { get; }
        public Coefficients(Biquad[] bands) { Bands = bands; }
        public static readonly Coefficients Empty = new(Array.Empty<Biquad>());
    }

    /// <summary>
    /// Normalised biquad coefficients (a0 folded into the others).
    /// Filter formulas: Robert Bristow-Johnson, "Audio EQ Cookbook".
    /// </summary>
    internal readonly struct Biquad
    {
        public readonly float B0, B1, B2, A1, A2;

        public Biquad(float b0, float b1, float b2, float a1, float a2)
        {
            B0 = b0; B1 = b1; B2 = b2; A1 = a1; A2 = a2;
        }

        /// <summary>Pass-through (identity) biquad. y[n] = x[n].</summary>
        public static readonly Biquad Identity = new(1f, 0f, 0f, 0f, 0f);

        public static Biquad Peaking(int fs, float f0, float Q, float dBgain)
        {
            // RBJ peaking EQ.
            if (dBgain == 0f) return Identity;
            double A = Math.Pow(10.0, dBgain / 40.0);
            double w0 = 2.0 * Math.PI * f0 / fs;
            double cosw0 = Math.Cos(w0);
            double sinw0 = Math.Sin(w0);
            double alpha = sinw0 / (2.0 * Q);

            double b0 = 1.0 + alpha * A;
            double b1 = -2.0 * cosw0;
            double b2 = 1.0 - alpha * A;
            double a0 = 1.0 + alpha / A;
            double a1 = -2.0 * cosw0;
            double a2 = 1.0 - alpha / A;
            return Normalize(b0, b1, b2, a0, a1, a2);
        }

        public static Biquad LowShelf(int fs, float f0, float Q, float dBgain)
        {
            if (dBgain == 0f) return Identity;
            double A = Math.Pow(10.0, dBgain / 40.0);
            double w0 = 2.0 * Math.PI * f0 / fs;
            double cosw0 = Math.Cos(w0);
            double sinw0 = Math.Sin(w0);
            double alpha = sinw0 / (2.0 * Q);
            double twoSqrtAalpha = 2.0 * Math.Sqrt(A) * alpha;

            double b0 =     A * ((A + 1.0) - (A - 1.0) * cosw0 + twoSqrtAalpha);
            double b1 = 2.0 * A * ((A - 1.0) - (A + 1.0) * cosw0);
            double b2 =     A * ((A + 1.0) - (A - 1.0) * cosw0 - twoSqrtAalpha);
            double a0 =         (A + 1.0) + (A - 1.0) * cosw0 + twoSqrtAalpha;
            double a1 =  -2.0 * ((A - 1.0) + (A + 1.0) * cosw0);
            double a2 =         (A + 1.0) + (A - 1.0) * cosw0 - twoSqrtAalpha;
            return Normalize(b0, b1, b2, a0, a1, a2);
        }

        public static Biquad HighShelf(int fs, float f0, float Q, float dBgain)
        {
            if (dBgain == 0f) return Identity;
            double A = Math.Pow(10.0, dBgain / 40.0);
            double w0 = 2.0 * Math.PI * f0 / fs;
            double cosw0 = Math.Cos(w0);
            double sinw0 = Math.Sin(w0);
            double alpha = sinw0 / (2.0 * Q);
            double twoSqrtAalpha = 2.0 * Math.Sqrt(A) * alpha;

            double b0 =      A * ((A + 1.0) + (A - 1.0) * cosw0 + twoSqrtAalpha);
            double b1 = -2.0 * A * ((A - 1.0) + (A + 1.0) * cosw0);
            double b2 =      A * ((A + 1.0) + (A - 1.0) * cosw0 - twoSqrtAalpha);
            double a0 =          (A + 1.0) - (A - 1.0) * cosw0 + twoSqrtAalpha;
            double a1 =    2.0 * ((A - 1.0) - (A + 1.0) * cosw0);
            double a2 =          (A + 1.0) - (A - 1.0) * cosw0 - twoSqrtAalpha;
            return Normalize(b0, b1, b2, a0, a1, a2);
        }

        private static Biquad Normalize(double b0, double b1, double b2,
                                        double a0, double a1, double a2)
        {
            return new Biquad(
                (float)(b0 / a0),
                (float)(b1 / a0),
                (float)(b2 / a0),
                (float)(a1 / a0),
                (float)(a2 / a0));
        }
    }

    // ── Audio-thread DSP ──────────────────────────────────────────────
    /// <summary>
    /// Per-attachment DSP. State (z1/z2) is per-stage per-channel and
    /// owned by the audio thread; nothing else touches it. The coefficient
    /// snapshot is the only cross-thread piece, read once per buffer.
    /// </summary>
    private sealed class BiquadProvider : ISampleProvider
    {
        // Max stages = 7 (the larger layout). Pre-allocated so mode toggles
        // never allocate on the audio thread — they just clear state.
        private const int MaxStages = 7;

        private readonly ISampleProvider _source;
        private readonly EqualizerInstance _owner;
        private readonly int _channels;
        private readonly float[,] _z1;
        private readonly float[,] _z2;
        private int _lastStages;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public BiquadProvider(ISampleProvider source, EqualizerInstance owner)
        {
            _source = source;
            _owner = owner;
            _channels = source.WaveFormat.Channels;
            _z1 = new float[MaxStages, Math.Max(1, _channels)];
            _z2 = new float[MaxStages, Math.Max(1, _channels)];
            _lastStages = 0;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (read <= 0) return read;

            // Snapshot the coefficient set once per buffer. Any mid-buffer
            // UI knob change won't be seen until the next call to Read —
            // that's the cost of atomic-publish coherence.
            var c = _owner.ReadCoefficients();
            var bands = c.Bands;
            int stages = bands.Length;

            // Mode toggle changes the cascade length. Clear residual state
            // from the prior layout to avoid an audible transient from the
            // old z1/z2 values being interpreted with new coefficients.
            if (_lastStages != stages)
            {
                Array.Clear(_z1);
                Array.Clear(_z2);
                _lastStages = stages;
            }
            if (stages == 0) return read;

            int chans = _channels;
            // Single-channel fast path skips the modulo.
            if (chans == 1)
            {
                for (int i = 0; i < read; i++)
                {
                    float x = buffer[offset + i];
                    for (int s = 0; s < stages; s++)
                    {
                        var b = bands[s];
                        // Direct-form II transposed.
                        float y = b.B0 * x + _z1[s, 0];
                        _z1[s, 0] = b.B1 * x - b.A1 * y + _z2[s, 0];
                        _z2[s, 0] = b.B2 * x - b.A2 * y;
                        x = y;
                    }
                    buffer[offset + i] = x;
                }
            }
            else
            {
                // Interleaved multi-channel.
                int ch = 0;
                for (int i = 0; i < read; i++)
                {
                    float x = buffer[offset + i];
                    for (int s = 0; s < stages; s++)
                    {
                        var b = bands[s];
                        float y = b.B0 * x + _z1[s, ch];
                        _z1[s, ch] = b.B1 * x - b.A1 * y + _z2[s, ch];
                        _z2[s, ch] = b.B2 * x - b.A2 * y;
                        x = y;
                    }
                    buffer[offset + i] = x;
                    ch++;
                    if (ch >= chans) ch = 0;
                }
            }

            return read;
        }
    }

    // ── Avalonia UI ───────────────────────────────────────────────────
    /// <summary>
    /// Editor for one EqualizerInstance. Mode toggle at the top, a column
    /// of vertical gain sliders below — count matches the current mode.
    /// Slider drags push straight into the instance, so the audio thread
    /// picks them up on the next buffer (~tens of ms).
    /// </summary>
    private sealed class EqualizerControl : UserControl
    {
        private readonly EqualizerInstance _owner;
        private readonly StackPanel _slidersHost;

        public EqualizerControl(EqualizerInstance owner)
        {
            _owner = owner;

            var modeToggle = new ToggleSwitch
            {
                OnContent = "7-band",
                OffContent = "3-band",
                IsChecked = _owner.Mode == 1,
                Margin = new Thickness(0, 0, 0, 8),
            };
            modeToggle.IsCheckedChanged += (_, _) =>
            {
                _owner.SetMode(modeToggle.IsChecked == true ? 1 : 0);
                BuildSliders();
            };

            _slidersHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
            };

            var root = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(8),
                Spacing = 4,
            };
            root.Children.Add(modeToggle);
            root.Children.Add(_slidersHost);

            Content = root;
            BuildSliders();
        }

        private void BuildSliders()
        {
            _slidersHost.Children.Clear();
            var freqs = _owner.Mode == 1 ? Bands7 : Bands3;
            for (int i = 0; i < freqs.Length; i++)
            {
                int idx = i;
                var column = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Width = 54,
                    Spacing = 2,
                };

                var freqLabel = new TextBlock
                {
                    Text = FormatHz(freqs[i]),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 11,
                };

                var slider = new Slider
                {
                    Minimum = MinGainDb,
                    Maximum = MaxGainDb,
                    Value = _owner.GetGainDb(idx),
                    Orientation = Orientation.Vertical,
                    Height = 140,
                    TickFrequency = 6,
                    IsSnapToTickEnabled = false,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };

                var dbLabel = new TextBlock
                {
                    Text = FormatDb((float)slider.Value),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 11,
                };

                slider.ValueChanged += (_, e) =>
                {
                    float v = (float)e.NewValue;
                    _owner.SetGainDb(idx, v);
                    dbLabel.Text = FormatDb(v);
                };

                column.Children.Add(freqLabel);
                column.Children.Add(slider);
                column.Children.Add(dbLabel);
                _slidersHost.Children.Add(column);
            }
        }

        private static string FormatHz(float hz) =>
            hz >= 1000f ? $"{hz / 1000f:0.#}k" : $"{hz:0}";

        private static string FormatDb(float db) =>
            db == 0f ? "0 dB" : $"{db:+0.0;-0.0} dB";
    }
}
