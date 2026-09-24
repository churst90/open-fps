using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The image-source solver held to its own physics, edge by edge.
///
/// Written against the mutants Stryker found surviving in ImageSource.cs (2026-09-24): the thresholds,
/// the diffuse taps, the Fresnel aperture, the second-order chain, the occlusion callbacks, the box
/// faces and the insert that keeps the strongest arrivals. Every expected number here comes from the
/// geometry — path difference over the speed of sound, distance ratio times what the surface kept,
/// the Lambert share of a rough patch, the first Fresnel zone — never from what the code printed.
/// </summary>
public class ImageSourceMutationTests
{
    private const float C = 343f;
    private static readonly float Lambda = 343f / ImageSource.FresnelReferenceHz;

    private static ReflectingSurface Face(Vector3 centre, Vector3 normal, Vector3 halfU, Vector3 halfV,
                                          float absorption = 0f, int id = 1, float scattering = 0f)
        => new(centre, normal, halfU, halfV, absorption, id, scattering);

    /// <summary>A face in the x = 0 plane facing +X, centred on the origin.</summary>
    private static ReflectingSurface WallX0(float halfY = 10f, float halfZ = 10f, float absorption = 0f,
                                            int id = 1, float scattering = 0f)
        => Face(Vector3.Zero, Vector3.UnitX, new Vector3(0, halfY, 0), new Vector3(0, 0, halfZ),
                absorption, id, scattering);

    /// <summary>A face in the plane x = <paramref name="x"/>, facing <paramref name="nx"/> along X.</summary>
    private static ReflectingSurface WallAtX(float x, float nx, float centreZ, float halfZ, float halfY = 20f,
                                             float absorption = 0f, int id = 1, float scattering = 0f,
                                             float centreY = 0f)
        => Face(new Vector3(x, centreY, centreZ), new Vector3(nx, 0, 0), new Vector3(0, halfY, 0),
                new Vector3(0, 0, halfZ), absorption, id, scattering);

    /// <summary>The Lambert share a patch of <paramref name="area"/> sends from source to listener, as
    /// an amplitude: sqrt(A·cosθs·cosθl·direct² / (π·rs²·rl²)), times what the surface returned.</summary>
    private static float LambertGain(float area, Vector3 source, Vector3 tap, Vector3 listener,
                                     Vector3 normal, float returned)
    {
        double rS = Vector3.Distance(source, tap), rL = Vector3.Distance(tap, listener);
        double cosS = Vector3.Dot(source - tap, normal) / rS;
        double cosL = Vector3.Dot(listener - tap, normal) / rL;
        double direct = Vector3.Distance(source, listener);
        double energy = area * cosS * cosL * direct * direct / (Math.PI * rS * rS * rL * rL);
        return (float)(Math.Sqrt(energy) * returned);
    }

    /// <summary>A speed of sound at which <paramref name="extraMetres"/> of extra path
    /// takes exactly <see cref="ImageSource.MinDelaySeconds"/> in float arithmetic.</summary>
    private static float SpeedGivingMinDelay(float extraMetres)
    {
        float up = extraMetres / ImageSource.MinDelaySeconds, down = up;
        for (int i = 0; i < 1000; i++)
        {
            if (extraMetres / up == ImageSource.MinDelaySeconds) return up;
            if (extraMetres / down == ImageSource.MinDelaySeconds) return down;
            up = MathF.BitIncrement(up);
            down = MathF.BitDecrement(down);
        }
        throw new InvalidOperationException("no speed of sound lands exactly on the threshold");
    }

    /// <summary>A listener offset D along Z at which direct/path is exactly <see cref="ImageSource.MinGain"/>,
    /// the source at <paramref name="source"/> and the image at <paramref name="image"/>.</summary>
    private static bool TryListenerAtMinGain(Vector3 source, Vector3 image, out Vector3 listener)
    {
        float dx = MathF.Abs(image.X - source.X);
        float d = dx * ImageSource.MinGain * 0.999f;
        float stop = dx * ImageSource.MinGain * 1.001f;
        for (; d < stop; d = MathF.BitIncrement(d))
        {
            var l = new Vector3(source.X, source.Y, source.Z + d);
            float direct = Vector3.Distance(source, l);
            float path = Vector3.Distance(image, l);
            if (direct / path == ImageSource.MinGain && path <= ImageSource.MaxPathLength)
            { listener = l; return true; }
        }
        listener = default;
        return false;
    }

    // ── The specular test's thresholds ──────────────────────────────────────────────────────────

    /// <summary>
    /// A bounce that lands exactly on the edge of a face is ON the face. The source and listener are
    /// placed so the bounce point is the origin, and the face is moved so its edge — in one case along
    /// its U axis, in the other along V — passes through that point.
    /// </summary>
    [Fact]
    public void ABounceExactlyOnTheEdgeOfAFaceStillReflects()
    {
        var source = new Vector3(8f, 0f, -4f);
        var listener = new Vector3(8f, 0f, 4f);
        Span<Reflection> into = stackalloc Reflection[4];

        var edgeInV = Face(new Vector3(0, 0, -2f), Vector3.UnitX, new Vector3(0, 10f, 0), new Vector3(0, 0, 2f));
        Assert.Equal(1, ImageSource.FirstOrder(new[] { edgeInV }, source, listener, C, into));
        Assert.Equal(Vector3.Zero, into[0].BouncePoint);

        var edgeInU = Face(new Vector3(0, -2f, 0), Vector3.UnitX, new Vector3(0, 2f, 0), new Vector3(0, 0, 10f));
        Assert.Equal(1, ImageSource.FirstOrder(new[] { edgeInU }, source, listener, C, into));
        Assert.Equal(Vector3.Zero, into[0].BouncePoint);
    }

    /// <summary>
    /// <see cref="ImageSource.MaxPathLength"/> is an inclusive limit. A 3-4-5 triangle scaled by 80
    /// gives a reflected path of exactly 400 m (kept, at the gain its 320 m direct path over 400 m
    /// says); moving the pair ten metres further from the wall makes it 412 m, and it is gone.
    /// </summary>
    [Fact]
    public void AMirrorReflectionIsKeptAtExactlyTheMaximumPathAndDroppedPastIt()
    {
        var wall = WallX0(halfY: 50f, halfZ: 250f, absorption: 0.02f);
        Span<Reflection> into = stackalloc Reflection[4];

        int n = ImageSource.FirstOrder(new[] { wall }, new Vector3(120f, 0, -160f), new Vector3(120f, 0, 160f), C, into);
        Assert.Equal(1, n);
        Assert.Equal(400f, into[0].PathLength);
        // The Fresnel zone at 200 m + 200 m is sqrt(0.49 * 100) = 7 m: a 50 m face covers it fully.
        Assert.Equal(320f / 400f * 0.98f, into[0].Gain, 4);
        Assert.Equal(80f / C, into[0].DelaySeconds, 5);

        Assert.Equal(0, ImageSource.FirstOrder(new[] { wall }, new Vector3(130f, 0, -160f),
                                               new Vector3(130f, 0, 160f), C, into));
    }

    /// <summary>
    /// The same limit on the scattered share. A single tap at the centre of a fully rough face, 200 m
    /// from both source and listener, travels exactly 400 m and is kept at its Lambert gain; ten metres
    /// further out it travels 412 m and there is nothing.
    /// </summary>
    [Fact]
    public void ADiffuseTapIsKeptAtExactlyTheMaximumPathAndDroppedPastIt()
    {
        var rough = WallX0(halfY: 50f, halfZ: 250f, absorption: 0.02f, scattering: 1f);
        var source = new Vector3(120f, 0, -160f);
        var listener = new Vector3(120f, 0, 160f);
        Span<Reflection> into = stackalloc Reflection[4];

        int n = ImageSource.FirstOrder(new[] { rough }, source, listener, C, into, null, diffuseTaps: 1);
        Assert.Equal(1, n);
        Assert.True(into[0].IsDiffuse);
        Assert.Equal(Vector3.Zero, into[0].BouncePoint);
        Assert.Equal(400f, into[0].PathLength);
        float area = 4f * 50f * 250f;
        Assert.Equal(LambertGain(area, source, Vector3.Zero, listener, Vector3.UnitX, 0.98f), into[0].Gain, 3);

        Assert.Equal(0, ImageSource.FirstOrder(new[] { rough }, new Vector3(130f, 0, -160f),
                                               new Vector3(130f, 0, 160f), C, into, null, 1));
    }

    /// <summary>
    /// <see cref="ImageSource.MinGain"/> is inclusive. With a lossless face much bigger than its
    /// Fresnel zone the gain is the distance ratio alone, so a listener is placed where direct/path
    /// is exactly 0.02 — and that reflection is kept.
    /// </summary>
    [Fact]
    public void AMirrorReflectionExactlyAtTheMinimumGainIsKept()
    {
        var wall = WallX0(halfY: 20f, halfZ: 100f);
        var source = new Vector3(50f, 0, 0);
        Assert.True(TryListenerAtMinGain(source, new Vector3(-50f, 0, 0), out var listener));

        Span<Reflection> into = stackalloc Reflection[4];
        Assert.Equal(1, ImageSource.FirstOrder(new[] { wall }, source, listener, C, into));
        Assert.Equal(ImageSource.MinGain, into[0].Gain);
    }

    /// <summary>
    /// <see cref="ImageSource.MinDelaySeconds"/> is inclusive, for the mirror image and for a diffuse
    /// tap. A 3-4-5 triangle scaled by 2.0625: source and listener 6.1875 m apart and 4.125 m off the
    /// wall, a reflected path of 10.3125 m, 4.125 m extra — which at 343.75 m/s is exactly 12 ms.
    /// </summary>
    [Fact]
    public void AReflectionExactlyAtTheFusionThresholdIsKept()
    {
        var source = new Vector3(4.125f, 0, -3.09375f);
        var listener = new Vector3(4.125f, 0, 3.09375f);
        float c = SpeedGivingMinDelay(4.125f);
        Assert.Equal(343.75f, c);
        Span<Reflection> into = stackalloc Reflection[4];

        Assert.Equal(1, ImageSource.FirstOrder(new[] { WallX0() }, source, listener, c, into));
        Assert.Equal(ImageSource.MinDelaySeconds, into[0].DelaySeconds);

        // A fully rough face: no mirror image at all, one tap at its centre, 5.15625 m each way.
        Assert.Equal(1, ImageSource.FirstOrder(new[] { WallX0(scattering: 1f) }, source, listener, c, into, null, 1));
        Assert.True(into[0].IsDiffuse);
        Assert.Equal(ImageSource.MinDelaySeconds, into[0].DelaySeconds);
    }

    // ── Occlusion ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A mirror reflection needs BOTH legs clear. Blocking only source-to-wall, or only wall-to-ear,
    /// removes it; blocking neither keeps it.
    /// </summary>
    [Fact]
    public void AMirrorReflectionNeedsBothLegsClear()
    {
        var source = new Vector3(8f, 0, -4f);
        var listener = new Vector3(8f, 0, 4f);
        var walls = new[] { WallX0() };
        Span<Reflection> into = stackalloc Reflection[4];

        Assert.Equal(1, ImageSource.FirstOrder(walls, source, listener, C, into, (a, b) => false));
        Assert.Equal(0, ImageSource.FirstOrder(walls, source, listener, C, into, (a, b) => a == source));
        Assert.Equal(0, ImageSource.FirstOrder(walls, source, listener, C, into, (a, b) => b == listener));
        Assert.Equal(0, ImageSource.FirstOrder(walls, source, listener, C, into, (a, b) => true));
    }

    /// <summary>The same for the scattered share: a tap the source cannot see, or the ear cannot,
    /// sends nothing.</summary>
    [Fact]
    public void ADiffuseTapNeedsBothLegsClear()
    {
        var source = new Vector3(8f, 0, -4f);
        var listener = new Vector3(8f, 0, 4f);
        var walls = new[] { WallX0(scattering: 1f) };
        Span<Reflection> into = stackalloc Reflection[4];

        Assert.Equal(1, ImageSource.FirstOrder(walls, source, listener, C, into, (a, b) => false, 1));
        Assert.Equal(0, ImageSource.FirstOrder(walls, source, listener, C, into, (a, b) => a == source, 1));
        Assert.Equal(0, ImageSource.FirstOrder(walls, source, listener, C, into, (a, b) => b == listener, 1));
        Assert.Equal(0, ImageSource.FirstOrder(walls, source, listener, C, into, (a, b) => true, 1));
    }

    // ── The scattered share ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A face scattering one per cent or less is a mirror: it sends no diffuse taps even when taps are
    /// asked for — though at this range a 60 m face at 1 % would still be loud enough to hear (its
    /// Lambert share times 0.01 is over MinGain), which is what makes the threshold matter. At 2 % the
    /// tap is there, at its Lambert gain. (Source and listener 8 m apart, so the 60 m face is not
    /// "much larger than the scene"; one that is scatters from round the bounce point instead — see
    /// GroundScatterTests.)
    /// </summary>
    [Fact]
    public void ANearlySmoothFaceSendsNoDiffuseTaps()
    {
        var source = new Vector3(5f, 0, -2f);
        var listener = new Vector3(5f, 0, 6f);
        Span<Reflection> into = stackalloc Reflection[4];

        float lambertAtOnePercent = LambertGain(3600f, source, Vector3.Zero, listener, Vector3.UnitX, 0.01f);
        Assert.True(lambertAtOnePercent > ImageSource.MinGain);

        int n = ImageSource.FirstOrder(new[] { WallX0(30f, 30f, scattering: 0.01f) }, source, listener, C, into, null, 1);
        for (int i = 0; i < n; i++) Assert.False(into[i].IsDiffuse);

        n = ImageSource.FirstOrder(new[] { WallX0(30f, 30f, scattering: 0.02f) }, source, listener, C, into, null, 1);
        var diffuse = into[..n].ToArray().Where(r => r.IsDiffuse).ToArray();
        var tap = Assert.Single(diffuse);
        Assert.Equal(LambertGain(3600f, source, Vector3.Zero, listener, Vector3.UnitX, 0.02f), tap.Gain, 4);
    }

    /// <summary>
    /// Three taps on a face longer in V than in U sit at -0.9, 0 and +0.9 of its V half-extent, each
    /// arriving (rS + rL - direct)/c late at its Lambert gain for a third of the area, each with its
    /// own stable identity, and every one of them on the face.
    /// </summary>
    [Fact]
    public void DiffuseTapsSpreadEvenlyAlongTheLongerAxis()
    {
        var halfU = new Vector3(0, 4f, 0);
        var halfV = new Vector3(0, 0, 5f);
        CheckTaps(Face(Vector3.Zero, Vector3.UnitX, halfU, halfV, 0f, 3, 1f), halfV);
    }

    /// <summary>The same with the longer axis in U: the taps follow it.</summary>
    [Fact]
    public void DiffuseTapsFollowUWhenUIsTheLongerAxis()
    {
        var halfU = new Vector3(0, 0, 5f);
        var halfV = new Vector3(0, 4f, 0);
        CheckTaps(Face(Vector3.Zero, Vector3.UnitX, halfU, halfV, 0f, 3, 1f), halfU);
    }

    private static void CheckTaps(ReflectingSurface face, Vector3 along)
    {
        // Near the +along end, so a tap past the end of the face would be loud enough to be kept.
        var source = new Vector3(6f, 0, 6f);
        var listener = new Vector3(6f, 0, 12f);
        float direct = Vector3.Distance(source, listener);
        Span<Reflection> into = stackalloc Reflection[8];

        int n = ImageSource.FirstOrder(new[] { face }, source, listener, C, into, null, diffuseTaps: 3);
        Assert.Equal(3, n);

        float area = 4f * face.HalfU.Length() * face.HalfV.Length();
        float[] fractions = { -0.9f, 0f, 0.9f };
        for (int t = 0; t < 3; t++)
        {
            Vector3 expected = face.Centre + along * fractions[t];
            int id = face.SurfaceId * 397 + t + 1;
            var r = into[..n].ToArray().Single(x => x.SurfaceId == id);
            Assert.True(r.IsDiffuse);
            Assert.True(Vector3.Distance(expected, r.BouncePoint) < 1e-4f, $"tap {t} at {r.BouncePoint}, expected {expected}");
            Assert.Equal(r.BouncePoint, r.ApparentPosition);
            float path = Vector3.Distance(source, expected) + Vector3.Distance(expected, listener);
            Assert.Equal(path, r.PathLength, 3);
            Assert.Equal((path - direct) / C, r.DelaySeconds, 5);
            Assert.Equal(LambertGain(area / 3f, source, expected, listener, face.Normal, 1f), r.Gain, 4);
        }
    }

    /// <summary>One tap is the centre of the face, not a point off its end.</summary>
    [Fact]
    public void ASingleDiffuseTapSitsAtTheCentreOfTheFace()
    {
        var face = Face(new Vector3(0, 1f, 2f), Vector3.UnitX, new Vector3(0, 4f, 0), new Vector3(0, 0, 5f), 0f, 3, 1f);
        var source = new Vector3(6f, 0, -2f);
        var listener = new Vector3(6f, 0, 6f);
        Span<Reflection> into = stackalloc Reflection[4];

        Assert.Equal(1, ImageSource.FirstOrder(new[] { face }, source, listener, C, into, null, 1));
        Assert.Equal(face.Centre, into[0].BouncePoint);
        Assert.Equal(3 * 397 + 1, into[0].SurfaceId);
        Assert.Equal(LambertGain(80f, source, face.Centre, listener, Vector3.UnitX, 1f), into[0].Gain, 4);
    }

    /// <summary>A diffuse tap arriving 7 ms after the direct sound fuses with it; it gets no voice,
    /// however loud a big rough wall two metres away makes it.</summary>
    [Fact]
    public void ADiffuseTapInsideTheFusionWindowIsDropped()
    {
        var source = new Vector3(2f, 0, -1f);
        var listener = new Vector3(2f, 0, 1f);
        // 2.236 + 2.236 - 2 = 2.47 m extra: 7.2 ms.
        Assert.True(LambertGain(400f, source, Vector3.Zero, listener, Vector3.UnitX, 1f) > ImageSource.MinGain);
        Span<Reflection> into = stackalloc Reflection[4];
        Assert.Equal(0, ImageSource.FirstOrder(new[] { WallX0(scattering: 1f) }, source, listener, C, into, null, 1));
    }

    /// <summary>A two-metre rough panel thirty metres away scatters far less than MinGain back: no tap.</summary>
    [Fact]
    public void AQuietDiffuseTapIsDropped()
    {
        var source = new Vector3(30f, 0, -5f);
        var listener = new Vector3(30f, 0, 5f);
        Assert.True(LambertGain(4f, source, Vector3.Zero, listener, Vector3.UnitX, 1f) < ImageSource.MinGain);
        Span<Reflection> into = stackalloc Reflection[4];
        Assert.Equal(0, ImageSource.FirstOrder(new[] { WallX0(1f, 1f, scattering: 1f) }, source, listener, C, into, null, 1));
    }

    /// <summary>
    /// Two taps 0.9 m apart with identical path lengths are two arrivals, not one: the duplicate test
    /// only merges arrivals within <c>DuplicateMetres</c> (0.75 m) of each other.
    /// </summary>
    [Fact]
    public void TapsFurtherApartThanTheDuplicateDistanceAreBothKept()
    {
        // A 1 m x 0.6 m rough panel; the taps sit at +-0.45 m along its longer (Y) axis, symmetric
        // about the line the source and listener stand on, so their paths are equal.
        var panel = Face(Vector3.Zero, Vector3.UnitX, new Vector3(0, 0.5f, 0), new Vector3(0, 0, 0.3f), 0f, 1, 1f);
        var source = new Vector3(2.5f, 0, 0);
        var listener = new Vector3(10f, 0, 0);
        Span<Reflection> into = stackalloc Reflection[4];

        int n = ImageSource.FirstOrder(new[] { panel }, source, listener, C, into, null, 2);
        Assert.Equal(2, n);
        Assert.Equal(0.9f, Vector3.Distance(into[0].BouncePoint, into[1].BouncePoint), 4);
        Assert.Equal(into[0].PathLength, into[1].PathLength, 4);
    }

    // ── The Fresnel aperture ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A panel smaller than the first Fresnel zone returns the square root of the share of the zone it
    /// covers. Source 5.66 m and listener 17 m from the bounce point: the zone radius is
    /// sqrt(λ·d1·d2/(d1+d2)) = 1.44 m at 700 Hz, so a 0.6 m by 1 m panel covers 0.21 by 0.35 of it and
    /// returns sqrt(0.21 x 0.35) = 0.27 of what a big wall at the same place does.
    /// </summary>
    [Fact]
    public void APanelSmallerThanTheFresnelZoneReturnsItsShareOfIt()
    {
        var source = new Vector3(4f, 0, -4f);
        var listener = new Vector3(12f, 0, 12f);
        var panel = Face(Vector3.Zero, Vector3.UnitX, new Vector3(0, 0.3f, 0), new Vector3(0, 0, 0.5f), 0.02f);
        var wall = WallX0(absorption: 0.02f);
        Span<Reflection> into = stackalloc Reflection[4];

        Assert.Equal(1, ImageSource.FirstOrder(new[] { wall }, source, listener, C, into));
        float big = into[0].Gain;
        Assert.True(Vector3.Distance(into[0].BouncePoint, Vector3.Zero) < 1e-4f);

        float d1 = Vector3.Distance(source, Vector3.Zero), d2 = Vector3.Distance(Vector3.Zero, listener);
        float zone = MathF.Sqrt(Lambda * d1 * d2 / (d1 + d2));
        float aperture = MathF.Sqrt(MathF.Min(1f, 0.3f / zone) * MathF.Min(1f, 0.5f / zone));
        float direct = Vector3.Distance(source, listener);
        float path = Vector3.Distance(new Vector3(-4f, 0, -4f), listener);
        Assert.Equal(direct / path * 0.98f, big, 4);

        Assert.Equal(1, ImageSource.FirstOrder(new[] { panel }, source, listener, C, into));
        Assert.Equal(direct / path * 0.98f * aperture, into[0].Gain, 4);
        Assert.InRange(aperture, 0.2f, 0.35f);
    }

    // ── Keeping the strongest ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The near wall's reflection is louder than the far wall's, and it comes out first whichever
    /// order the walls were found in; with room for only one, the near one is kept either way.
    /// </summary>
    [Fact]
    public void TheStrongestReflectionIsFirstAndWinsTheLastSlot()
    {
        var source = new Vector3(8f, 0, -4f);
        var listener = new Vector3(8f, 0, 4f);
        var near = WallX0(id: 1);                                  // 8 m away: gain 0.45
        var far = WallAtX(30f, -1f, 0f, 20f, id: 2);               // 22 m away: gain 0.18
        Span<Reflection> into = stackalloc Reflection[4];

        Assert.Equal(2, ImageSource.FirstOrder(new[] { far, near }, source, listener, C, into));
        Assert.Equal(1, into[0].SurfaceId);
        Assert.Equal(2, into[1].SurfaceId);
        Assert.True(into[0].Gain > into[1].Gain);

        Span<Reflection> one = stackalloc Reflection[1];
        Assert.Equal(1, ImageSource.FirstOrder(new[] { far, near }, source, listener, C, one));
        Assert.Equal(1, one[0].SurfaceId);
        Assert.Equal(1, ImageSource.FirstOrder(new[] { near, far }, source, listener, C, one));
        Assert.Equal(1, one[0].SurfaceId);
    }

    /// <summary>A newcomer has to be LOUDER to take a full slot: two exactly equal walls either side
    /// of a symmetric pair, and the one found first keeps it.</summary>
    [Fact]
    public void AnEquallyLoudNewcomerDoesNotDisplaceTheArrivalAlreadyHeld()
    {
        var source = new Vector3(8f, 0, -4f);
        var listener = new Vector3(8f, 0, 4f);
        var left = WallX0(id: 7);
        var right = WallAtX(16f, -1f, 0f, 10f, halfY: 10f, id: 9);
        Span<Reflection> two = stackalloc Reflection[2];
        Assert.Equal(2, ImageSource.FirstOrder(new[] { left, right }, source, listener, C, two));
        Assert.Equal(two[0].Gain, two[1].Gain);

        Span<Reflection> one = stackalloc Reflection[1];
        Assert.Equal(1, ImageSource.FirstOrder(new[] { left, right }, source, listener, C, one));
        Assert.Equal(7, one[0].SurfaceId);
    }

    // ── Box faces ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An axis-aligned box is six faces, each at the centre plus the half size along its axis, with
    /// its normal pointing OUT, its two half-extents in the plane, the material, and ids baseId..+5.
    /// A span too short for six gets nothing.
    /// </summary>
    [Fact]
    public void AnAxisAlignedBoxHasSixOutwardFaces()
    {
        var centre = new Vector3(1f, 2f, 3f);
        var size = new Vector3(4f, 6f, 8f);
        var into = new ReflectingSurface[6];
        Assert.Equal(6, ImageSource.FacesOfBox(centre, size, 0.3f, 10, into, 0.4f));

        var x = new Vector3(2f, 0, 0); var y = new Vector3(0, 3f, 0); var z = new Vector3(0, 0, 4f);
        var expected = new[]
        {
            (centre + x, Vector3.UnitX, y, z), (centre - x, -Vector3.UnitX, y, z),
            (centre + y, Vector3.UnitY, x, z), (centre - y, -Vector3.UnitY, x, z),
            (centre + z, Vector3.UnitZ, x, y), (centre - z, -Vector3.UnitZ, x, y),
        };
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(expected[i].Item1, into[i].Centre);
            Assert.Equal(expected[i].Item2, into[i].Normal);
            Assert.Equal(expected[i].Item3, into[i].HalfU);
            Assert.Equal(expected[i].Item4, into[i].HalfV);
            Assert.Equal(10 + i, into[i].SurfaceId);
            Assert.Equal(0.3f, into[i].Absorption);
            Assert.Equal(0.4f, into[i].Scattering);
            Assert.True(Vector3.Dot(into[i].Centre - centre, into[i].Normal) > 0f);
        }

        Assert.Equal(0, ImageSource.FacesOfBox(centre, size, 0.3f, 10, new ReflectingSurface[5]));
    }

    /// <summary>
    /// A box turned 30 degrees about the vertical has its faces turned with it: the +X face's normal is
    /// (cos 30, 0, -sin 30), it sits half the box's length out along that normal, and its in-plane
    /// half-extents are the turned Y and Z half sizes. Ids are baseId..+5 in the same order.
    /// </summary>
    [Fact]
    public void ARotatedBoxHasItsFacesRotatedWithIt()
    {
        float th = MathF.PI / 6f;
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, th);
        var centre = new Vector3(5f, 1f, -2f);
        var size = new Vector3(4f, 6f, 8f);
        var into = new ReflectingSurface[6];
        Assert.Equal(6, ImageSource.FacesOfBox(centre, size, q, 0.1f, 20, into, 0.2f));

        var ux = new Vector3(MathF.Cos(th), 0, -MathF.Sin(th));
        var uy = Vector3.UnitY;
        var uz = new Vector3(MathF.Sin(th), 0, MathF.Cos(th));
        var x = ux * 2f; var y = uy * 3f; var z = uz * 4f;
        var expected = new[]
        {
            (centre + x, ux, y, z), (centre - x, -ux, y, z),
            (centre + y, uy, x, z), (centre - y, -uy, x, z),
            (centre + z, uz, x, y), (centre - z, -uz, x, y),
        };
        for (int i = 0; i < 6; i++)
        {
            AssertNear(expected[i].Item1, into[i].Centre);
            AssertNear(expected[i].Item2, into[i].Normal);
            AssertNear(expected[i].Item3, into[i].HalfU);
            AssertNear(expected[i].Item4, into[i].HalfV);
            Assert.Equal(20 + i, into[i].SurfaceId);
            Assert.Equal(0.1f, into[i].Absorption);
            Assert.Equal(0.2f, into[i].Scattering);
        }

        Assert.Equal(0, ImageSource.FacesOfBox(centre, size, q, 0.1f, 20, new ReflectingSurface[5]));
    }

    /// <summary>A box with no depth still hands back finite normals — never a NaN that would poison
    /// every reflection solved against it.</summary>
    [Fact]
    public void AFlatBoxStillHasFiniteNormals()
    {
        var into = new ReflectingSurface[6];
        ImageSource.FacesOfBox(Vector3.Zero, new Vector3(4f, 6f, 0f), Quaternion.Identity, 0f, 0, into);
        foreach (var f in into)
        {
            Assert.False(float.IsNaN(f.Normal.X) || float.IsNaN(f.Normal.Y) || float.IsNaN(f.Normal.Z));
            Assert.Equal(1f, f.Normal.Length(), 4);
        }
    }

    private static void AssertNear(Vector3 expected, Vector3 actual)
        => Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");

    // ── Second order ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A street 10 m wide: wall A in x = 0 facing +X, wall B in x = 10 facing -X, source at x = 5 and
    /// listener 20 m down the street. Each of A-then-B and B-then-A has its second image 20 m across,
    /// so the path is sqrt(20² + 20²); the gain is direct/path times what BOTH walls kept; the delay is
    /// the extra over c; the identity is A·31 + B for the ordered pair; the roughness is the rougher
    /// wall's; and it is a mirror image, not a diffuse tap.
    /// </summary>
    [Fact]
    public void ASecondOrderReflectionHasThePathGainAndIdentityTheTwoWallsGiveIt()
    {
        var a = WallAtX(0f, 1f, 10f, 20f, absorption: 0.2f, id: 3, scattering: 0.1f);
        var b = WallAtX(10f, -1f, 10f, 20f, absorption: 0.5f, id: 5, scattering: 0.6f);
        var source = new Vector3(5f, 0, 0);
        var listener = new Vector3(5f, 0, 20f);
        Span<Reflection> into = stackalloc Reflection[4];

        int n = ImageSource.SecondOrder(new[] { a, b }, source, listener, C, into);
        Assert.Equal(2, n);

        float path = MathF.Sqrt(20f * 20f + 20f * 20f);
        foreach (var r in into[..n].ToArray())
        {
            Assert.Equal(path, r.PathLength, 3);
            Assert.Equal((path - 20f) / C, r.DelaySeconds, 5);
            Assert.Equal(20f / path * 0.8f * 0.5f, r.Gain, 4);
            Assert.False(r.IsDiffuse);
            Assert.Equal(0.6f, r.Scattering);
        }

        var ab = into[..n].ToArray().Single(r => r.SurfaceId == 3 * 31 + 5);
        var ba = into[..n].ToArray().Single(r => r.SurfaceId == 5 * 31 + 3);
        // A then B: imaged to x = -5 through A, then to x = 25 through B; the last bounce is on B,
        // three quarters of the way down the street.
        AssertNear(new Vector3(25f, 0, 0), ab.ApparentPosition);
        AssertNear(new Vector3(10f, 0, 15f), ab.BouncePoint);
        AssertNear(new Vector3(-15f, 0, 0), ba.ApparentPosition);
        AssertNear(new Vector3(0f, 0, 15f), ba.BouncePoint);
    }

    /// <summary>
    /// The path limit is inclusive for the second order too. Walls 120 m apart and a listener 320 m
    /// down the street put the second image 240 m across: a 240-320-400 triangle, kept. At 330 m the
    /// path is 408 m, and neither chain is reported.
    /// </summary>
    [Fact]
    public void ASecondOrderReflectionIsKeptAtExactlyTheMaximumPathAndDroppedPastIt()
    {
        var a = WallAtX(-60f, 1f, 160f, 200f);
        var b = WallAtX(60f, -1f, 160f, 200f);
        Span<Reflection> into = stackalloc Reflection[4];

        int n = ImageSource.SecondOrder(new[] { a, b }, Vector3.Zero, new Vector3(0, 0, 320f), C, into);
        Assert.Equal(2, n);
        Assert.Equal(400f, into[0].PathLength);
        Assert.Equal(400f, into[1].PathLength);

        Assert.Equal(0, ImageSource.SecondOrder(new[] { a, b }, Vector3.Zero, new Vector3(0, 0, 330f), C, into));
    }

    /// <summary>In a corridor two metres wide the second-order path is only 2.5 m longer than the
    /// direct one — 7 ms, fused with it — so it is not a separate arrival.</summary>
    [Fact]
    public void ASecondOrderReflectionInsideTheFusionWindowIsDropped()
    {
        var a = WallAtX(-1f, 1f, 1f, 5f);
        var b = WallAtX(1f, -1f, 1f, 5f);
        Span<Reflection> into = stackalloc Reflection[4];
        Assert.Equal(0, ImageSource.SecondOrder(new[] { a, b }, Vector3.Zero, new Vector3(0, 0, 2f), C, into));
    }

    /// <summary>Two walls that each keep five per cent: the double bounce keeps a quarter of one per
    /// cent, far under MinGain, and there is nothing.</summary>
    [Fact]
    public void AnAbsorbedSecondOrderReflectionIsDropped()
    {
        var a = WallAtX(-5f, 1f, 10f, 20f, absorption: 0.95f);
        var b = WallAtX(5f, -1f, 10f, 20f, absorption: 0.95f);
        Span<Reflection> into = stackalloc Reflection[4];
        Assert.Equal(0, ImageSource.SecondOrder(new[] { a, b }, Vector3.Zero, new Vector3(0, 0, 20f), C, into));
    }

    /// <summary>MinGain is inclusive for the second order: lossless walls, a listener placed so that
    /// direct/path is exactly 0.02.</summary>
    [Fact]
    public void ASecondOrderReflectionExactlyAtTheMinimumGainIsKept()
    {
        var a = WallAtX(0f, 1f, 0f, 100f);
        var b = WallAtX(50f, -1f, 0f, 100f);
        var source = new Vector3(10f, 0, 0);
        // A then B images the source to x = 110: a hundred metres across from the listener's x.
        Assert.True(TryListenerAtMinGain(source, new Vector3(110f, 0, 0), out var listener));
        Span<Reflection> into = stackalloc Reflection[4];
        int n = ImageSource.SecondOrder(new[] { a, b }, source, listener, C, into);
        Assert.Equal(2, n);
        Assert.Equal(ImageSource.MinGain, into[0].Gain);
    }

    /// <summary>MinDelaySeconds is inclusive for the second order: walls 4.125 m apart and the listener
    /// 6.1875 m down the street put the double image 8.25 m across — the same scaled 3-4-5 triangle,
    /// a 10.3125 m path, 4.125 m extra, exactly 12 ms at 343.75 m/s.</summary>
    [Fact]
    public void ASecondOrderReflectionExactlyAtTheFusionThresholdIsKept()
    {
        var a = WallAtX(-2.0625f, 1f, 3f, 20f);
        var b = WallAtX(2.0625f, -1f, 3f, 20f);
        float c = SpeedGivingMinDelay(4.125f);
        Span<Reflection> into = stackalloc Reflection[4];
        int n = ImageSource.SecondOrder(new[] { a, b }, Vector3.Zero, new Vector3(0, 0, 6.1875f), c, into);
        Assert.Equal(2, n);
        Assert.Equal(10.3125f, into[0].PathLength);
        Assert.Equal(ImageSource.MinDelaySeconds, into[0].DelaySeconds);
    }

    /// <summary>
    /// A double bounce has three legs and every one must be clear: blocking only the first, only the
    /// crossing between the walls, or only the last removes it.
    /// </summary>
    [Fact]
    public void ASecondOrderReflectionNeedsAllThreeLegsClear()
    {
        var a = WallAtX(0f, 1f, 10f, 20f, id: 3);
        var b = WallAtX(10f, -1f, 10f, 20f, id: 5);
        var walls = new[] { a, b };
        var source = new Vector3(5f, 0, 0);
        var listener = new Vector3(5f, 0, 20f);
        Span<Reflection> into = stackalloc Reflection[4];

        Assert.Equal(2, ImageSource.SecondOrder(walls, source, listener, C, into, (p, q) => false));
        Assert.Equal(0, ImageSource.SecondOrder(walls, source, listener, C, into, (p, q) => p == source));
        Assert.Equal(0, ImageSource.SecondOrder(walls, source, listener, C, into, (p, q) => p != source && q != listener));
        Assert.Equal(0, ImageSource.SecondOrder(walls, source, listener, C, into, (p, q) => q == listener));
        Assert.Equal(0, ImageSource.SecondOrder(walls, source, listener, C, into, (p, q) => true));
    }

    /// <summary>
    /// Both bounces must land on their faces. In the 10 m street A-then-B strikes B 15 m down and A
    /// 5 m down; B-then-A the other way round. Shorten either wall to the 8-12 m stretch and each
    /// chain misses one of its faces, so there is no double bounce at all; full-length walls give two.
    /// </summary>
    [Fact]
    public void ASecondOrderReflectionNeedsBothBouncesOnTheirFaces()
    {
        var source = new Vector3(5f, 0, 0);
        var listener = new Vector3(5f, 0, 20f);
        var longA = WallAtX(0f, 1f, 10f, 20f, id: 1);
        var longB = WallAtX(10f, -1f, 10f, 20f, id: 2);
        var shortA = WallAtX(0f, 1f, 10f, 2f, id: 1);
        var shortB = WallAtX(10f, -1f, 10f, 2f, id: 2);
        Span<Reflection> into = stackalloc Reflection[4];

        Assert.Equal(2, ImageSource.SecondOrder(new[] { longA, longB }, source, listener, C, into));
        Assert.Equal(0, ImageSource.SecondOrder(new[] { longA, shortB }, source, listener, C, into));
        Assert.Equal(0, ImageSource.SecondOrder(new[] { shortA, longB }, source, listener, C, into));
    }

    /// <summary>
    /// The on-the-face test is inclusive for the second order. In an 8 m street with the listener 16 m
    /// down, A-then-B strikes B at exactly (4, 0, 12): a B spanning z 8..12 has that point on its V
    /// edge, and a B whose U edge is at y = 0 has every bounce on it on that edge.
    /// </summary>
    [Fact]
    public void ASecondOrderBounceExactlyOnTheEdgeOfAFaceStillReflects()
    {
        var a = WallAtX(-4f, 1f, 8f, 20f, id: 1);
        var listener = new Vector3(0, 0, 16f);
        Span<Reflection> into = stackalloc Reflection[4];

        var edgeInV = Face(new Vector3(4f, 0, 10f), -Vector3.UnitX, new Vector3(0, 10f, 0), new Vector3(0, 0, 2f), 0f, 2);
        Assert.Equal(1, ImageSource.SecondOrder(new[] { a, edgeInV }, Vector3.Zero, listener, C, into));
        Assert.Equal(new Vector3(4f, 0, 12f), into[0].BouncePoint);

        var edgeInU = Face(new Vector3(4f, -2f, 12f), -Vector3.UnitX, new Vector3(0, 2f, 0), new Vector3(0, 0, 10f), 0f, 2);
        Assert.Equal(2, ImageSource.SecondOrder(new[] { a, edgeInU }, Vector3.Zero, listener, C, into));
    }

    /// <summary>
    /// In a corner — A in x = 0 facing +X, B in z = 0 facing +Z and running on behind A — the chain
    /// A-then-B would need its first leg to reach B at a point behind A (x = -2.6), which is not a
    /// crossing of A at all. Only B-then-A is a path: off B at (2.6, 0, 0), off A at (0, 0, 5.8), the
    /// double image at (-4, 0, -3).
    /// </summary>
    [Fact]
    public void InACornerOnlyTheChainThatCrossesBothFacesIsAPath()
    {
        var a = Face(new Vector3(0, 0, 5f), Vector3.UnitX, new Vector3(0, 10f, 0), new Vector3(0, 0, 10f), 0f, 1);
        var b = Face(Vector3.Zero, Vector3.UnitZ, new Vector3(20f, 0, 0), new Vector3(0, 10f, 0), 0f, 2);
        var source = new Vector3(4f, 0, 3f);
        var listener = new Vector3(1f, 0, 8f);
        Span<Reflection> into = stackalloc Reflection[4];

        int n = ImageSource.SecondOrder(new[] { a, b }, source, listener, C, into);
        Assert.Equal(1, n);
        Assert.Equal(2 * 31 + 1, into[0].SurfaceId);
        AssertNear(new Vector3(0, 0, 5.8f), into[0].BouncePoint);
        AssertNear(new Vector3(-4f, 0, -3f), into[0].ApparentPosition);
        Assert.Equal(MathF.Sqrt(25f + 121f), into[0].PathLength, 3);
    }

    /// <summary>A source BEHIND wall A cannot bounce off A's front: with the source and listener both
    /// behind A and in front of B, there is no second-order path through A.</summary>
    [Fact]
    public void ASourceBehindTheFirstWallHasNoChainThroughIt()
    {
        var a = Face(new Vector3(0, 0, 5f), Vector3.UnitX, new Vector3(0, 10f, 0), new Vector3(0, 0, 10f), 0f, 1);
        var b = Face(Vector3.Zero, Vector3.UnitZ, new Vector3(20f, 0, 0), new Vector3(0, 10f, 0), 0f, 2);
        Span<Reflection> into = stackalloc Reflection[4];
        Assert.Equal(0, ImageSource.SecondOrder(new[] { a, b }, new Vector3(-5f, 0, 5f), new Vector3(-10f, 0, 3f), C, into));
    }

    /// <summary>A second wall turned AWAY from the first image and the listener is no mirror for
    /// them: B faces -Z while everything stands at +Z, and there is no double bounce.</summary>
    [Fact]
    public void ASecondWallFacingAwayGivesNoChain()
    {
        var a = Face(new Vector3(0, 0, 5f), Vector3.UnitX, new Vector3(0, 10f, 0), new Vector3(0, 0, 10f), 0f, 1);
        var b = Face(Vector3.Zero, -Vector3.UnitZ, new Vector3(20f, 0, 0), new Vector3(0, 10f, 0), 0f, 2);
        Span<Reflection> into = stackalloc Reflection[4];
        Assert.Equal(0, ImageSource.SecondOrder(new[] { a, b }, new Vector3(4f, 0, 3f), new Vector3(8f, 0, 2f), C, into));
    }
}
