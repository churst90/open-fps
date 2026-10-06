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
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --wet-roads: tyres on wet roads (RoadWater, WetTyres; docs/WET_ROADS.md), measured and played
/// through the game.
///
///   --wet-roads water
///        the road's water at each rain class (texture, sheet in the wheel paths, gutter spread,
///        puddles), how it drains and dries after rain, and the grip it leaves a car and a bus
///   --wet-roads levels out=DIR [cars=i4_midsize,transit_bus] [sec=6]
///        one vehicle's whole voice alone at a metre (no lift, no law, no HRTF), held at 30, 50 and
///        80 km/h on a dry road, a damp one and the water of light, moderate and heavy rain: one mono
///        float WAV each (pascals x 0.05) and levels.csv, for tools/wet_roads.py
///   --wet-roads game out=DIR [set=all|passby|bus|puddle|cabin|drying]
///        the game's own path (ClientAudioSystem over the FMOD provider: HRTF, ear model, loudness law,
///        the rain itself when it rains), each car a client entity whose wheels carry the water
///        the server would send. Captured from the master in float (DIR/capture.post.wav) with
///        DIR/segments.csv. No map: no walls, echoes or reverb; flat asphalt.
/// </summary>
public static class WetRoadSpike
{
    private const int Rate = 48000;
    private const float PascalsToFull = 0.05f;
    private static readonly byte Asphalt = RoadSurfaces.IndexOf("Asphalt");

    /// <summary>The street of the renders: two lanes either side of a crown along x, 7 m kerb to kerb.</summary>
    private const float HalfWidth = 3.5f, LaneOffset = 1.75f;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        if (args.Contains("game")) return Game(args);
        if (args.Contains("levels")) return Levels(args);
        return WaterTable();
    }

    // ── The water ───────────────────────────────────────────────────────────────────────────────

    private static readonly (string Name, float Rain)[] Classes =
    {
        ("dry", 0f), ("drizzle", 0.3f), ("light", Rainfall.LightRate), ("moderate", Rainfall.ModerateRate),
        ("heavy", Rainfall.HeavyRate), ("violent", Rainfall.ViolentRate),
    };

    /// <summary>A road settled in this rain.</summary>
    private static RoadWater Settled(float rain)
    {
        var w = new RoadWater();
        w.Step(rain, 0.05f, 1f);
        return w;
    }

    /// <summary>The water under a wheel <paramref name="fromCrown"/> metres off the crown of the render street.</summary>
    private static float WheelWater(RoadWater w, float fromCrown, float puddle = 0f)
        => w.WaterMm(Asphalt, MathF.Abs(fromCrown), HalfWidth - MathF.Abs(fromCrown), HalfWidth, puddle);

    private static int WaterTable()
    {
        var car = MachineRegistry.VehicleFor("i4_midsize");
        var bus = MachineRegistry.VehicleFor("transit_bus");
        Console.WriteLine("Rain class, rate; water mm at 1 m / 2.5 m / 3.3 m from the crown (kerb at 3.5 m); gutter spread m; puddle fill;");
        Console.WriteLine("grip left (share of dry) for the car and the bus in the wheel path at 30 / 50 / 80 / 110 km/h");
        foreach (var (name, rain) in Classes)
        {
            var w = Settled(rain);
            var d = w.Drainage;
            float q = d.RunoffCoefficient * w.Through(d.GutterSeconds) / 3.6e6f * d.InletSpacingMetres * HalfWidth;
            float spread = RoadWaterLaw.GutterSpreadMetres(q, d);
            float wp = WheelWater(w, 2.5f);
            string Grip(VehicleProfile v) => string.Join(" / ", new[] { 30f, 50f, 80f, 110f }.Select(k =>
                RoadWaterLaw.GripFactor(Asphalt, wp, k / 3.6f, v.Tyres.InflationKPa, v.Tyres.TreadDepthMm).ToString("F2", CultureInfo.InvariantCulture)));
            Console.WriteLine($"  {name,-9} {rain,5:F1} mm/h  {WheelWater(w, 1f),5:F2} / {wp,5:F2} / {WheelWater(w, 3.3f),5:F2}  spread {spread:F2}  puddles {w.PuddleFill:F2}"
                              + $"   car {Grip(car)}   bus {Grip(bus)}");
        }
        Console.WriteLine($"\nAquaplaning speed, km/h (Gallaway), car tyre {car.Tyres.InflationKPa:F0} kPa {car.Tyres.TreadDepthMm:F0} mm tread / bus {bus.Tyres.InflationKPa:F0} kPa / slick 160 kPa no tread; Horne's 6.36 sqrt(p): "
                          + $"{6.36f * MathF.Sqrt(car.Tyres.InflationKPa):F0} / {6.36f * MathF.Sqrt(bus.Tyres.InflationKPa):F0}");
        foreach (float film in new[] { 0.1f, 0.5f, 1f, 2f, 5f, 10f })
            Console.WriteLine($"  film {film,4:F1} mm: {RoadWaterLaw.AquaplaningKmh(film, 0.7f, car.Tyres.InflationKPa, car.Tyres.TreadDepthMm),5:F0}"
                              + $" / {RoadWaterLaw.AquaplaningKmh(film, 0.7f, bus.Tyres.InflationKPa, bus.Tyres.TreadDepthMm),5:F0}"
                              + $" / {RoadWaterLaw.AquaplaningKmh(film, 0.7f, 160f, 0f),5:F0}");

        Console.WriteLine("\nAfter an hour of heavy rain stops, an overcast spring afternoon (15 C, 85 %, 3 m/s): the water in the wheel path, mm");
        foreach (var (label, hour, rh, temp) in new[] { ("afternoon, overcast", 14f, 0.85f, 15f), ("night, humid", 2f, 0.9f, 12f), ("summer noon, dry air", 12f, 0.5f, 25f) })
        {
            var w = Settled(Rainfall.HeavyRate);
            float evap = Evaporation(temp, rh, 3f, hour, 150, 0f);
            var sb = new StringBuilder($"  {label,-22} E {evap:F2} mm/h: ");
            float t = 0f;
            foreach (float minute in new[] { 0f, 2f, 5f, 10f, 20f, 40f, 60f, 90f, 120f, 180f, 240f })
            {
                while (t < minute * 60f) { w.Step(0f, evap, 10f); t += 10f; }
                sb.Append(CultureInfo.InvariantCulture, $"{minute:0}m {WheelWater(w, 2.5f):F2}  ");
            }
            Console.WriteLine(sb.ToString());
        }
        return 0;
    }

    /// <summary>Penman's evaporation for the weather (as RoadWaterSystem works it out on the server).</summary>
    private static float Evaporation(float celsius, float humidity, float windMps, float hour, int day, float precipitation)
    {
        float cloud = RoadWaterLaw.CloudFrom(precipitation, humidity);
        float sun = RoadWaterLaw.NetRadiationWm2(hour, day, cloud, RoadDrainageSpec.Default.LatitudeDegrees);
        return RoadWaterLaw.EvaporationMmPerHour(celsius, humidity, windMps * 0.75f, sun);
    }

    // ── Wheels as the server sends them ─────────────────────────────────────────────────────────

    /// <summary>One vehicle's wheels held at a steady speed, and where each is in the car's frame.</summary>
    private sealed class Rig
    {
        public readonly VehicleProfile Profile;
        public readonly WheelDynamics Body;
        public readonly float CogZ;
        public Rig(VehicleProfile v, float speed)
        {
            Profile = v;
            Body = new WheelDynamics(v);
            CogZ = v.Running.CentreOfGravityZ;
            Body.Hold(speed, 0f, 0f);
        }

        /// <summary>The wheels on the wire with this water under each.</summary>
        public WheelState[] Wire(Func<int, float> water)
        {
            var wire = new WheelState[Body.Wheels.Length];
            for (int i = 0; i < wire.Length; i++)
            {
                ref var w = ref Body.Wheels[i];
                wire[i] = WheelState.Encode(w.Load, w.AngularSpeed, w.SlipRatio, w.SlipAngle, w.Surface, w.Demand, water(i));
            }
            return wire;
        }
    }

    // ── One voice at a metre ────────────────────────────────────────────────────────────────────

    private static int Levels(string[] args)
    {
        string outDir = Arg(args, "out=") ?? "/tmp/openfps-wet-levels";
        Directory.CreateDirectory(outDir);
        float sec = float.Parse(Arg(args, "sec=") ?? "6", CultureInfo.InvariantCulture);
        string[] cars = (Arg(args, "cars=") ?? "i4_midsize,transit_bus").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var conditions = new (string Name, Func<float, float> Water)[]
        {
            ("dry", _ => 0f),
            ("damp", _ => 0.35f),
            ("light", x => WheelWater(Settled(Rainfall.LightRate), x)),
            ("moderate", x => WheelWater(Settled(Rainfall.ModerateRate), x)),
            ("heavy", x => WheelWater(Settled(Rainfall.HeavyRate), x)),
        };
        var csv = new StringBuilder("file,vehicle,kmh,condition,water_mm\n");
        foreach (var key in cars)
        {
            var v = MachineRegistry.VehicleFor(key);
            foreach (float kmh in new[] { 30f, 50f, 80f })
            foreach (var (cond, water) in conditions)
            {
                float speed = kmh / 3.6f;
                var rig = new Rig(v, speed);
                var voice = new EngineVoiceState(v, Rate, 11) { TargetSpeed = speed, CompensateLevel = false };
                voice.PlaceAtSpeed(speed);
                voice.Revive();
                // The listener 7.5 m off the car's right side, ear height: the pass-by microphone.
                voice.SetListener(new Vector3(7.5f, 1.2f, 0f) - v.ExhaustSlot);
                var wires = rig.Wire(i => water(LaneOffset + rig.Body.Wheels[i].Y));
                voice.Wheels = wires;
                float mean = wires.Average(w => w.WaterMm);
                int n = (int)(sec * Rate);
                var buf = new float[512];
                var all = new List<float>(n);
                // Two seconds to settle, then the record.
                for (int b = 0; b < (2 * Rate) / buf.Length; b++) voice.Render(buf);
                while (all.Count < n) { voice.Render(buf); all.AddRange(buf); }
                string file = $"{key}_{kmh:0}_{cond}.wav";
                RunningWaterSpike.WriteFloatWav(Path.Combine(outDir, file), all.Select(x => x * voice.PascalsAtFullScale * PascalsToFull).ToArray(), 1);
                csv.Append(CultureInfo.InvariantCulture, $"{file},{key},{kmh:0},{cond},{mean:F3}\n");
                Console.WriteLine($"  {file}  water {mean:F2} mm");
            }
        }
        File.WriteAllText(Path.Combine(outDir, "levels.csv"), csv.ToString());
        Console.WriteLine($"Pascals x {PascalsToFull} (0 dBFS is {20 * Math.Log10(1 / PascalsToFull / 20e-6):F1} dB SPL at a metre). tools/wet_roads.py levels {outDir}");
        return 0;
    }

    // ── Through the game ────────────────────────────────────────────────────────────────────────

    private sealed record Segment(string Name, double Start, double Seconds, string Note);

    private static int Game(string[] args)
    {
        string outDir = Arg(args, "out=") ?? "/tmp/openfps-wet-game";
        string set = Arg(args, "set=") ?? "all";
        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", Path.Combine(outDir, "capture.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(outDir, "capture.post.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        Console.WriteLine($"Ear model {(OpenFPS.Common.Hearing.EarModel.Enabled ? "on" : "off")}; /levels {Loudness.DynamicRangeCompression:F2}");
        var provider = new FmodAudioProvider();
        var facade = new AudioEngineFacade(provider);
        string sounds = LabPaths.Sounds();
        facade.InitializeForTest(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        provider.EarWindEnabled = false;
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
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(900f, 1f, 900f), IsSolid = true },
            Material = new MaterialComponent { Material = "Asphalt" },
        });
        WindField.Weather = WindWeather.Steady(0f, 250f, 0f);

        var segments = new List<Segment>();
        Action<double>? perFrame = null;
        int nextId = 100;
        var roadNow = new RoadWater();

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
        }
        void Stand(Vector3 feet, float yaw)
        {
            player.Position = feet;
            player.Yaw = yaw;
            player.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        }
        // The weather as the server would broadcast it: the rain falling and the road's water.
        void Weather(float rain, RoadWater road)
        {
            roadNow = road;
            world.UpdateAtmosphere(new WorldStateUpdate
            {
                Temperature = 14f, Humidity = rain > 0f ? 0.92f : 0.85f, AirPressure = 1013.25f, AirAbsorptionMultiplier = 1f,
                PrecipitationIntensity = rain > 0f ? Rainfall.IntensityFor(rain) : 0f,
                RainRateMmPerHour = rain,
                RoadWater = road.Save(),
            });
        }

        // A car heading +x along the near lane (z = -LaneOffset: its right is -z, toward the south kerb
        // at z = -HalfWidth). The listener on the south pavement.
        var heading = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        int AddCar(string preset)
        {
            int id = nextId++;
            var profile = MachineRegistry.VehicleFor(preset);
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.NPC, Moves = true,
                Transform = new Transform { Position = new Vector3(-500f, 0f, -LaneOffset), Rotation = heading },
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
        // Where each wheel is across the road, and the puddles along it.
        var puddles = new List<(float X0, float X1, float Reach, float Depth)>();
        float WaterUnder(Rig rig, int i, float carX)
        {
            ref var w = ref rig.Body.Wheels[i];
            float x = carX + w.X + rig.CogZ;
            float z = -LaneOffset - w.Y;                   // right of +x is -z
            float fromCrown = MathF.Abs(z), fromKerb = HalfWidth - fromCrown;
            float puddle = 0f;
            foreach (var p in puddles)
            {
                if (z > 0f) continue;
                float u = (x - 0.5f * (p.X0 + p.X1)) / (0.5f * (p.X1 - p.X0));
                float r2 = u * u + (fromKerb / p.Reach) * (fromKerb / p.Reach);
                float fill = roadNow.PuddleFill;
                if (r2 < fill) puddle = MathF.Max(puddle, p.Depth * (fill - r2));
            }
            return roadNow.WaterMm(Asphalt, fromCrown, fromKerb, HalfWidth, puddle);
        }
        void Drive(int id, Rig rig, Vector3 at, float speed)
        {
            var wire = rig.Wire(i => WaterUnder(rig, i, at.X));
            world.SyncState(new[] { new EntityState
            {
                EntityId = id, Transform = QuantizedTransform.FromTransform(new Transform { Position = at, Rotation = heading }),
                LinearVelocity = new Vector3(speed, 0f, 0f), Wheels = wire,
            } });
        }
        void Remove(int id)
        {
            world.RemoveEntities(new[] { id });
            audio.ForgetEntity(id);
        }
        double maxWater = 0;
        void PassBy(string preset, float kmh, float lateral, double seconds, string label, string note)
        {
            float speed = kmh / 3.6f;
            var rig = new Rig(MachineRegistry.VehicleFor(preset), speed);
            Stand(new Vector3(0f, 0f, -LaneOffset - lateral), 0f);     // facing the road (+z)
            double t0 = clock.Elapsed.TotalSeconds + 2.0;
            Vector3 At(double t) => new((float)(speed * (t - t0 - seconds / 2.0)), 0f, -LaneOffset);
            int id = AddCar(preset);
            maxWater = 0;
            perFrame = t =>
            {
                var p = At(t);
                Drive(id, rig, p, speed);
                for (int i = 0; i < rig.Body.Wheels.Length; i++) maxWater = Math.Max(maxWater, WaterUnder(rig, i, p.X));
            };
            Pump(2.0);
            Record(label, seconds, note);
            Console.WriteLine($"           deepest water under a wheel {maxWater:F2} mm");
            perFrame = null;
            Remove(id);
            Pump(1.5);
        }
        void Cabin(string preset, float kmh, double seconds, string label, string note)
        {
            float speed = kmh / 3.6f;
            var rig = new Rig(MachineRegistry.VehicleFor(preset), speed);
            int id = AddCar(preset);
            double t0 = clock.Elapsed.TotalSeconds;
            player.RidingEntityId = id;
            perFrame = t =>
            {
                var p = new Vector3(-200f + (float)(speed * (t - t0)), 0f, -LaneOffset);
                Drive(id, rig, p, speed);
                Stand(p + new Vector3(0.3f, 0f, 0.4f), MathF.PI / 2f);
            };
            Pump(4.0);
            Record(label, seconds, note);
            perFrame = null;
            player.RidingEntityId = -1;
            Remove(id);
            Pump(1.5);
        }
        string Water(RoadWater w) => $"water under the crown-side wheels {WheelWater(w, LaneOffset - 0.78f):F2} mm, the kerb-side {WheelWater(w, LaneOffset + 0.78f):F2} mm, puddles {w.PuddleFill:F2} full";

        try
        {
            Weather(0f, Settled(0f));
            Pump(2.0);
            Record("silence", 2.0, "");

            if (set is "all" or "passby")
            {
                foreach (float kmh in new[] { 30f, 50f })
                    foreach (var (cond, rain) in new[] { ("dry", 0f), ("light rain", Rainfall.LightRate), ("heavy rain", Rainfall.HeavyRate) })
                    {
                        var road = Settled(rain);
                        Weather(rain, road);
                        Pump(3.0);
                        PassBy("i4_midsize", kmh, 3.5f, 10.0, $"car {kmh:0} kmh {cond}", $"rain {rain:0.#} mm/h; {Water(road)}; listener 3.5 m from the lane's middle");
                    }
                // The same heavy-rain road with the rain stopped a minute ago: the tyres without the rain.
                var after = Settled(Rainfall.HeavyRate);
                for (int k = 0; k < 6; k++) after.Step(0f, 0.05f, 10f);
                Weather(0f, after);
                Pump(3.0);
                PassBy("i4_midsize", 50f, 3.5f, 10.0, "car 50 kmh wet road rain just stopped", $"no rain falling; {Water(after)}");
                Weather(Rainfall.HeavyRate, Settled(Rainfall.HeavyRate));
                Pump(3.0);
                Record("heavy rain alone", 6.0, "no car: the rain on the street, for reference");
            }
            if (set is "all" or "bus")
            {
                foreach (var (cond, rain) in new[] { ("dry", 0f), ("heavy rain", Rainfall.HeavyRate) })
                {
                    var road = Settled(rain);
                    Weather(rain, road);
                    Pump(3.0);
                    PassBy("transit_bus", 40f, 3.5f, 12.0, $"bus 40 kmh {cond}", $"rain {rain:0.#} mm/h; {Water(road)}");
                }
            }
            if (set is "all" or "puddle")
            {
                // After the rain: no rain falling, the road wet, the puddles full. One puddle at the kerb
                // in front of the listener, 3 m long and reaching 1.4 m into the road, 20 mm at its deepest.
                var road = Settled(Rainfall.HeavyRate);
                for (int k = 0; k < 12; k++) road.Step(0f, 0.05f, 10f);
                Weather(0f, road);
                puddles.Add((-1.5f, 1.5f, 1.4f, 20f));
                Pump(3.0);
                foreach (float kmh in new[] { 25f, 45f })
                    PassBy("i4_midsize", kmh, 3.5f, 9.0, $"puddle splash car {kmh:0} kmh", $"no rain; {Water(road)}; kerb-side wheels through a 3 m puddle");
                puddles.Clear();
            }
            if (set is "all" or "cabin")
            {
                foreach (var (cond, rain) in new[] { ("dry", 0f), ("heavy rain", Rainfall.HeavyRate), ("wet road no rain", -1f) })
                {
                    RoadWater road;
                    if (rain >= 0f) road = Settled(rain);
                    else { road = Settled(Rainfall.HeavyRate); for (int k = 0; k < 30; k++) road.Step(0f, 0.05f, 10f); }
                    Weather(MathF.Max(0f, rain), road);
                    Pump(2.0);
                    Cabin("i4_midsize", 50f, 10.0, $"cabin car 50 kmh {cond}", $"rain {MathF.Max(0f, rain):0.#} mm/h; {Water(road)}");
                }
            }
            if (set is "all" or "drying")
            {
                // An hour of heavy rain ends; an overcast spring afternoon. A car passes every 6 s, and
                // between passes the road is moved on by the minutes shown (time compressed).
                var road = Settled(Rainfall.HeavyRate);
                float evap = Evaporation(15f, 0.85f, 3f, 14f, 120, 0f);
                float t = 0f;
                foreach (float minute in new[] { 0f, 2f, 5f, 10f, 20f, 40f, 60f, 90f, 120f, 180f })
                {
                    while (t < minute * 60f) { road.Step(0f, evap, 10f); t += 10f; }
                    Weather(0f, road);
                    PassBy("i4_midsize", 50f, 3.5f, 6.0, $"drying {minute:0} min after rain", $"evaporation {evap:F2} mm/h; {Water(road)}");
                }
            }
            Record("silence end", 1.0, "");
        }
        finally
        {
            facade.Dispose();
        }

        var sb = new StringBuilder("name,start,seconds,note\n");
        foreach (var s in segments) sb.Append(CultureInfo.InvariantCulture, $"{s.Name},{s.Start:F3},{s.Seconds:F3},\"{s.Note}\"\n");
        File.WriteAllText(Path.Combine(outDir, "segments.csv"), sb.ToString());
        Console.WriteLine($"Wrote {outDir}/capture.post.wav and segments.csv ({segments.Count} segments). tools/wet_roads.py game {outDir}");
        return 0;
    }

    private static string? Arg(string[] args, string key) => args.FirstOrDefault(x => x.StartsWith(key, StringComparison.Ordinal))?[key.Length..];
}
