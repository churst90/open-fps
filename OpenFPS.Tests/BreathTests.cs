using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Tests;

/// <summary>
/// A breath is turbulence, and turbulence is broadband: a resonated, sharp-attacked breath was the
/// "random banging" Cody chased for six sessions (docs/CLIENT_NOTES.md, "A breath is turbulence"). The
/// bands are asserted before anybody listens.
/// </summary>
public class BreathTests
{
    private const int Sr = TransientSynth.SampleRate;

    public BreathTests() => AcousticRegistry.Initialize();

    private static float[] Render(float hz, float seconds) => TransientSynth.Render(new TransientSound
    {
        Character = SoundCharacter.Hiss, LevelDb = 50f, Hz = hz, DecaySeconds = seconds, Noisiness = 1f,
    }, seed: 7);

    /// <summary>The top of the band is where a breath lives: 2-4 and 4-8 kHz were 23 and 32 dB down,
    /// which read as a thump.</summary>
    [Fact]
    public void AnExhaleHasItsEnergyWhereAirDoes()
    {
        var bands = Spectrum.BandsDb(Render(500f, 0.30f), Sr);

        int b1k = Array.FindIndex(Spectrum.BandEdges, e => e >= 1000f) - 1;
        int b2k = b1k + 1, b4k = b1k + 2;

        // The 1-8 kHz span carries the character and must be within a few decibels of the loudest
        // band, not twenty under it.
        float loudest = bands.Max();
        Assert.True(bands[b2k] > loudest - 8f, $"2-4 kHz is {loudest - bands[b2k]:F1} dB under the peak");
        Assert.True(bands[b4k] > loudest - 12f, $"4-8 kHz is {loudest - bands[b4k]:F1} dB under the peak");

        // And not a rumble: a breath has nothing below 100 Hz.
        Assert.True(bands[0] < loudest - 25f, "there is bass in a breath");
        Assert.True(bands[1] < loudest - 12f, "there is too much low end in a breath");
    }

    /// <summary>An inhale, drawn through a narrower opening, is brighter, from its own frequency and not a
    /// second sound.</summary>
    [Fact]
    public void AnInhaleIsBrighterThanAnExhale()
    {
        var exhale = Spectrum.BandsDb(Render(500f, 0.30f), Sr);
        var inhale = Spectrum.BandsDb(Render(830f, 0.22f), Sr);

        float Top(float[] b) => b[^1] + b[^2] + b[^3];   // the three highest bands, together
        Assert.True(Top(inhale) > Top(exhale),
            $"an inhale at 830 Hz came out no brighter than an exhale at 500 ({Top(inhale):F1} against {Top(exhale):F1})");
    }

    /// <summary>A breath swells: its loudest part is well inside it, not at the front, the difference
    /// between air and a knock.</summary>
    [Fact]
    public void ABreathHasNoOnset()
    {
        var b = Render(500f, 0.30f);

        // Where the envelope peaks, as a fraction of the whole.
        int window = Math.Max(1, b.Length / 100);
        float best = 0f; int bestAt = 0;
        for (int i = 0; i + window < b.Length; i += window)
        {
            float rms = 0f;
            for (int k = i; k < i + window; k++) rms += b[k] * b[k];
            if (rms > best) { best = rms; bestAt = i; }
        }

        float at = bestAt / (float)b.Length;
        Assert.InRange(at, 0.12f, 0.6f);

        // And the first instant is well under the peak.
        float head = 0f;
        for (int i = 0; i < window; i++) head += b[i] * b[i];
        Assert.True(head < best * 0.25f,
            $"a breath started at {MathF.Sqrt(head / best):P0} of its own peak — that is an onset");
    }

    /// <summary>Breathing outlasts the running that caused it (breaths came "seconds after I've stopped
    /// moving").</summary>
    [Fact]
    public void BreathingOutlastsTheRunning()
    {
        var lungs = new Breathing();
        const float dt = 1f / 60f;
        int afterStopping = 0;
        float stopAt = 12f;

        for (float t = 0; t < 40f; t += dt)
        {
            float speed = t < stopAt ? PhysicsConstants.SprintSpeed : 0f;
            if (lungs.Update(speed, dt, out var breath) && breath.Taken && t > stopAt + 3f) afterStopping++;
        }

        Assert.True(afterStopping >= 8,
            $"only {afterStopping} breaths in the twenty-five seconds after the running stopped");
    }
}
