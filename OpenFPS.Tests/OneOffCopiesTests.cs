using System.Diagnostics;
using System.Numerics;
using OpenFPS.Audio.Acoustics;
using OpenFPS.Audio.Core;
using OpenFPS.Client.Core;
using OpenFPS.Materials;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Geometry;
using OpenFPS.Sound;

namespace OpenFPS.Tests;

/// <summary>
/// A one-off sound's copies are found for where the listener is as it begins (WorldAudioPlayer.QueueEarlyEchoes and
/// the rest). For a knock that is right; a ring that lasts seconds is walked away from while it plays. Each copy is
/// heard only while the way from where it last came off to the ears is clear (WorldAudioPlayer.KeepCopiesTrue):
/// begun with the listener in a room, a telephone was heard by its copies, in full, from behind the wall of the
/// next. Coordinates are the engine's: x east, y up, z north.
/// </summary>
public class OneOffCopiesTests
{
    public OneOffCopiesTests() { AcousticRegistry.EnsureInitialized(); }

    private static readonly Vector3 Ears = new(0f, 1.6f, 0f);
    private static readonly Vector3 Ahead = new(0f, 1.6f, 3f);
    /// <summary>South, behind the screen from the east wall.</summary>
    private static readonly Vector3 Behind = new(0f, 1.6f, -6f);

    private const int EastWall = 1, Screen = 2, Leaf = 3;

    /// <summary>A brick wall whose face is the plane x = 4, and a screen at x = 2 from z = -10 to -2, between that
    /// wall and anyone standing at <see cref="Behind"/>. Its triangles are built at once, as the game builds them on
    /// a worker.</summary>
    private static ClientWorldState Room()
    {
        var world = new ClientWorldState();
        world.Geometry.Runner = work => work();
        world.Clear(new Vector3(200, 40, 200));
        world.RegisterDefinition(Box(EastWall, new Vector3(4.1f, 1.5f, 0f), new Vector3(0.2f, 20f, 40f)));
        world.RegisterDefinition(Box(Screen, new Vector3(2f, 1.5f, -6f), new Vector3(0.1f, 20f, 8f)));
        return world;
    }

    private static EntityDefinition Box(int id, Vector3 centre, Vector3 size, string material = "Brick") => new()
    {
        EntityId = id,
        Type = EntityType.StaticObject,
        Transform = new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
        Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
        Material = new MaterialComponent { Material = material },
    };

    /// <summary>The one-off sounds over a mixer that only records, pumped by hand.</summary>
    private sealed class Rig
    {
        public readonly RecordingMixer Mixer = new();
        public readonly AudioEngineFacade Facade;
        public readonly OneOffSounds Player;
        public readonly ClientWorldState World;

        public Rig(ClientWorldState world)
        {
            World = world;
            Facade = new AudioEngineFacade(Mixer);
            Facade.InitializeForTest();
            Player = new OneOffSounds(Facade, new SpatialAcoustics(), prewarm: false);
        }

        /// <summary>One update with the ears at <paramref name="ears"/>, then the audio thread's turn.</summary>
        public void Hear(Vector3 ears, double now)
        {
            Facade.UpdateListener(ears, Quaternion.Identity, Vector3.Zero, -1);
            var snap = World.GetSnapshot();
            Player.Update(snap, ears, now, air: snap.Outdoors);
            // Twice: a submission is queued as a direct play, which the next pump hands to the mixer.
            Facade.PumpForTest();
            Facade.PumpForTest();
        }

        /// <summary>
        /// A ring of four seconds at <paramref name="at"/>, heard from <paramref name="ears"/>: rendered on a worker,
        /// then played with its copies. The clock stands still meanwhile, so it is never too late.
        /// </summary>
        public void Ring(Vector3 at, Vector3 ears, Vector3? movesTo = null, float moveSeconds = 0f)
        {
            var ring = new TransientSound
            {
                Character = SoundCharacter.Ring, Hz = 900f, LevelDb = 80f, DecaySeconds = 4f, Noisiness = 0.2f, Position = at,
                MovesTo = movesTo ?? default, MoveSeconds = moveSeconds,
            };
            new WorldAudioPlayer(Player).Receive(new WorldAudioEvent { SourceEntityId = -1, Label = "ring", Sounds = new() { ring }, Seed = 1 }, 0.0);
            var clock = Stopwatch.StartNew();
            while (!Mixer.Started.Exists(e => Vector3.Distance(e.Position, at) < 0.01f) && clock.Elapsed < TimeSpan.FromSeconds(60))
            {
                Hear(ears, 0.0);
                System.Threading.Thread.Sleep(2);
            }
            Assert.True(Mixer.Started.Exists(e => Vector3.Distance(e.Position, at) < 0.01f), "the ring never played");
        }

        /// <summary>The copies off the east wall: placed at their images, beyond it.</summary>
        public List<int> OffTheEastWall() => Mixer.Started.Where(e => e.Position.X > 6f).Select(e => e.EntityId).ToList();

        /// <summary>What a voice's middle band is now, as the mixer was last told.</summary>
        public float Mid(int voice) => Mixer.Latest[voice].EqMid;

        /// <summary>The east wall's mirror copy (the clean one, not its wash): the copy placed at the image of the source.</summary>
        public int MirrorOffTheEastWall() => OffTheEastWall().OrderByDescending(c => Mixer.Started.First(e => e.EntityId == c).Volume).First();
    }

    /// <summary>A ring carried 3 m north along the east wall over two seconds, as a sliding door's run is carried across
    /// its doorway (TransientSound.MovesTo).</summary>
    private static readonly Vector3 CarriedTo = new(0f, 1.6f, 6f);

    /// <summary>
    /// A one-off that moves as it plays has its copies found again from where it is (OneOffSounds.FollowCopies): the
    /// east wall's copy goes north with it, to the image of where it is, and is placed there as a one-off begun there
    /// places its copy. Before, the copy stayed at the image of where it began, and the ring seemed to stay with it.
    /// </summary>
    [Fact]
    public void ACopyGoesWithAOneOffThatMovesAsItPlays()
    {
        var rig = new Rig(Room());
        rig.Ring(Ahead, Ears, CarriedTo, moveSeconds: 2f);
        int copy = rig.MirrorOffTheEastWall();
        Assert.Equal(3f, rig.Mixer.Latest[copy].Position.Z, 2);
        rig.Hear(Ears, 1.0);
        rig.Hear(Ears, 2.0);
        var moved = rig.Mixer.Latest[copy];
        Assert.True(rig.Mixer.Live.Contains(copy), "the copy was stopped, not moved");
        Assert.Equal(8f, moved.Position.X, 2);
        Assert.Equal(6f, moved.Position.Z, 2);
        Assert.Equal(moved.Position, moved.ApparentPosition);
        Assert.True(moved.CarriesPath, "a moved copy must carry its path, or the mixer does not glide its bands to it");

        // The same ring begun where the moving one has got to: its copy, placed by the play loop.
        var still = new Rig(Room());
        still.Ring(CarriedTo, Ears);
        var fresh = still.Mixer.Latest[still.MirrorOffTheEastWall()];
        Assert.Equal(fresh.Position.Z, moved.Position.Z, 3);
        Assert.Equal(fresh.Volume, moved.Volume, 4);
        Assert.Equal(fresh.EqLow, moved.EqLow, 4);
        Assert.Equal(fresh.EqHigh, moved.EqHigh, 4);
        Assert.Equal(fresh.EffectiveDistance, moved.EffectiveDistance, 3);
        Assert.Equal(fresh.AirHighDb, moved.AirHighDb, 3);
    }

    /// <summary>
    /// A wall built of two solids in one plane (a facade's storey bands, a wall in two lengths) answers a moving
    /// one-off from one and then the other: its copy goes on from the second, where its image has gone, rather than
    /// dropping out because the surface's id has changed.
    /// </summary>
    [Fact]
    public void ACopyGoesOnWhenItsWallIsAnotherSolidInTheSamePlane()
    {
        var world = new ClientWorldState();
        world.Geometry.Runner = work => work();
        world.Clear(new Vector3(200, 40, 200));
        world.RegisterDefinition(Box(EastWall, new Vector3(4.1f, 1.5f, -8f), new Vector3(0.2f, 20f, 24f)));
        world.RegisterDefinition(Box(Screen, new Vector3(4.1f, 1.5f, 12f), new Vector3(0.2f, 20f, 16f)));
        var rig = new Rig(world);
        // The bounce, half way along, goes from z = 1.5 on the first length to z = 6 on the second.
        rig.Ring(Ahead, Ears, new Vector3(0f, 1.6f, 12f), moveSeconds: 1f);
        int copy = rig.MirrorOffTheEastWall();
        for (int k = 1; k <= 12; k++) rig.Hear(Ears, k * 0.1);
        var moved = rig.Mixer.Latest[copy];
        Assert.True(rig.Mid(copy) > 0.1f, $"the copy dropped out at {rig.Mid(copy):0.000} when the second length took over");
        Assert.Equal(12f, moved.Position.Z, 2);
        Assert.Equal(8f, moved.Position.X, 2);
    }

    /// <summary>
    /// A moving one-off whose wall no longer answers it from where it has gone (the way from the wall to the ears now
    /// passes the screen) has that copy faded out, still playing, rather than left sounding where the source began.
    /// </summary>
    [Fact]
    public void ACopyItsWallNoLongerAnswersFadesAsItsSourceMoves()
    {
        var rig = new Rig(Room());
        // South, so the east wall's bounce goes behind the screen: from (4, -7) to the ears it crosses x = 2 at z = -3.5.
        rig.Ring(Ahead, Ears, new Vector3(0f, 1.6f, -14f), moveSeconds: 1f);
        int copy = rig.MirrorOffTheEastWall();
        Assert.True(rig.Mid(copy) > 0.1f);
        rig.Hear(Ears, 1.5);
        Assert.True(rig.Mixer.Live.Contains(copy), "a copy no longer answered is faded, never cut");
        Assert.True(rig.Mid(copy) < 1e-3f, $"the copy still heard at {rig.Mid(copy):0.000} off a wall that no longer answers");
    }

    /// <summary>Two one-offs moving at once (the two leaves of a sliding pair) have their copies found one in each
    /// update, each in its turn: the search is never made twice in one update.</summary>
    [Fact]
    public void OneMovingOneOffsCopiesAreFoundInEachUpdateEachInItsTurn()
    {
        var rig = new Rig(Room());
        var a = new TransientSound
        {
            Character = SoundCharacter.Ring, Hz = 900f, LevelDb = 80f, DecaySeconds = 4f, Noisiness = 0.2f,
            Position = Ahead, MovesTo = CarriedTo, MoveSeconds = 2f,
        };
        var b = a with { Position = Ahead + new Vector3(0f, 0f, -1f), MovesTo = CarriedTo + new Vector3(0f, 0f, -1f), Hz = 700f };
        new WorldAudioPlayer(rig.Player).Receive(new WorldAudioEvent { SourceEntityId = -1, Label = "pair", Sounds = new() { a, b }, Seed = 1 }, 0.0);
        var clock = Stopwatch.StartNew();
        while (rig.Mixer.Started.Count(e => e.Position.X > 6f) < 4 && clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            rig.Hear(Ears, 0.0);
            System.Threading.Thread.Sleep(2);
        }
        var copies = rig.OffTheEastWall();
        Assert.True(copies.Count >= 4, "both leaves' copies off the east wall");
        var began = copies.ToDictionary(c => c, c => rig.Mixer.Latest[c].Position);
        for (int k = 1; k <= 4; k++)
        {
            rig.Hear(Ears, k * 0.2);
            Assert.Equal(k, rig.Player.CopyFinds);
        }
        Assert.All(copies, c => Assert.NotEqual(began[c], rig.Mixer.Latest[c].Position));
    }

    /// <summary>
    /// The copies are searched for again only while their one-off moves (or the ears do), and at most twenty times a
    /// second: once a run has got where it was going, none, and never for a one-off that stays where it began, however
    /// the ears move. The cost is the moving ones' alone.
    /// </summary>
    [Fact]
    public void CopiesAreFoundAgainOnlyWhileTheirOneOffMoves()
    {
        var rig = new Rig(Room());
        rig.Ring(Ahead, Ears, CarriedTo, moveSeconds: 1f);
        rig.Hear(Ears, 0.5);
        rig.Hear(Ears, 1.0);
        Assert.Equal(2, rig.Player.CopyFinds);
        for (int k = 0; k < 10; k++) rig.Hear(Ears, 1.2 + k * 0.1);
        Assert.Equal(2, rig.Player.CopyFinds);

        // Carried 12 m in a second and heard a hundred times in it: twenty times a second at most.
        var fast = new Rig(Room());
        fast.Ring(Ahead, Ears, new Vector3(0f, 1.6f, 15f), moveSeconds: 1f);
        for (int k = 1; k <= 100; k++) fast.Hear(Ears, k * 0.01);
        Assert.InRange(fast.Player.CopyFinds, 15, 21);

        var still = new Rig(Room());
        still.Ring(Ahead, Ears);
        int copy = still.MirrorOffTheEastWall();
        var placed = still.Mixer.Latest[copy].Position;
        still.Hear(Ears + new Vector3(0f, 0f, 1f), 0.5);
        still.Hear(Ears + new Vector3(0f, 0f, 2f), 1.0);
        Assert.Equal(0, still.Player.CopyFinds);
        Assert.Equal(placed, still.Mixer.Latest[copy].Position);
    }

    /// <summary>A copy that came off the east wall reaches ears with nothing between them and where it came off, and
    /// not ears behind the screen, nor ears the other side of the wall itself; by triangles and by boxes alike.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACopyReachesOnlyEarsWithAClearWayFromWhereItCameOff(bool triangles)
    {
        var world = Room().GetSnapshot();
        if (!triangles) world.Geometry = null;
        else Assert.NotNull(world.Geometry);
        var bounce = new Vector3(4f, 1.6f, 1.5f);
        Assert.True(OneOffSounds.Reaches(world, bounce, Ears));
        Assert.False(OneOffSounds.Reaches(world, bounce, Behind));
        Assert.False(OneOffSounds.Reaches(world, bounce, new Vector3(8f, 1.6f, 1.5f)));
        Assert.True(OneOffSounds.Reaches(world, bounce, bounce));
    }

    [Fact]
    public void ACopyIsNotHeardFromBehindAWallWalkedRoundAfterItsSoundBegan()
    {
        var rig = new Rig(Room());
        rig.Ring(Ahead, Ears);
        var copies = rig.OffTheEastWall();
        Assert.NotEmpty(copies);
        var heard = copies.ToDictionary(c => c, rig.Mid);
        Assert.All(copies, c => Assert.True(heard[c] > 0.1f, $"copy {c} began at {heard[c]:0.000}"));
        rig.Hear(Ears, 0.25);
        Assert.All(copies, c => Assert.Equal(heard[c], rig.Mid(c)));

        // Behind the screen: still ringing, and none of the wall's copies heard.
        rig.Hear(Behind, 0.5);
        Assert.All(copies, c => Assert.True(rig.Mixer.Live.Contains(c), $"copy {c} was stopped"));
        Assert.All(copies, c => Assert.True(rig.Mid(c) < 1e-3f, $"copy {c} still heard at {rig.Mid(c):0.000} from behind the screen"));

        // Out again: each as it began.
        rig.Hear(Ears, 1.0);
        Assert.All(copies, c => Assert.Equal(heard[c], rig.Mid(c)));
    }

    /// <summary>The same with the ears still and a door shut across the way from the wall to them.</summary>
    [Fact]
    public void ACopyIsNotHeardThroughADoorShutOnItAfterItsSoundBegan()
    {
        var world = Room();
        // A leaf of a doorway, standing open well away to the south.
        var leaf = Box(Leaf, new Vector3(2f, 1.5f, -30f), new Vector3(0.1f, 20f, 1f), "Wood");
        leaf.Portal = new PortalComponent { RegionAId = 100, RegionBId = 101 };
        world.RegisterDefinition(leaf);
        var rig = new Rig(world);
        rig.Ring(Ahead, Ears);
        var copies = rig.OffTheEastWall();
        Assert.NotEmpty(copies);
        rig.Hear(Ears, 0.25);
        Assert.All(copies, c => Assert.True(rig.Mid(c) > 0.1f, $"copy {c} at {rig.Mid(c):0.000} before the door was shut"));

        // Shut across the way from the wall's copies to the ears (from about (4, 1.5) to (0, 0) in x, z).
        world.SyncState(new[] { new EntityState
        {
            EntityId = Leaf,
            Transform = QuantizedTransform.FromTransform(new Transform { Position = new Vector3(2f, 1.5f, 0.75f), Rotation = Quaternion.Identity, Scale = Vector3.One }),
        } });
        rig.Hear(Ears, 0.5);
        Assert.All(copies, c => Assert.True(rig.Mid(c) < 1e-3f, $"copy {c} still heard at {rig.Mid(c):0.000} through the shut door"));
    }
}
