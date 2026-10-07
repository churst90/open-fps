using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using OpenFPS.Client.AudioEngine.Acoustics;
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
/// --driving out=DIR [set=all|horn|siren|bend|rails|gates|aircraft]: the driving controls and aids of
/// docs/DRIVING_AIDS.md, heard through the game: ClientAudioSystem over the FMOD provider, the HRTF, the
/// loudness law, the cabin when sitting in a vehicle. Each scene is driven the way the server drives it
/// (the horn and siren switches on EntityState.Signals, the wheels on the wire, a crossing's closed
/// signal on its emitters, the roads as MapRoads sends them) and captured from the master
/// (DIR/capture.post.wav) with DIR/segments.csv, and what the driving aids said with when (DIR/speech.csv).
///
/// Scenes: a car's and a bus's horn held, from the kerb and the driver's seat; the police car's siren
/// through its tones, from the kerb and the seat; a right turn off Main Street from 65 km/h by a driver
/// who brakes on the spoken junction alone, and by one who brakes on the brake cue; a car over a level
/// crossing's rails from the kerb and the seat; a crossing's gates going down and up with the bell; a
/// light single landing, rolling out, turning round and taking off again, from beside the runway.
/// </summary>
public static class DrivingSpike
{
    private sealed record Segment(string Name, double Start, double Seconds, string Note);

    private static string? Arg(string[] args, string key) => args.FirstOrDefault(x => x.StartsWith(key, StringComparison.Ordinal))?[key.Length..];

    private sealed class Rig
    {
        public readonly WheelDynamics Body;
        public Rig(VehicleProfile v) { Body = new WheelDynamics(v); }
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
        TracedReverbSet.Configure(ctx, scene);

        var provider = new FmodAudioProvider();
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
        var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds);
        world.RegisterDefinition(new EntityDefinition
        {
            EntityId = 1,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0f, -0.5f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(4000f, 1f, 4000f), IsSolid = true },
            // Concrete in the client's world: the driving aids read any square of asphalt with no name as
            // a junction, and four kilometres of it would be one.
            Material = new MaterialComponent { Material = "Concrete" },
        });
        WindField.Weather = WindWeather.Steady(0f, 250f, 0f);
        world.UpdateAtmosphere(new WorldStateUpdate { Temperature = 15f, Humidity = 0.6f, AirPressure = 1013.25f, AirAbsorptionMultiplier = 1f });

        var segments = new List<Segment>();
        var speech = new List<(double At, string Text)>();
        audio.Driving.Announce += text => speech.Add((clock.Elapsed.TotalSeconds, text));
        Action<double>? perFrame = null;
        int nextId = 1000;

        void Pump(double seconds)
        {
            var until = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < until)
            {
                perFrame?.Invoke(clock.Elapsed.TotalSeconds);
                audio.Update(world.GetSnapshot());
                facade.PumpForTest();
                Thread.Sleep(4);
            }
        }
        // Each scene starts recording a moment after it is set up: a vehicle's voice made in the same
        // frame as the listener is seated in it starts as the outside of the car for a fifth of a second.
        const double Preroll = 0.8;
        void Record(string name, double seconds, string note)
        {
            if (!name.StartsWith("silence", StringComparison.Ordinal)) Pump(Preroll);
            double start = clock.Elapsed.TotalSeconds;
            Pump(seconds);
            segments.Add(new Segment(name, start, seconds, note));
            Console.WriteLine($"  {start,7:F2} s  {name} ({seconds:F1} s)  {note}");
        }
        void RecordUntil(string name, Func<bool> done, double limit, string note)
        {
            Pump(Preroll);
            double start = clock.Elapsed.TotalSeconds;
            while (!done() && clock.Elapsed.TotalSeconds - start < limit) Pump(0.05);
            Pump(1.5);
            double seconds = clock.Elapsed.TotalSeconds - start;
            segments.Add(new Segment(name, start, seconds, note));
            Console.WriteLine($"  {start,7:F2} s  {name} ({seconds:F1} s)  {note}");
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
                float grip = 0.95f * WheelDynamics.G;
                decel = 0f;
                if (!brakeCue)
                {
                    if (said >= 0 && t - said > 1.0 && speed > 25f / 3.6f) decel = 0.2f * WheelDynamics.G;
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
                world.SyncState(new[] { new EntityState { EntityId = id, Transform = QuantizedTransform.FromTransform(new Transform { Position = pos, Rotation = rot }), LinearVelocity = vel } });
                log.Append(CultureInfo.InvariantCulture, $"{t - t0:F2},{phase}{(run != null ? "/" + run.State : "")},{pos.X:F1},{pos.Y:F1},{pos.Z:F1},{vel.Length():F1}\n");
            };
            RecordUntil(label, () => done, 200, $"{air.Name}: a 3-degree approach from 700 m, touchdown, landing roll, the turn round, a 4 s hold, the take-off roll and the climb-out; "
                                             + "heard from 45 m beside the runway at the touchdown point");
            File.WriteAllText(Path.Combine(outDir, label + ".track.csv"), log.ToString());
            perFrame = null;
            Remove(id);
            Pump(1.0);
        }

        try
        {
            Stand(new Vector3(0f, 0f, -300f), 0f);
            Pump(2.0);
            Record("silence", 2.0, "");
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
            TracedReverbSet.Dispose();
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
