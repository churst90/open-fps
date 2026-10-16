using System.Globalization;
using System.Numerics;
using System.Text;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --driving out=DIR [set=all|horn|siren|bend|lines|rails|gates|aircraft] [compare]: the driving controls
/// and aids (docs/DRIVING_AIDS.md) through ClientAudioSystem, the HRTF, the loudness law and the cabin,
/// each scene driven as the server drives it, captured to DIR/capture.post.wav with DIR/segments.csv
/// and the aids' speech in DIR/speech.csv. Scenes: horns and the siren from kerb and seat (compare adds
/// street life's honk), a right turn off Main Street with and without the brake cue, drifting onto the
/// lines, rails, a crossing's gates and bell, a light single landing and taking off.
///
/// set=ranks (not in all): the provider's own orderings (docs/SPECIAL_CASES_AUDIT.md, S20). Ten cars idle on a
/// street ahead at 8 to 160 m, alone, then with a garden fire pit 3 m off, then with it 35 m off, then in
/// moderate rain (six cars from 45 m out and the far fire pit for six traced echo rigs); each second it notes which
/// engines run at full detail, which voices hold the late field's slots and the traced echoes, and the render
/// pool's order, in DIR/ranks.txt, the last of each scene printed.
/// </summary>
public static class DrivingSpike
{
    private sealed record Segment(string Name, double Start, double Seconds, string Note);

    private static string? Arg(string[] args, string key) => args.FirstOrDefault(x => x.StartsWith(key, StringComparison.Ordinal))?[key.Length..];

    private sealed class Rig
    {
        public readonly WheelDynamics Body;
        public Rig(VehicleProfile v) { Body = new WheelDynamics(v.Mechanics); }
        public WheelState[] Wire(float speed, float accel)
        {
            Body.Hold(speed, accel, 0f);
            var wire = new WheelState[Body.Wheels.Length];
            for (int i = 0; i < wire.Length; i++)
            {
                ref var w = ref Body.Wheels[i];
                wire[i] = WheelState.Encode(w.Load, w.AngularSpeed, w.SlipRatio, w.SlipAngle, w.Surface, w.Demand, 0f);
            }
            return wire;
        }
    }

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string outDir = Arg(args, "out=") ?? "/tmp/openfps-driving";
        string set = Arg(args, "set=") ?? "all";
        bool Want(string s) => set == "all" || set.Split(',').Contains(s);
        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(outDir, "capture.post.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");

        // The city's roads and crossings, as the server sends them, and its carriageways near Main
        // Street and Central Street for the lane sensors.
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        if (!maps.TryGetMap("city", out var cityWorld, out _, out _, out _) || !maps.TryGetMapData("city", out var cityData))
        { Console.WriteLine("FAIL: no city map"); return 1; }
        var rail = new RailSystem();
        rail.Spawn(maps);
        var crossingSystem = new CrossingSystem(rail);
        crossingSystem.Spawn(maps);
        var roads = MapRoadsBuilder.Build(cityData, crossingSystem.Rails("city"));
        var roadBoxes = EntityDefinitionFactory.StaticDefinitions(cityWorld)
            .Where(d => string.Equals(d.Material.Material, "Asphalt", StringComparison.OrdinalIgnoreCase)
                     && MathF.Abs(d.Transform.Position.X) < 160f && MathF.Abs(d.Transform.Position.Z) < 160f)
            .ToList();
        Console.WriteLine($"City: {roads.Roads.Count} roads, {roads.Crossings.Count} crossings, {roadBoxes.Count} carriageway boxes near Main and Central.");

        // Open asphalt for the acoustics: a runway, a street with no buildings, a crossing in the open.
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no Steam Audio context"); return 1; }
        var scene = new SteamAudioScene(ctx);
        scene.Build(new List<SteamAudioScene.Box> { new(new Vector3(0f, -0.5f, 0f), new Vector3(4000f, 1f, 4000f), Quaternion.Identity, "Asphalt") });
        var provider = new FmodAudioProvider();
        provider.Traced.Configure(ctx, scene);

        var facade = new AudioEngineFacade(provider);
        string sounds = LabPaths.Sounds();
        facade.InitializeForTest(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        provider.EarWindEnabled = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var world = new ClientWorldState();
        world.Clear(new Vector3(6000, 600, 6000));
        world.SetAcousticMap(new AcousticMap(new Vector3(4000, 400, 4000), new Vector3(-2000, -10, -2000)) { GlobalEnvironmentId = AcousticConstants.GlobalRegionId });
        var player = new LocalPlayerState();
        var mapping = new SoundMappingService(player);
        mapping.Initialize(sounds);
        // One clock, as in the game: the world stamps positions on the clock the audio ages them on.
        world.Clock = facade.Clock;
        var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds);
        world.RegisterDefinition(new EntityDefinition
        {
            EntityId = 1,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0f, -0.5f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(4000f, 1f, 4000f), IsSolid = true },
            // Concrete: the driving aids read any unnamed square of asphalt as a junction.
            Material = new MaterialComponent { Material = "Concrete" },
        });
        facade.Weather.Wind = WindWeather.Steady(0f, 250f, 0f);
        world.UpdateAtmosphere(new WorldStateUpdate { Temperature = 15f, Humidity = 0.6f, AirPressure = 1013.25f, AirAbsorptionMultiplier = 1f });

        var segments = new List<Segment>();
        var speech = new List<(double At, string Text)>();
        audio.Driving.Announce += text => speech.Add((clock.Elapsed.TotalSeconds, text));
        Action<double>? perFrame = null;
        int nextId = 1000;

        // The longest frame of each scene: the update and the audio thread's tick, which place every source.
        double worstFrameMs = 0;
        void Pump(double seconds)
        {
            var until = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < until)
            {
                perFrame?.Invoke(clock.Elapsed.TotalSeconds);
                long f0 = System.Diagnostics.Stopwatch.GetTimestamp();
                audio.Update(world.GetSnapshot());
                facade.PumpForTest();
                worstFrameMs = Math.Max(worstFrameMs, System.Diagnostics.Stopwatch.GetElapsedTime(f0).TotalMilliseconds);
                Thread.Sleep(4);
            }
        }
        // A voice made in the frame the listener is seated starts as the car's outside for a fifth of a second.
        const double Preroll = 0.8;
        void Record(string name, double seconds, string note)
        {
            worstFrameMs = 0;
            if (!name.StartsWith("silence", StringComparison.Ordinal)) Pump(Preroll);
            double start = clock.Elapsed.TotalSeconds;
            Pump(seconds);
            segments.Add(new Segment(name, start, seconds, note));
            Console.WriteLine($"  {start,7:F2} s  {name} ({seconds:F1} s)  {note}  [worst frame {worstFrameMs:F0} ms; traced reverb {(provider.TracedActive ? "on" : "off")}, cabin trace {(provider.Traced.Cabin != null ? "ready" : "none")}]");
        }
        void RecordUntil(string name, Func<bool> done, double limit, string note)
        {
            worstFrameMs = 0;
            Pump(Preroll);
            double start = clock.Elapsed.TotalSeconds;
            while (!done() && clock.Elapsed.TotalSeconds - start < limit) Pump(0.05);
            Pump(1.5);
            double seconds = clock.Elapsed.TotalSeconds - start;
            segments.Add(new Segment(name, start, seconds, note));
            Console.WriteLine($"  {start,7:F2} s  {name} ({seconds:F1} s)  {note}  [worst frame {worstFrameMs:F0} ms; traced reverb {(provider.TracedActive ? "on" : "off")}, cabin trace {(provider.Traced.Cabin != null ? "ready" : "none")}]");
        }

        int AddVehicle(string preset, Vector3 at, Quaternion heading)
        {
            int id = nextId++;
            var profile = MachineRegistry.VehicleFor(preset);
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.NPC, Moves = true,
                Transform = new Transform { Position = at, Rotation = heading },
                Collider = new ColliderComponent { Shape = ColliderShape.Box, IsSolid = true,
                                                   Size = new Vector3(profile.WidthMetres, profile.HeightMetres, profile.LengthMetres) },
            };
            def.SoundEmitter = new SoundEmitterComponent
            {
                IsSynth = true, SoundId = "engine:" + preset, Mode = PlaybackMode.LoopOne, Volume = 1f,
                Range = Loudness.AudibleRange(profile.SourceLevelDb), MinDistance = 3f,
            };
            world.RegisterDefinition(def);
            return id;
        }
        int AddRoom(VehicleProfile v, Vector3 at, Quaternion heading, out Vector3 centre)
        {
            centre = Vector3.Zero;
            if (VehicleCabin.Measure(v) is not { } g) return -1;
            centre = new Vector3(0f, g.FloorTop + g.Hc * 0.5f, g.Cz);
            var size = new Vector3(g.Wc, g.Hc, g.Lc);
            var body = v.Body ?? VehicleBody.Saloon;
            int I(string m) => AcousticRegistry.TryGetResonanceIndex(m, out int k) ? k : 0;
            string lining = VehicleCabin.MaterialOf(body.CabinAbsorption >= 0.25f ? VehicleCabin.Carpet : VehicleCabin.Steel);
            int id = nextId++;
            world.RegisterDefinition(new EntityDefinition
            {
                EntityId = id, Type = EntityType.Trigger, Moves = true,
                Transform = new Transform { Position = at + Vector3.Transform(centre, heading), Rotation = heading, Scale = Vector3.One },
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = false },
                Region = new RegionComponent
                {
                    FriendlyName = v.Name, IsIndoor = true, RoomSize = size, ReverbTimeScale = 1f,
                    Materials = new[] { I(lining), I(lining), I("Glass"), I("Glass"), I("Glass"), I("Glass") },
                },
            });
            return id;
        }
        void Remove(params int[] ids)
        {
            var real = ids.Where(i => i >= 0).ToArray();
            world.RemoveEntities(real);
            foreach (int i in real) audio.ForgetEntity(i);
        }
        void State(int id, Vector3 at, Quaternion heading, Vector3 velocity, WheelState[]? wheels, byte signals, int room = -1, Vector3 roomCentre = default)
        {
            var states = new List<EntityState> { new EntityState
            {
                EntityId = id, Transform = QuantizedTransform.FromTransform(new Transform { Position = at, Rotation = heading }),
                LinearVelocity = velocity, Wheels = wheels, Signals = signals,
            } };
            if (room >= 0)
                states.Add(new EntityState
                {
                    EntityId = room,
                    Transform = QuantizedTransform.FromTransform(new Transform { Position = at + Vector3.Transform(roomCentre, heading), Rotation = heading, Scale = Vector3.One }),
                    LinearVelocity = velocity,
                });
            world.SyncState(states.ToArray());
        }
        void Stand(Vector3 feet, float yaw)
        {
            player.RidingEntityId = -1;
            player.RidingControls = false;
            player.Position = feet;
            player.Yaw = yaw;
            player.Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f);
        }
        void Sit(int id, VehicleProfile v, Vector3 at, Quaternion heading, float yaw, bool drives)
        {
            player.RidingEntityId = id;
            player.RidingControls = drives;
            player.Position = at + Vector3.Transform(CabinSpike.Seat(v, "driver"), heading);
            player.Yaw = yaw;
            player.Rotation = heading;
        }
        byte Sig(bool horn, bool siren = false, SirenMode tone = SirenMode.Wail) => VehicleSignalBits.Encode(true, horn, siren, tone);

        // ── Horns ────────────────────────────────────────────────────────────────────────────────
        void Horn(string preset, bool seated, string label, bool streetLife = false)
        {
            var v = MachineRegistry.VehicleFor(preset);
            var heading = Quaternion.Identity;                 // nose north
            var at = Vector3.Zero;
            int id = AddVehicle(preset, at, heading);
            int room = seated ? AddRoom(v, at, heading, out var rc) : -1;
            var roomCentre = seated ? AddRoomCentre(v) : Vector3.Zero;
            var rig = new Rig(v);
            double t0 = clock.Elapsed.TotalSeconds;
            // The listener at the kerb: 3 m to the right of the bonnet, facing the car; or in its seat.
            if (seated) Sit(id, v, at, heading, 0f, drives: false);
            else Stand(new Vector3(3f, 0f, v.LengthMetres * 0.5f + 2f), -MathF.PI / 2f);
            bool honked = false;
            perFrame = t =>
            {
                double s = t - t0 - 2.0;
                // Two seconds standing, a long press of 1.5 s, half a second off, two quick taps.
                bool horn = s is >= 0 and < 1.5 || s is >= 2.0 and < 2.2 || s is >= 2.45 and < 2.65;
                // For comparison, the same rhythm as street life sends it: one honk event.
                if (streetLife && s >= 0 && !honked)
                {
                    audio.WorldAudio.HornReceived!(id, VehicleProfile.HornFor(v), new[] { 1.5f, 0.5f, 0.2f, 0.25f, 0.2f });
                    honked = true;
                }
                State(id, at, heading, Vector3.Zero, rig.Wire(0f, 0f), streetLife ? Sig(false) : Sig(horn), room, roomCentre);
                if (seated) Sit(id, v, at, heading, 0f, drives: false);
            };
            Record(label, 6.0, $"{v.Name}, horn {VehicleProfile.HornFor(v)}: idle, held 1.5 s from 1.2 s, then two taps; " + (seated ? "driver's seat" : "kerb, 3 m right of the bonnet"));
            perFrame = null;
            Remove(id, room);
            Stand(new Vector3(0f, 0f, -300f), 0f);
            Pump(1.0);
        }
        Vector3 AddRoomCentre(VehicleProfile v)
            => VehicleCabin.Measure(v) is { } g ? new Vector3(0f, g.FloorTop + g.Hc * 0.5f, g.Cz) : Vector3.Zero;

        // ── Siren ────────────────────────────────────────────────────────────────────────────────
        void Siren(bool seated, string label)
        {
            var v = MachineRegistry.VehicleFor("police_interceptor");
            var heading = Quaternion.Identity;
            var at = Vector3.Zero;
            int id = AddVehicle("police_interceptor", at, heading);
            int room = seated ? AddRoom(v, at, heading, out _) : -1;
            var roomCentre = seated ? AddRoomCentre(v) : Vector3.Zero;
            var rig = new Rig(v);
            double t0 = clock.Elapsed.TotalSeconds;
            if (seated) Sit(id, v, at, heading, 0f, drives: false);
            else Stand(new Vector3(12f, 0f, 4f), -MathF.PI / 2f);
            perFrame = t =>
            {
                double s = t - t0 - 1.5;
                bool on = s >= 0 && s < (seated ? 9.5 : 13.0);
                var tone = s < (seated ? 4.0 : 7.0) ? SirenMode.Wail : s < (seated ? 7.0 : 10.5) ? SirenMode.Yelp : SirenMode.Phaser;
                State(id, at, heading, Vector3.Zero, rig.Wire(0f, 0f), Sig(false, on, tone), room, roomCentre);
                if (seated) Sit(id, v, at, heading, 0f, drives: false);
            };
            Record(label, seated ? 12.5 : 16.0, seated
                ? "police interceptor, driver's seat, idling: wail 4 s, yelp 3 s, phaser 2.5 s, off"
                : "police interceptor at the kerb 12 m off, idling: wail 7 s, yelp 3.5 s, phaser 2.5 s, off");
            perFrame = null;
            Remove(id, room);
            Stand(new Vector3(0f, 0f, -300f), 0f);
            Pump(1.0);
        }

        // ── A right turn off Main Street ───────────────────────────────────────────────────────
        void Bend(bool brakeCue, string label)
        {
            foreach (var b in roadBoxes) world.RegisterDefinition(b);
            audio.Driving.SetRoads(roads);
            DrivingCues.BrakeCue = brakeCue;
            var v = MachineRegistry.VehicleFor("i4_economy");
            var p = new Vector3(4.5f, 0.05f, -7f - 100f);
            float heading = 0f, speed = 65f / 3.6f, yaw = 0f, decel = 0f;
            var rot = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f);
            int id = AddVehicle("i4_economy", p, rot);
            int room = AddRoom(v, p, rot, out _);
            var roomCentre = AddRoomCentre(v);
            var rig = new Rig(v);
            double said = -1, cueSince = -1, last = clock.Elapsed.TotalSeconds;
            bool indicated = false, done = false;
            var log = new StringBuilder("t,x,z,kmh,ratio,band,decel\n");
            double t0 = clock.Elapsed.TotalSeconds;
            void Heard(string text) { if (said < 0 && text.StartsWith("Junction in", StringComparison.Ordinal)) said = clock.Elapsed.TotalSeconds; }
            audio.Driving.Announce += Heard;
            perFrame = t =>
            {
                float dt = (float)Math.Clamp(t - last, 0.0, 0.05);
                last = t;
                if (!indicated && t - t0 > 0.3) { audio.Driving.ToggleIndicator(+1); indicated = true; }
                var plan = audio.Driving.Plan;
                var fwd = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading));
                float grip = 0.95f * Earth.StandardGravity;
                decel = 0f;
                if (!brakeCue)
                {
                    if (said >= 0 && t - said > 1.0 && speed > 25f / 3.6f) decel = 0.2f * Earth.StandardGravity;
                }
                else if (plan != null)
                {
                    int band = DrivingCueBands.Of(plan.BrakeRatio);
                    if (band >= 2) { if (cueSince < 0) cueSince = t; } else cueSince = -1;
                    if (band >= 2 && t - cueSince > 0.5) decel = MathF.Min(grip * 0.9f, MathF.Max(1.5f, plan.NeededDecel * 1.15f));
                    else if (band == 1) decel = 0.6f;
                }
                var aim = plan is { Located: true } && plan.Path.Count > 1 ? plan.GuidePoint - p : fwd * 10f;
                aim.Y = 0f;
                var to = Vector3.Normalize(aim);
                float alpha = MathF.Atan2(Vector3.Cross(fwd, to).Y, Vector3.Dot(fwd, to));
                float k = 2f * MathF.Sin(alpha) / MathF.Max(1f, aim.Length());
                float maxK = MathF.Sqrt(MathF.Max(0f, grip * grip - decel * decel)) / MathF.Max(0.5f, speed * speed);
                k = Math.Clamp(k, -maxK, maxK);
                speed = MathF.Max(0f, speed - decel * dt);
                yaw = speed * k;
                heading += yaw * dt;
                p += new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading)) * speed * dt;
                rot = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f);
                var vel = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading)) * speed;
                State(id, p, rot, vel, rig.Wire(speed, -decel), Sig(false), room, roomCentre);
                Sit(id, v, p, rot, heading, drives: true);
                if (p.X > 45f || t - t0 > 25) done = true;
                log.Append(CultureInfo.InvariantCulture, $"{t - t0:F2},{p.X:F2},{p.Z:F2},{speed * 3.6f:F1},{plan?.BrakeRatio ?? 0:F2},{DrivingCueBands.Of(plan?.BrakeRatio ?? 0)},{decel:F2}\n");
            };
            RecordUntil(label, () => done, 30, brakeCue
                ? "hatchback from 65 km/h, right indicator, a driver braking on the brake cue"
                : "hatchback from 65 km/h, right indicator, a driver braking on the spoken junction only (brake cue off)");
            File.WriteAllText(Path.Combine(outDir, label + ".track.csv"), log.ToString());
            audio.Driving.Announce -= Heard;
            perFrame = null;
            Stand(new Vector3(0f, 0f, -300f), 0f);
            Remove(id, room);
            foreach (var b in roadBoxes) world.RemoveEntities(new[] { b.EntityId });
            DrivingCues.BrakeCue = true;
            Pump(1.0);
        }

        // ── Onto the lines ───────────────────────────────────────────────────────────────────────
        void Lines(string label)
        {
            foreach (var b in roadBoxes) world.RegisterDefinition(b);
            audio.Driving.SetRoads(roads);
            var v = MachineRegistry.VehicleFor("i4_economy");
            float speed = 40f / 3.6f;
            var p = new Vector3(4.5f, 0.05f, -110f);
            var rot = Quaternion.Identity;
            int id = AddVehicle("i4_economy", p, rot);
            int room = AddRoom(v, p, rot, out _);
            var roomCentre = AddRoomCentre(v);
            var rig = new Rig(v);
            double t0 = clock.Elapsed.TotalSeconds, last = t0;
            // Northbound in the kerb lane of four (x 3..6 is the kerb lane, 0 the centre line): drift
            // left until the left wheels are on the centre line, hold, come back; then right until the
            // right wheels are over the kerb-side edge, hold, come back.
            float XAt(double s) => s switch
            {
                < 1.5 => 4.5f,
                < 4.0 => 4.5f - (float)((s - 1.5) / 2.5) * 4.2f,          // to x 0.3: the left side 0.6 over the line
                < 6.0 => 0.3f,
                < 8.0 => 0.3f + (float)((s - 6.0) / 2.0) * 4.2f,
                < 9.5 => 4.5f + (float)((s - 8.0) / 1.5) * 1.3f,          // to x 5.8: the right side 0.7 over the edge
                < 11.5 => 5.8f,
                < 13.0 => 5.8f - (float)((s - 11.5) / 1.5) * 1.3f,
                _ => 4.5f,
            };
            perFrame = t =>
            {
                float dt = (float)Math.Clamp(t - last, 0.0, 0.05);
                last = t;
                p.Z += speed * dt;
                p.X = XAt(t - t0);
                State(id, p, rot, new Vector3(0f, 0f, speed), rig.Wire(speed, 0f), Sig(false), room, roomCentre);
                Sit(id, v, p, rot, 0f, drives: true);
            };
            Record(label, 15.0, "hatchback at 40 km/h up Main Street: drifting onto the centre line (raised markers on the left), back, then over the kerb-side edge (rumble strip on the right), back; the approach beeps before each");
            perFrame = null;
            Stand(new Vector3(0f, 0f, -300f), 0f);
            Remove(id, room);
            foreach (var b in roadBoxes) world.RemoveEntities(new[] { b.EntityId });
            Pump(1.0);
        }

        // ── Over the rails ───────────────────────────────────────────────────────────────────────
        void Rails(bool seated, string label)
        {
            audio.SetCrossings(new[] { new CrossingRails { Name = "test crossing", Centre = Vector3.Zero, Along = Vector3.UnitX, HalfLengthMetres = 7.5f } });
            var v = MachineRegistry.VehicleFor("i4_economy");
            float speed = 30f / 3.6f;
            var rot = Quaternion.Identity;
            var p = new Vector3(1.75f, 0.05f, -45f);
            int id = AddVehicle("i4_economy", p, rot);
            int room = seated ? AddRoom(v, p, rot, out _) : -1;
            var roomCentre = seated ? AddRoomCentre(v) : Vector3.Zero;
            var rig = new Rig(v);
            double last = clock.Elapsed.TotalSeconds;
            if (!seated) Stand(new Vector3(7.5f, 0f, -4f), -MathF.PI / 2f);
            perFrame = t =>
            {
                float dt = (float)Math.Clamp(t - last, 0.0, 0.05);
                last = t;
                p.Z += speed * dt;
                State(id, p, rot, new Vector3(0f, 0f, speed), rig.Wire(speed, 0f), Sig(false), room, roomCentre);
                if (seated) Sit(id, v, p, rot, 0f, drives: false);
            };
            Record(label, 10.0, seated
                ? "hatchback at 30 km/h over a level crossing's two rails, from the driver's seat"
                : "hatchback at 30 km/h over a level crossing's two rails, heard from the kerb 6 m beside the crossing");
            perFrame = null;
            Stand(new Vector3(0f, 0f, -300f), 0f);
            Remove(id, room);
            audio.SetCrossings(null);
            Pump(1.0);
        }

        // ── The gates and the bell ───────────────────────────────────────────────────────────────
        void Gates(string label)
        {
            int bell = nextId++;
            var gates = new[] { nextId++, nextId++ };
            EntityDefinition Emitter(int id, string sound, Vector3 at, bool running)
            {
                var def = new EntityDefinition
                {
                    EntityId = id, Type = EntityType.StaticObject,
                    Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
                    Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.4f, 0.4f, 0.4f), IsSolid = false },
                };
                def.SoundEmitter = new SoundEmitterComponent
                {
                    IsSynth = true, SoundId = sound, Mode = PlaybackMode.LoopOne, Volume = 1f,
                    Range = Loudness.AudibleRange(86f), MinDistance = 1f, SynthRunning = running,
                };
                return def;
            }
            var bellAt = new Vector3(-7.6f, 2.6f, -4.45f);
            var gateAt = new[] { new Vector3(7.6f, 0.6f, -4.45f), new Vector3(-7.6f, 0.6f, 4.45f) };
            void Set(bool closed)
            {
                world.RegisterDefinition(Emitter(bell, "bell:crossing_gong", bellAt, closed));
                for (int i = 0; i < 2; i++) world.RegisterDefinition(Emitter(gates[i], "gate:crossing_gate", gateAt[i], closed));
            }
            Set(false);
            Stand(new Vector3(10f, 0f, -8f), -MathF.PI * 0.75f);
            Pump(1.5);
            double t0 = clock.Elapsed.TotalSeconds;
            bool closedNow = false;
            perFrame = t =>
            {
                double s = t - t0;
                bool closed = s >= 1.0 && s < 23.0;
                if (closed != closedNow) { closedNow = closed; Set(closed); }
            };
            Record(label, 36.0, "level crossing: the bell rings 1 s in; the gates start down 4 s later and are down in 12 s; the bell stops at 23 s and the gates rise in 9 s. "
                              + "Heard from the kerb 3.6 m from the nearer gate, 8 m from the far one");
            perFrame = null;
            Remove(bell, gates[0], gates[1]);
            Stand(new Vector3(0f, 0f, -300f), 0f);
            Pump(1.0);
        }

        // ── An aeroplane lands, rolls out, turns round and takes off ─────────────────────────────
        void Aircraft(string preset, string label)
        {
            var air = AircraftProfile.ByName(preset);
            var spec = air.Ground!;
            var touchdown = new Vector3(0f, 0.1f, 0f);
            var dir = Vector3.UnitZ;
            // A three-degree approach from 700 m out, at approach speed, flaring to touchdown speed.
            const float approachMetres = 700f, flare = 0.6f;
            float glide = MathF.Tan(3f * MathF.PI / 180f);
            int id = nextId++;
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.NPC, Moves = true,
                Transform = new Transform { Position = touchdown - dir * approachMetres + new Vector3(0f, approachMetres * glide, 0f), Rotation = Quaternion.Identity },
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(air.WingspanMetres, 6f, air.LengthMetres), IsSolid = false },
            };
            def.SoundEmitter = new SoundEmitterComponent
            {
                IsSynth = true, SoundId = "aircraft:" + preset, Mode = PlaybackMode.LoopOne, Volume = 1f,
                Range = Loudness.AudibleRange(air.SourceLevelDb), MinDistance = 3f,
            };
            world.RegisterDefinition(def);
            // Beside the runway, 45 m east of its centreline, level with the touchdown point.
            Stand(new Vector3(45f, 0f, 0f), -MathF.PI / 2f);
            float along = -approachMetres, speed = air.ApproachSpeedMps;
            AircraftGroundRun? run = null;
            double last = clock.Elapsed.TotalSeconds, climbFor = 0;
            Vector3 climbFrom = Vector3.Zero, climbDir = Vector3.Zero;
            bool done = false;
            string phase = "approach";
            var log = new StringBuilder("t,phase,x,y,z,speed\n");
            double t0 = last;
            perFrame = t =>
            {
                float dt = (float)Math.Clamp(t - last, 0.0, 0.05);
                last = t;
                Vector3 pos, vel;
                float hdg;
                // Its lever as the server that flies it would send it (MachinePower.AircraftLever).
                AircraftGroundRun? onWheels = null;
                bool liftedOff = false;
                if (run == null && phase == "approach")
                {
                    float remaining = -along;
                    float vtd = spec.TouchdownSpeedMps;
                    speed = MathF.Min(air.ApproachSpeedMps, MathF.Sqrt(vtd * vtd + 2f * flare * remaining));
                    along += speed * dt;
                    pos = touchdown + dir * along + new Vector3(0f, MathF.Max(0f, -along) * glide, 0f);
                    vel = dir * speed + new Vector3(0f, -speed * glide, 0f);
                    hdg = 0f;
                    if (along >= 0f) { run = new AircraftGroundRun(spec, touchdown, dir, speed, 4f); phase = "ground"; }
                }
                else if (run != null && run.State != AircraftGroundRun.Phase.Done)
                {
                    run.Update(dt);
                    pos = run.Position; vel = run.Velocity; hdg = run.Heading;
                    if (run.State == AircraftGroundRun.Phase.Done)
                    {
                        liftedOff = true;
                        phase = "climb";
                        climbFrom = run.Position;
                        climbDir = Vector3.Normalize(new Vector3(run.Velocity.X, 0f, run.Velocity.Z));
                        speed = run.Speed;
                    }
                }
                else
                {
                    climbFor += dt;
                    speed = MathF.Min(speed + 1.5f * dt, air.ApproachSpeedMps * 1.3f);
                    float d = (float)(climbFor * speed);
                    pos = climbFrom + climbDir * d + new Vector3(0f, d * MathF.Tan(7f * MathF.PI / 180f), 0f);
                    vel = climbDir * speed + new Vector3(0f, speed * MathF.Tan(7f * MathF.PI / 180f), 0f);
                    hdg = MathF.Atan2(climbDir.X, climbDir.Z);
                    if (climbFor > 12) done = true;
                }
                var rot = Quaternion.CreateFromYawPitchRoll(hdg, 0f, 0f);
                if (phase == "ground") onWheels = run;
                float lever = liftedOff ? 1f : MachinePower.AircraftLever(air, onWheels, phase == "approach", pos.Y - touchdown.Y, vel);
                world.SyncState(new[] { new EntityState { EntityId = id, Transform = QuantizedTransform.FromTransform(new Transform { Position = pos, Rotation = rot }), LinearVelocity = vel, Lever = MachinePower.Encode(lever) } });
                log.Append(CultureInfo.InvariantCulture, $"{t - t0:F2},{phase}{(run != null ? "/" + run.State : "")},{pos.X:F1},{pos.Y:F1},{pos.Z:F1},{vel.Length():F1}\n");
            };
            RecordUntil(label, () => done, 200, $"{air.Name}: a 3-degree approach from 700 m, touchdown, landing roll, the turn round, a 4 s hold, the take-off roll and the climb-out; "
                                             + "heard from 45 m beside the runway at the touchdown point");
            File.WriteAllText(Path.Combine(outDir, label + ".track.csv"), log.ToString());
            perFrame = null;
            Remove(id);
            Pump(1.0);
        }

        // ── The provider's own orderings (S20) ──────────────────────────────────────────────────
        var ranksLog = new StringBuilder();
        void Ranks()
        {
            string[] presets = { "i4_compact", "i4_midsize", "v8_mild", "transit_bus", "i6_street", "boxer4_street", "sportbike", "v8_glasspack", "charger440", "i4_midsize" };
            float[] metres = { 8f, 14f, 22f, 32f, 45f, 60f, 80f, 105f, 130f, 160f };
            var names = new Dictionary<int, string>();
            var cars = new List<(int Id, Vector3 At, Quaternion Heading, Rig Rig)>();
            // The street as a place of its own, as a map's are: the late field traces only sources sending to the
            // listener's place.
            int I(string m) => AcousticRegistry.TryGetResonanceIndex(m, out int k) ? k : 0;
            int street = nextId++;
            world.RegisterDefinition(new EntityDefinition
            {
                EntityId = street, Type = EntityType.Trigger,
                Transform = new Transform { Position = new Vector3(0f, 10f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(300f, 20f, 300f), IsSolid = false },
                Region = new RegionComponent
                {
                    FriendlyName = "Ranks street", IsIndoor = false, RoomSize = new Vector3(300f, 20f, 300f), ReverbTimeScale = 1f,
                    Materials = new[] { I("Asphalt"), I("Asphalt"), I("Brick"), I("Brick"), I("Brick"), I("Brick") },
                },
            });
            Stand(Vector3.Zero, 0f);
            for (int i = 0; i < presets.Length; i++)
            {
                // Along a street 6 m ahead, east and west in turn, each car `metres` from the ear.
                float side = i % 2 == 0 ? 1f : -1f;
                var at = new Vector3(side * MathF.Sqrt(metres[i] * metres[i] - 36f), 0f, 6f);
                var heading = Quaternion.CreateFromAxisAngle(Vector3.UnitY, side * MathF.PI / 2f);
                int id = AddVehicle(presets[i], at, heading);
                names[id] = $"{presets[i]}@{metres[i]:0}m";
                cars.Add((id, at, heading, new Rig(MachineRegistry.VehicleFor(presets[i]))));
            }
            perFrame = _ =>
            {
                foreach (var (id, at, heading, rig) in cars) State(id, at, heading, Vector3.Zero, rig.Wire(0f, 0f), Sig(false));
            };
            string Name(FmodAudioProvider.RankedVoice v)
                => names.TryGetValue(v.Id, out var n) ? n : $"{v.Sound}#{v.Id}@{v.Distance:0}m";
            void Phase(string label, double settle, int samples)
            {
                Pump(settle);
                double start = clock.Elapsed.TotalSeconds;
                for (int k = 0; k < samples; k++)
                {
                    Pump(1.0);
                    var r = provider.Ranks();
                    var byId = new Dictionary<int, FmodAudioProvider.RankedVoice>();
                    foreach (var v in r.Voices) byId[v.Id] = v;
                    var full = r.Voices.Where(v => v.Detail == OpenFPS.Client.AudioEngine.Core.Engine.EngineDetail.Full).OrderBy(v => v.Distance).Select(Name);
                    var reduced = r.Voices.Where(v => v.Detail == OpenFPS.Client.AudioEngine.Core.Engine.EngineDetail.Reduced).OrderBy(v => v.Distance).Select(Name);
                    var late = r.Voices.Where(v => v.LateField).OrderBy(v => v.Distance).Select(Name);
                    var echoes = r.Voices.Where(v => v.Echoes).OrderBy(v => v.Distance).Select(Name);
                    var order = r.RenderAhead.Select(id => byId.TryGetValue(id, out var v) ? Name(v) : $"#{id}");
                    string line = $"[{label} {k + 1}] full: {string.Join(", ", full)}\n"
                                + $"    reduced: {string.Join(", ", reduced)}\n"
                                + $"    late field: {string.Join(", ", late)}\n"
                                + $"    traced echoes: {string.Join(", ", echoes)}\n"
                                + $"    render ahead: {string.Join(", ", order)}\n";
                    ranksLog.Append(line);
                    if (k == samples - 1) Console.Write(line);
                }
                segments.Add(new Segment(label, start, clock.Elapsed.TotalSeconds - start, "ranks"));
            }
            // A garden fire pit as its prefab places it (fire_pit.json): 45 dB of headroom, given back as 29 dB of gain.
            int AddFire(float x, float z)
            {
                var spec = FireSpec.ByName("fire_pit");
                int fire = nextId++;
                var fireDef = new EntityDefinition
                {
                    EntityId = fire, Type = EntityType.StaticObject,
                    Transform = new Transform { Position = new Vector3(x, FireSpike.Height(spec), z), Rotation = Quaternion.Identity, Scale = Vector3.One },
                    Collider = new ColliderComponent { Shape = ColliderShape.Box, IsSolid = false,
                                                       Size = new Vector3(spec.AreaWidth, MathF.Max(0.5f, spec.FlameHeightMetres), spec.AreaDepth) },
                    Material = new MaterialComponent { Material = "None" },
                };
                fireDef.SoundEmitter = new SoundEmitterComponent
                {
                    IsSynth = true, SoundId = FireSpec.KeyFor("fire_pit"), Mode = PlaybackMode.LoopOne, Volume = 1f, Range = 120f, MinDistance = 1f,
                };
                world.RegisterDefinition(fireDef);
                names[fire] = $"fire_pit@{new Vector2(x, z).Length():0}m";
                return fire;
            }
            Phase("cars", 4.0, 6);
            int near = AddFire(3f, 0f);
            Phase("cars+fire", 4.0, 6);
            Remove(near);
            Pump(2.0);
            // Past the distance traced echoes start at (30 m), where it is one of their candidates.
            int far = AddFire(-5f, 35f);
            Phase("cars+far fire", 4.0, 6);
            Remove(far);
            Pump(2.0);
            world.UpdateAtmosphere(new WorldStateUpdate
            {
                Temperature = 15f, Humidity = 0.8f, AirPressure = 1013.25f, AirAbsorptionMultiplier = 1f,
                PrecipitationIntensity = 0.5f, RainRateMmPerHour = Rainfall.ModerateRate,
            });
            Phase("cars+rain", 6.0, 6);
            world.UpdateAtmosphere(new WorldStateUpdate { Temperature = 15f, Humidity = 0.6f, AirPressure = 1013.25f, AirAbsorptionMultiplier = 1f });
            perFrame = null;
            Remove(cars.Select(c => c.Id).Append(street).ToArray());
            Pump(2.0);
            File.WriteAllText(Path.Combine(outDir, "ranks.txt"), ranksLog.ToString());
        }

        try
        {
            Stand(new Vector3(0f, 0f, -300f), 0f);
            Pump(2.0);
            Record("silence", 2.0, "");
            if (set.Split(',').Contains("ranks")) Ranks();
            if (Want("horn"))
            {
                Horn("i4_economy", seated: false, "horn_car_kerb");
                if (args.Contains("compare")) Horn("i4_economy", seated: false, "horn_car_kerb_streetlife", streetLife: true);
                Horn("transit_bus", seated: false, "horn_bus_kerb");
                Horn("i4_economy", seated: true, "horn_car_seat");
                if (args.Contains("compare")) Horn("i4_economy", seated: true, "horn_car_seat_streetlife", streetLife: true);
                Horn("transit_bus", seated: true, "horn_bus_seat");
            }
            if (Want("siren"))
            {
                Siren(seated: false, "siren_kerb");
                Siren(seated: true, "siren_seat");
            }
            if (Want("bend"))
            {
                Bend(brakeCue: false, "bend_speech_only");
                Bend(brakeCue: true, "bend_brake_cue");
            }
            if (Want("lines")) Lines("lines_rumble");
            if (Want("rails"))
            {
                Rails(seated: false, "rails_kerb");
                Rails(seated: true, "rails_seat");
            }
            if (Want("gates")) Gates("crossing_gates");
            if (Want("aircraft")) Aircraft("piston_single", "aircraft_rollout_single");
            Record("silence end", 1.0, "");
        }
        finally
        {
            double end = clock.Elapsed.TotalSeconds;
            facade.Dispose();
            provider.Traced.Release();
            scene.Dispose();
            Phonon.iplContextRelease(ref ctx);
            File.WriteAllText(Path.Combine(outDir, "clock_end.txt"), end.ToString("F3", CultureInfo.InvariantCulture));
        }

        var sb = new StringBuilder("name,start,seconds,note\n");
        foreach (var s in segments) sb.Append(CultureInfo.InvariantCulture, $"{s.Name},{s.Start:F3},{s.Seconds:F3},\"{s.Note}\"\n");
        File.WriteAllText(Path.Combine(outDir, "segments.csv"), sb.ToString());
        var sp = new StringBuilder("at,text\n");
        foreach (var (at, text) in speech) sp.Append(CultureInfo.InvariantCulture, $"{at:F3},\"{text.Replace("\"", "'")}\"\n");
        File.WriteAllText(Path.Combine(outDir, "speech.csv"), sp.ToString());
        Console.WriteLine($"Wrote {outDir}/capture.post.wav, segments.csv ({segments.Count}), speech.csv ({speech.Count}).");
        return 0;
    }
}
