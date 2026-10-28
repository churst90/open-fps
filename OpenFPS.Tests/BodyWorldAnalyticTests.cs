using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Geometry;
using OpenFPS.Materials;
using OpenFPS.Physics;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Jolt behind <see cref="BodyWorld"/> against closed-form physics (docs/PHYSICS_BOUNDARY.md 18.2 step 4, 18.9): bounce
/// heights from restitution, momentum and energy across a collision, a box on a slope held and sliding, a throw off a
/// roof, a stack going to sleep, and the same scene twice giving the same bits. Every bound says what it allows for. The
/// step is 1/120 s; Jolt solves contacts speculatively and lets a resting body sink by its penetration slop (a millimetre
/// or two measured), so heights are read at a body's apex, where it is not in contact.
/// </summary>
public class BodyWorldAnalyticTests
{
    private const float G = Earth.StandardGravity;
    private const float Dt = BodyWorld.StepSeconds;
    private static readonly Quaternion Upright = Quaternion.Identity;
    /// <summary>Jolt's default distance at which a contact is made ahead of touching, metres.</summary>
    private const float SpeculativeDistance = 0.02f;
    private readonly ITestOutputHelper _out;
    public BodyWorldAnalyticTests(ITestOutputHelper output) => _out = output;

    /// <summary>A floor with its top face at height 0, friction and restitution as given.</summary>
    private static int Floor(BodyWorld w, float friction, float restitution)
    {
        int id = w.Add(w.AddBox(new Vector3(200f, 0.5f, 200f)), BodyMotion.Static, new Vector3(0f, -0.5f, 0f), Upright, "Concrete");
        w.SetFriction(id, friction);
        w.SetRestitution(id, restitution);
        return id;
    }

    // ── Bounce heights ─────────────────────────────────────────────────────────────────────────

    /// <summary>Steps until the ball has bounced this many times and returns the highest centre of each flight.</summary>
    private static List<float> Apexes(BodyWorld w, int ball, int bounces)
    {
        var apexes = new List<float>();
        float top = float.MinValue;
        bool rising = false;
        for (int i = 0; i < 120 * 20 && apexes.Count < bounces; i++)
        {
            w.Step();
            if (w.Velocity(ball).Y > 0f) { rising = true; top = MathF.Max(top, w.Position(ball).Y); }
            else if (rising) { apexes.Add(top); rising = false; top = float.MinValue; }
        }
        return apexes;
    }

    [Theory]
    [InlineData(0.93f, 0.93f, 0.93f)]      // the size of glass on glass, the table's one measured restitution (TANG)
    [InlineData(0.5f, 0.5f, 0.5f)]
    [InlineData(0.8f, 0.3f, 0.8f)]         // Jolt's pair rule is the larger of the two: ball larger
    [InlineData(0.3f, 0.8f, 0.8f)]         // floor larger
    public void BounceHeightsFallByTheSquareOfRestitution(float ballE, float floorE, float e)
    {
        using var w = new BodyWorld();
        Floor(w, 0.5f, floorE);
        const float h0 = 2f, radius = 0.1f;
        int ball = w.Add(w.AddSphere(radius), BodyMotion.Dynamic, new Vector3(0f, h0 + radius, 0f), Upright, "Metal", 1f);
        w.SetRestitution(ball, ballE);
        var apexes = Apexes(w, ball, 3);
        Assert.Equal(3, apexes.Count);
        for (int n = 0; n < 3; n++)
        {
            float expected = h0 * MathF.Pow(e, 2f * (n + 1));
            float height = apexes[n] - radius;
            _out.WriteLine($"e={e} bounce {n + 1}: apex {height:F4} m, closed form {expected:F4} m, off {(height - expected) * 1000:F2} mm ({(height / expected - 1) * 100:F2} %)");
            // Half a percent of the height for the solver's float velocities, and the step's phase: the apex moves with
            // where in a 1/120 s step the ball arrived, by up to the speculative distance plus half of what a step
            // carries it into the floor, (1 - e) v dt (the sawtooth test below measures it), v the arrival speed,
            // sqrt(2 g h) from the apex before. The exact law is in speed, tested after this.
            float arrived = MathF.Sqrt(2f * G * h0 * MathF.Pow(e, 2f * n));
            float tolerance = 0.005f * expected + SpeculativeDistance + 0.5f * (1f - e) * arrived * Dt;
            if (expected < 5f * SpeculativeDistance) continue;       // a bounce lower than five times the tolerance says nothing
            Assert.InRange(height, expected - tolerance, expected + tolerance);
        }
    }

    [Theory]
    [InlineData(0.2f)]
    [InlineData(0.5f)]
    [InlineData(0.8f)]
    [InlineData(0.93f)]
    public void ABallLeavesAtRestitutionTimesTheSpeedItArrivedAt(float e)
    {
        // The law itself, in speed, where the step's phase does not enter: the speed after the turn over the speed in
        // the step before it, across a run of drop heights.
        for (int k = 0; k < 12; k++)
        {
            using var w = new BodyWorld();
            Floor(w, 0.5f, e);
            int ball = w.Add(w.AddSphere(0.1f), BodyMotion.Dynamic, new Vector3(0f, 2.1f + k * 0.37f, 0f), Upright, "Metal", 1f);
            w.SetRestitution(ball, e);
            float arrived = 0f, left = 0f;
            for (int i = 0; i < 120 * 4 && left == 0f; i++)
            {
                float before = w.Velocity(ball).Y;
                w.Step();
                if (before < -2f && w.Velocity(ball).Y > 0f) { arrived = -before; left = w.Velocity(ball).Y; }
            }
            Assert.True(left > 0f, "it never bounced");
            _out.WriteLine($"e={e}: arrived {arrived:F4}, left {left:F4}, ratio {left / arrived:F5}");
            // Measured exact to five places: the solver reflects the speed the step started with, so only float rounding
            // is left.
            Assert.Equal(e, left / arrived, 0.0005f);
        }
    }

    [Fact]
    public void TheBounceHeightErrorIsASawtoothOfTheStepPhaseWithNoBias()
    {
        // Jolt's contacts are speculative within 2 cm (its default) and a ball at 6 m/s moves 5 cm a step. A ball
        // that ends a step within 2 cm of the floor is turned back there, up to 2 cm early; one that was farther
        // goes on into the floor (measured: 31 mm at 6.3 m/s) and is turned back from there. So the apex is above or
        // below the closed form by an amount that depends on where in the step the ball arrived. Dropped from heights
        // a few millimetres apart across one period (the fall speed changes by g dt = 0.08 m/s every 5 cm of
        // height), the error runs from plus to minus and averages to nothing.
        foreach (float e in new[] { 0.2f, 0.5f, 0.8f })
        {
            float sum = 0f, worst = 0f;
            const int points = 16;
            for (int k = 0; k < points; k++)
            {
                float h0 = 2f + k * 0.0035f;
                using var w = new BodyWorld();
                Floor(w, 0.5f, e);
                int ball = w.Add(w.AddSphere(0.1f), BodyMotion.Dynamic, new Vector3(0f, h0 + 0.1f, 0f), Upright, "Metal", 1f);
                w.SetRestitution(ball, e);
                float error = Apexes(w, ball, 1)[0] - 0.1f - h0 * e * e;
                sum += error; worst = MathF.Max(worst, MathF.Abs(error));
            }
            float bound = SpeculativeDistance + 0.5f * (1f - e) * MathF.Sqrt(2f * G * 2f) * Dt;
            _out.WriteLine($"e={e}: worst error {worst * 1000:F1} mm of a bound {bound * 1000:F1} mm, mean {sum / points * 1000:F2} mm");
            Assert.True(worst <= bound, $"worst {worst * 1000:F1} mm over {bound * 1000:F1} mm");
            Assert.True(MathF.Abs(sum / points) < 0.005f, $"mean {sum / points * 1000:F2} mm: a bias");
        }
    }

    [Fact]
    public void GlassOnGlassBouncesAtTheTablesOwnRestitution()
    {
        // The table's numbers through ContactMaterial.Of, not set by hand.
        float e = ContactMaterial.Of("Glass").Restitution;
        Assert.Equal(0.93f, e);
        using var w = new BodyWorld();
        int floor = w.Add(w.AddBox(new Vector3(50f, 0.5f, 50f)), BodyMotion.Static, new Vector3(0f, -0.5f, 0f), Upright, "Glass");
        int ball = w.Add(w.AddSphere(0.05f), BodyMotion.Dynamic, new Vector3(0f, 1.05f, 0f), Upright, "Glass", 0.5f);
        Assert.Equal(e, w.Restitution(ball));
        Assert.Equal(e, w.Restitution(floor));
        float apex = Apexes(w, ball, 1)[0] - 0.05f;
        _out.WriteLine($"glass: first apex {apex:F4} m, closed form {e * e:F4} m");
        float tolerance = 0.005f * e * e + SpeculativeDistance + 0.5f * (1f - e) * MathF.Sqrt(2f * G) * Dt;
        Assert.InRange(apex, e * e - tolerance, e * e + tolerance);
    }

    [Fact]
    public void ALandingUnderJoltsRestitutionSpeedDoesNotBounce()
    {
        // Jolt applies restitution only to a closing speed over 1 m/s (its default), so the bounces of a ball end when its
        // speed falls under it, whatever the table says: a ball dropped 2 cm lands at 0.63 m/s.
        using var w = new BodyWorld();
        Floor(w, 0.5f, 0.9f);
        int ball = w.Add(w.AddSphere(0.1f), BodyMotion.Dynamic, new Vector3(0f, 0.12f, 0f), Upright, "Metal", 1f);
        w.SetRestitution(ball, 0.9f);
        float top = 0f;
        for (int i = 0; i < 120; i++) { w.Step(); if (i > 20) top = MathF.Max(top, w.Position(ball).Y - 0.1f); }
        _out.WriteLine($"dropped 2 cm at e 0.9: highest after landing {top * 1000:F2} mm (closed form {0.02f * 0.81f * 1000:F2} mm)");
        Assert.True(top < 0.002f, $"it bounced {top * 1000:F2} mm");
    }

    // ── Collisions of two dynamic bodies ───────────────────────────────────────────────────────

    private static (BodyWorld w, int a, int b) Pair(float m1, float v1, float m2, float v2, float e)
    {
        // No gravity: a head-on collision along x alone, frictionless.
        var w = new BodyWorld(0f);
        int a = w.Add(w.AddSphere(0.25f), BodyMotion.Dynamic, new Vector3(-1f, 0f, 0f), Upright, "Metal", m1);
        int b = w.Add(w.AddSphere(0.25f), BodyMotion.Dynamic, new Vector3(1f, 0f, 0f), Upright, "Metal", m2);
        w.SetRestitution(a, e); w.SetRestitution(b, e);
        w.SetFriction(a, 0f); w.SetFriction(b, 0f);
        w.SetVelocity(a, new Vector3(v1, 0f, 0f));
        w.SetVelocity(b, new Vector3(v2, 0f, 0f));
        return (w, a, b);
    }

    [Theory]
    [InlineData(2f, 4f, 1f, -1f, 0f)]       // perfectly inelastic: they leave together
    [InlineData(2f, 4f, 1f, -1f, 0.5f)]
    [InlineData(2f, 4f, 1f, -1f, 1f)]       // elastic
    [InlineData(10f, 3f, 0.5f, 0f, 0.7f)]   // heavy on light at rest
    [InlineData(1f, 2.5f, 1f, 0f, 1f)]      // equal masses swap velocities
    public void MomentumIsConservedAndEnergyIsLostByRestitution(float m1, float v1, float m2, float v2, float e)
    {
        var (w, a, b) = Pair(m1, v1, m2, v2, e);
        using (w)
        {
            for (int i = 0; i < 240; i++) w.Step();
            float va = w.Velocity(a).X, vb = w.Velocity(b).X;
            float expectedA = (m1 * v1 + m2 * v2 + m2 * e * (v2 - v1)) / (m1 + m2);
            float expectedB = (m1 * v1 + m2 * v2 + m1 * e * (v1 - v2)) / (m1 + m2);
            float p0 = m1 * v1 + m2 * v2, p1 = m1 * va + m2 * vb;
            float ke0 = 0.5f * (m1 * v1 * v1 + m2 * v2 * v2), ke1 = 0.5f * (m1 * va * va + m2 * vb * vb);
            float mu = m1 * m2 / (m1 + m2), vrel = v1 - v2;
            float expectedLoss = 0.5f * mu * vrel * vrel * (1f - e * e);
            _out.WriteLine($"e={e}: va {va:F5} (closed {expectedA:F5}), vb {vb:F5} ({expectedB:F5}); momentum {p0:F5} -> {p1:F5}; energy lost {ke0 - ke1:F5} J (closed {expectedLoss:F5})");
            // Momentum: no outside impulse acts (frictionless, no gravity), so the sum holds to float rounding.
            Assert.Equal(p0, p1, MathF.Max(1e-4f, MathF.Abs(p0) * 1e-5f));
            // Velocities by the restitution law, to a tenth of a percent of the closing speed.
            Assert.Equal(expectedA, va, 0.001f * MathF.Abs(vrel) + 1e-4f);
            Assert.Equal(expectedB, vb, 0.001f * MathF.Abs(vrel) + 1e-4f);
            // Energy lost is (1 - e^2) of the kinetic energy about the centre of mass, to half a percent; never gained.
            Assert.True(ke1 <= ke0 * 1.00001f, $"energy rose from {ke0} to {ke1}");
            Assert.Equal(expectedLoss, ke0 - ke1, MathF.Max(1e-3f, expectedLoss * 0.005f));
        }
    }

    // ── A box on a slope ───────────────────────────────────────────────────────────────────────

    /// <summary>A slab tilted by this angle, rising towards +x, and a box lying on it 1 mm above the surface, at rest.
    /// Returns the box and the downhill direction in the world.</summary>
    private static (BodyWorld w, int box, Vector3 down) OnSlope(float degrees, string slopeMaterial, string boxMaterial, float? friction = null)
    {
        float theta = degrees * MathF.PI / 180f;
        var w = new BodyWorld();
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, theta);
        int slab = w.Add(w.AddBox(new Vector3(40f, 0.5f, 5f)), BodyMotion.Static, Vector3.Zero, tilt, slopeMaterial);
        var normal = Vector3.Transform(Vector3.UnitY, tilt);
        var down = -Vector3.Transform(Vector3.UnitX, tilt);
        int box = w.Add(w.AddBox(new Vector3(0.25f, 0.25f, 0.25f)), BodyMotion.Dynamic, normal * (0.5f + 0.25f + 0.001f), tilt, boxMaterial, 10f);
        if (friction is float f) { w.SetFriction(slab, f); w.SetFriction(box, f); }
        w.SetRestitution(slab, 0f); w.SetRestitution(box, 0f);
        return (w, box, down);
    }

    [Theory]
    [InlineData(10f)]
    [InlineData(25f)]       // tan 25 = 0.466 against Metal's 0.57
    [InlineData(28f)]       // tan 28 = 0.532, 7 percent under
    public void ABoxOnASlopeUnderItsFrictionAngleStaysPut(float degrees)
    {
        var (w, box, down) = OnSlope(degrees, "Metal", "Metal");
        using (w)
        {
            float mu = ContactMaterial.Of("Metal").Friction;          // equal values: their geometric mean is the value
            Assert.True(MathF.Tan(degrees * MathF.PI / 180f) < mu);
            var start = w.Position(box);
            for (int i = 0; i < 240; i++) w.Step();
            float slid = Vector3.Dot(w.Position(box) - start, down);
            _out.WriteLine($"{degrees} deg, mu {mu}: moved {slid * 1000:F3} mm along the slope in 2 s, speed {w.Velocity(box).Length() * 1000:F3} mm/s");
            // Closing the 1 mm gap moves it along the slope by at most that gap times sin theta; after that, nothing.
            Assert.True(MathF.Abs(slid) < 0.002f, $"slid {slid * 1000:F3} mm");
        }
    }

    [Theory]
    [InlineData(35f, 0.57f)]    // tan 35 = 0.700 against Metal's measured kinetic 0.57
    [InlineData(40f, 0.57f)]
    [InlineData(30f, 0.3f)]
    [InlineData(20f, 0.1f)]
    public void ABoxOnASlopePastItsFrictionAngleSlidesAtGSinMinusMuCos(float degrees, float mu)
    {
        var (w, box, down) = OnSlope(degrees, "Metal", "Metal", mu);
        using (w)
        {
            float theta = degrees * MathF.PI / 180f;
            float a = G * (MathF.Sin(theta) - mu * MathF.Cos(theta));
            // Leave the first 0.1 s (the gap closes and the contact settles), then read the speed gained over one second.
            for (int i = 0; i < 12; i++) w.Step();
            float v0 = Vector3.Dot(w.Velocity(box), down);
            var p0 = w.Position(box);
            for (int i = 0; i < 120; i++) w.Step();
            float measured = Vector3.Dot(w.Velocity(box), down) - v0;
            float across = Vector3.Dot(w.Position(box) - p0, Vector3.UnitZ);
            float travelled = Vector3.Dot(w.Position(box) - p0, down);
            float closed = v0 + 0.5f * a;
            _out.WriteLine($"{degrees} deg mu {mu}: acceleration {measured:F4} (closed form {a:F4}, {(measured / a - 1) * 100:F3} %); travelled {travelled:F4} m (closed {closed:F4}); sideways {across * 1000:F3} mm");
            // Exact for a rigid block with Coulomb friction; Jolt solves with iterated impulses, so half a percent of the
            // acceleration, and 2 mm/s^2 for float rounding where the acceleration is small.
            Assert.Equal(a, measured, MathF.Abs(a) * 0.005f + 0.002f);
            Assert.Equal(closed, travelled, MathF.Abs(closed) * 0.01f + 0.002f);
            Assert.True(MathF.Abs(across) < 0.001f, "it should slide straight");
        }
    }

    [Fact]
    public void TheFrictionAngleIsTheGeometricMeanOfThePair()
    {
        // Metal on Ice, the table's kinetic figures: mu = sqrt(0.57 * 0.035) = 0.141, an angle of 8.04 degrees.
        float mu = MathF.Sqrt(ContactMaterial.Of("Metal").Friction * ContactMaterial.Of("Ice").Friction);
        float angle = MathF.Atan(mu) * 180f / MathF.PI;
        _out.WriteLine($"pair mu {mu:F4}, friction angle {angle:F3} deg");
        foreach (float degrees in new[] { angle - 1.5f, angle + 1.5f })
        {
            var (w, box, down) = OnSlope(degrees, "Ice", "Metal");
            using (w)
            {
                for (int i = 0; i < 12; i++) w.Step();
                float v0 = Vector3.Dot(w.Velocity(box), down);
                for (int i = 0; i < 120; i++) w.Step();
                float measured = Vector3.Dot(w.Velocity(box), down) - v0;
                float theta = degrees * MathF.PI / 180f;
                float a = G * (MathF.Sin(theta) - mu * MathF.Cos(theta));
                _out.WriteLine($"{degrees:F2} deg: acceleration {measured:F4}, closed form {a:F4}");
                if (a < 0f) Assert.True(Vector3.Dot(w.Velocity(box), down) < 0.005f, "held");
                else Assert.Equal(a, measured, a * 0.01f + 0.002f);
            }
        }
    }

    // ── A throw off a roof ─────────────────────────────────────────────────────────────────────

    /// <summary>The flight with no drag from a launch at height y0 (ball centre) at (vx, vy) to the height restY: the time
    /// and the horizontal range.</summary>
    private static (float t, float x, float vyLand) Ballistic(float y0, float vx, float vy, float restY)
    {
        float t = (vy + MathF.Sqrt(vy * vy + 2f * G * (y0 - restY))) / G;
        return (t, vx * t, vy - G * t);
    }

    /// <summary>Throws the ball and steps until it lands (its fall is stopped: a vertical speed under -1 m/s one step and
    /// under 0.5 m/s the next); gives the step's time, place and the vertical speed just before.</summary>
    private static void ThrowAndLand(BodyWorld w, int ball, Vector3 velocity, out float t, out Vector3 landed, out float speedBefore)
    {
        w.SetVelocity(ball, velocity);
        float prevVy = velocity.Y;
        for (int i = 1; i <= 120 * 10; i++)
        {
            w.Step();
            float vy = w.Velocity(ball).Y;
            // Contact turns a fall of 13 m/s into about 1 m/s in one step (the speculative contact closes the remaining gap
            // and no more), and then to rest; free fall changes the speed by 0.08 m/s a step.
            if (prevVy < -1f && vy - prevVy > 10f * G * Dt) { t = i * Dt; landed = w.Position(ball); speedBefore = prevVy; return; }
            prevVy = vy;
        }
        throw new InvalidOperationException("the ball never landed");
    }

    /// <summary>Checks a landing against the flight: the time to within two steps, the range to within what two steps of
    /// horizontal travel and the integrator's offset allow.</summary>
    private void CheckLanding(string what, float vx, float t, float dx, float vyLand, float tLand, float x, float speedBefore)
    {
        // Jolt integrates velocity first and then position (symplectic Euler), so after n steps the height is below the
        // true parabola by g dt t / 2 (4 cm after a second): the ball lands early by that over its landing speed. The
        // landing step is read up to one step late, and one more is slack.
        float early = G * Dt * t / 2f / MathF.Abs(vyLand);
        _out.WriteLine($"{what}: flight {tLand:F4} s (closed {t:F4}), range {x:F4} m (closed {dx:F4}, off {(x - dx) * 1000:F1} mm), vertical speed before landing {-speedBefore:F3} (closed {-vyLand:F3}); integrator offset {early * 1000:F2} ms");
        Assert.InRange(tLand, t - early - Dt, t + 2f * Dt);
        Assert.InRange(x, dx - MathF.Abs(vx) * (early + Dt), dx + MathF.Abs(vx) * 2f * Dt);
        Assert.Equal(vyLand, speedBefore, 0.15f);
    }

    [Theory]
    [InlineData(6f, 0f)]
    [InlineData(5f, 4f)]       // thrown up and out
    [InlineData(6f, -3f)]      // thrown down
    public void AThrowOffABoxRoofLandsWhereTheFlightSays(float vx, float vy)
    {
        using var w = new BodyWorld();
        // A roof 8 m up with its edge at x = 0, the street at height 0. The ball is thrown from 1 m over the roof, 0.5 m
        // in from the edge, and clears the edge in every case.
        w.Add(w.AddBox(new Vector3(10f, 4f, 10f)), BodyMotion.Static, new Vector3(-10f, 4f, 0f), Upright, "Concrete");
        int floor = Floor(w, 0.5f, 0f);
        const float radius = 0.1f;
        float y0 = 8f + 1f + radius;
        int ball = w.Add(w.AddSphere(radius), BodyMotion.Dynamic, new Vector3(-0.5f, y0, 0f), Upright, "Metal", 0.5f);
        w.SetRestitution(ball, 0f);
        var (t, dx, vyLand) = Ballistic(y0, vx, vy, radius);
        ThrowAndLand(w, ball, new Vector3(vx, vy, 0f), out float tLand, out var at, out float speedBefore);
        CheckLanding($"box roof v=({vx},{vy})", vx, t, dx, vyLand, tLand, at.X + 0.5f, speedBefore);
        Assert.True(w.Has(floor));
    }

    [Fact]
    public void AThrowOffATileMeshRoofLandsWhereTheFlightSays()
    {
        // The same flight over tile meshes made by PieceBodies (step 3, as the game hands geometry to Jolt): a street
        // and a building 8 m high whose east face is at x = 110.
        AcousticRegistry.Initialize();
        SolidSpec Box(int owner, Vector3 at, Vector3 size)
            => new(owner, at, Quaternion.Identity, size, EntityGeometry.SurfaceOf("Concrete", size, 0, 0, false, 0, 0, false, false, false));
        var tiles = new TriangleWorldBuilder(250f).Build(new[]
        {
            Box(1, new Vector3(125f, -0.1f, 125f), new Vector3(200f, 0.2f, 200f)),
            Box(2, new Vector3(100f, 4f, 125f), new Vector3(20f, 8f, 20f)),
        }, Array.Empty<SolidSpec>());
        using var w = new BodyWorld();
        using var bodies = new PieceBodies(w);
        Assert.Equal(0, bodies.Follow(tiles, PieceBodies.Prepare));
        while (bodies.Pump(1000) > 0) { }
        const float radius = 0.1f, vx = 5f, vy = 3f;
        float y0 = 8f + 0.5f + radius;
        var start = new Vector3(109.5f, y0, 125f);
        int ball = w.Add(w.AddSphere(radius), BodyMotion.Dynamic, start, Upright, "Metal", 0.5f);
        w.SetRestitution(ball, 0f);
        var (t, dx, vyLand) = Ballistic(y0, vx, vy, radius);
        ThrowAndLand(w, ball, new Vector3(vx, vy, 0f), out float tLand, out var at, out float speedBefore);
        CheckLanding("tile roof", vx, t, dx, vyLand, tLand, at.X - start.X, speedBefore);
        Assert.Equal(125f, at.Z, 1e-3f);
    }

    // ── A stack at rest ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AStackAtRestGoesToSleepWhereItStood()
    {
        using var w = new BodyWorld();
        Floor(w, 0.6f, 0.1f);
        const float half = 0.25f;
        int shape = w.AddBox(new Vector3(half, half, half));
        var boxes = new List<int>();
        for (int i = 0; i < 4; i++)
            boxes.Add(w.Add(shape, BodyMotion.Dynamic, new Vector3(0f, half + i * (2f * half + 0.002f), 0f), Upright, "Wood", 5f));
        int firstAsleep = -1;
        for (int i = 0; i < 120 * 6; i++)
        {
            w.Step();
            if (firstAsleep < 0 && boxes.All(b => w.State(b).Sleeping)) firstAsleep = i + 1;
        }
        _out.WriteLine($"all four asleep after {firstAsleep} steps ({firstAsleep * Dt:F3} s)");
        Assert.True(firstAsleep > 0, "the stack never slept");
        // Jolt's default is half a second under 0.03 m/s before a body sleeps, and an island sleeps together; the 2 mm
        // gaps close in the first steps.
        Assert.InRange(firstAsleep * Dt, 0.45f, 1.5f);
        for (int i = 0; i < boxes.Count; i++)
        {
            var p = w.Position(boxes[i]);
            float stacked = half + i * 2f * half;
            _out.WriteLine($"box {i}: y {p.Y:F5} (stacked {stacked:F3}), off {(p.Y - stacked) * 1000:F3} mm");
            // Each joint may sink by up to the penetration slop (2 cm) and the 2 mm gaps are closed.
            Assert.InRange(p.Y, stacked - 0.02f * (i + 1), stacked + 0.01f);
            Assert.True(MathF.Abs(p.X) < 0.005f && MathF.Abs(p.Z) < 0.005f, $"box {i} drifted to {p}");
        }
        // Asleep means it stays: five seconds on nothing has moved.
        var before = boxes.Select(w.Position).ToArray();
        for (int i = 0; i < 120 * 5; i++) w.Step();
        for (int i = 0; i < boxes.Count; i++) Assert.Equal(before[i], w.Position(boxes[i]));
    }

    [Fact]
    public void ABodyThatIsStillFallingIsNotAsleep()
    {
        using var w = new BodyWorld();
        int ball = w.Add(w.AddSphere(0.1f), BodyMotion.Dynamic, new Vector3(0f, 100f, 0f), Upright, "Metal", 1f);
        for (int i = 0; i < 120 * 2; i++) w.Step();
        Assert.False(w.State(ball).Sleeping);
    }

    // ── The same scene twice ───────────────────────────────────────────────────────────────────

    /// <summary>A busy scene: sixty boxes, balls and capsules dropped on a slab and a floor, thrown and spinning. Every
    /// body's pose and velocity as raw float bits, hashed.</summary>
    public static ulong BusyScene(out int bodies)
    {
        using var w = new BodyWorld();
        Floor(w, 0.6f, 0.2f);
        w.Add(w.AddBox(new Vector3(6f, 0.5f, 4f)), BodyMotion.Static, new Vector3(4f, 0.8f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f), "Concrete");
        var rng = new Random(12345);
        int boxShape = w.AddBox(new Vector3(0.2f, 0.15f, 0.3f)), ballShape = w.AddSphere(0.18f), capsule = w.AddCapsule(0.3f, 0.15f);
        for (int i = 0; i < 60; i++)
        {
            int shape = (i % 3) switch { 0 => boxShape, 1 => ballShape, _ => capsule };
            var at = new Vector3((float)(rng.NextDouble() * 6 - 1), 1f + i * 0.35f, (float)(rng.NextDouble() * 3 - 1.5));
            var q = Quaternion.CreateFromYawPitchRoll((float)rng.NextDouble() * 6f, (float)rng.NextDouble(), (float)rng.NextDouble());
            int id = w.Add(shape, BodyMotion.Dynamic, at, q, i % 2 == 0 ? "Wood" : "Metal", 1f + i % 7);
            w.SetVelocity(id, new Vector3((float)rng.NextDouble() * 4 - 2, 0f, (float)rng.NextDouble() * 4 - 2));
            w.SetAngularVelocity(id, new Vector3((float)rng.NextDouble() * 6 - 3, 0f, 0f));
        }
        for (int i = 0; i < 120 * 6; i++) w.Step();
        var states = new List<BodyState>();
        w.ReadBack(states);
        bodies = states.Count;
        ulong h = 14695981039346656037UL;
        void Mix(float f) { h ^= (uint)BitConverter.SingleToInt32Bits(f); h *= 1099511628211UL; }
        foreach (var s in states)
        {
            Mix(s.Position.X); Mix(s.Position.Y); Mix(s.Position.Z);
            Mix(s.Orientation.X); Mix(s.Orientation.Y); Mix(s.Orientation.Z); Mix(s.Orientation.W);
            Mix(s.Velocity.X); Mix(s.Velocity.Y); Mix(s.Velocity.Z);
        }
        return h;
    }

    [Fact]
    public void TheSameSceneTwiceGivesTheSameBitsOnAnyThread()
    {
        ulong first = BusyScene(out int n);
        ulong second = BusyScene(out _);
        // The world has no worker threads, so neither the thread that steps it nor the core count the process was told
        // may matter: the same scene on another thread too. The suite is also run with DOTNET_PROCESSOR_COUNT=4.
        ulong other = 0;
        var t = new Thread(() => other = BusyScene(out _), 16 * 1024 * 1024);
        t.Start(); t.Join();
        _out.WriteLine($"{n} bodies, hash {first:x16}, processor count {System.Environment.ProcessorCount}");
        Assert.Equal(62, n);                 // the floor, the slab and sixty
        Assert.Equal(first, second);
        Assert.Equal(first, other);
    }
}
