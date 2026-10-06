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
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --cabin: sitting in a vehicle, heard through the game (CabinPaths).
///
///   --cabin game out=DIR [paths=on|off] [set=all|car|bus|police] [sec=10]
///        the game's own path: ClientAudioSystem over the FMOD provider, the HRTF, the ear model, the
///        loudness law, the cabin's traced reverberation (TracedReverbSet.RideIn over a street of open
///        ground), the rain when it rains. The listener sits where the server seats them
///        (VehicleShell's rows: the driver on the left of the front row), the vehicle drives along a
///        straight road with its wheels as the server sends them. Captured from the master in float
///        (DIR/capture.post.wav) with DIR/segments.csv, for tools/interaural.py segments and
///        tools/cabin.py. paths=off plays the interior from one point, as before 2026-10-06.
///   --cabin model out=DIR [cars=i4_economy,transit_bus] [sec=8]
///        the interior model alone, no HRTF, no mixer: the voice inside with every path summed
///        (EngineVoiceState.Render carries the paths no tap is playing), paths on and off, one mono
///        float WAV per condition, for the level check (tools/cabin.py model).
/// </summary>
public static class CabinSpike
{
    private const int Rate = 48000;
    private static readonly byte Asphalt = RoadSurfaces.IndexOf("Asphalt");

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        if (Arg(args, "paths=") is "off" or "0") CabinPaths.Enabled = false;
        if (args.Contains("model")) return Model(args);
        return Game(args);
    }

    /// <summary>One vehicle's wheels at a speed, and the wire the server would send.</summary>
    private sealed class Rig
    {
        public readonly WheelDynamics Body;
        public Rig(VehicleProfile v) { Body = new WheelDynamics(v); }
        public WheelState[] Wire(float speed, float accel, float water)
        {
            Body.Hold(speed, accel, 0f);
            var wire = new WheelState[Body.Wheels.Length];
            for (int i = 0; i < wire.Length; i++)
            {
                ref var w = ref Body.Wheels[i];
                wire[i] = WheelState.Encode(w.Load, w.AngularSpeed, w.SlipRatio, w.SlipAngle, w.Surface, w.Demand, water);
            }
            return wire;
        }
    }

    /// <summary>Where the server seats someone (VehicleShell.Build), in the vehicle's frame: the feet.</summary>
    internal static Vector3 Seat(VehicleProfile v, string which)
    {
        if (VehicleCabin.Measure(v) is not { } g) return Vector3.Zero;
        int rows = VehicleCabin.Rows(g);
        float firstRow = g.Front - 0.75f, offset = g.Wc * 0.25f;
        return which switch
        {
            "passenger" => new Vector3(offset, g.FloorTop, firstRow),
            // A bus passenger: the middle row, by the right-hand window.
            "middle" => new Vector3(offset, g.FloorTop, firstRow - (rows / 2) * VehicleCabin.RowPitch),
            _ => new Vector3(-offset, g.FloorTop, firstRow),
        };
    }

    // ── The interior model alone ────────────────────────────────────────────────────────────────

    private static int Model(string[] args)
    {
        string outDir = Arg(args, "out=") ?? "/tmp/openfps-cabin-model";
        Directory.CreateDirectory(outDir);
        float sec = float.Parse(Arg(args, "sec=") ?? "8", CultureInfo.InvariantCulture);
        string[] cars = (Arg(args, "cars=") ?? "i4_economy,transit_bus").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var csv = new StringBuilder("file,vehicle,kmh,water_mm,paths\n");
        foreach (var key in cars)
        foreach (float kmh in new[] { 0f, 50f, 100f })
        foreach (float water in kmh > 0f ? new[] { 0f, 1.5f } : new[] { 0f })
        foreach (bool on in new[] { false, true })
        {
            CabinPaths.Enabled = on;
            var v = MachineRegistry.VehicleFor(key);
            float speed = kmh / 3.6f;
            var rig = new Rig(v);
            var voice = new EngineVoiceState(v, Rate, 11) { TargetSpeed = speed, CompensateLevel = false, Interior = true };
            voice.PlaceAtSpeed(speed);
            voice.Revive();
            voice.Wheels = rig.Wire(speed, 0f, water);
            int n = (int)(sec * Rate);
            var buf = new float[512];
            var all = new List<float>(n);
            for (int b = 0; b < (2 * Rate) / buf.Length; b++) voice.Render(buf);
            while (all.Count < n) { voice.Render(buf); all.AddRange(buf); }
            string file = $"{key}_{kmh:0}_{(water > 0f ? "wet" : "dry")}_{(on ? "paths" : "mono")}.wav";
            RunningWaterSpike.WriteFloatWav(Path.Combine(outDir, file), all.Select(x => x * voice.PascalsAtFullScale * 0.05f).ToArray(), 1);
            csv.Append(CultureInfo.InvariantCulture, $"{file},{key},{kmh:0},{water:F2},{(on ? "on" : "off")}\n");
            Console.WriteLine($"  {file}  layout {(voice.CabinLayout?.Count ?? 0)} paths");
        }
        File.WriteAllText(Path.Combine(outDir, "model.csv"), csv.ToString());
        Console.WriteLine($"Pascals x 0.05. tools/cabin.py model {outDir}");
        return 0;
    }

    // ── Through the game ────────────────────────────────────────────────────────────────────────

    private sealed record Segment(string Name, double Start, double Seconds, string Note);

    private static int Game(string[] args)
    {
        string outDir = Arg(args, "out=") ?? "/tmp/openfps-cabin-game";
        string set = Arg(args, "set=") ?? "all";
        float sec = float.Parse(Arg(args, "sec=") ?? "10", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", Path.Combine(outDir, "capture.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(outDir, "capture.post.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        Console.WriteLine($"Cabin paths {(CabinPaths.Enabled ? "on" : "off (one point)")}; ear model {(OpenFPS.Common.Hearing.EarModel.Enabled ? "on" : "off")}; /levels {Loudness.DynamicRangeCompression:F2}");

        // The street the traced reverberation hears outside: open asphalt. Riding, the room is the
        // cabin, traced from the vehicle's own shell (TracedReverbSet.RideIn).
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no Steam Audio context"); return 1; }
        var scene = new SteamAudioScene(ctx);
        scene.Build(new List<SteamAudioScene.Box> { new(new Vector3(0f, -0.5f, 0f), new Vector3(1200f, 1f, 1200f), Quaternion.Identity, "Asphalt") });
        TracedReverbSet.Configure(ctx, scene);

        var provider = new FmodAudioProvider();
        var facade = new AudioEngineFacade(provider);
        string sounds = LabPaths.Sounds();
        facade.InitializeForTest(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        provider.EarWindEnabled = false;
        provider.SetAcousticMap(new AcousticMap(new Vector3(1200, 100, 1200), new Vector3(-600, -10, -600)) { GlobalEnvironmentId = AcousticConstants.GlobalRegionId });
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var world = new ClientWorldState();
        world.Clear(new Vector3(4000, 400, 4000));
        var player = new LocalPlayerState();
        var mapping = new SoundMappingService(player);
        mapping.Initialize(sounds);
        var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds);
        world.RegisterDefinition(new EntityDefinition
        {
            EntityId = 1,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0f, -0.5f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(1200f, 1f, 1200f), IsSolid = true },
            Material = new MaterialComponent { Material = "Asphalt" },
        });
        WindField.Weather = WindWeather.Steady(0f, 250f, 0f);

        var segments = new List<Segment>();
        Action<double>? perFrame = null;
        int nextId = 100;
        double lastLog = 0;

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
        void Record(string name, double seconds, string note)
        {
            double start = clock.Elapsed.TotalSeconds;
            Pump(seconds);
            segments.Add(new Segment(name, start, seconds, note));
            Console.WriteLine($"  {start,7:F2} s  {name} ({seconds:F1} s)  {note}");
            Console.WriteLine($"           cabin trace {(TracedReverbSet.Cabin != null ? "ready" : "none")}, listener trace {(TracedReverbSet.Listener != null ? "ready" : "none")}, HRTF voices free {provider.SpatialVoicesFree}");
        }
        void Weather(float rain, RoadWater road)
        {
            world.UpdateAtmosphere(new WorldStateUpdate
            {
                Temperature = 14f, Humidity = rain > 0f ? 0.92f : 0.85f, AirPressure = 1013.25f, AirAbsorptionMultiplier = 1f,
                PrecipitationIntensity = rain > 0f ? Rainfall.IntensityFor(rain) : 0f,
                RainRateMmPerHour = rain,
                RoadWater = road.Save(),
            });
        }
        RoadWater Settled(float rain)
        {
            var w = new RoadWater();
            w.Step(rain, 0.05f, 1f);
            return w;
        }

        // Heading +x along a lane 1.75 m south of the crown.
        var heading = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        int AddCar(string preset)
        {
            int id = nextId++;
            var profile = MachineRegistry.VehicleFor(preset);
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.NPC, Moves = true,
                Transform = new Transform { Position = new Vector3(-300f, 0f, -1.75f), Rotation = heading },
            };
            def.SoundEmitter = new SoundEmitterComponent();
            def.SoundEmitter.IsSynth = true;
            def.SoundEmitter.SoundId = "engine:" + preset;
            def.SoundEmitter.Mode = PlaybackMode.LoopOne;
            def.SoundEmitter.Volume = 1f;
            def.SoundEmitter.Range = Loudness.AudibleRange(profile.SourceLevelDb);
            def.SoundEmitter.MinDistance = 3f;
            world.RegisterDefinition(def);
            return id;
        }
        void Remove(int id)
        {
            world.RemoveEntities(new[] { id });
            audio.ForgetEntity(id);
        }
        string SeatNote(VehicleProfile v, Vector3 seat) => $"seat ({seat.X:F2}, {seat.Y:F2}, {seat.Z:F2}) m in the {v.Name}'s frame, ear 1.0 m above it";

        // A ride: the vehicle along the lane at the speed the profile gives for each moment, the
        // listener in the seat, facing the way it faces.
        void Ride(string preset, string seatName, Func<double, float> speedAt, double seconds, string label, string note, float waterMm = 0f)
        {
            var v = MachineRegistry.VehicleFor(preset);
            var rig = new Rig(v);
            int id = AddCar(preset);
            var seat = Seat(v, seatName);
            double t0 = clock.Elapsed.TotalSeconds;
            double lastT = t0;
            float x = -300f, lastSpeed = speedAt(0);
            player.RidingEntityId = id;
            perFrame = t =>
            {
                float dt = (float)(t - lastT);
                lastT = t;
                float sp = speedAt(t - t0);
                float accel = dt > 1e-4f ? (sp - lastSpeed) / dt : 0f;
                lastSpeed = sp;
                x += sp * dt;
                var at = new Vector3(x, 0f, -1.75f);
                world.SyncState(new[] { new EntityState
                {
                    EntityId = id, Transform = QuantizedTransform.FromTransform(new Transform { Position = at, Rotation = heading }),
                    LinearVelocity = new Vector3(sp, 0f, 0f), Wheels = rig.Wire(sp, accel, waterMm),
                } });
                player.Position = at + Vector3.Transform(seat, heading);
                player.Yaw = MathF.PI / 2f;
                player.Rotation = heading;
                if (t - lastLog > 5) { lastLog = t; }
            };
            Pump(4.0);
            Record(label, seconds, $"{note}; {SeatNote(v, seat)}");
            perFrame = null;
            player.RidingEntityId = -1;
            Remove(id);
            Pump(1.5);
        }

        try
        {
            Weather(0f, Settled(0f));
            Pump(2.0);
            Record("silence", 2.0, "");
            if (set is "all" or "car")
            {
                Ride("i4_economy", "driver", _ => 0f, sec, "car driver idle", "engine idling, standing");
                // Pulling away: two seconds at idle, then about 0.18 g to 50 km/h, and holding it.
                Ride("i4_economy", "driver", t => t < 2.0 ? 0f : MathF.Min(50f / 3.6f, (float)(t - 2.0) * 1.8f), sec + 2.0,
                     "car driver pulling away", "idle, then 1.8 m/s2 up to 50 km/h");
                Ride("i4_economy", "driver", _ => 50f / 3.6f, sec, "car driver 50 kmh", "cruise, dry");
                Ride("i4_economy", "driver", _ => 100f / 3.6f, sec, "car driver 100 kmh", "cruise, dry");
                var road = Settled(Rainfall.HeavyRate);
                Weather(Rainfall.HeavyRate, road);
                Pump(2.0);
                float water = road.WaterMm(Asphalt, 1.75f - 0.75f, 3.5f - 1.0f, 3.5f, 0f);
                Ride("i4_economy", "driver", _ => 50f / 3.6f, sec, "car driver 50 kmh heavy rain",
                     $"heavy rain {Rainfall.HeavyRate:0.#} mm/h on the roof and the road, {water:F2} mm under the wheels", water);
                Weather(0f, Settled(0f));
                Pump(2.0);
            }
            if (set is "all" or "bus")
                Ride("transit_bus", "middle", _ => 40f / 3.6f, sec, "bus passenger 40 kmh", "cruise, dry, a passenger halfway down by the right-hand window");
            if (set is "all" or "police")
                Ride("police_interceptor", "driver", _ => 60f / 3.6f, sec, "police car driver 60 kmh", "cruise, dry");
            Record("silence end", 1.0, "");
        }
        finally
        {
            facade.Dispose();
            TracedReverbSet.Dispose();
            scene.Dispose();
            Phonon.iplContextRelease(ref ctx);
        }

        var sb = new StringBuilder("name,start,seconds,note\n");
        foreach (var s in segments) sb.Append(CultureInfo.InvariantCulture, $"{s.Name},{s.Start:F3},{s.Seconds:F3},\"{s.Note}\"\n");
        File.WriteAllText(Path.Combine(outDir, "segments.csv"), sb.ToString());
        Console.WriteLine($"Wrote {outDir}/capture.post.wav and segments.csv ({segments.Count} segments). tools/interaural.py segments {outDir}");
        return 0;
    }

    private static string? Arg(string[] args, string key) => args.FirstOrDefault(x => x.StartsWith(key, StringComparison.Ordinal))?[key.Length..];
}
