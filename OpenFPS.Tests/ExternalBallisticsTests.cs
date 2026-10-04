using System;
using System.Numerics;
using OpenFPS.Common;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The flown bullet against the published figures for the round the M700 fires.
///
/// The reference is Federal's own table for Gold Medal Match 168 grain Sierra MatchKing (GM308M,
/// federalpremium.com, read 2026-10-04): 2650 ft/s, G7 0.224, a 100-yard zero with the sight 1.5
/// inches over the bore, a standard day. Velocity 2460 ft/s at 100 yards, 2103 at 300 and 1778 at 500;
/// drop 4.3 inches at 200 yards and 15.3 at 300; drift in a 10 mph full-value crosswind 7.4 inches at
/// 300 yards and 22.0 at 500. Federal's page did not give a clean drop past 300 yards, so 600 m is held
/// by the time of flight and by the drop that time of flight implies.
///
/// The drop matches to a tenth of an inch. The velocity runs a little under Federal's past 300 yards
/// (1735 ft/s at 500 against 1778, 2.4 per cent) and the drift a little over (23.6 inches against 22):
/// Federal's tables are computed from the G1 coefficient, 0.462, whose drag shape is a flat-based
/// bullet's and under-reads a boat-tail's slowing once it is well below Mach 2. Solvers that fly the G7
/// number, as this does, read the same way. The tolerances are three and nine per cent for that.
/// </summary>
public class ExternalBallisticsTests
{
    private readonly ITestOutputHelper _out;
    public ExternalBallisticsTests(ITestOutputHelper output) => _out = output;

    private static readonly WeaponDefinition Rifle = WeaponRegistry.M700;
    private const float Yard = 0.9144f, Inch = 0.0254f, FootPerSecond = 0.3048f;
    private const float SightHeight = 1.5f * Inch;

    private static FlightPoint At(float metres, float zeroMetres, Vector3 wind = default)
        => ExternalBallistics.Fly(Rifle, metres, ExternalBallistics.ZeroAngle(Rifle, zeroMetres, SightHeight),
                                  SightHeight, Air.Standard, wind);

    [Fact]
    public void TheRoundIsTheOneTheTablesAreWrittenFor()
    {
        Assert.Equal(2650f * FootPerSecond, Rifle.MuzzleVelocity, 0);
        Assert.Equal(0.224f, Rifle.BallisticCoefficientG7);
        Assert.Equal(5, Rifle.MagazineCapacity);
    }

    [Theory]
    [InlineData(100, 2460)]
    [InlineData(300, 2103)]
    [InlineData(500, 1778)]
    public void ItSlowsAsTheTableSays(int yards, int feetPerSecond)
    {
        float v = At(yards * Yard, 100 * Yard).Speed / FootPerSecond;
        _out.WriteLine($"{yards} yd: {v:F0} ft/s against {feetPerSecond}");
        Assert.InRange(v, feetPerSecond * 0.97f, feetPerSecond * 1.03f);
    }

    [Theory]
    [InlineData(200, -4.3)]
    [InlineData(300, -15.3)]
    public void ItDropsAsTheTableSays(int yards, double inches)
    {
        float drop = At(yards * Yard, 100 * Yard).Height / Inch;
        _out.WriteLine($"{yards} yd: {drop:F2} in against {inches}");
        // A few per cent, and never less than a quarter inch: the table is printed to a tenth.
        float tolerance = MathF.Max(0.25f, MathF.Abs((float)inches) * 0.05f);
        Assert.InRange(drop, (float)inches - tolerance, (float)inches + tolerance);
    }

    [Fact]
    public void ZeroedAtAHundredItIsOnTheLineAtAHundred()
    {
        Assert.InRange(At(100f, 100f).Height, -0.002f, 0.002f);
        Assert.InRange(At(100 * Yard, 100 * Yard).Height, -0.002f, 0.002f);
    }

    /// <summary>
    /// At 600 m: about a second in the air and a drop from a 100 m zero of about three and a third
    /// metres, 5.6 mil, which is what G7 solvers give for this load. The drop is less than half g t²
    /// less the zero's angle, because drag slows the falling as well as the going.
    /// </summary>
    [Fact]
    public void SixHundredMetresIsASecondAndThreeMetresDown()
    {
        var p = At(600f, 100f);
        float mil = -p.Height / 600f * 1000f;
        _out.WriteLine($"600 m: {p.Seconds:F3} s, {p.Height:F2} m ({mil:F2} mil), {p.Speed:F0} m/s");
        Assert.InRange(p.Seconds, 0.92f, 1.03f);
        Assert.InRange(mil, 5.2f, 6.0f);
        float angle = ExternalBallistics.ZeroAngle(Rifle, 100f, SightHeight);
        float vacuum = 0.5f * ExternalBallistics.Gravity * p.Seconds * p.Seconds - 600f * angle - SightHeight;
        Assert.InRange(-p.Height, 0.7f * vacuum, vacuum);
        var p300 = At(300f, 100f);
        _out.WriteLine($"300 m: {p300.Seconds:F3} s, {p300.Height:F2} m ({-p300.Height / 0.3f:F2} mil)");
        Assert.InRange(-p300.Height / 0.3f, 1.6f, 2.3f);
    }

    [Theory]
    [InlineData(300, 7.4)]
    [InlineData(500, 22.0)]
    public void ACrosswindCarriesItAsTheTableSays(int yards, double inches)
    {
        // Ten miles an hour, from the left: the air moving toward +X, which is to the right facing +Z.
        var wind = new Vector3(10f * 0.44704f, 0f, 0f);
        float drift = At(yards * Yard, 100 * Yard, wind).Drift / Inch;
        _out.WriteLine($"{yards} yd: {drift:F2} in against {inches}");
        Assert.InRange(drift, (float)inches * 0.92f, (float)inches * 1.09f);
    }

    [Fact]
    public void TheWindCarriesItDownwind()
    {
        // Facing north (+Z), a wind toward the east (+X) carries it east; toward the west, west.
        Assert.True(At(400f, 100f, new Vector3(4f, 0f, 0f)).Drift > 0.1f);
        Assert.True(At(400f, 100f, new Vector3(-4f, 0f, 0f)).Drift < -0.1f);
        // A headwind slows it, so it is later and lower than in still air.
        var still = At(400f, 100f);
        var head = At(400f, 100f, new Vector3(0f, 0f, -8f));
        Assert.True(head.Seconds > still.Seconds && head.Height < still.Height);
    }

    [Fact]
    public void ATurretSettingIsZeroedWhereTheComeUpSaysAndBack()
    {
        var scope = ScopeRegistry.Scope4To12;
        foreach (float metres in new[] { 200f, 400f, 600f, 800f })
        {
            float mil = ExternalBallistics.ComeUpMil(Rifle, metres, 100f, scope.SightHeightMetres);
            float angle = ExternalBallistics.ZeroAngle(Rifle, 100f, scope.SightHeightMetres) + mil / 1000f;
            float? back = ExternalBallistics.ZeroDistanceFor(Rifle, angle, scope.SightHeightMetres);
            _out.WriteLine($"{metres} m: {mil:F2} mil up, zeroed back at {back:F1} m");
            Assert.NotNull(back);
            Assert.InRange(back!.Value, metres - 1f, metres + 1f);
        }
        // The come-up grows with the range, and faster the further out.
        float c3 = ExternalBallistics.ComeUpMil(Rifle, 300f, 100f, scope.SightHeightMetres);
        float c6 = ExternalBallistics.ComeUpMil(Rifle, 600f, 100f, scope.SightHeightMetres);
        Assert.True(c6 > 2f * c3);
        // Nothing dialled is a 100 m zero.
        float baseAngle = ExternalBallistics.ZeroAngle(Rifle, 100f, scope.SightHeightMetres);
        Assert.InRange(ExternalBallistics.ZeroDistanceFor(Rifle, baseAngle, scope.SightHeightMetres)!.Value, 99f, 101f);
    }

    [Fact]
    public void RaisingTheBarrelTurnsItUpInItsOwnPlane()
    {
        Vector3 level = ExternalBallistics.Raise(Vector3.UnitZ, 0.01f);
        Assert.True(level.Y > 0f && MathF.Abs(level.X) < 1e-6f);
        // Shooting down off a roof at 30 degrees, raising it still lifts it toward the sky.
        Vector3 down = Vector3.Normalize(new Vector3(0f, -0.5f, 0.866f));
        Vector3 raised = ExternalBallistics.Raise(down, 0.01f);
        Assert.True(raised.Y > down.Y);
        Assert.InRange(MathF.Acos(Vector3.Dot(raised, down)), 0.0099f, 0.0101f);
    }

    // ── A moving target ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Flies one bullet at a body walking across the line at 300 m, and says whether it struck: the
    /// same per-stretch test the server makes, in the body's moving frame.
    /// </summary>
    private static bool Strikes(float leadMetres, float range = 300f)
    {
        const float walk = 1.4f;             // m/s, to the right
        float zeroAngle = ExternalBallistics.ZeroAngle(Rifle, range, 0.038f);
        Vector3 eye = new(0f, 1.7f, 0f);
        // The body starts on the line (its chest at eye height: same level) and walks to +X.
        Vector3 feet = new(0f, 0.5f, range);
        Vector3 aimAt = feet + new Vector3(leadMetres, 1.2f, 0f);
        Vector3 sight = Vector3.Normalize(aimAt - eye);
        var s = new BulletState
        {
            Position = eye - new Vector3(0f, 0.038f, 0f),
            Velocity = ExternalBallistics.Raise(sight, zeroAngle) * Rifle.MuzzleVelocity,
        };
        Vector3 bodyVel = new(walk, 0f, 0f);
        for (int i = 0; i < 300; i++)
        {
            var before = s;
            ExternalBallistics.Advance(ref s, 0.01f, Rifle.BallisticCoefficientG7, Air.Standard, Vector3.Zero);
            Vector3 bodyNow = feet + bodyVel * before.Seconds;
            if (ExternalBallistics.SegmentHitsBody(before.Position, s.Position, 0.01f, bodyNow, bodyVel,
                                                   ExternalBallistics.BodyRadius, ExternalBallistics.BodyHeight, out _, out _))
                return true;
            if (s.Position.Z > range + 5f) return false;
        }
        return false;
    }

    [Fact]
    public void AWalkingTargetIsMissedWithoutLeadAndHitWithIt()
    {
        float tof = At(300f, 300f).Seconds;
        float lead = 1.4f * tof;
        _out.WriteLine($"300 m: {tof:F3} s in flight, lead {lead:F2} m");
        Assert.False(Strikes(0f), "a walking target was hit with no lead at all");
        Assert.True(Strikes(lead), "a walking target was missed with the right lead");
    }

    [Fact]
    public void ABodyIsHitWhereItIsWhenTheBulletArrivesNotWhereItWas()
    {
        // Standing still, it is hit square; stepped aside by a metre in the time, it is not.
        Assert.True(ExternalBallistics.SegmentHitsBody(new Vector3(0, 1.2f, -1f), new Vector3(0, 1.2f, 1f), 0.01f,
                    Vector3.Zero, Vector3.Zero, 0.225f, 1.8f, out float frac, out float h));
        Assert.InRange(frac, 0.38f, 0.40f);
        Assert.Equal(1.2f, h, 2);
        Assert.False(ExternalBallistics.SegmentHitsBody(new Vector3(0, 1.2f, -1f), new Vector3(0, 1.2f, 1f), 0.01f,
                     new Vector3(-1f, 0f, 0f), Vector3.Zero, 0.225f, 1.8f, out _, out _));
        // Over the head is a miss; through the feet is a hit at the bottom.
        Assert.False(ExternalBallistics.SegmentHitsBody(new Vector3(0, 2.0f, -1f), new Vector3(0, 2.0f, 1f), 0.01f,
                     Vector3.Zero, Vector3.Zero, 0.225f, 1.8f, out _, out _));
        // Walking into the path during the stretch is a hit.
        Assert.True(ExternalBallistics.SegmentHitsBody(new Vector3(0, 1.2f, -1f), new Vector3(0, 1.2f, 1f), 1f,
                    new Vector3(-0.5f, 0f, 0f), new Vector3(1f, 0f, 0f), 0.225f, 1.8f, out float mid, out _));
        Assert.InRange(mid, 0.3f, 0.5f);
    }

    [Fact]
    public void TheDragTableIsTheG7()
    {
        Assert.Equal(0.1198f, ExternalBallistics.DragCoefficientG7(0f), 4);
        Assert.Equal(0.3803f, ExternalBallistics.DragCoefficientG7(1f), 4);
        Assert.Equal(0.2980f, ExternalBallistics.DragCoefficientG7(2f), 4);
        // Between points it interpolates.
        float mid = ExternalBallistics.DragCoefficientG7(2.025f);
        Assert.InRange(mid, 0.2951f, 0.2980f);
    }
}
