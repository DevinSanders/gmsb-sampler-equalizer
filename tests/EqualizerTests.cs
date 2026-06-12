using System.Text.Json;
using FluentAssertions;
using NAudio.Wave;
using SoundBoard.PluginApi;
using Xunit;

namespace EqualizerPlugin.Tests;

public class FactoryTests
{
    [Fact]
    public void Plugin_advertises_all_attachment_points_and_stable_identity()
    {
        var plugin = new EqualizerPlugin();
        plugin.Id.Should().Be("sampler.equalizer");
        plugin.SupportedAttachments.Should().Be(SamplerAttachmentPoints.All);
        plugin.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CreateInstance_returns_distinct_objects_sharing_no_state()
    {
        var plugin = new EqualizerPlugin();
        var a = plugin.CreateInstance();
        var b = plugin.CreateInstance();
        a.Should().NotBeSameAs(b);

        // Mutating one instance's config must not bleed into the other.
        a.DeserializeConfig("""{"mode":"7band","gains":[12,0,0,0,0,0,0]}""");
        b.SerializeConfig().Should().Contain("3band",
            "a fresh instance keeps its defaults regardless of sibling edits");
    }
}

public class ConfigRoundTripTests
{
    private static ISamplerInstance NewInstance() => new EqualizerPlugin().CreateInstance();

    [Fact]
    public void Defaults_serialize_to_three_band_all_flat()
    {
        using var doc = JsonDocument.Parse(NewInstance().SerializeConfig());
        doc.RootElement.GetProperty("mode").GetString().Should().Be("3band");
        var gains = doc.RootElement.GetProperty("gains").EnumerateArray()
                       .Select(e => e.GetSingle()).ToArray();
        gains.Should().HaveCount(7).And.OnlyContain(g => g == 0f);
    }

    [Fact]
    public void Round_trip_preserves_mode_and_every_gain()
    {
        var inst = NewInstance();
        inst.DeserializeConfig("""{"mode":"7band","gains":[1,2,3,4,5,6,7]}""");

        using var doc = JsonDocument.Parse(inst.SerializeConfig());
        doc.RootElement.GetProperty("mode").GetString().Should().Be("7band");
        var gains = doc.RootElement.GetProperty("gains").EnumerateArray()
                       .Select(e => e.GetSingle()).ToArray();
        gains.Should().Equal(1, 2, 3, 4, 5, 6, 7);
    }

    [Fact]
    public void Out_of_range_gains_clamp_to_plus_minus_18()
    {
        var inst = NewInstance();
        inst.DeserializeConfig("""{"mode":"3band","gains":[99,-99,0,0,0,0,0]}""");

        using var doc = JsonDocument.Parse(inst.SerializeConfig());
        var gains = doc.RootElement.GetProperty("gains").EnumerateArray()
                       .Select(e => e.GetSingle()).ToArray();
        gains[0].Should().Be(18f);
        gains[1].Should().Be(-18f);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("""{"mode":42}""")]
    public void Malformed_or_empty_config_never_throws(string json)
    {
        var inst = NewInstance();
        var act = () => inst.DeserializeConfig(json);
        act.Should().NotThrow();
    }
}

public class EffectTests
{
    private static ISampleProvider Effect(string? config, Func<long, float> signal)
    {
        var inst = new EqualizerPlugin().CreateInstance();
        if (config != null) inst.DeserializeConfig(config);
        return inst.CreateEffect(new SignalProvider(Fmt.Stereo, signal));
    }

    [Fact]
    public void Preserves_wave_format()
    {
        var fx = Effect(null, _ => 0f);
        fx.WaveFormat.SampleRate.Should().Be(48000);
        fx.WaveFormat.Channels.Should().Be(2);
        fx.WaveFormat.Encoding.Should().Be(Fmt.Stereo.Encoding);
    }

    [Fact]
    public void All_flat_passes_audio_through_unchanged()
    {
        // Every band at 0 dB → identity biquads → bit-exact passthrough.
        var sine = Signals.Sine(1000);
        var fx = Effect("""{"mode":"3band","gains":[0,0,0,0,0,0,0]}""", sine);
        var outBuf = Signals.Pull(fx, 4096);

        var reference = new float[outBuf.Length];
        for (int i = 0; i < reference.Length; i++) reference[i] = sine(i);

        outBuf.Should().Equal(reference);
    }

    [Fact]
    public void Cutting_the_mid_band_attenuates_a_tone_at_its_center()
    {
        // 3-band layout: band index 1 is a peaking bell centered at 1 kHz.
        const double f = 1000;
        var dryRms = Signals.Rms(Signals.Pull(Effect(null, Signals.Sine(f)), 8192), skip: 4096);
        var wetRms = Signals.Rms(
            Signals.Pull(Effect("""{"mode":"3band","gains":[0,-18,0,0,0,0,0]}""", Signals.Sine(f)), 8192),
            skip: 4096);

        wetRms.Should().BeLessThan(dryRms * 0.5,
            "a -18 dB cut at the band center should roughly halve the tone or better");
    }

    [Fact]
    public void Boosting_the_mid_band_amplifies_a_tone_at_its_center()
    {
        const double f = 1000;
        var dryRms = Signals.Rms(Signals.Pull(Effect(null, Signals.Sine(f)), 8192), skip: 4096);
        var wetRms = Signals.Rms(
            Signals.Pull(Effect("""{"mode":"3band","gains":[0,18,0,0,0,0,0]}""", Signals.Sine(f)), 8192),
            skip: 4096);

        wetRms.Should().BeGreaterThan(dryRms * 1.5,
            "a +18 dB boost at the band center should clearly raise the tone");
    }

    [Fact]
    public void A_tone_far_from_a_cut_band_is_largely_untouched()
    {
        // Cut the mid (1 kHz) band hard, but probe with a 60 Hz tone — well
        // below the bell, so it should survive nearly intact.
        const double f = 60;
        var dryRms = Signals.Rms(Signals.Pull(Effect(null, Signals.Sine(f)), 8192), skip: 4096);
        var wetRms = Signals.Rms(
            Signals.Pull(Effect("""{"mode":"3band","gains":[0,-18,0,0,0,0,0]}""", Signals.Sine(f)), 8192),
            skip: 4096);

        wetRms.Should().BeApproximately(dryRms, dryRms * 0.1);
    }

    [Fact]
    public void Seven_band_mode_produces_finite_output()
    {
        var fx = Effect("""{"mode":"7band","gains":[6,-6,6,-6,6,-6,6]}""", Signals.Sine(2000));
        var outBuf = Signals.Pull(fx, 4096);
        outBuf.Should().OnlyContain(s => float.IsFinite(s));
    }
}

public class ThreadingTests
{
    [Fact]
    public async Task Live_config_push_while_reading_never_throws()
    {
        var inst = new EqualizerPlugin().CreateInstance();
        var fx = inst.CreateEffect(new SignalProvider(Fmt.Stereo, Signals.Sine(1000)));
        var buffer = new float[2048];

        using var cts = new CancellationTokenSource();
        Exception? caught = null;

        var writer = Task.Run(() =>
        {
            var rnd = new Random(1234);
            string[] modes = ["3band", "7band"];
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var mode = modes[rnd.Next(2)];
                    var gains = string.Join(',', Enumerable.Range(0, 7).Select(_ => rnd.Next(-18, 19)));
                    inst.DeserializeConfig($$"""{"mode":"{{mode}}","gains":[{{gains}}]}""");
                }
            }
            catch (Exception ex) { caught = ex; }
        }, cts.Token);

        // Pump the audio path for a few thousand buffers on this thread.
        for (int i = 0; i < 5000; i++)
            fx.Read(buffer, 0, buffer.Length);

        cts.Cancel();
        await writer;

        caught.Should().BeNull();
    }
}
