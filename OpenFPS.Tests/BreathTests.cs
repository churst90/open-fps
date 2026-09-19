using System;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A breath is turbulence, and turbulence is broadband.
///
/// `Breathing`'s own note says it: *"a breath is turbulent air through a narrow aperture, which is a
/// HISS"*. The renderer did not do that. It was noise through ONE resonator at Q 0.9 with an attack a
/// twelfth of its own length — a resonance rather than a flow, and a transient rather than a swell —
/// and what came out was a 300 ms thump.
///
/// It cost six sessions. Reported as *"random banging... bang, wait a few seconds, bang"*, chased
/// through the reverb model, the room equation, the movement engine, the landing code and the
/// reflection machinery, and finally identified by the listener himself: *"it is 2 different bangs so
/// it makes me think it's breathing in and out... I don't hear the breathing either"*. Both halves of
/// that are one fault. Two bangs, because an inhale and an exhale sit at different frequencies; no
/// breathing, because neither of them sounded like breathing.
///
/// So the shape is asserted, not left to the ear. These are the bands, and they are the same rule the
/// footsteps and the clap carry: measure the spectrum BEFORE anybody listens, because "that does not
/// sound like X" is a question about the spectrum and the ear is slow at answering it.
/// </summary>
public class BreathTests
{
    private const int Sr = TransientSynth.SampleRate;

    public BreathTests() => AcousticRegistry.Initialize();

    private static float[] Render(float hz, float seconds) => TransientSynth.Render(new TransientSound
    {
        Character = SoundCharacter.Hiss, LevelDb = 50f, Hz = hz, DecaySeconds = seconds, Noisiness = 1f,
    }, seed: 7);

    /// <summary>
    /// The top of the band is where a breath lives. It was 23 and 32 dB down at 2-4 and 4-8 kHz — the
    /// two bands that carry "air" — which is why it read as a thump.
    /// </summary>
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

        // ...and it is not a rumble. A breath has nothing below a hundred hertz.
        Assert.True(bands[0] < loudest - 25f, "there is bass in a breath");
        Assert.True(bands[1] < loudest - 12f, "there is too much low end in a breath");
    }

    /// <summary>
    /// An inhale is drawn through a narrower opening, so it is brighter — and that has to fall out of
    /// its own frequency rather than being a second sound. It is the other of the "2 different bangs".
    /// </summary>
    [Fact]
    public void AnInhaleIsBrighterThanAnExhale()
    {
        var exhale = Spectrum.BandsDb(Render(500f, 0.30f), Sr);
        var inhale = Spectrum.BandsDb(Render(830f, 0.22f), Sr);

        float Top(float[] b) => b[^1] + b[^2] + b[^3];   // the three highest bands, together
        Assert.True(Top(inhale) > Top(exhale),
            $"an inhale at 830 Hz came out no brighter than an exhale at 500 ({Top(inhale):F1} against {Top(exhale):F1})");
    }

    /// <summary>
    /// A breath SWELLS. It has no onset: the loudest part is well inside it, not at the very front,
    /// and that is the difference between air and a knock. A twelfth-of-its-length attack put the
    /// peak in the first few per cent, which is a transient however smooth the rest is.
    /// </summary>
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

        // And the first instant is well under the peak — nothing arrives at full level.
        float head = 0f;
        for (int i = 0; i < window; i++) head += b[i] * b[i];
        Assert.True(head < best * 0.25f,
            $"a breath started at {MathF.Sqrt(head / best):P0} of its own peak — that is an onset");
    }

    /// <summary>
    /// And the model that drives it: breathing outlasts the running, which is the whole reason it is
    /// information. It is also why breaths arrive "seconds after I've stopped moving".
    /// </summary>
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
