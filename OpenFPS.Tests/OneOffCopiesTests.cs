using System.Diagnostics;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

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
        public void Ring(Vector3 at, Vector3 ears)
        {
            var ring = new TransientSound
            {
                Character = SoundCharacter.Ring, Hz = 900f, LevelDb = 80f, DecaySeconds = 4f, Noisiness = 0.2f, Position = at,
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
