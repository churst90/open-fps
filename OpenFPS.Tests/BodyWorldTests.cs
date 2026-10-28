using System.Numerics;
using OpenFPS.Geometry;
using OpenFPS.Physics;

namespace OpenFPS.Tests;

/// <summary>
/// The body API of physics stage 4, steps 1 and 2 (docs/PHYSICS_BOUNDARY.md 18.2): Jolt's native library loads and
/// starts, and every value the world sets reads back as it was set, because the 2.22.0 binding once ignored a setter
/// without a word (14.8). Scenes are small and run on the calling thread alone.
/// </summary>
public class BodyWorldTests
{
    private static readonly Quaternion Upright = Quaternion.Identity;

    private static int Floor(BodyWorld w)
    {
        // Top face at height 0.
        int shape = w.AddBox(new Vector3(50f, 0.5f, 50f));
        return w.Add(shape, BodyMotion.Static, new Vector3(0f, -0.5f, 0f), Upright, "Concrete");
    }

    private static void Run(BodyWorld w, int steps)
    {
        for (int i = 0; i < steps; i++) w.Step();
    }

    // ── The native library ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheNativeLibraryLoadsAndStarts()
    {
        Assert.True(BodyWorld.TryStartNative(out var problem), problem);
        Assert.True(BodyWorld.TryStartNative(out _), "starting twice is allowed");
        using var w = new BodyWorld();
        Assert.Equal(0, w.BodyCount);
    }

    [Fact]
    public void TheNativeLibraryIsBesideTheExecutable()
    {
        string name = OperatingSystem.IsWindows() ? "joltc.dll" : OperatingSystem.IsMacOS() ? "libjoltc.dylib" : "libjoltc.so";
        string root = AppContext.BaseDirectory;
        bool found = File.Exists(Path.Combine(root, name))
                     || Directory.EnumerateFiles(Path.Combine(root, "runtimes"), name, SearchOption.AllDirectories).Any();
        Assert.True(found, $"{name} is neither in {root} nor under its runtimes folder");
    }

    // ── Gravity is the world's ─────────────────────────────────────────────────────────────────

    [Fact]
    public void GravityIsEarthsByDefaultAndReadsBackWhatWasSet()
    {
        using var w = new BodyWorld();
        Assert.Equal(Earth.StandardGravity, w.Gravity);
        w.Gravity = 3.71f;
        Assert.Equal(3.71f, w.Gravity);
        using var moon = new BodyWorld(1.62f);
        Assert.Equal(1.62f, moon.Gravity);
    }

    [Fact]
    public void ABallFallsAtTheWorldsGravity()
    {
        foreach (float g in new[] { Earth.StandardGravity, 1.62f, 0f })
        {
            using var w = new BodyWorld(g);
            int ball = w.Add(w.AddSphere(0.1f), BodyMotion.Dynamic, new Vector3(0f, 100f, 0f), Upright, "Metal", massKg: 1f);
            Run(w, 120);                       // one second
            Assert.Equal(-g, w.Velocity(ball).Y, 0.02f);
            Assert.Equal(100f - 0.5f * g, w.Position(ball).Y, 0.05f);
        }
    }

    // ── What is set is what is read ────────────────────────────────────────────────────────────

    [Fact]
    public void PoseVelocityAndMassReadBack()
    {
        using var w = new BodyWorld(0f);
        int ball = w.Add(w.AddSphere(0.2f), BodyMotion.Dynamic, new Vector3(1f, 2f, 3f), Upright, "Metal", massKg: 12.5f);
        Assert.Equal(new Vector3(1f, 2f, 3f), w.Position(ball));
        Assert.Equal(12.5f, w.Mass(ball), 1e-3f);

        var turn = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.7f, 0.2f, -0.4f));
        w.SetPose(ball, new Vector3(-4f, 5f, 6f), turn);
        Assert.Equal(new Vector3(-4f, 5f, 6f), w.Position(ball));
        var back = w.Orientation(ball);
        Assert.True(MathF.Abs(Quaternion.Dot(back, turn)) > 0.99999f, $"{back} against {turn}");

        w.SetVelocity(ball, new Vector3(1.5f, -2f, 0.25f));
        Assert.Equal(new Vector3(1.5f, -2f, 0.25f), w.Velocity(ball));
        w.SetAngularVelocity(ball, new Vector3(0.1f, 0.2f, 0.3f));
        Assert.Equal(new Vector3(0.1f, 0.2f, 0.3f), w.AngularVelocity(ball));

        w.SetMass(ball, 3f);
        Assert.Equal(3f, w.Mass(ball), 1e-4f);
        Assert.Equal(3f, w.State(ball).MassKg, 1e-4f);
    }

    [Fact]
    public void AWorldOutlivesACollection()
    {
        // The solver points at its layer tables without owning them; when only the constructor held them the collector
        // freed them under a live world and the next step took the process down (2026-10-27).
        using var w = new BodyWorld();
        int ball = w.Add(w.AddSphere(0.1f), BodyMotion.Dynamic, new Vector3(0f, 1f, 0f), Upright, "Metal", 1f);
        w.Add(w.AddBox(new Vector3(5f, 0.5f, 5f)), BodyMotion.Static, new Vector3(0f, -0.5f, 0f), Upright, "Concrete");
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Run(w, 60);
        }
        Assert.True(w.Position(ball).Y < 0.2f);
    }

    [Fact]
    public void NoDragIsAddedThatTheWorldDidNotAsk()
    {
        // Jolt's default damping is 5 percent a second, which would take 0.24 m/s off a body in free fall in one second.
        using var w = new BodyWorld(0f);
        int ball = w.Add(w.AddSphere(0.1f), BodyMotion.Dynamic, new Vector3(0f, 1000f, 0f), Upright, "Metal", 1f);
        w.SetVelocity(ball, new Vector3(30f, 0f, 0f));
        w.SetAngularVelocity(ball, new Vector3(0f, 5f, 0f));
        Run(w, 240);
        Assert.Equal(new Vector3(30f, 0f, 0f), w.Velocity(ball));
        Assert.Equal(new Vector3(0f, 5f, 0f), w.AngularVelocity(ball));
    }

    [Fact]
    public void FrictionAndRestitutionComeFromTheMaterialAndReadBack()
    {
        using var w = new BodyWorld();
        int shape = w.AddSphere(0.1f);
        // Glass: kinetic 0.4, restitution 0.93. Wood: no kinetic value, so its static 0.38, and no restitution, so the
        // unmeasured one. Neither is zero.
        int glass = w.Add(shape, BodyMotion.Dynamic, new Vector3(0f, 1f, 0f), Upright, "Glass", 1f);
        int wood = w.Add(shape, BodyMotion.Dynamic, new Vector3(1f, 1f, 0f), Upright, "Wood", 1f);
        Assert.Equal(0.4f, w.Friction(glass));
        Assert.Equal(0.93f, w.Restitution(glass));
        Assert.Equal(0.38f, w.Friction(wood));
        Assert.Equal(ContactMaterial.UnmeasuredRestitution, w.Restitution(wood));
        Assert.Equal(new ContactMaterial(0.4f, 0.93f), ContactMaterial.Of("Glass"));

        w.SetFriction(wood, 0.9f);
        w.SetRestitution(wood, 0.25f);
        Assert.Equal(0.9f, w.Friction(wood));
        Assert.Equal(0.25f, w.Restitution(wood));
    }

    [Fact]
    public void AnUnknownMaterialIsGenericNotFrictionless()
    {
        var generic = ContactMaterial.Of("Generic");
        var nothing = ContactMaterial.Of("NoSuchStuff");
        Assert.True(nothing.Friction > 0f);
        Assert.Equal(generic, nothing);
        Assert.Equal(generic, ContactMaterial.Of(null));
    }

    [Fact]
    public void ReadBackListsEveryBodyByHandleAsABodyState()
    {
        using var w = new BodyWorld();
        int floor = Floor(w);
        int shape = w.AddCapsule(0.4f, 0.3f);
        int a = w.Add(shape, BodyMotion.Dynamic, new Vector3(0f, 3f, 0f), Upright, "Metal", 70f);
        int b = w.Add(shape, BodyMotion.Kinematic, new Vector3(5f, 3f, 0f), Upright, "Metal");
        var all = new List<BodyState> { default };
        w.ReadBack(all);
        Assert.Equal(new[] { floor, a, b }, all.Select(s => s.Id).ToArray());
        Assert.True(all[0].Sleeping, "a static body is at rest");
        Assert.Equal(0f, all[0].MassKg);
        Assert.Equal(70f, all[1].MassKg, 1e-3f);
        Assert.False(all[1].Sleeping);
        Assert.Equal(shape, all[1].ShapeId);
        Assert.Equal(new Vector3(0f, 3f, 0f), all[1].Position);
        Assert.Equal(Grounding.Unknown, all[1].Grounding);
    }

    // ── The shapes ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASphereABoxAndACapsuleComeToRestOnAFloor()
    {
        using var w = new BodyWorld();
        Floor(w);
        int ball = w.Add(w.AddSphere(0.25f), BodyMotion.Dynamic, new Vector3(-2f, 1f, 0f), Upright, "Dirt", 2f);
        int box = w.Add(w.AddBox(new Vector3(0.3f, 0.2f, 0.1f)), BodyMotion.Dynamic, new Vector3(0f, 1f, 0f), Upright, "Dirt", 5f);
        int capsule = w.Add(w.AddCapsule(0.5f, 0.3f), BodyMotion.Dynamic, new Vector3(2f, 1.5f, 0f), Upright, "Dirt", 70f);
        Run(w, 480);
        Assert.Equal(0.25f, w.Position(ball).Y, 0.01f);
        Assert.Equal(0.2f, w.Position(box).Y, 0.01f);
        Assert.Equal(0.8f, w.Position(capsule).Y, 0.01f);
    }

    [Fact]
    public void ATriangleMeshFromOurOwnAssetIsAFloor()
    {
        // Two triangles, counter-clockwise from above, so the face normal is up: (b - a) x (c - a).
        var mesh = new MeshAsset(
            new[] { new Vector3(-50f, 0f, -50f), new Vector3(-50f, 0f, 50f), new Vector3(50f, 0f, 50f), new Vector3(50f, 0f, -50f) },
            new[] { 0, 1, 2, 0, 2, 3 }, new byte[2], closed: false, convex: false);
        using var w = new BodyWorld();
        int ground = w.Add(w.AddMesh(mesh), BodyMotion.Static, Vector3.Zero, Upright, "Concrete");
        int ball = w.Add(w.AddSphere(0.25f), BodyMotion.Dynamic, new Vector3(3f, 2f, -4f), Upright, "Dirt", 1f);
        Run(w, 480);
        Assert.Equal(0.25f, w.Position(ball).Y, 0.01f);
        Assert.Equal(Vector3.Zero, w.Position(ground));
    }

    [Fact]
    public void ATriangleMeshCannotBeDynamic()
    {
        var mesh = new MeshAsset(new[] { Vector3.Zero, Vector3.UnitZ, Vector3.UnitX }, new[] { 0, 1, 2 }, new byte[1], false, false);
        using var w = new BodyWorld();
        int shape = w.AddMesh(mesh);
        Assert.Throws<ArgumentException>(() => w.Add(shape, BodyMotion.Dynamic, Vector3.Zero, Upright, "Concrete", 1f));
    }

    [Fact]
    public void AKinematicBodyIsNotMovedByGravity()
    {
        using var w = new BodyWorld();
        int leaf = w.Add(w.AddBox(new Vector3(0.5f, 1f, 0.05f)), BodyMotion.Kinematic, new Vector3(0f, 5f, 0f), Upright, "Wood");
        Run(w, 120);
        Assert.Equal(new Vector3(0f, 5f, 0f), w.Position(leaf));
        Assert.Equal(0f, w.Mass(leaf));
    }

    // ── Handles, removal, disposal ─────────────────────────────────────────────────────────────

    [Fact]
    public void BodiesAndShapesComeAndGoByHandle()
    {
        using var w = new BodyWorld();
        int shape = w.AddSphere(0.1f);
        int a = w.Add(shape, BodyMotion.Dynamic, new Vector3(0f, 1f, 0f), Upright, "Metal", 1f);
        int b = w.Add(shape, BodyMotion.Dynamic, new Vector3(1f, 1f, 0f), Upright, "Metal", 1f);
        Assert.NotEqual(a, b);
        Assert.Equal(2, w.BodyCount);
        Assert.Throws<InvalidOperationException>(() => w.RemoveShape(shape));
        w.Remove(a);
        Assert.False(w.Has(a));
        Assert.True(w.Has(b));
        Assert.Throws<KeyNotFoundException>(() => w.Position(a));
        Assert.Throws<KeyNotFoundException>(() => w.Remove(a));
        w.Remove(b);
        w.RemoveShape(shape);
        Assert.Equal(0, w.ShapeCount);
        int c = w.Add(w.AddSphere(0.1f), BodyMotion.Dynamic, Vector3.Zero, Upright, "Metal", 1f);
        Assert.True(c > b, "a handle is never reused");
        Run(w, 4);
    }

    [Fact]
    public void AWorldIsUsableAfterAnotherIsDisposed()
    {
        var first = new BodyWorld();
        first.Add(first.AddSphere(0.1f), BodyMotion.Dynamic, Vector3.Zero, Upright, "Metal", 1f);
        first.Dispose();
        first.Dispose();                       // twice is fine
        Assert.Throws<ObjectDisposedException>(() => first.Step());
        using var second = new BodyWorld();
        int ball = second.Add(second.AddSphere(0.1f), BodyMotion.Dynamic, new Vector3(0f, 10f, 0f), Upright, "Metal", 1f);
        Run(second, 12);
        Assert.True(second.Position(ball).Y < 10f);
    }

    [Fact]
    public void BadSizesAndMassesAreRefused()
    {
        using var w = new BodyWorld();
        Assert.Throws<ArgumentOutOfRangeException>(() => w.AddSphere(0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.AddBox(new Vector3(1f, -1f, 1f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.AddCapsule(float.NaN, 0.3f));
        int shape = w.AddSphere(0.1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Add(shape, BodyMotion.Dynamic, Vector3.Zero, Upright, "Metal", 0f));
        Assert.Throws<KeyNotFoundException>(() => w.Add(99, BodyMotion.Static, Vector3.Zero, Upright, "Metal"));
    }

    // ── The step ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheStepIs120HzAndFourMakeATick()
    {
        Assert.Equal(1f / 120f, BodyWorld.StepSeconds);
        Assert.Equal(4, BodyWorld.StepsPerTick);
        Assert.Equal(1f / 30f, BodyWorld.StepSeconds * BodyWorld.StepsPerTick, 1e-7f);

        BodyState Fall(Action<BodyWorld> advance)
        {
            using var w = new BodyWorld();
            int ball = w.Add(w.AddSphere(0.1f), BodyMotion.Dynamic, new Vector3(0f, 50f, 0f), Upright, "Metal", 1f);
            advance(w);
            return w.State(ball);
        }
        var byTicks = Fall(w => { for (int i = 0; i < 30; i++) w.Tick(); });
        var bySteps = Fall(w => Run(w, 120));
        Assert.Equal(bySteps, byTicks);
        // Thirty ticks are a second of fall.
        Assert.Equal(-Earth.StandardGravity, byTicks.Velocity.Y, 0.02f);
    }

    private static ulong Scene()
    {
        using var w = new BodyWorld();
        Floor(w);
        int ball = w.AddSphere(0.25f), box = w.AddBox(new Vector3(0.3f, 0.2f, 0.1f)), capsule = w.AddCapsule(0.5f, 0.3f);
        var rng = new Random(7);
        for (int i = 0; i < 60; i++)
        {
            var at = new Vector3((float)rng.NextDouble() * 4f - 2f, 1f + i * 0.5f, (float)rng.NextDouble() * 4f - 2f);
            var turn = Quaternion.CreateFromYawPitchRoll((float)rng.NextDouble() * 6f, (float)rng.NextDouble(), 0f);
            int shape = (i % 3) switch { 0 => ball, 1 => box, _ => capsule };
            int id = w.Add(shape, BodyMotion.Dynamic, at, turn, i % 2 == 0 ? "Wood" : "Rubber", 1f + i);
            w.SetVelocity(id, new Vector3(1f, 0f, -0.5f));
        }
        for (int i = 0; i < 480; i++) w.Step();
        var states = new List<BodyState>();
        w.ReadBack(states);
        ulong hash = 14695981039346656037UL;
        void Mix(float f) { hash ^= BitConverter.SingleToUInt32Bits(f); hash *= 1099511628211UL; }
        foreach (var s in states)
        {
            Mix(s.Position.X); Mix(s.Position.Y); Mix(s.Position.Z);
            Mix(s.Orientation.X); Mix(s.Orientation.Y); Mix(s.Orientation.Z); Mix(s.Orientation.W);
            Mix(s.Velocity.X); Mix(s.Velocity.Y); Mix(s.Velocity.Z);
            Mix(s.AngularVelocity.X); Mix(s.AngularVelocity.Y); Mix(s.AngularVelocity.Z);
        }
        return hash;
    }

    [Fact]
    public void TheSameSceneTwiceGivesTheSamePositionsBitForBit()
    {
        ulong first = Scene();
        ulong second = Scene();
        Assert.Equal(first, second);
        Assert.NotEqual(14695981039346656037UL, first);
    }
}
