using System.Numerics;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A car's power windows: rolled down and up from a seat, over the motor's own travel; a hole in the
/// cabin's wall when they are down, so the street comes in and a person talking in the car is heard out
/// in the street; and the window's sound, simulated from its motor, worm and glass.
/// </summary>
public class CarWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-windows-{Guid.NewGuid():N}");
    private readonly ITestOutputHelper _o;
    public CarWindowTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }
    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    // ── The command ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// /window from the seat: the clients are told where the glass is going, the motors are heard, one in
    /// each front door and each its own character, and the glass gets there in the motor's own time, about
    /// three seconds. Again, and it comes back up.
    /// </summary>
    [Fact]
    public void TheWindowCommandRollsTheWindowsDownAndUpOverTheMotorsTravel()
    {
        var f = new Fixture(_dir);
        var (root, car) = f.ParkedCar(new Vector3(20, 0, 20));
        var driver = f.Player("driver_one", new Vector3(19, 0, 20));
        Assert.True(f.Seats.Enter(driver, root, null, out string entered), entered);

        Assert.InRange(CarWindow.DownSeconds, 2.0f, 3.5f);
        Assert.InRange(CarWindow.UpSeconds, CarWindow.DownSeconds, 4.0f);   // lifting the glass is the slower way

        int resent = -1;
        var heard = new List<TransientSound>();
        string said = WindowSystem.Command(f.World, driver.Entity, f.Lookup, Array.Empty<string>(),
                                           id => resent = id, (_, _, s) => heard.AddRange(s));
        Assert.Equal("You roll the windows down.", said);
        Assert.Equal(root, resent);
        Assert.Equal(1f, f.World.Get<SoundEmitterComponent>(car).WindowsOpen);
        // A hatchback has two rows, so four doors' motors, each a different window.
        Assert.Equal(4, heard.Count);
        Assert.All(heard, s => Assert.True(CarWindow.TryParseKey(s.SynthKey, out _, out float from, out float to) && from == 0f && to == 1f, s.SynthKey));
        Assert.Equal(4, heard.Select(s => s.SynthKey).Distinct().Count());
        Assert.All(heard, s => Assert.True(s.OnBody));

        // One second in, a third of the way or so; past the motor's travel, all the way.
        f.Tick(30);
        float open = f.World.Get<WindowsComponent>(car).Open;
        Assert.InRange(open, 1f / CarWindow.DownSeconds - 0.03f, 1f / CarWindow.DownSeconds + 0.03f);
        f.Tick((int)MathF.Ceiling(CarWindow.DownSeconds * 30) + 3);
        Assert.Equal(1f, f.World.Get<WindowsComponent>(car).Open);

        said = WindowSystem.Command(f.World, driver.Entity, f.Lookup, Array.Empty<string>(), null, null);
        Assert.Equal("You roll the windows up.", said);
        Assert.Equal(0f, f.World.Get<SoundEmitterComponent>(car).WindowsOpen);
        f.Tick((int)MathF.Floor(CarWindow.UpSeconds * 30) - 6);
        Assert.True(f.World.Get<WindowsComponent>(car).Open > 0f, "the glass went up faster than its motor drives it");
        f.Tick(10);
        Assert.Equal(0f, f.World.Get<WindowsComponent>(car).Open);
    }

    /// <summary>Half way, and asking for where they already are.</summary>
    [Fact]
    public void HalfWayAndAlreadyThere()
    {
        var f = new Fixture(_dir);
        var (root, car) = f.ParkedCar(new Vector3(20, 0, 20));
        var passenger = f.Player("passenger", new Vector3(21, 0, 20));
        Assert.True(f.Seats.Enter(passenger, root, "front passenger", out string entered), entered);

        Assert.Equal("The windows are already up.", WindowSystem.Command(f.World, passenger.Entity, f.Lookup, new[] { "up" }, null, null));
        Assert.Equal("You roll the windows half way down.", WindowSystem.Command(f.World, passenger.Entity, f.Lookup, new[] { "half" }, null, null));
        f.Tick(90);
        Assert.Equal(0.5, f.World.Get<WindowsComponent>(car).Open, 3);
        Assert.Equal("The windows are already half way.", WindowSystem.Command(f.World, passenger.Entity, f.Lookup, new[] { "half" }, null, null));
        Assert.Contains("or /window down, up or half", WindowSystem.Command(f.World, passenger.Entity, f.Lookup, new[] { "sideways" }, null, null));
    }

    /// <summary>Only someone sitting in the car can reach the switch.</summary>
    [Fact]
    public void OnlySomebodySeatedCanRollTheWindows()
    {
        var f = new Fixture(_dir);
        var (_, car) = f.ParkedCar(new Vector3(20, 0, 20));
        var walker = f.Player("walker", new Vector3(19, 0, 20));
        bool heard = false;
        string said = WindowSystem.Command(f.World, walker.Entity, f.Lookup, Array.Empty<string>(), null, (_, _, _) => heard = true);
        Assert.Equal("You are not sitting in anything.", said);
        Assert.False(heard);
        Assert.False(f.World.Has<WindowsComponent>(car));
        Assert.Equal(0f, f.World.Get<SoundEmitterComponent>(car).WindowsOpen);
    }

    // ── The hole in the wall ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The street through the cabin: the glass by its mass, the seals, and with the windows down the
    /// windows' own share of the wall at every frequency. The share is the open area over the cabin's
    /// whole wall area, worked out here from the cabin's own dimensions, and the loss with it is
    /// computed again from first principles.
    /// </summary>
    [Fact]
    public void WindowsDownTheCabinLossFallsByTheOpenShare()
    {
        var v = MachineRegistry.VehicleFor("i4_economy");
        var g = VehicleCabin.Measure(v)!.Value;
        int rows = VehicleCabin.Rows(g);
        float each = (g.Lc / rows - CarWindow.PillarM) * (g.RoofUnder - g.Belt - CarWindow.FrameM) * CarWindow.ShapeShare;
        float surface = 2f * (g.Lc * g.Wc + g.Lc * g.Hc + g.Wc * g.Hc);
        float share = 2 * rows * each / surface;
        _o.WriteLine($"{rows} rows, each window {each:F2} m2, cabin wall {surface:F1} m2: open share {share:P1}");
        Assert.InRange(each, 0.25f, 0.6f);                 // a side window is about a third of a square metre
        Assert.Equal((double)share, CarWindow.OpenShare(v, 1f), 4);
        Assert.Equal((double)share / 2, CarWindow.OpenShare(v, 0.5f), 4);

        float glass = 2500f * 0.004f, seal = v.Body!.SealLeak * v.Body.SealLeak;
        float Expected(float hz, float s)
        {
            float t = 415f / (MathF.PI * hz * glass);
            return 10f * MathF.Log10(MathF.Min(1f, t * t * (1 - s) + seal + s));
        }
        var shut = CabinWalls.LossDb(v, 0f);
        var down = CabinWalls.LossDb(v, 1f);
        _o.WriteLine($"shut {shut.Low:F1}/{shut.Mid:F1}/{shut.High:F1} dB, down {down.Low:F1}/{down.Mid:F1}/{down.High:F1} dB");
        Assert.Equal((double)Expected(150f, 0f), shut.Low, 2);
        Assert.Equal((double)Expected(1000f, 0f), shut.Mid, 2);
        Assert.Equal((double)Expected(4000f, 0f), shut.High, 2);
        Assert.Equal((double)Expected(150f, share), down.Low, 2);
        Assert.Equal((double)Expected(1000f, share), down.Mid, 2);
        Assert.Equal((double)Expected(4000f, share), down.High, 2);
        // Shut, the top end is about thirty decibels down; down, the hole is most of what gets in and the
        // street is barely ten down at any height.
        Assert.True(shut.High < -25f);
        Assert.True(down.High > -12f && down.Mid > -12f);
        Assert.True(down.High - shut.High > 15f);
        // A motorcycle has no cabin to open.
        Assert.Null(CarWindow.Measure(MachineRegistry.VehicleFor("sportbike")));
    }

    /// <summary>
    /// Somebody talking in a closed car, heard from the street: their voice is found inside the cabin, and
    /// the cabin takes its top end; windows down, much less of everything.
    /// </summary>
    [Fact]
    public void ATalkerInAClosedCarIsMuffledAndHeardWithTheWindowsDown()
    {
        var world = new WorldSnapshot();
        var rotation = Quaternion.CreateFromYawPitchRoll(0.6f, 0f, 0f);
        var car = new EntitySnapshot
        {
            Id = 7,
            Definition = new EntityDefinition { EntityId = 7, Moves = true },
            Transform = new Transform { Position = new Vector3(30f, 0f, -12f), Rotation = rotation },
        };
        car.Definition.SoundEmitter = new SoundEmitterComponent { SoundId = "engine:i4_economy", IsSynth = true };
        world.Entities[7] = car;
        world.DynamicEntities.Add(car);
        var v = MachineRegistry.VehicleFor("i4_economy");
        var g = VehicleCabin.Measure(v)!.Value;

        // The driver's seat: on the left of the front row, feet on the floor.
        var seat = car.Transform.Position + Vector3.Transform(new Vector3(-g.Wc * 0.25f, g.FloorTop, g.Front - 0.75f), rotation);
        Assert.True(CabinWalls.TryFind(world, seat, out var found, out _));
        Assert.Equal(7, found.Id);
        // Standing beside the car is not in it.
        var beside = car.Transform.Position + Vector3.Transform(new Vector3(-g.W, 0f, 0f), rotation);
        Assert.False(CabinWalls.TryFind(world, beside, out _, out _));

        var cabins = new CabinWalls();
        var shut = CabinWalls.LossDb(v, cabins.WindowsOpen(found, 0.0));
        Assert.True(shut.High < -25f && shut.Mid < -20f, $"a closed car took only {shut.Mid:F1}/{shut.High:F1} dB");
        Assert.True(shut.Low > shut.High, "the glass passes the bottom of a voice more than its top");

        // Windows down, and the client follows the glass at the motor's pace.
        car.Definition.SoundEmitter = car.Definition.SoundEmitter with { WindowsOpen = 1f };
        float halfway = cabins.WindowsOpen(found, CarWindow.DownSeconds * 0.5);
        Assert.InRange(halfway, 0.45f, 0.55f);
        float down = cabins.WindowsOpen(found, CarWindow.DownSeconds + 0.1);
        Assert.Equal(1f, down);
        var open = CabinWalls.LossDb(v, down);
        _o.WriteLine($"a talker in the car, from outside: shut {shut.Low:F1}/{shut.Mid:F1}/{shut.High:F1} dB, down {open.Low:F1}/{open.Mid:F1}/{open.High:F1} dB");
        Assert.True(open.Mid - shut.Mid > 12f && open.High - shut.High > 15f);
        // A new client that first sees the car with its windows down does not hear them roll down.
        Assert.Equal(1f, new CabinWalls().WindowsOpen(found, 50.0));
    }

    /// <summary>
    /// Sitting in the car at 25 m/s: windows down, the outside comes in — the engine and the tyres as the
    /// street hears them at the windows, and the wind from outside the glass — so it is much louder, and
    /// still no louder than a car at motorway speed with its windows down is (about 85-90 dB).
    /// </summary>
    [Fact]
    public void InsideTheCarWindowsDownIsLouder()
    {
        static (float Db, float High) Listen(float windows)
        {
            const int rate = 44100;
            var voice = new EngineVoiceState(MachineRegistry.VehicleFor("i4_economy"), rate, 7) { Interior = true, WindowsOpen = windows };
            voice.PlaceAtSpeed(25f);
            voice.TargetSpeed = 25f;
            voice.Revive();
            var buf = new float[1024];
            for (int i = 0; i < rate / 1024; i++) voice.Render(buf);
            double all = 0, high = 0; long n = 0;
            float hpA = MathF.Exp(-2f * MathF.PI * 2000f / rate), hp = 0f, prev = 0f;
            for (int b = 0; b < rate / 1024; b++)
            {
                voice.Render(buf);
                foreach (float s in buf)
                {
                    float pa = s * voice.PascalsAtFullScale;
                    if (!float.IsFinite(pa)) throw new Xunit.Sdk.XunitException("non-finite sample");
                    hp = hpA * (hp + pa - prev); prev = pa;
                    all += (double)pa * pa; high += (double)hp * hp; n++;
                }
            }
            return (10f * MathF.Log10((float)(all / n) / 4e-10f), 10f * MathF.Log10((float)(high / Math.Max(1e-30, all))));
        }
        var shut = Listen(0f);
        var down = Listen(1f);
        _o.WriteLine($"25 m/s inside: shut {shut.Db:F1} dB ({shut.High:F1} dB above 2 kHz), down {down.Db:F1} dB ({down.High:F1})");
        Assert.True(down.Db - shut.Db > 6f, $"windows down only {down.Db - shut.Db:F1} dB louder");
        Assert.True(down.Db < 90f, $"windows down at 25 m/s was {down.Db:F1} dB");
    }

    // ── The sound ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders: finite, under full scale, as long as the stroke says, and as loud as the level the server
    /// declares for them (the model's own, no calibration). Down ends on the stop, up in the seal, half
    /// way with the switch let go.
    /// </summary>
    [Theory]
    [InlineData(0, 0f, 1f)]
    [InlineData(1, 1f, 0f)]
    [InlineData(2, 0f, 0.5f)]
    [InlineData(3, 0.5f, 0f)]
    public void AStrokeRendersFiniteUnderFullScaleAtItsDeclaredLevel(int variant, float from, float to)
    {
        var report = new CarWindow.Report();
        var pcm = CarWindow.Render(variant, from, to, 48000, report);
        _o.WriteLine(report.ToString());
        Assert.All(pcm, s => Assert.True(float.IsFinite(s)));
        float peak = pcm.Max(MathF.Abs);
        Assert.InRange(peak, 0.01f, 0.95f);
        Assert.InRange(pcm.Length / 48000f, CarWindow.Seconds(from, to) - 0.15f, CarWindow.Seconds(from, to) + 0.15f);
        double peakDb = 20 * Math.Log10(report.PeakPascals / 2e-5);
        Assert.InRange(peakDb, CarWindow.LevelDb(variant, to) - 1.5, CarWindow.LevelDb(variant, to) + 1.5);
        // The motor turns, draws a few amps running and stalls near twenty; going up it lifts the glass,
        // so it runs slower and harder than going down.
        Assert.InRange(report.RunningRpm, 5000, 7500);
        if (to <= 0f) Assert.InRange(report.RunningAmps, 1.5, 5);
        var key = CarWindow.RenderKey(CarWindow.Key(variant, from, to), 48000);
        Assert.Equal(1f, key.Max(MathF.Abs), 3);
    }

    [Fact]
    public void KeysNameAStrokeToTheQuarter()
    {
        string key = CarWindow.Key(5, 0.02f, 0.49f);
        Assert.Equal("carwindow:1:0:2", key);
        Assert.True(CarWindow.TryParseKey(key, out int v, out float from, out float to));
        Assert.Equal((1, 0f, 0.5f), (v, from, to));
        Assert.False(CarWindow.TryParseKey("carwindow:1:2:2", out _, out _, out _));
        Assert.False(CarWindow.TryParseKey("slidingdoor:auto:open:0:100:100:210", out _, out _, out _));
    }

    // ── A map with a car on it ───────────────────────────────────────────────────────────────────

    private sealed class Fixture
    {
        public readonly MapManager Maps;
        public readonly CompositeService Composites;
        public readonly OccupancyService Seats;
        public readonly SessionManager Sessions = new();
        public readonly string MapId = "default";
        public World World = null!;
        public Dictionary<int, Entity> Lookup = null!;
        private int _nextConnection = 1;

        public Fixture(string dir)
        {
            string mapDir = Path.Combine(dir, "maps");
            Directory.CreateDirectory(mapDir);
            foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "maps"), "*.json"))
                File.Copy(file, Path.Combine(mapDir, Path.GetFileName(file)), overwrite: true);
            var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
            Maps = new MapManager(new MapRepository(mapDir), prefabs);
            Maps.Initialize();
            Composites = new CompositeService(Maps, prefabs, new CompositeRepository(Path.Combine(dir, "composites")));
            Seats = new OccupancyService(Maps);
            Assert.True(Maps.TryGetMap(MapId, out World, out _, out _, out Lookup));
        }

        /// <summary>A hatchback, parked: windows up, engine off.</summary>
        public (int Id, Entity Root) ParkedCar(Vector3 at)
        {
            int root = Composites.Place(MapId, "vehicle:i4_economy", at, Quaternion.Identity, "", out _, out string error);
            Assert.True(root >= 0, error);
            return (root, Lookup[root]);
        }

        public UserSession Player(string username, Vector3 at)
        {
            int connection = _nextConnection++;
            var entity = World.Create(
                new PlayerComponent { ConnectionId = connection, Username = username },
                EntityType.Player,
                new Transform { Position = at, Rotation = Quaternion.Identity },
                new Velocity { Linear = Vector3.Zero },
                new NameComponent { Name = username },
                new ColliderComponent
                {
                    Shape = ColliderShape.Cylinder,
                    Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight, PhysicsConstants.PlayerRadius * 2),
                    IsSolid = true,
                });
            Maps.IndexEntity(MapId, entity);
            var session = new UserSession { ConnectionId = connection, Username = username, Entity = entity, CurrentMapId = MapId };
            Sessions.AddSession(connection, session);
            return session;
        }

        /// <summary>The server's ticks, as far as windows go.</summary>
        public void Tick(int ticks)
        {
            for (int i = 0; i < ticks; i++) WindowSystem.Update(World, PhysicsConstants.FixedDeltaTime);
        }
    }
}
