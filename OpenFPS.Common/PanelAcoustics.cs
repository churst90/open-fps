using System;
using System.Collections.Generic;

namespace OpenFPS.Common;

/// <summary>
/// What a slab of material does when something hits it.
///
/// A door leaf, a pane of glass, a car's wing and the side of a shipping container are all a flat
/// piece of stuff that rings when struck, and they ring for the same reasons and by the same law.
/// This is that law, in one place, because the alternative is each of them growing its own copy that
/// slowly stops agreeing with the others.
///
///   THE NOTE is the plate-bending fundamental: 0.4755 t sqrt(E/rho) (1/a² + 1/b²). The constant is
///   (pi/2) / (2 sqrt(3(1 - v²))) at a Poisson's ratio of 0.3 — geometry, not a tuning knob. Both
///   spans enter it, which is why a tall narrow door and a square hatch of the same area do not
///   ring alike, and why a big thin panel booms where a small thick one knocks.
///
///   HOW LONG is damping, and damping is not absorption. A steel plate and a carpet absorb about the
///   same sound out of the air and differ by orders of magnitude in how long they ring, which is the
///   whole of why one clangs and the other thuds.
///
///   HOW LOUD is the energy arriving. Half m v squared, and level goes as its log, so twice the
///   speed is six decibels. That ratio is what makes a slam recognisable AS a slam rather than as a
///   louder close, and it is the same ratio for a car hitting a wall.
/// </summary>
public static class PanelAcoustics
{
    /// <summary>Below this a panel does not have a note, it has a thud. Nothing should clamp a
    /// frequency into existence for something that does not ring.</summary>
    public const float MinimumRingHz = 40f;

    /// <summary>
    /// How much damping a panel picks up simply by being FIXED to something rather than floating free.
    ///
    /// Added to the material's own internal loss, and for anything metallic it dominates it
    /// completely. Steel's intrinsic loss factor is about two ten-thousandths, which on its own says
    /// a steel door rings for a minute and a half; it does not, because it is bolted to hinges and
    /// meets a frame, and that path carries energy away far faster than the steel itself loses it.
    /// Published TOTAL loss factors for building panels in situ run one to five per cent and are
    /// dominated by exactly this, which is why measured figures for a mounted panel bear so little
    /// relation to the material's own.
    /// </summary>
    public const float MountedLoss = 0.03f;

    /// <summary>
    /// How long a ring has to last to be a ring at all.
    ///
    /// Shorter than this and it dies with the blow that caused it, so it is not something you hear
    /// afterwards, it is part of what you heard. The distinction matters because the modal series
    /// will hand back an audible FREQUENCY for almost anything — a carpet has modes too — and a
    /// carpet very obviously does not ring. Asking how long it lasts is what tells them apart, and it
    /// is the same question as "is this a bell or a bag of sand".
    /// </summary>
    public const float MinimumRingSeconds = 0.15f;

    /// <summary>Whether a panel of this stuff, at this note, actually rings rather than thudding.</summary>
    public static bool RingsAudibly(MaterialProperties material, float hz, float mountingLoss = MountedLoss)
        => hz > 0f && RingSeconds(material, hz, mountingLoss) >= MinimumRingSeconds;

    /// <summary>Level at one metre for one joule arriving, dB SPL. Everything scales logarithmically
    /// from here, so this one number sets the absolute and nothing else does.</summary>
    public const float ImpactReferenceDb = 74f;

    /// <summary>
    /// The note a flat panel of this stuff, this size, rings at. Zero if it does not ring.
    ///
    /// Returns the lowest mode that is actually AUDIBLE, which is usually but not always the
    /// fundamental. A big thin panel — a window pane, a car door skin — has a fundamental down
    /// around ten or fifteen hertz, and it plainly does not thud at fifteen hertz when you strike
    /// it: what you hear is the lowest of its higher modes that your ears can reach. A plate's modes
    /// go as (m²/a² + n²/b²), so climbing that series is the honest way up rather than clamping the
    /// fundamental to the bottom of hearing and calling it a note.
    ///
    /// If the series has to be climbed a long way, the thing is not ringing at all — it is a sheet
    /// of something floppy, and it thuds.
    /// </summary>
    public static float RingHz(MaterialProperties material, float width, float height, float thickness)
    {
        float rho = MathF.Max(1f, material.DensityKgM3);
        float e = material.YoungsModulusGPa * 1e9f;
        if (e <= 0f) return 0f;

        float a = MathF.Max(0.05f, width);
        float b = MathF.Max(0.05f, height);
        float t = Math.Clamp(thickness, 0.002f, 0.5f);
        float scale = 0.4755f * t * MathF.Sqrt(e / rho);

        float best = 0f;
        for (int m = 1; m <= 5; m++)
            for (int n = 1; n <= 5; n++)
            {
                float hz = scale * (m * m / (a * a) + n * n / (b * b));
                if (hz < MinimumRingHz) continue;
                if (best == 0f || hz < best) best = hz;
            }

        return best == 0f ? 0f : MathF.Min(best, 6000f);
    }

    /// <summary>One plate-bending mode: which one it is, and what note it sounds.</summary>
    public readonly record struct PlateMode(int M, int N, float Hz, float Weight);

    /// <summary>
    /// Every mode of a panel that a broadband drive actually excites, lowest first.
    ///
    /// <see cref="RingHz"/> answers "what note does this thing make when struck", which needs one
    /// frequency. Colouring a continuous sound needs the SERIES, because that is what a panel does to
    /// everything passing through it rather than what it does once when hit.
    ///
    /// The weights are not a taste curve, they are a selection rule. A plate driven by pressure
    /// spread over its whole face couples to a mode by the integral of that mode's shape across it,
    /// and for a simply-supported plate that integral is proportional to
    /// (1 - cos m*pi)(1 - cos n*pi) / (m n pi²) — which is ZERO unless m and n are both odd, and
    /// 4/(m n pi²) when they are. Half the modes are simply not driven: their two halves move in
    /// opposite directions and cancel. So a uniformly-driven panel is a much sparser thing than its
    /// mode count suggests, and the ones that survive fall away as 1/(m n).
    /// </summary>
    public static List<PlateMode> Modes(MaterialProperties material, float width, float height,
                                        float thickness, float maxHz = 6000f, int order = 7)
    {
        var modes = new List<PlateMode>();

        float rho = MathF.Max(1f, material.DensityKgM3);
        float e = material.YoungsModulusGPa * 1e9f;
        if (e <= 0f) return modes;

        float a = MathF.Max(0.05f, width);
        float b = MathF.Max(0.05f, height);
        float t = Math.Clamp(thickness, 0.0002f, 0.5f);
        float scale = 0.4755f * t * MathF.Sqrt(e / rho);

        // Odd m and odd n only — the rest are driven with zero net force by a distributed pressure.
        for (int m = 1; m <= order; m += 2)
            for (int n = 1; n <= order; n += 2)
            {
                float hz = scale * (m * m / (a * a) + n * n / (b * b));
                if (hz < MinimumRingHz || hz > maxHz) continue;
                modes.Add(new PlateMode(m, n, hz, 1f / (m * n)));
            }

        modes.Sort((x, y) => x.Hz.CompareTo(y.Hz));
        return modes;
    }

    /// <summary>
    /// How long that ring takes to fall 60 dB.
    ///
    /// T60 = 2.2 / (loss factor * frequency) — the standard relation. Pass a mounting loss of zero
    /// for something flying through the air, which genuinely does ring far longer than the same thing
    /// bolted down.
    /// </summary>
    public static float RingSeconds(MaterialProperties material, float hz, float mountingLoss = MountedLoss)
    {
        if (hz <= 0f) return 0f;
        float loss = MathF.Max(1e-5f, material.LossFactor + MathF.Max(0f, mountingLoss));
        // Two and a half seconds is the ceiling because nothing in a building rings longer than that
        // except a bell, and very little in this game is one.
        return Math.Clamp(2.2f / (loss * hz), 0.01f, 2.5f);
    }

    /// <summary>How loud an impact of this much energy is at one metre, dB SPL.</summary>
    public static float ImpactDb(float joules)
        => ImpactReferenceDb + 10f * MathF.Log10(MathF.Max(0.001f, joules));

    /// <summary>
    /// The energy in two things meeting, from the speed they are closing at.
    ///
    /// The REDUCED mass, not either one of them: a lorry hitting a drink can and a drink can hitting
    /// a lorry are the same collision, and both are governed by the lighter of the two. Using the
    /// heavier would make a truck brushing a bollard sound like the end of the world.
    /// </summary>
    public static float ImpactJoules(float massA, float massB, float closingSpeed)
    {
        float a = MathF.Max(0.01f, massA);
        float b = MathF.Max(0.01f, massB);
        float reduced = a * b / (a + b);
        return 0.5f * reduced * closingSpeed * closingSpeed;
    }
}
