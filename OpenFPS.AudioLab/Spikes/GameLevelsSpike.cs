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
/// --game-levels [out=DIR] [set=measure|render|compare|all|faults] [cars=a,b,..]: what the game's own mixer puts out
/// for one thing at a time, captured from the real output. set=faults is the Resonance faults of 2026-10-06:
/// a light single and an airliner landing, a train blowing for a crossing, two window units side by side.
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
/// DIR/segments.csv beside DIR/capture.wav (stereo, the mixer's rate, the game's full scale). tools/game_levels.py
/// cuts and measures them.
///
/// Car frame: the car faces +Z, its origin on the ground at its middle. The listener's ear is 1.7 m up.
/// </summary>
public static class GameLevelsSpike
{
    private sealed record Segment(string Name, double Start, double Seconds);

    public static int Run(string[] args)
    {
        if (args.Contains("spectra"))
            return Spectra(args.Where(a => !a.StartsWith("--") && a != "spectra" && !a.Contains('=')).ToArray());
        string outDir = Arg(args, "out=") ?? "/tmp/openfps-game-levels";
        string set = Arg(args, "set=") ?? "measure";
        string[] cars = (Arg(args, "cars=") ?? "i4_economy,i4_midsize,police_interceptor").Split(',', StringSplitOptions.RemoveEmptyEntries);
        Directory.CreateDirectory(outDir);
        if (Arg(args, "calm=") is { } calm)
        {
            var c = calm.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            Calm = WindWeather.Steady(c[0], 250f, c.Length > 1 ? c[1] : 0f);
        }
        // The ear model (docs/EAR_MODEL.md): ear=off is the game before it, for a before/after pair.
        if (Arg(args, "ear=") is { } ear) OpenFPS.Common.Hearing.EarModel.Enabled = ear is not ("off" or "0");
        if (Arg(args, "listening=") is { } listening)
            OpenFPS.Common.Hearing.EarModel.ListeningLevelDb = float.Parse(listening, CultureInfo.InvariantCulture);
        Console.WriteLine($"Ear model {(OpenFPS.Common.Hearing.EarModel.Enabled ? "on" : "off")}, listening level "
                        + $"{OpenFPS.Common.Hearing.EarModel.ListeningLevelDb:F2} dB, /levels {Loudness.DynamicRangeCompression:F2}");
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

        // Thunder from a ground strike d metres off, as the server sends it: one event whose key rebuilds
        // the channel. Recorded from the flash, so the record runs past the sound's travel time.
        void ThunderAt(float d, double seconds)
        {
            Stand(new Vector3(-60f, 0f, 60f), 0f);
            var strike = ThunderSpike.Ground(d, 40f, 7, 3);
            audio.WorldAudio.Receive(new WorldAudioEvent
            {
                SourceEntityId = -1, Label = "thunder", Seed = 7,
                Sounds = new List<TransientSound> { new TransientSound { SynthKey = strike.Key(), Position = player.Position } },
            }, AudioClock.Now);
            Record($"thunder {d / 1000f:0.#}km", seconds);
            Pump(2.0);
        }

        // Rain at a rate, on the asphalt round you, as the server's weather would set it.
        void RainAt(float mmPerHour, string label, double seconds)
        {
            Stand(new Vector3(0f, 0f, -80f), 0f);
            world.UpdateAtmosphere(new WorldStateUpdate
            {
                Temperature = 15f, Humidity = 0.8f, AirPressure = 101325f, AirAbsorptionMultiplier = 1f,
                PrecipitationIntensity = 0.5f, RainRateMmPerHour = mmPerHour,
            });
            Pump(4.0);
            Record($"rain {label}", seconds);
            world.UpdateAtmosphere(new WorldStateUpdate { Temperature = 15f, Humidity = 0.6f, AirPressure = 101325f, AirAbsorptionMultiplier = 1f });
            Pump(3.0);
        }

        // ── The Resonance faults (inbox/fault-fixes-2026-10-06) ───────────────────────────────────
        //
        // An aeroplane arriving on a runway 40 m in front of you, flown as the server flies one: down a
        // three-degree slope at its approach speed, the wheels on the ground the instant the path
        // reaches it (ClientAudioSystem.OnTheWheels), then the rollout slowing at a quarter of a g.
        void Landing(string preset, float offset, double approach, double rollout)
        {
            var p = AircraftProfile.ByName(preset);
            float vApp = p.ApproachSpeedMps, slope = MathF.Tan(3f * MathF.PI / 180f), wheels = 1.0f;
            Stand(new Vector3(0f, 0f, -offset), 0f);                // facing the runway (+Z), which runs along X
            double tTouch = clock.Elapsed.TotalSeconds + 1.5 + approach;
            var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
            (Vector3 At, Vector3 Vel) Path(double t)
            {
                float before = (float)(t - tTouch);
                if (before < 0f)
                    return (new Vector3(before * vApp, wheels - before * vApp * slope, 0f), new Vector3(vApp, -vApp * slope, 0f));
                float v = MathF.Max(6f, vApp - 2.5f * before);
                float x = before < (vApp - 6f) / 2.5f ? vApp * before - 1.25f * before * before
                        : (vApp * vApp - 36f) / 5f + 6f * (before - (vApp - 6f) / 2.5f);
                return (new Vector3(x, wheels, 0f), new Vector3(v, 0f, 0f));
            }
            int id = nextId++;
            var (at0, vel0) = Path(clock.Elapsed.TotalSeconds);
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.NPC, Moves = true,
                Transform = new Transform { Position = at0, Rotation = rot },
            };
            def.SoundEmitter = new SoundEmitterComponent();
            def.SoundEmitter.IsSynth = true;
            def.SoundEmitter.SoundId = "aircraft:" + preset;
            def.SoundEmitter.Mode = PlaybackMode.LoopOne;
            def.SoundEmitter.Volume = 1f;
            def.SoundEmitter.Range = Loudness.AudibleRange(p.SourceLevelDb);
            def.SoundEmitter.MinDistance = 3f;
            world.RegisterDefinition(def);
            Move(id, at0, rot, vel0);
            perFrame = t => { var (a, v) = Path(t); Move(id, a, rot, v); };
            Pump(1.5);
            Console.WriteLine($"    {preset}: approach {vApp:F0} m/s, spin-up {p.Gear?.SpinUpSeconds(vApp) * 1000f:F0} ms at touchdown");
            Record($"landing {preset} {offset:0}m", approach + rollout);
            perFrame = null;
            Remove(id);
            Pump(1.5);
        }

        // A train blowing for a level crossing you are standing beside, `lateral` metres from the
        // track. Its sources are placed as RailSystem places them (TrainLayout, head minus along) and
        // the crossing is sounded as RailSystem.SoundForCrossings sounds it, eighteen seconds out.
        void TrainCrossing(string preset, float speed, float lateral, double seconds)
        {
            var profile = TrainProfile.ByName(preset);
            var layout = TrainLayout.Sources(profile);
            const string trainKey = "fault_train";
            const float hornLead = 18f;
            Stand(new Vector3(0f, 0f, -lateral), 0f);               // the track runs along X, the crossing at x = 0
            double start = clock.Elapsed.TotalSeconds + 1.5;
            float headAtStart = -speed * (hornLead + 1.5f);          // the horn starts 1.5 s into the recording
            var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
            var vel = new Vector3(speed, 0f, 0f);
            float Head(double t) => headAtStart + speed * (float)(t - start);
            var ids = new int?[layout.Count];
            for (int i = 0; i < layout.Count; i++)
            {
                var src = layout[i];
                int id = nextId++;
                ids[i] = id;
                var at = new Vector3(Head(clock.Elapsed.TotalSeconds) - src.AlongMetres, src.HeightMetres, 0f);
                var def = new EntityDefinition
                {
                    EntityId = id, Type = EntityType.NPC, Moves = true,
                    Transform = new Transform { Position = at, Rotation = rot },
                };
                def.SoundEmitter = new SoundEmitterComponent();
                def.SoundEmitter.IsSynth = true;
                def.SoundEmitter.SoundId = $"rail:{preset}/{trainKey}/{src.Index}";
                def.SoundEmitter.Mode = PlaybackMode.LoopOne;
                def.SoundEmitter.Volume = 1f;
                def.SoundEmitter.Range = Loudness.AudibleRange(src.LevelDb);
                def.SoundEmitter.MinDistance = 3f;
                world.RegisterDefinition(def);
                Move(id, at, rot, vel);
            }
            bool sounded = false;
            perFrame = t =>
            {
                float head = Head(t);
                for (int i = 0; i < layout.Count; i++)
                    if (ids[i] is int id) Move(id, new Vector3(head - layout[i].AlongMetres, layout[i].HeightMetres, 0f), rot, vel);
                float eta = -head / speed;
                if (!sounded && eta <= hornLead)
                {
                    sounded = true;
                    SoundForCrossing(preset, trainKey, layout, ids, eta);
                }
            };
            Pump(1.5);
            Record($"train {preset} crossing {lateral:0}m", seconds);
            perFrame = null;
            foreach (var id in ids) if (id is int i) Remove(i);
            Pump(1.5);
        }
        // What RailSystem.SoundForCrossings sends: the train's own horn (or whistle) in the crossing
        // rhythm and its bell until it is on the crossing (TrainSignal), from its warning source.
        // (Before 2026-10-06 it sent the road vehicle's horn, Honk, on the train's first entity, and
        // the signal sources were not spawned.)
        void SoundForCrossing(string preset, string trainKey, IReadOnlyList<TrainLayout.Entry> layout, int?[] ids, float eta)
        {
            int warn = TrainSignal.WarningSource(layout), bellAt = TrainSignal.BellSource(layout);
            if (warn < 0 && bellAt < 0) return;
            var (warning, bell) = TrainSignal.ForCrossing(eta);
            if (warn < 0) warning = Array.Empty<float>();
            if (bellAt < 0) bell = 0f;
            int from = warn >= 0 ? warn : bellAt;
            audio.WorldAudio.Receive(new WorldAudioEvent
            {
                SourceEntityId = ids[from]!.Value, Label = "horn", Seed = 1,
                Sounds = new List<TransientSound> { new TransientSound
                {
                    Character = SoundCharacter.Ring, Position = Vector3.Zero, LevelDb = layout[from].LevelDb,
                    DecaySeconds = TrainSignal.Duration(warning, bell), SynthKey = TrainSignal.Key(preset, trainKey, warning, bell),
                } },
            }, AudioClock.Now);
        }

        // Two of the same machine side by side, 1.8 m apart, 3 m in front of you, on a warm day.
        void TwoMachines(string preset, float celsius, double seconds)
        {
            Stand(new Vector3(-60f, 0f, -60f), 0f);
            world.UpdateAtmosphere(new WorldStateUpdate { Temperature = celsius, Humidity = 0.6f, AirPressure = 101325f, AirAbsorptionMultiplier = 1f });
            var spec = SmallMachineSpec.ByName(preset);
            int a = AddEmitter("machine:" + preset, player.Position + new Vector3(-0.9f, 1.7f, 3f), Loudness.AudibleRange(spec.SourceLevelDb), 1.2f);
            int b = AddEmitter("machine:" + preset, player.Position + new Vector3(0.9f, 1.7f, 3f), Loudness.AudibleRange(spec.SourceLevelDb), 1.2f);
            Pump(4.0);
            Record($"two {preset} {celsius:0}C", seconds);
            Remove(a); Remove(b);
            world.UpdateAtmosphere(new WorldStateUpdate { Temperature = 15f, Humidity = 0.6f, AirPressure = 101325f, AirAbsorptionMultiplier = 1f });
            Pump(1.5);
        }

        try
        {
            Pump(2.0);
            Record("silence", 2.0);
            if (set is "faults")
            {
                Landing("piston_single", 40f, 7.0, 9.0);
                Landing("airliner", 60f, 7.0, 9.0);
                TrainCrossing("amtrak", 25f, 15f, 30.0);
                TwoMachines("ac_window", 30f, 20.0);
            }
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
            if (set is "ear")
            {
                // The sources whose balance Cody approved by ear, for the ear model's before/after
                // (docs/EAR_MODEL.md): run once with ear=off and once with ear=on.
                Speak(2f, 7.0);
                Footsteps(6.0);
                Steady("fountain park", "water:park_fountain", 5f, 0.6f, 6.0, 160f);
                Steady("ac_window", "machine:ac_window", 3f, 1.7f, 6.0, 90f);
                Door(2f, 5.0);
                foreach (var c in cars) IdleCar(c, new[] { ("front", 2f), ("rear", 2f) }, 6.0);
                foreach (var c in cars) PassBy(c, 30f, 7.5f, 10.0);
                ThunderAt(3000f, 24.0);
                RainAt(Rainfall.ModerateRate, "moderate", 8.0);
                Wind(4.5f, 6.0);
            }
            if (set is "wind")
                foreach (var w in new[] { 2.2f, 4.5f, 7f }) Wind(w, 14.0);
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

    /// <summary>
    /// --game-levels spectra [preset ...]: how much of an engine's sound is low bass, idling and at
    /// full load. The voice alone in pascals (no lift, no law), heard 7.5 m to the side: unweighted
    /// and A-weighted, the idle (last 3 s of 8) and the loudest second of a pull from rest with the
    /// throttle open. The idle lift and the loudness law both work on the unweighted level; this is
    /// what that leaves out.
    /// </summary>
    public static int Spectra(string[] names)
    {
        const int rate = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.DefaultRate, block = 512;   // the rate the game runs it at
        if (names.Length == 0) names = new[] { "i4_economy", "i4_midsize", "police_interceptor", "v8_mild", "diesel_i4" };
        Console.WriteLine($"{"preset",-20} {"declared",8} {"idle Z",7} {"idle A",7} {"Z-A",5} {"load Z",7} {"load A",7} {"Z-A",5}");
        foreach (var n in names)
        {
            var v = MachineRegistry.VehicleFor(n);
            var voice = new EngineVoiceState(v, rate, 11) { TargetSpeed = 0f, CompensateLevel = false };
            voice.PlaceAtSpeed(0f);
            voice.Revive();
            voice.SetListener(new Vector3(7.5f, 1.2f, 0f) - v.ExhaustSlot);
            var aw = new AWeight(rate);
            var buf = new float[block];
            var z = new List<double>(); var a = new List<double>();
            double ez = 0, ea = 0; int cnt = 0;
            for (int b = 0; b < rate * 26 / block; b++)
            {
                if (b == rate * 8 / block) voice.TargetSpeed = 40f;
                voice.Produce(); voice.Consume(buf);
                foreach (var y in buf)
                {
                    double p = y * voice.PascalsAtFullScale;
                    ez += p * p; double q = aw.Step(p); ea += q * q; cnt++;
                    if (cnt == rate) { z.Add(10 * Math.Log10(ez / cnt / 4e-10)); a.Add(10 * Math.Log10(ea / cnt / 4e-10)); ez = ea = 0; cnt = 0; }
                }
            }
            double Pow(IEnumerable<double> d) => 10 * Math.Log10(d.Average(x => Math.Pow(10, x / 10)));
            double idleZ = Pow(z.Skip(5).Take(3)), idleA = Pow(a.Skip(5).Take(3));
            int loud = Enumerable.Range(8, z.Count - 8).OrderByDescending(i => z[i]).First();
            Console.WriteLine($"{n,-20} {v.SourceLevelDb,8:F1} {idleZ,7:F1} {idleA,7:F1} {idleZ - idleA,5:F1} {z[loud],7:F1} {a[loud],7:F1} {z[loud] - a[loud],5:F1}");
        }
        Console.WriteLine("dB SPL at 7.5 m to the side; 'load' is the loudest second of a pull from rest.");
        return 0;
    }

    /// <summary>IEC 61672 A-weighting as four cascaded sections (bilinear, prewarped poles), at 44.1 or 48 kHz.</summary>
    private sealed class AWeight
    {
        private readonly (double b0, double b1, double b2, double a1, double a2)[] _s;
        private readonly double[] _z1, _z2;
        private readonly double _gain;
        public AWeight(int rate)
        {
            double T = 1.0 / rate;
            double W(double f) => 2.0 / T * Math.Tan(Math.PI * f * T);
            double f1 = W(20.598997), f2 = W(107.65265), f3 = W(737.86223), f4 = W(12194.217);
            // s^4 / ((s+f1)^2 (s+f2)(s+f3)(s+f4)^2): two high-passes at f1, a high-pass pair at f2,f3,
            // two low-passes at f4; each first-order section by the bilinear transform.
            var list = new List<(double, double, double, double, double)>();
            void HighPass(double w) { double k = 2.0 / T; double a0 = k + w; list.Add((k / a0, -k / a0, 0, (w - k) / a0, 0)); }
            void LowPass(double w) { double k = 2.0 / T; double a0 = k + w; list.Add((w / a0, w / a0, 0, (w - k) / a0, 0)); }
            HighPass(f1); HighPass(f1); HighPass(f2); HighPass(f3); LowPass(f4); LowPass(f4);
            _s = list.ToArray(); _z1 = new double[_s.Length]; _z2 = new double[_s.Length];
            // Normalise to 0 dB at 1 kHz.
            double g = 1;
            foreach (var (b0, b1, _, a1, _) in _s)
            {
                var zz = System.Numerics.Complex.FromPolarCoordinates(1, -2 * Math.PI * 1000.0 / rate);
                g *= ((b0 + b1 * zz) / (1 + a1 * zz)).Magnitude;
            }
            _gain = 1 / g;
        }
        public double Step(double x)
        {
            x *= _gain;
            for (int i = 0; i < _s.Length; i++)
            {
                var (b0, b1, _, a1, _) = _s[i];
                double y = b0 * x + _z1[i];
                _z1[i] = b1 * x - a1 * y;
                x = y;
            }
            return x;
        }
    }

    /// <summary>calm=SPEED,TURBULENCE: the air for every scene but the wind's own (default still air).</summary>
    private static WindWeather Calm = WindWeather.Steady(0f, 250f, 0f);

    private static string? Arg(string[] args, string key) => args.FirstOrDefault(x => x.StartsWith(key, StringComparison.Ordinal))?[key.Length..];
}
