using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --probable-bugs scene=pa|landing|bell|yard|rooms [out=DIR] [room=flat|stair]: the sounds the probable bugs
/// of 2026-10-07 change, rendered through the game's own mixer for a before and an after.
///
///   pa       a public-address speaker (a recording of speech) on a 4 m pole, from 10 and 30 m, over
///            asphalt: whether a sustained recording gets the ground reflection.
///   landing  your own jump landings on concrete and on wood, with a few steps on each for scale.
///   bell     the crossing gong, the tram gong and the locomotive bell ringing, 5 m off.
///   yard     the PA in a yard walled in concrete, walked round at 1.4 m/s: the walls' copies of it.
///   rooms    four claps in a carpeted flat or a tiled stairwell (room=), the listener's traced tail
///            and the placed copies, as --clap-room does: whether the room's colour reaches the tail.
///
/// All but rooms are the whole client path (a ClientAudioSystem over the facade over the
/// FmodAudioProvider, as --game-levels): the loudness law at the default /levels, the master and the
/// HRTF. Output: DIR/capture.wav and DIR/segments.csv for tools/game_levels.py. The rooms scene drives
/// the provider directly (there is no map) and writes DIR/capture-ROOM.wav.
/// </summary>
public static class ProbableBugsSpike
{
    private sealed record Segment(string Name, double Start, double Seconds);

    public static int Run(string[] args)
    {
        string scene = Arg(args, "scene=") ?? "pa";
        string outDir = Arg(args, "out=") ?? "/tmp/openfps-probable-bugs";
        Directory.CreateDirectory(outDir);
        AcousticRegistry.Initialize();
        if (scene == "rooms") return Rooms(outDir, Arg(args, "room=") ?? "flat");

        string wav = Path.Combine(outDir, "capture.wav");
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
        WindField.Weather = WindWeather.Steady(0f, 250f, 0f);

        // The ground: asphalt, as a street or a platform is.
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
        void Stand(Vector3 feet, Vector3 facing)
        {
            float yaw = MathF.Atan2(facing.X - feet.X, facing.Z - feet.Z);
            player.Position = feet;
            player.Yaw = yaw;
            player.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        }
        int AddEmitter(string soundId, bool synth, Vector3 at, float range, float minDistance)
        {
            int id = nextId++;
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.StaticObject,
                Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
            };
            def.SoundEmitter = new SoundEmitterComponent();
            def.SoundEmitter.IsSynth = synth;
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

        try
        {
            Record("silence start", 1.0);
            if (scene == "pa")
            {
                // As prefabs/pa_speaker.json: range 400, reference 12 m, a horn on a pole.
                foreach (float d in new[] { 10f, 30f })
                {
                    var pole = new Vector3(0f, 4f, 0f);
                    Stand(new Vector3(0f, 0f, -d), pole);
                    int id = AddEmitter("ANNOUNCE/st_louis_welcome", false, pole, 400f, 12f);
                    Pump(1.0);
                    Record($"pa speech {d:0}m", 12.0);
                    Remove(id);
                    Pump(1.5);
                }
            }
            else if (scene == "landing")
            {
                foreach (var surface in new[] { "Concrete", "Wood" })
                {
                    Stand(new Vector3(20f, 0f, 20f), new Vector3(20f, 0f, 30f));
                    Pump(0.5);
                    double start = clock.Elapsed.TotalSeconds, next = start + 0.3;
                    int steps = 0;
                    perFrame = t =>
                    {
                        if (t < next || steps >= 4) return;
                        next += 0.52; steps++;
                        audio.OnOwnFootstep(player.Position + new Vector3((steps & 1) == 0 ? 0.12f : -0.12f, 0f, 0f), surface, "0");
                    };
                    Record($"steps {surface.ToLowerInvariant()}", 3.0);
                    perFrame = null;
                    Pump(1.0);
                    start = clock.Elapsed.TotalSeconds; next = start + 0.3;
                    int landings = 0;
                    perFrame = t =>
                    {
                        if (t < next || landings >= 4) return;
                        next += 1.5; landings++;
                        audio.OnOwnLand(player.Position, surface, "0");
                    };
                    Record($"landings {surface.ToLowerInvariant()}", 6.5);
                    perFrame = null;
                    Pump(1.5);
                }
            }
            else if (scene == "bell")
            {
                foreach (var bell in new[] { "crossing_gong", "tram_gong", "loco_bell" })
                {
                    var at = new Vector3(0f, 3f, 0f);
                    Stand(new Vector3(0f, 0f, -5f), at);
                    int id = AddEmitter("bell:" + bell, true, at, 400f, 3f);
                    Pump(2.0);
                    Record($"bell {bell} 5m", 8.0);
                    Remove(id);
                    Pump(3.0);
                }
            }
            else if (scene == "yard")
            {
                // A yard walled in concrete, 30 m square, the PA in it, and you walking round it at 1.4 m/s:
                // each wall's copy of the speech, and whether it stays on its wall.
                int wall = 2;
                void Solid(Vector3 centre, Vector3 size) => world.RegisterDefinition(new EntityDefinition
                {
                    EntityId = wall++, Type = EntityType.StaticObject,
                    Transform = new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
                    Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
                    Material = new MaterialComponent { Material = "Concrete" },
                });
                Solid(new Vector3(15.25f, 3f, 0f), new Vector3(0.5f, 6f, 31f));
                Solid(new Vector3(-15.25f, 3f, 0f), new Vector3(0.5f, 6f, 31f));
                Solid(new Vector3(0f, 3f, 15.25f), new Vector3(31f, 6f, 0.5f));
                Solid(new Vector3(0f, 3f, -15.25f), new Vector3(31f, 6f, 0.5f));
                var speaker = new Vector3(3f, 2f, 4f);
                const float Radius = 11f, Speed = 1.4f;
                double t0 = clock.Elapsed.TotalSeconds;
                Vector3 At(double t) { float a = (float)((t - t0) * Speed / Radius); return new Vector3(Radius * MathF.Cos(a), 0f, Radius * MathF.Sin(a)); }
                Stand(At(t0), speaker);
                int id = AddEmitter("ANNOUNCE/st_louis_welcome", false, speaker, 400f, 12f);
                // The walls' copies play under ClientAudioSystem.ReflectionVoiceId's ids: say how many, and
                // count the times one of them moved more than a metre while it played (handed to another wall).
                var lastAt = new Dictionary<int, Vector3>();
                int jumps = 0; double nextSay = t0 + 5.0;
                perFrame = t =>
                {
                    Stand(At(t), speaker);
                    int playing = 0;
                    for (int slot = 0; slot < EarlyReflections.MaxArrivals; slot++)
                    {
                        int voice = -30000 - id * (EarlyReflections.MaxArrivals + 1) - slot;
                        if (!facade.IsPlaying(voice)) { lastAt.Remove(voice); continue; }
                        playing++;
                        var p = provider.GetSoundPosition(voice);
                        if (lastAt.TryGetValue(voice, out var was) && Vector3.Distance(was, p) > 1f) jumps++;
                        lastAt[voice] = p;
                    }
                    if (t >= nextSay) { nextSay += 5.0; Console.WriteLine($"    {t - t0,5:F1} s: {playing} wall copies playing, {jumps} jump(s) so far"); }
                };
                Pump(2.0);
                Record("yard walk round the pa", 40.0);
                perFrame = null;
                Remove(id);
                Pump(1.5);
            }
            else { Console.WriteLine($"No scene '{scene}'."); return 1; }
            Record("silence end", 1.0);
        }
        finally
        {
            facade.Dispose();
        }

        var sb = new StringBuilder("name,start,seconds\n");
        foreach (var s in segments) sb.Append(CultureInfo.InvariantCulture, $"{s.Name},{s.Start:F3},{s.Seconds:F3}\n");
        File.WriteAllText(Path.Combine(outDir, "segments.csv"), sb.ToString());
        Console.WriteLine($"Wrote {wav} and segments.csv ({segments.Count} segments); /levels {Loudness.DynamicRangeCompression:F2}");
        return 0;
    }

    // ── Rooms ──────────────────────────────────────────────────────────────────────────────────────

    private const int RoomId = 87;

    /// <summary>
    /// Four claps, each half a metre from the ear, in a room traced as the game traces the listener's:
    /// the flat of --clap-room (carpet on concrete, plaster) or a tiled stairwell (3 x 6 m, three
    /// storeys, tile floor, concrete walls and ceiling). The claps are placed by the loudness law and
    /// get the copies the game plans for them.
    /// </summary>
    private static int Rooms(string outDir, string room)
    {
        var q = Quaternion.Identity;
        List<SteamAudioScene.Box> boxes;
        Vector3 ear;
        RegionComponent region;
        int I(string m) => AcousticRegistry.GetProperties(m).ResonanceIndex;
        if (room == "stair")
        {
            const float W = 3f, D = 6f, H = 9f;
            boxes = new List<SteamAudioScene.Box>
            {
                new(new Vector3(0, -0.1f, 0), new Vector3(W + 0.4f, 0.2f, D + 0.4f), q, "Tile"),
                new(new Vector3(0, H + 0.1f, 0), new Vector3(W + 0.4f, 0.2f, D + 0.4f), q, "Concrete"),
                new(new Vector3(-W / 2 - 0.1f, H / 2, 0), new Vector3(0.2f, H, D), q, "Concrete"),
                new(new Vector3(W / 2 + 0.1f, H / 2, 0), new Vector3(0.2f, H, D), q, "Concrete"),
                new(new Vector3(0, H / 2, -D / 2 - 0.1f), new Vector3(W, H, 0.2f), q, "Concrete"),
                new(new Vector3(0, H / 2, D / 2 + 0.1f), new Vector3(W, H, 0.2f), q, "Concrete"),
                // The flights: two concrete slabs with tiled treads, half way up and at the first landing.
                new(new Vector3(-0.75f, 1.5f, 0.5f), new Vector3(1.4f, 0.2f, 3.5f), q, "Tile"),
                new(new Vector3(0.75f, 4.5f, -0.5f), new Vector3(1.4f, 0.2f, 3.5f), q, "Tile"),
            };
            ear = new Vector3(0.6f, 1.7f, -2f);
            region = new RegionComponent
            {
                FriendlyName = "Tiled stairwell", IsIndoor = true, RoomSize = new Vector3(W, H, D), ReverbTimeScale = 1f,
                Materials = new[] { I("Concrete"), I("Concrete"), I("Tile"), I("Concrete"), I("Concrete"), I("Concrete") },
            };
        }
        else
        {
            boxes = new List<SteamAudioScene.Box>
            {
                new(new Vector3(0, 0.04f, 0), new Vector3(8.65f, 0.04f, 17.86f), q, "Carpet"),
                new(new Vector3(0, -0.07f, 0), new Vector3(21f, 0.17f, 90f), q, "Concrete"),
                new(new Vector3(0, 2.735f, 0), new Vector3(20.5f, 0.03f, 89.3f), q, "Plaster"),
                new(new Vector3(4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, 80f), q, "Brick"),
                new(new Vector3(-4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, 17.86f), q, "Plaster"),
                new(new Vector3(0, 1.4f, -9.0f), new Vector3(8.65f, 2.73f, 0.2f), q, "Plaster"),
                new(new Vector3(0, 1.4f, 9.0f), new Vector3(8.65f, 2.73f, 0.2f), q, "Plaster"),
                new(new Vector3(3.2f, 0.32f, -3.4f), new Vector3(1.8f, 0.6f, 1.8f), q, "Audience"),
            };
            ear = new Vector3(0.175f, 1.7f, 0.16f);
            region = new RegionComponent
            {
                FriendlyName = "Carpeted flat", IsIndoor = true, RoomSize = new Vector3(8.65f, 2.7f, 17.86f), ReverbTimeScale = 1f,
                Materials = new[] { I("Plaster"), I("Brick"), I("Carpet"), I("Plaster"), I("Plaster"), I("Plaster") },
            };
        }
        var hands = ear + new Vector3(0f, -0.39f, 0.3f);

        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no Steam Audio context"); return 1; }
        var saScene = new SteamAudioScene(ctx);
        saScene.Build(boxes);
        TracedReverbSet.Configure(ctx, saScene);
        TracedReverbSet.SetListener(ear);

        string wav = Path.Combine(outDir, $"capture-{room}.wav");
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", wav);
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("FAIL: provider init failed."); return 1; }
        var placed = Loudness.Place(92f);
        try
        {
            var map = new AcousticMap(new Vector3(100, 40, 100), new Vector3(-50, 0, -50)) { GlobalEnvironmentId = AcousticConstants.GlobalRegionId };
            map.Regions[RoomId] = region;
            map.RegionPositions[RoomId] = new Vector3(0f, region.RoomSize.Y / 2f, 0f);
            map.RegionRotations[RoomId] = Quaternion.Identity;
            provider.SetAcousticMap(map);
            float[] pcm = Applause.RenderClap(TransientSynth.SampleRate, 1);
            provider.RegisterSynthesisedSound("synth:clap:lab", TransientSynth.ToPcm16(pcm), TransientSynth.SampleRate);

            // The copies the game places round a clap, as --clap-room places them.
            var solids = boxes.Select(b => new EarlyReflections.Solid(b.Center, b.Size, b.Rotation, b.Material)).ToList();
            var found = new List<EarlyReflections.Arrival>();
            var plan = new List<WorldAudioPlayer.RoomEcho>();
            float c0 = AudioPhysics.CurrentSpeedOfSound;
            EarlyReflections.Find(hands, ear, solids, found, c0, maxOrder: 2, keep: WorldAudioPlayer.MaxRoomEchoes * 2,
                                  maxExtraPathMetres: WorldAudioPlayer.RoomEchoWindowSeconds * c0);
            float direct0 = Vector3.Distance(hands, ear);
            WorldAudioPlayer.PlanRoomEchoes(found, hands, ear, direct0, placed.ReferenceDistance, FmodAudioProvider.CopiesTrim, audible: true, plan);
            var washIds = new HashSet<string>();
            foreach (var e in plan)
                if (!e.InVoice && e.WashGain >= ImageSource.MinGain)
                {
                    int step = (int)MathF.Round(e.Scattering * 4f);
                    string id = $"synth:clap:lab~wash{step}";
                    if (washIds.Add(id))
                        provider.RegisterSynthesisedSound(id, TransientSynth.ToPcm16(WorldAudioPlayer.Diffuse(pcm, step / 4f, 1)), TransientSynth.SampleRate);
                }
            int voice = -1000;
            void Copy(string id, Vector3 at, float gain, float lowDb, float highDb)
            {
                float dist = Vector3.Distance(ear, at);
                provider.PlaySpatialSound(new SpatialEmitter
                {
                    EntityId = voice--, SoundId = id, Mode = PlaybackMode.Single, Type = EmitterType.WorldLocked,
                    Position = at, ApparentPosition = at, EffectiveDistance = dist,
                    Volume = placed.Gain * gain, MinDistance = placed.ReferenceDistance, Range = 200f, Pitch = 1f,
                    ConeInside = 360f, ConeOutside = 360f, ConeOutsideVolume = 1f,
                    EqLow = MathF.Pow(10f, lowDb / 20f), EqMid = 1f, EqHigh = MathF.Pow(10f, highDb / 20f), ApertureFactor = 1f,
                    IsEvent = true, Essential = true, TargetRegionId = RoomId, LevelDb = 0f,
                    DelayMs = dist / AudioPhysics.CurrentSpeedOfSound * 1000f,
                });
            }
            void PlayCopies()
            {
                foreach (var e in plan)
                {
                    if (e.InVoice) continue;
                    var a = e.Arrival;
                    float lowDb = 20f * MathF.Log10(MathF.Max(1e-4f, a.GainLow) / MathF.Max(1e-4f, a.GainMid));
                    float highDb = 20f * MathF.Log10(MathF.Max(1e-4f, a.GainHigh) / MathF.Max(1e-4f, a.GainMid));
                    var loss = WorldAudioPlayer.SpecularLoss(a.Scattering, a.Order);
                    if (e.WashGain >= ImageSource.MinGain)
                        Copy($"synth:clap:lab~wash{(int)MathF.Round(e.Scattering * 4f)}", a.ImagePosition, e.WashGain, lowDb, highDb);
                    if (e.MirrorGain >= ImageSource.MinGain)
                        Copy("synth:clap:lab", a.ImagePosition, e.MirrorGain, loss.LowDb + lowDb, loss.HighDb + highDb);
                }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            double next = 3.0;
            int played = 0;
            while (sw.Elapsed.TotalSeconds < 3.0 + 4 * 2.0 + 1.0)
            {
                provider.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, RoomId);
                if (played < 4 && sw.Elapsed.TotalSeconds >= next)
                {
                    next += 2.0;
                    provider.PlaySpatialSound(new SpatialEmitter
                    {
                        EntityId = -100 - played++, SoundId = "synth:clap:lab",
                        Mode = PlaybackMode.Single, Type = EmitterType.WorldLocked,
                        Position = hands, ApparentPosition = hands,
                        Volume = placed.Gain, Range = 200f, MinDistance = placed.ReferenceDistance, Pitch = 1f,
                        ConeInside = 360f, ConeOutside = 360f, ConeOutsideVolume = 1f,
                        EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
                        IsEvent = true, Essential = true, TargetRegionId = RoomId,
                        LevelDb = 92f, EffectiveDistance = direct0,
                        DelayMs = direct0 / AudioPhysics.CurrentSpeedOfSound * 1000f,
                    });
                    PlayCopies();
                }
                provider.Update();
                Thread.Sleep(10);
            }
            Console.WriteLine($"  {region.FriendlyName}: listener trace {(TracedReverbSet.Listener != null ? "ready" : "MISSING")}, "
                            + $"tail {FmodAudioProvider.TailDb:F0} dB, copies {FmodAudioProvider.CopiesDb:F0} dB, {plan.Count} planned copies");
        }
        finally { provider.Dispose(); TracedReverbSet.Dispose(); saScene.Dispose(); Phonon.iplContextRelease(ref ctx); }
        Console.WriteLine($"Wrote {wav}");
        return 0;
    }

    private static string? Arg(string[] args, string key) => args.FirstOrDefault(x => x.StartsWith(key, StringComparison.Ordinal))?[key.Length..];
}
