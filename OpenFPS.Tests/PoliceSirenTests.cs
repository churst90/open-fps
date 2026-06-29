using System;
using OpenFPS.Client.Core.AudioEngine.Tools;

namespace OpenFPS.Tests;

/// <summary>Tests for the generated police-siren wail (replaces the old thin beep).</summary>
public class PoliceSirenTests
{
    [Fact]
    public void Generate_HasExpectedLengthAndIsInRange()
    {
        var s = PoliceSirenGenerator.Generate();
        Assert.Equal((int)(PoliceSirenGenerator.SampleRate * PoliceSirenGenerator.WailSeconds), s.Length);

        float peak = 0f;
        foreach (var x in s)
        {
            Assert.InRange(x, -1f, 1f);
            peak = MathF.Max(peak, MathF.Abs(x));
        }
        // Normalized to ~0.9 headroom: should be loud but never clip.
        Assert.InRange(peak, 0.5f, 0.9001f);
    }

    [Fact]
    public void Generate_LoopsSeamlessly()
    {
        var s = PoliceSirenGenerator.Generate();
        // The buffer is one whole wail cycle (T·favg ∈ ℤ), so sample n would equal sample 0 — the loop is
        // truly periodic. The join s[n-1]→s[0] is therefore an ordinary inter-sample step, not a click:
        // assert it is no larger than the biggest step seen anywhere else in the buffer.
        float maxInteriorStep = 0f;
        for (int i = 1; i < s.Length; i++)
            maxInteriorStep = MathF.Max(maxInteriorStep, MathF.Abs(s[i] - s[i - 1]));

        float joinStep = MathF.Abs(s[0] - s[^1]);
        Assert.True(joinStep <= maxInteriorStep + 1e-3f,
            $"loop join step {joinStep} exceeds max interior step {maxInteriorStep} — not seamless");
    }

    [Fact]
    public void ToWav16_WritesValidRiffHeader()
    {
        var s = PoliceSirenGenerator.Generate();
        var wav = PoliceSirenGenerator.ToWav16(s);

        Assert.Equal(44 + s.Length * 2, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        // Sample rate field at offset 24 (little-endian).
        int sr = wav[24] | (wav[25] << 8) | (wav[26] << 16) | (wav[27] << 24);
        Assert.Equal(PoliceSirenGenerator.SampleRate, sr);
    }
}
