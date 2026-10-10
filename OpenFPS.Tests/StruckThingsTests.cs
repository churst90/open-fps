using System.Numerics;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Struck things by modal synthesis (docs/MATTER.md section 7): the modes of the simple shapes against their
/// textbook values, how they scale with size and material, the contacts, the keys, the level anchor, and the
/// knuckle's fit to the door-knock recording as a ratchet.
/// </summary>
public class StruckThingsTests
{
    private readonly ITestOutputHelper _o;
    public StruckThingsTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }

    private const int Rate = 48000;

    private static StruckThing Cube(string material, float size, StruckSupport support = StruckSupport.Hung)
        => new() { Material = material, Shape = StruckShape.Block, Length = size, Width = size, Thickness = size, Support = support };

    private static double First(StruckThing t, float u = 0.37f, float v = 0.41f)
        => StruckThings.ModesOf(t, u, v).Modes[0].Hz;

    // ── Modes against their textbook values ─────────────────────────────────────────────────────────

    /// <summary>A slender free-free bar: Euler-Bernoulli's (4.730)^2 / (2 pi L^2) sqrt(E I / rho A), and the overtone
    /// pattern 1 : 2.756 : 5.404 that tells the ear "bar".</summary>
    [Fact]
    public void AFreeBarRingsAtEulerBernoullisNotes()
    {
        var bar = new StruckThing { Material = "Aluminium", Shape = StruckShape.Bar, Length = 0.4f, Width = 0.04f, Thickness = 0.01f, Support = StruckSupport.Hung };
        var p = AcousticRegistry.GetProperties("Aluminium");
        double f1 = 4.73004 * 4.73004 / (2 * Math.PI * 0.16) * 0.01 / Math.Sqrt(12) * Math.Sqrt(p.YoungsModulusGPa * 1e9 / p.DensityKgM3);
        var bending = StruckThings.ModesOf(bar, 0.5f, 0.5f).Modes.Where(m => Math.Abs(m.Drive) > 0.1).Select(m => m.Hz).ToList();
        _o.WriteLine($"expected {f1:F1} Hz; modes {string.Join(", ", bending.Take(4).Select(h => h.ToString("F1")))}");
        Assert.InRange(bending[0] / f1, 0.98, 1.0);   // Timoshenko lowers it a little, never raises it
        // Struck in the middle the antisymmetric modes have a node there: modes 1, 3, 5 of the series ring.
        var all = StruckThings.ModesOf(bar, 0.2f, 0.5f).Modes.Where(m => Math.Abs(m.Drive) > 0.05).Select(m => m.Hz).ToList();
        Assert.InRange(all[1] / all[0], 2.70, 2.76);
        Assert.InRange(all[2] / all[0], 5.25, 5.41);
    }

    /// <summary>A simply supported plate's fundamental: (pi / 2) sqrt(D / rho h) (1/a^2 + 1/b^2).</summary>
    [Fact]
    public void APlateInAFrameRingsAtItsFundamental()
    {
        var pane = new StruckThing { Material = "Glass", Shape = StruckShape.Plate, Length = 1f, Width = 1f, Thickness = 0.01f, Support = StruckSupport.Built };
        var p = AcousticRegistry.GetProperties("Glass");
        double d = p.YoungsModulusGPa * 1e9 * 1e-6 / (12 * (1 - p.Poisson * p.Poisson));
        double f11 = Math.PI / 2 * Math.Sqrt(d / (p.DensityKgM3 * 0.01)) * 2;
        Assert.InRange(First(pane) / f11, 0.995, 1.005);
    }

    /// <summary>A free square plate's first mode, the twisting one: Leissa's exact 13.47 for w a^2 sqrt(rho h / D),
    /// which Warburton's approximation puts about 5 % high.</summary>
    [Fact]
    public void AFreeSquareSheetsFirstModeIsLeissasTwist()
    {
        var sheet = new StruckThing { Material = "Aluminium", Shape = StruckShape.FreePlate, Length = 0.5f, Width = 0.5f, Thickness = 0.002f, Support = StruckSupport.Hung };
        var p = AcousticRegistry.GetProperties("Aluminium");
        double d = p.YoungsModulusGPa * 1e9 * 8e-9 / (12 * (1 - p.Poisson * p.Poisson));
        double lambda = 2 * Math.PI * First(sheet) * 0.25 * Math.Sqrt(p.DensityKgM3 * 0.002 / d);
        _o.WriteLine($"lambda {lambda:F2} (Leissa 13.47)");
        Assert.InRange(lambda, 13.47 * 0.97, 13.47 * 1.08);
    }

    /// <summary>The Ritz block, made long and thin, is a bar: its first bending mode within a few per cent of
    /// the bar's (Timoshenko-corrected) note, from a method that knows nothing of beams.</summary>
    [Fact]
    public void ALongBlockIsABar()
    {
        var block = new StruckThing { Material = "Metal", Shape = StruckShape.Block, Length = 1f, Width = 0.1f, Thickness = 0.1f, Support = StruckSupport.Hung };
        var bar = block with { Shape = StruckShape.Bar };
        double fBlock = First(block, 0.13f, 0.3f), fBar = First(bar, 0.13f, 0.3f);
        _o.WriteLine($"block {fBlock:F1} Hz, bar {fBar:F1} Hz");
        Assert.InRange(fBlock / fBar, 0.97, 1.04);
    }

    /// <summary>The Ritz solution has converged: order 8 (what is used) against order 10 on a cube's first ten.</summary>
    [Fact]
    public void TheCubesModesHaveConverged()
    {
        var a = StruckModes.Ritz(1, 1, 0.33, 8);
        var b = StruckModes.Ritz(1, 1, 0.33, 10);
        for (int i = 0; i < 10; i++)
            Assert.InRange(Math.Sqrt(a.Eigen[i] / b.Eigen[i]), 1.0, 1.01);
    }

    // ── Scaling: size, material, thickness ──────────────────────────────────────────────────────────

    /// <summary>MATTER.md 7.1: every note goes as sqrt(E / rho) over the size. Five times the cube is a fifth of
    /// the note; aluminium and steel ring at almost the same pitch; lead far lower.</summary>
    [Fact]
    public void PitchFallsWithSizeAndFollowsTheSpeedOfSound()
    {
        double al20 = First(Cube("Aluminium", 0.2f)), al100 = First(Cube("Aluminium", 1f));
        Assert.InRange(al20 / al100, 4.95, 5.05);
        double steel20 = First(Cube("Metal", 0.2f)), lead20 = First(Cube("Lead", 0.2f));
        _o.WriteLine($"20 cm cube: aluminium {al20:F0} Hz, steel {steel20:F0} Hz, lead {lead20:F0} Hz");
        Assert.InRange(steel20 / al20, 0.93, 1.07);
        Assert.True(lead20 < 0.3 * al20);
    }

    /// <summary>A plate's note goes as its thickness over its span squared.</summary>
    [Fact]
    public void AThickerPlateRingsHigher()
    {
        var thin = new StruckThing { Material = "Glass", Shape = StruckShape.Plate, Length = 1f, Width = 0.8f, Thickness = 0.004f, Support = StruckSupport.Built };
        Assert.InRange(First(thin with { Thickness = 0.008f }) / First(thin), 1.99, 2.01);
        Assert.InRange(First(thin) / First(thin with { Length = 2f, Width = 1.6f }), 3.98, 4.02);
    }

    /// <summary>Decay comes from the loss factor: aluminium on a string rings for seconds, lead, concrete and rubber
    /// hardly at all; the holding takes its share (a cube on the ground rings shorter than on a string).</summary>
    [Fact]
    public void RingTimesFollowTheLossFactor()
    {
        double T60(StruckThing t) { var m = StruckThings.ModesOf(t).Modes[0]; return 2.2 / (m.Loss * m.Hz); }
        double al = T60(Cube("Aluminium", 0.2f)), lead = T60(Cube("Lead", 0.2f)), concrete = T60(Cube("Concrete", 0.2f));
        double rubber = T60(Cube("Rubber", 0.2f)), resting = T60(Cube("Aluminium", 0.2f, StruckSupport.Resting));
        _o.WriteLine($"T60: aluminium {al:F2} s, on the ground {resting:F2} s, lead {lead:F3}, concrete {concrete:F3}, rubber {rubber:F3}");
        Assert.True(al > 0.3);
        Assert.True(resting < al);
        Assert.True(lead < 0.15 && concrete < 0.15 && rubber < 0.15);
    }

    /// <summary>Wood across the grain is a fourteenth as stiff: a board spanning its short way along the grain rings
    /// far higher than the same board with its grain along its length, which an isotropic board would not.</summary>
    [Fact]
    public void TheGrainChangesTheNote()
    {
        var along = new StruckThing { Material = "Oak", Shape = StruckShape.Plate, Length = 1f, Width = 0.3f, Thickness = 0.02f, Support = StruckSupport.Built };
        var across = along with { Length = 0.3f, Width = 1f };
        _o.WriteLine($"grain along the length {First(along):F0} Hz, across it {First(across):F0} Hz");
        Assert.True(First(across) > 1.5 * First(along));
        var mdf = along with { Material = "MDF" };
        Assert.InRange(First(mdf with { Length = 0.3f, Width = 1f }) / First(mdf), 0.99, 1.01);
    }

    // ── Contacts ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A fingertip stays in contact for milliseconds; a knuckle through its skin to the bone, and a rod,
    /// are shorter; on rubber everything is long.</summary>
    [Fact]
    public void HardStrikersMakeShortContacts()
    {
        double Contact(string material, Striker s, float speed)
        {
            StruckThings.RenderPascals(new Strike(Cube(material, 0.2f, StruckSupport.Resting), new[] { new Blow(s, speed, 0.4f, 0.4f) }), Rate, out _);
            return StruckThings.LastContactSeconds;
        }
        double finger = Contact("Metal", Striker.Fingertip, 0.8f), knuckle = Contact("Metal", Striker.Knuckle, 1.5f);
        double rod = Contact("Metal", Striker.Rod, 1f), rodOnRubber = Contact("Rubber", Striker.Rod, 1f);
        _o.WriteLine($"contact: finger {finger * 1000:F2} ms, knuckle {knuckle * 1000:F2} ms, rod {rod * 1000:F3} ms, rod on rubber {rodOnRubber * 1000:F2} ms");
        Assert.InRange(finger, 0.002, 0.012);
        Assert.True(knuckle < finger);
        Assert.True(rod < 0.0003);
        Assert.True(rodOnRubber > 10 * rod);
    }

    // ── Renders ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Aluminium", 0.05f, "finger")]
    [InlineData("Aluminium", 1f, "knuckle")]
    [InlineData("Lead", 0.2f, "knuckle")]
    [InlineData("Glass", 0.2f, "rod")]
    [InlineData("Rubber", 0.2f, "finger")]
    public void RendersAreFiniteAndEndQuietly(string material, float size, string striker)
    {
        Assert.True(Striker.TryByName(striker, out var s));
        var pcm = StruckThings.Render(new Strike(Cube(material, size, StruckSupport.Resting), new[] { new Blow(s, 1f, 0.4f, 0.4f) }), Rate, out float db);
        Assert.All(pcm, v => Assert.True(float.IsFinite(v)));
        Assert.InRange(pcm.Max(MathF.Abs), 0.999f, 1.001f);
        Assert.True(pcm.Length <= StruckThings.MaxSeconds * Rate + 1);
        Assert.True(MathF.Abs(pcm[^1]) < 1e-3f);
        Assert.InRange(db, 0f, 130f);
    }

    /// <summary>A strike on a ringing thing lasts; on a dead one it is gone in a blink (a render is cut 70 dB
    /// under its peak, so its length is how long it rings).</summary>
    [Fact]
    public void AluminiumOnAStringRingsAndConcreteThuds()
    {
        float Seconds(StruckThing t)
            => StruckThings.RenderPascals(new Strike(t, new[] { new Blow(Striker.Rod, 1f, 0.37f, 0.41f) }), Rate, out _).Length / (float)Rate;
        float al = Seconds(Cube("Aluminium", 0.2f)), concrete = Seconds(Cube("Concrete", 0.2f, StruckSupport.Resting));
        _o.WriteLine($"rings for: aluminium {al:F2} s, concrete {concrete:F3} s");
        Assert.True(al > 0.5f);
        Assert.True(concrete < 0.2f);
    }

    // ── Keys ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AKeyCarriesTheWholeStrike()
    {
        var thing = new StruckThing
        {
            Material = "Glass", Shape = StruckShape.Plate, Length = 2.1f, Width = 0.9f, Thickness = 0.01f, Support = StruckSupport.Built,
            PlayMm = 2f, LooseMaterial = "Metal",
        };
        var strike = new Strike(thing, StruckThings.BodyBump(1.2f, 0.4f, 0.6f, true, 0f), 2);
        string key = StruckThings.Key(strike);
        Assert.True(StruckThings.TryParseKey(key, out var back));
        Assert.Equal(key, StruckThings.Key(back));
        Assert.Equal(thing.Material, back.Thing.Material);
        Assert.Equal(3, back.Blows.Count);
        Assert.Equal(2, back.Seed);
        Assert.False(StruckThings.TryParseKey("strike:Glass|9|1|1|1|0|0|0|0|0||0|finger,80,50,50,0|0", out _));   // no such shape
        Assert.False(StruckThings.TryParseKey("strike:Glass|2|0|1|1|0|0|0|0|0||0|finger,80,50,50,0|0", out _));   // no size
        Assert.False(StruckThings.TryParseKey("strike:Glass|2|1|1|1|0|0|0|0|0||0|hammer,80,50,50,0|0", out _));   // no such striker
        Assert.False(StruckThings.TryParseKey("knock:3", out _));
    }

    // ── What a thing in the world is ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheWorldsBoxesAreDescribedByWhatTheyAre()
    {
        var wall = StruckThings.Describe("Plaster", new Vector3(0.1f, 2.6f, 3f), Vector3.UnitX, 0.0125f, 0.6f, false, false, out bool up);
        Assert.Equal(StruckShape.Plate, wall.Shape);
        Assert.Equal(0.0125f, wall.Thickness);
        Assert.InRange(wall.Cavity, 0.07f, 0.08f);
        Assert.Equal(0.6f, wall.Width, 3);
        Assert.True(up);
        var door = StruckThings.Describe("Glass", new Vector3(0.01f, 2.1f, 0.9f), Vector3.UnitX, 0f, 0f, true, false, out _);
        Assert.True(door.PlayMm > 0f);
        Assert.Equal(StruckShape.Plate, door.Shape);
        var fence = StruckThings.Describe("Fence", new Vector3(0.05f, 1.8f, 2.4f), Vector3.UnitX, 0f, 0f, false, false, out _);
        Assert.Equal(StruckShape.Bar, fence.Shape);
        var car = StruckThings.Describe("Metal", new Vector3(1.8f, 1.4f, 4.5f), Vector3.UnitX, 0f, 0f, true, true, out _);
        Assert.Equal(StruckShape.ShellBox, car.Shape);
        Assert.Equal(VehicleBody.Saloon.PanelThicknessM, car.Wall);
        var block = StruckThings.Describe("Granite", new Vector3(0.5f, 0.5f, 0.5f), Vector3.UnitY, 0f, 0f, false, false, out _);
        Assert.Equal(StruckShape.Block, block.Shape);
    }

    // ── The level anchor and the fit ─────────────────────────────────────────────────────────────

    /// <summary>The anchor puts the model's heel on a slab where the footstep bank plays a step on concrete; and it
    /// is one number, so the model's own levels against each other are untouched.</summary>
    [Fact]
    public void TheLevelIsAnchoredToTheFootstepTakes()
    {
        float anchor = StruckThings.LevelAnchorDb;
        _o.WriteLine($"anchor {anchor:F1} dB; the model's heel {StruckThings.ModelFootstepDb:F1} dB(A)");
        Assert.InRange(anchor, -30f, 10f);
        Assert.Equal(Loudness.FootstepDb + StruckThings.FootstepTakeLoudestA20msDbfs, StruckThings.ModelFootstepDb + anchor, 2);
    }

    /// <summary>
    /// The recording the knuckle was fitted to (inbox/door sounds, "Heavy Door Knocks", 19 knocks, the first 120 ms
    /// of each, measured by `--struck fit` on 2026-10-10): its band shape, dB of the total, 30 Hz to 16 kHz. The
    /// model's knock on a solid wooden door must stay within 3 dB rms of it from 125 Hz up (2.6 as fitted with
    /// 60 listed modes; 1.8 with 120). A ratchet: re-fit the knuckle if the model around it changes, do not widen this.
    /// </summary>
    private static readonly float[] HeavyDoorKnocks = { -10.1f, -5.5f, -6.9f, -6.7f, -7.3f, -18.3f, -32.9f, -36.4f, -45.7f };

    [Fact]
    public void TheKnuckleStillFitsTheRecordedKnock()
    {
        var door = new StruckThing { Material = "Wood", Shape = StruckShape.Plate, Length = 2.0f, Width = 0.9f, Thickness = 0.04f, Support = StruckSupport.Built };
        var p = StruckThings.RenderPascals(new Strike(door, new[] { new Blow(Striker.Knuckle, 1.5f, 0.55f, 0.4f) }), Rate, out _);
        var model = Shape(Octaves(p, Rate * 12 / 100));
        double mean = 0;
        for (int i = 2; i < model.Length; i++) mean += model[i] - HeavyDoorKnocks[i];
        mean /= model.Length - 2;
        double s = 0;
        for (int i = 2; i < model.Length; i++) { double d = model[i] - HeavyDoorKnocks[i] - mean; s += d * d; }
        double rms = Math.Sqrt(s / (model.Length - 2));
        _o.WriteLine($"rms {rms:F2} dB; model " + string.Join(" ", model.Select(v => v.ToString("F1"))));
        Assert.True(rms < 3.0, $"the knock drifted from the recording: {rms:F2} dB rms");
    }

    /// <summary>Band energies of the first <paramref name="len"/> samples, unwindowed (a strike at the start of a
    /// Hann window loses its top: see StruckSpike.Octaves).</summary>
    private static float[] Octaves(float[] x, int len)
    {
        len = Math.Min(len, x.Length);
        int n = 1; while (n < 2 * len) n <<= 1;
        var buf = new System.Numerics.Complex[n];
        for (int i = 0; i < len; i++) buf[i] = new System.Numerics.Complex(x[i], 0);
        Spectrum.Fft(buf);
        var e = new float[Spectrum.BandCount];
        for (int b = 0; b < e.Length; b++)
        {
            int k0 = (int)(Spectrum.BandEdges[b] * n / Rate), k1 = Math.Min(n / 2, (int)(Spectrum.BandEdges[b + 1] * n / Rate));
            double acc = 0;
            for (int k = k0; k < k1; k++) acc += buf[k].Real * buf[k].Real + buf[k].Imaginary * buf[k].Imaginary;
            e[b] = (float)acc;
        }
        return e;
    }

    private static float[] Shape(float[] e)
    {
        double total = e.Sum(v => (double)v);
        return e.Select(v => (float)(10 * Math.Log10(Math.Max(v / total, 1e-12)))).ToArray();
    }
}
