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
/// --game-levels [out=DIR] [set=measure|render|compare|all] [cars=a,b,..]: what the game's own mixer puts out
/// for one thing at a time, captured from the real output.
///
/// The whole client path, not a model of it: a ClientAudioSystem over an AudioEngineFacade over the
/// FmodAudioProvider, its output replaced by FMOD's WAV writer (OPENFPS_FMOD_WAV). Cars are entities in
/// a client world exactly as the server sends them, so the voice budget, the front/rear split, the
/// placement, the idle lift, the ground in each voice, the HRTF and the master (trim and makeup) are
/// all the game's. Footsteps come in through OnOwnFootstep, a walker's line and a door through
/// WorldAudioPlayer.Receive as the server's events, the fountain and the air conditioner as the
/// map's emitters. What is NOT here: the map, so no walls, no reflections and no reverb, and no other
/// sound. A flat asphalt ground is the only thing in the world.
///
/// Each scene plays alone, between gaps of silence, and its name and start time are written to
/// DIR/segments.csv beside DIR/capture.wav (stereo, 44.1 kHz, the game's full scale). tools/game_levels.py
/// cuts and measures them.
///
/// Car frame: the car faces +Z, its origin on the ground at its middle. The listener's ear is 1.7 m up.
/// </summary>
public static class GameLevelsSpike
{
    private sealed record Segment(string Name, double Start, double Seconds);

    public static int Run(string[] args)
    {
        string outDir = Arg(args, "out=") ?? "/tmp/openfps-game-levels";
        string set = Arg(args, "set=") ?? "measure";
        string[] cars = (Arg(args, "cars=") ?? "i4_economy,i4_midsize,police_interceptor").Split(',', StringSplitOptions.RemoveEmptyEntries);
        Directory.CreateDirectory(outDir);
        if (Arg(args, "calm=") is { } calm)
        {
            var c = calm.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            Calm = WindWeather.Steady(c[0], 250f, c.Length > 1 ? c[1] : 0f);
        }
        string wav = Path.Combine(outDir, "capture.wav");
        AcousticRegistry.Initialize();

        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", wav);
        var provider = new FmodAudioProvider();
        var facade = new AudioEngineFacade(provider);
        string sounds = LabPaths.Sounds();
        facade.InitializeForTest(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var world = new ClientWorldState();
        world.Clear(new Vector3(4000, 400, 4000));
        var player = new LocalPlayerState();
        var mapping = new SoundMappingService(player);
        mapping.Initialize(sounds);
        var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds);

        // The ground: asphalt, as a street is.
        world.RegisterDefinition(new EntityDefinition
        {
            EntityId = 1,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0f, -0.5f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(600f, 1f, 600f), IsSolid = true },
            Material = new MaterialComponent { Material = "Asphalt" },
        });

        var segments = new List<Segment>();
        Action<double>? perFrame = null;
        int nextId = 100;

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
        void Record(string name, double seconds)
        {
            double start = clock.Elapsed.TotalSeconds;
            Pump(seconds);
            segments.Add(new Segment(name, start, seconds));
            Console.WriteLine($"  {start,7:F2} s  {name} ({seconds:F1} s)");
        }
        void Stand(Vector3 feet, float yaw)
        {
            player.Position = feet;
            player.Yaw = yaw;
            player.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        }
        // Facing a point: the game's forward is +Z rotated about +Y by the yaw.
        float YawTo(Vector3 from, Vector3 to) => MathF.Atan2(to.X - from.X, to.Z - from.Z);

        int AddCar(string preset, Vector3 at, Vector3 velocity)
        {
            int id = nextId++;
            var profile = MachineRegistry.VehicleFor(preset);
            var rot = velocity.LengthSquared() > 0f ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(velocity.X, velocity.Z)) : Quaternion.Identity;
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.NPC, Moves = true,
                Transform = new Transform { Position = at, Rotation = rot },
            };
            // A real component, not default(struct): its initialisers (SynthRunning = true) only run in the constructor.
            def.SoundEmitter = new SoundEmitterComponent();
            def.SoundEmitter.IsSynth = true;
            def.SoundEmitter.SoundId = "engine:" + preset;
            def.SoundEmitter.Mode = PlaybackMode.LoopOne;
            def.SoundEmitter.Volume = 1f;
            def.SoundEmitter.Range = Loudness.AudibleRange(profile.SourceLevelDb);
            def.SoundEmitter.MinDistance = 3f;
            world.RegisterDefinition(def);
            Move(id, at, rot, velocity);
            return id;
        }
        void Move(int id, Vector3 at, Quaternion rot, Vector3 velocity)
            => world.SyncState(new[] { new EntityState { EntityId = id, Transform = QuantizedTransform.FromTransform(new Transform { Position = at, Rotation = rot }), LinearVelocity = velocity } });
        int AddEmitter(string soundId, Vector3 at, float range, float minDistance)
        {
            int id = nextId++;
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.StaticObject,
                Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
            };
            // A real component, not default(struct): its initialisers (SynthRunning = true) only run in the constructor.
            def.SoundEmitter = new SoundEmitterComponent();
            def.SoundEmitter.IsSynth = true;
            def.SoundEmitter.SoundId = soundId;
            def.SoundEmitter.Mode = PlaybackMode.LoopOne;
            def.SoundEmitter.Volume = 1f;
            def.SoundEmitter.Range = range;
            def.SoundEmitter.MinDistance = minDistance;
            world.RegisterDefinition(def);
            return id;
        }
        void Remove(int id)
        {
            world.RemoveEntities(new[] { id });
            audio.ForgetEntity(id);
        }

        // ── Scenes ───────────────────────────────────────────────────────────────────────────────
        void IdleCar(string preset, IEnumerable<(string Side, float D)> spots, double seconds)
        {
            var v = MachineRegistry.VehicleFor(preset);
            float half = v.LengthMetres * 0.5f, side = v.WidthMetres * 0.5f;
            Stand(new Vector3(0f, 0f, half + 2f), MathF.PI);
            int id = AddCar(preset, Vector3.Zero, Vector3.Zero);
            // Kept fresh, as the server's stream keeps a parked car's position fresh.
            perFrame = _ => Move(id, Vector3.Zero, Quaternion.Identity, Vector3.Zero);
            Pump(5.0);   // started, the idle lift settled
            foreach (var (where, d) in spots)
            {
                Vector3 feet = where switch
                {
                    "front" => new Vector3(0f, 0f, half + d),
                    "rear" => new Vector3(0f, 0f, -half - d),
                    _ => new Vector3(side + d, 0f, 0f),
                };
                Stand(feet, YawTo(feet, Vector3.Zero));
                Pump(0.8);
                Record($"idle {preset} {where} {d:0.#}m", seconds);
            }
            perFrame = null;
            Remove(id);
            Pump(1.5);
        }

        void PassBy(string preset, float kmh, float lateral, double seconds)
        {
            float speed = kmh / 3.6f;
            Stand(new Vector3(0f, 0f, lateral), MathF.PI);   // facing the road (-Z)
            double t0 = clock.Elapsed.TotalSeconds + 2.0;     // the car is level with you half way through the recording
            var vel = new Vector3(speed, 0f, 0f);
            var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
            Vector3 At(double t) => new((float)(speed * (t - t0 - seconds / 2.0)), 0f, 0f);
            int id = AddCar(preset, At(clock.Elapsed.TotalSeconds), vel);
            perFrame = t => Move(id, At(t), rot, vel);
            Pump(2.0);
            Record($"passby {preset} {kmh:0}kmh {lateral:0.#}m", seconds);
            perFrame = null;
            Remove(id);
            Pump(1.5);
        }

        void Footsteps(double seconds, string material = "Concrete")
        {
            // Walking on the spot, the game's walk cadence (a step every half second or so).
            Stand(new Vector3(30f, 0f, 30f), 0f);
            Pump(0.5);
            double start = clock.Elapsed.TotalSeconds, next = start + 0.2;
            perFrame = t =>
            {
                if (t < next) return;
                next += 0.52;
                float sideStep = ((int)((t - start) / 0.52) & 1) == 0 ? 0.12f : -0.12f;
                audio.OnOwnFootstep(player.Position + new Vector3(sideStep, 0f, 0f), material, "0");
            };
            Record($"footsteps own {material.ToLowerInvariant()}", seconds);
            perFrame = null;
            Pump(1.5);
        }

        // A walker's line at normal effort, said facing you from d metres. The first event of a key is
        // rendered and dropped (WorldAudioPlayer), so each is sent once to prime it.
        var takes = Speech.Takes.Where(t => t.Line.StartsWith("greet", StringComparison.Ordinal)
                                         && File.Exists(LabPaths.Sounds("VOICES", t.Voice, t.Line + ".ogg")))
                               .GroupBy(t => t.Voice).Select(g => g.First()).Take(4).ToList();
        void Speak(float d, double seconds)
        {
            Stand(new Vector3(-30f, 0f, 30f), 0f);
            var mouth = player.Position + new Vector3(0f, Speech.MouthHeight, d);
            WorldAudioEvent Line(Speech.Take take, int seed) => new()
            {
                SourceEntityId = -1, Label = "speech", Seed = seed,
                Sounds = new List<TransientSound> { new TransientSound
                {
                    Character = SoundCharacter.Hiss, Position = mouth, LevelDb = Speech.LevelDb(Speech.NormalDb),
                    DecaySeconds = take.Seconds, Noisiness = 0.5f, SynthKey = Speech.Key(take.Voice, take.Line),
                } },
            };
            foreach (var take in takes) audio.WorldAudio.Receive(Line(take, 1), AudioClock.Now);
            Pump(2.0);
            double start = clock.Elapsed.TotalSeconds;
            int k = 0; double next = start + 0.2;
            perFrame = t =>
            {
                if (t < next || k >= takes.Count) return;
                audio.WorldAudio.Receive(Line(takes[k], 1), AudioClock.Now);
                next += takes[k].Seconds + 0.4;
                k++;
            };
            Record($"speech normal {d:0.#}m", seconds);
            perFrame = null;
            Pump(1.5);
        }

        void Door(float d, double seconds)
        {
            Stand(new Vector3(30f, 0f, -30f), 0f);
            var at = player.Position + new Vector3(0f, 1.0f, d);
            WorldAudioEvent Ev(bool closing) => new()
            {
                SourceEntityId = -1, Label = "door", Seed = 3,
                Sounds = new List<TransientSound> { new TransientSound
                {
                    Character = SoundCharacter.Knock, Position = at, Hz = 500f, Noisiness = 1f,
                    LevelDb = closing ? KnobDoor.CloseLevelDb(KnobDoor.Shut.Normal) : KnobDoor.OpenLevelDb,
                    DecaySeconds = closing ? 1.6f : 1.85f,
                    SynthKey = KnobDoor.Key(closing, KnobDoor.Construction.HollowCore, 4242, 1.0f, KnobDoor.Shut.Normal, 0.9f, 2.1f, false),
                } },
            };
            audio.WorldAudio.Receive(Ev(false), AudioClock.Now);
            audio.WorldAudio.Receive(Ev(true), AudioClock.Now);
            Pump(4.0);
            double start = clock.Elapsed.TotalSeconds;
            bool opened = false, shut = false;
            perFrame = t =>
            {
                if (!opened && t > start + 0.3) { opened = true; audio.WorldAudio.Receive(Ev(false), AudioClock.Now); }
                if (!shut && t > start + 3.0) { shut = true; audio.WorldAudio.Receive(Ev(true), AudioClock.Now); }
            };
            Record($"door knob open+close {d:0.#}m", seconds);
            perFrame = null;
            Pump(1.5);
        }

        void Steady(string label, string soundId, float d, float height, double seconds, float range)
        {
            Stand(new Vector3(-30f, 0f, -30f), 0f);
            var at = player.Position + new Vector3(0f, height, d);
            int id = AddEmitter(soundId, at, range, 1.2f);
            Pump(4.0);
            Record($"{label} {d:0.#}m", seconds);
            Remove(id);
            Pump(1.5);
        }

        // The wind at the ears is a voice of its own (EarWindVoice) and blows on every outdoor listener;
        // the client's default before the server speaks is a 4.5 m/s breeze. Calm for everything but the
        // wind's own rows, so each scene is that one source alone.
        void Wind(float speed, double seconds)
        {
            Stand(new Vector3(60f, 0f, 60f), 0f);
            WindField.Weather = WindWeather.Steady(speed, 250f, 0.25f);
            Pump(3.0);
            Record($"ear wind {speed:0.#}ms", seconds);
            WindField.Weather = Calm;
            Pump(2.0);
        }
        WindField.Weather = Calm;

        try
        {
            Pump(2.0);
            Record("silence", 2.0);
            var measureSpots = new List<(string, float)>();
            foreach (var side in new[] { "front", "side", "rear" })
                foreach (var d in new[] { 1f, 2f, 5f, 10f })
                    measureSpots.Add((side, d));

            if (set is "measure" or "all")
            {
                foreach (var w in new[] { 2.2f, 4.5f, 7f }) Wind(w, 5.0);
                Footsteps(6.0);
                Speak(2f, 7.0);
                Steady("fountain park", "water:park_fountain", 5f, 0.6f, 5.0, 160f);
                Steady("ac_window", "machine:ac_window", 3f, 1.7f, 5.0, 90f);
                Door(2f, 5.0);
                foreach (var c in cars) IdleCar(c, measureSpots, 3.0);
                foreach (var c in cars)
                    foreach (var kmh in new[] { 30f, 50f })
                        PassBy(c, kmh, 7.5f, 10.0);
                PassBy("i4_economy", 30f, 3f, 10.0);
                PassBy("i4_economy", 50f, 3f, 10.0);
            }
            if (set is "render" or "all")
            {
                foreach (var c in cars)
                {
                    IdleCar(c, new[] { ("front", 2f), ("rear", 2f) }, 12.0);
                    PassBy(c, 10f, 2f, 16.0);
                }
            }
            if (set is "compare")
            {
                Footsteps(6.0);
                Speak(2f, 7.0);
                Steady("fountain park", "water:park_fountain", 5f, 0.6f, 6.0, 160f);
                IdleCar(cars[0], new[] { ("rear", 2f), ("front", 2f) }, 6.0);
            }
            Record("silence end", 1.0);
        }
        finally
        {
            facade.Dispose();
        }

        var sb = new StringBuilder("name,start,seconds\n");
        foreach (var s in segments) sb.Append(CultureInfo.InvariantCulture, $"{s.Name},{s.Start:F3},{s.Seconds:F3}\n");
        File.WriteAllText(Path.Combine(outDir, "segments.csv"), sb.ToString());
        Console.WriteLine($"Wrote {wav} and segments.csv ({segments.Count} segments); compression {Loudness.DynamicRangeCompression:F2}");
        return 0;
    }

    /// <summary>calm=SPEED,TURBULENCE: the air for every scene but the wind's own (default still air).</summary>
    private static WindWeather Calm = WindWeather.Steady(0f, 250f, 0f);

    private static string? Arg(string[] args, string key) => args.FirstOrDefault(x => x.StartsWith(key, StringComparison.Ordinal))?[key.Length..];
}
