using System.Collections.Generic;

namespace OpenFPS.Common;

/// <summary>
/// What a slab of material does when struck (a door leaf, a pane, a car's wing), by one law:
/// <list type="bullet">
/// <item>The note is the plate-bending fundamental, 0.4755 t sqrt(E/rho) (1/a² + 1/b²); the constant is
/// (pi/2) / (2 sqrt(3(1 - v²))) at a Poisson's ratio of 0.3.</item>
/// <item>How long is damping, not absorption: a steel plate and a carpet absorb about the same and ring
/// for orders of magnitude apart.</item>
/// <item>How loud is the energy arriving, ½mv², so twice the speed is six decibels: a slam, not a louder
/// close.</item>
/// </list>
/// </summary>
public static class PanelAcoustics
{
    /// <summary>Below this a panel has no note, only a thud.</summary>
    public const float MinimumRingHz = 40f;

    /// <summary>
    /// The damping a panel picks up by being fixed to something, added to its own. Steel's own loss
    /// (about 0.0002) would ring a door for a minute and a half; published total loss factors for
    /// building panels in situ run one to five per cent.
    /// </summary>
    public const float MountedLoss = 0.03f;

    /// <summary>How long a ring must last to be heard as a ring rather than part of the blow: the modal
    /// series gives a carpet an audible frequency too.</summary>
    public const float MinimumRingSeconds = 0.15f;

    /// <summary>Whether a panel of this stuff, at this note, actually rings rather than thudding.</summary>
    public static bool RingsAudibly(MaterialProperties material, float hz, float mountingLoss = MountedLoss)
        => hz > 0f && RingSeconds(material, hz, mountingLoss) >= MinimumRingSeconds;

    /// <summary>Level at one metre for one joule arriving, dB SPL: the one absolute in the law.</summary>
    public const float ImpactReferenceDb = 74f;

    /// <summary>
    /// The note a flat panel of this stuff and size rings at, or zero: the lowest audible mode, not
    /// always the fundamental. A window pane's fundamental is 10-15 Hz; what you hear is the lowest of
    /// its higher modes, (m²/a² + n²/b²), within the first five each way.
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
    /// Every mode a broadband pressure over the whole face excites, lowest first, for colouring a
    /// continuous sound. A simply-supported plate couples to a mode by (1 - cos mπ)(1 - cos nπ) / (m n π²):
    /// zero unless m and n are both odd, and then weighted 1/(m n).
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

    /// <summary>How long the ring takes to fall 60 dB: T60 = 2.2 / (loss factor × frequency). A mounting
    /// loss of zero for something flying free.</summary>
    public static float RingSeconds(MaterialProperties material, float hz, float mountingLoss = MountedLoss)
    {
        if (hz <= 0f) return 0f;
        float loss = MathF.Max(1e-5f, material.LossFactor + MathF.Max(0f, mountingLoss));
        // Nothing in a building but a bell rings longer than 2.5 s.
        return Math.Clamp(2.2f / (loss * hz), 0.01f, 2.5f);
    }

    /// <summary>How loud an impact of this much energy is at one metre, dB SPL.</summary>
    public static float ImpactDb(float joules)
        => ImpactReferenceDb + 10f * MathF.Log10(MathF.Max(0.001f, joules));

    /// <summary>The energy in two things meeting at a closing speed, by the reduced mass: a lorry and a
    /// drink can collide with about the can's mass.</summary>
    public static float ImpactJoules(float massA, float massB, float closingSpeed)
    {
        float a = MathF.Max(0.01f, massA);
        float b = MathF.Max(0.01f, massB);
        float reduced = a * b / (a + b);
        return 0.5f * reduced * closingSpeed * closingSpeed;
    }
}
