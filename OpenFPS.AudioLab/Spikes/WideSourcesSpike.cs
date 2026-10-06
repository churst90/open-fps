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
/// --wide-sources [out=DIR] [set=measure|render|all] [wide=on|off]: a tree, the Elm Park fountain, the
/// fire pit and street rain through the game's own client path with the HRTF, captured from the master
/// in float, for how alike the two ears are (tools/interaural.py) and for listening pairs.
///
/// The path is GameLevelsSpike's: a ClientAudioSystem over the AudioEngineFacade over the
/// FmodAudioProvider, the sources as map entities exactly as the server sends them (the fountain as its
/// five map taps at their places on the city map), the ear model on. No map, so no walls, reflections or
/// reverb; a flat asphalt ground. The wind at the ears is switched off, because it is noise of its own at
/// each ear and would be measured as the source's width; the tree's crown still has its breeze.
///
/// wide=off plays every extended source from one point (ExtendedSources.Enabled = false): the game before
/// this change. Each scene's name and start go to DIR/segments.csv beside DIR/capture.post.wav (float,
/// after the master limiter, the game's full scale), with the process's CPU and the mixer's load over it.
/// </summary>
public static class WideSourcesSpike
{
    private sealed record Segment(string Name, double Start, double Seconds, double CpuPercent, float MixerLoad, int Voices);

    // Where the sources stand, relative to their own middle, as on the city map (tools/gen_city.py).
    private static readonly Vector3 FountainCentre = new(0f, 0f, 0f);
    private static readonly (int Tap, Vector3 At)[] FountainTaps =
    {
        (0, new Vector3(0f, 1.9f, 0f)),
        (1, new Vector3(0f, 0.8f, 2.3f)),
        (2, new Vector3(2.3f, 0.8f, 0f)),
        (3, new Vector3(0f, 0.8f, -2.3f)),
        (4, new Vector3(-2.3f, 0.8f, 0f)),
    };
    private const float KerbMetres = 5.5f;

    public static int Run(string[] args)
    {
        string outDir = Arg(args, "out=") ?? "/tmp/openfps-wide-sources";
        string set = Arg(args, "set=") ?? "measure";
        bool wide = (Arg(args, "wide=") ?? "on") is not ("off" or "0");
        float renderSeconds = float.Parse(Arg(args, "sec=") ?? "30", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(outDir);
        ExtendedSources.Enabled = wide;
        Console.WriteLine($"Extended sources {(wide ? "spread" : "one point")}; ear model {(OpenFPS.Common.Hearing.EarModel.Enabled ? "on" : "off")}");

        AcousticRegistry.Initialize();
        Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", Path.Combine(outDir, "capture.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(outDir, "capture.post.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        var provider = new FmodAudioProvider();
        var facade = new AudioEngineFacade(provider);
        string sounds = LabPaths.Sounds();
        facade.InitializeForTest(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        provider.EarWindEnabled = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var proc = System.Diagnostics.Process.GetCurrentProcess();

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
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(600f, 1f, 600f), IsSolid = true },
            Material = new MaterialComponent { Material = "Asphalt" },
        });

        var segments = new List<Segment>();
        Action<double>? perFrame = null;
        int nextId = 100;
        double loadSum = 0; int loadCount = 0;

        void Pump(double seconds)
        {
            var until = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < until)
            {
                perFrame?.Invoke(clock.Elapsed.TotalSeconds);
                audio.Update(world.GetSnapshot());
                facade.PumpForTest();
                loadSum += facade.MixerLoad; loadCount++;
                Thread.Sleep(4);
            }
        }
        void Record(string name, double seconds)
        {
            double start = clock.Elapsed.TotalSeconds;
            proc.Refresh();
            var cpu0 = proc.TotalProcessorTime;
            loadSum = 0; loadCount = 0;
            int voicesBefore = provider.SpatialVoicesFree;
            Pump(seconds);
            proc.Refresh();
            double cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds / seconds * 100.0;
            int used = 160 - provider.SpatialVoicesFree;
            segments.Add(new Segment(name, start, seconds, cpu, loadCount > 0 ? (float)(loadSum / loadCount) : 0f, used));
            Console.WriteLine($"  {start,7:F2} s  {name} ({seconds:F1} s)  cpu {cpu:F0} %  mixer {(loadCount > 0 ? loadSum / loadCount : 0):P1}  HRTF voices in use {used}");
        }
        void Stand(Vector3 feet, float yaw)
        {
            player.Position = feet;
            player.Yaw = yaw;
            player.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        }
        float YawTo(Vector3 from, Vector3 to) => MathF.Atan2(to.X - from.X, to.Z - from.Z);
        int AddEmitter(string soundId, Vector3 at, float range, float minDistance)
        {
            int id = nextId++;
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.StaticObject,
                Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
            };
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
        void Remove(IEnumerable<int> ids)
        {
            var list = ids.ToArray();
            world.RemoveEntities(list);
            foreach (int id in list) audio.ForgetEntity(id);
        }

        var calm = WindWeather.Steady(0f, 250f, 0f);
        var breeze = WindWeather.Steady(4.5f, 250f, 0.25f);
        WindField.Weather = calm;

        // A park tree: its emitter at the middle of its crown, as the map places it (7 m up). d is from
        // the trunk, along the ground.
        void Tree(IEnumerable<float> distances, double seconds, string label = "tree")
        {
            Vector3 trunk = new(-60f, 0f, -60f);
            WindField.Weather = breeze;
            Stand(trunk + new Vector3(0f, 0f, -distances.First()), 0f);
            int id = AddEmitter("foliage:park_tree", trunk + new Vector3(0f, 7f, 0f), 90f, 4f);
            Pump(4.0);
            foreach (float d in distances)
            {
                var feet = trunk + new Vector3(0f, 0f, -d);
                Stand(feet, YawTo(feet, trunk));
                Pump(2.5);
                Record($"{label} {d:0.#}m", seconds);
            }
            Remove(new[] { id });
            WindField.Weather = calm;
            Pump(1.5);
        }

        // The Elm Park fountain, its five taps where the map has them. d is from the kerb (the basin is
        // 11 m square), standing south of it facing north.
        int[] AddFountain(Vector3 centre)
            => FountainTaps.Select(t => AddEmitter($"water:park_fountain/elm_park/{t.Tap}", centre + t.At, 160f, 1.5f)).ToArray();
        void Fountain(IEnumerable<float> distances, double seconds, string label = "fountain")
        {
            Vector3 centre = new(60f, 0f, -60f);
            WindField.Weather = WindWeather.Steady(2f, 250f, 0.2f);
            Stand(centre + new Vector3(0f, 0f, -KerbMetres - distances.First()), 0f);
            var ids = AddFountain(centre);
            Pump(4.0);
            foreach (float d in distances)
            {
                var feet = centre + new Vector3(0f, 0f, -KerbMetres - d);
                Stand(feet, YawTo(feet, centre));
                Pump(2.5);
                Record($"{label} {d:0.#}m", seconds);
            }
            Remove(ids);
            WindField.Weather = calm;
            Pump(1.5);
        }

        // Walking past the fountain at a slow walk, 4 m off its kerb, facing the way you walk.
        void FountainWalk(float halfLength, float speed)
        {
            Vector3 centre = new(60f, 0f, 60f);
            WindField.Weather = WindWeather.Steady(2f, 250f, 0.2f);
            float z = -KerbMetres - 4f;
            Stand(centre + new Vector3(-halfLength, 0f, z), MathF.PI / 2f);
            var ids = AddFountain(centre);
            Pump(4.0);
            double t0 = clock.Elapsed.TotalSeconds;
            double seconds = 2f * halfLength / speed;
            perFrame = t => Stand(centre + new Vector3(-halfLength + speed * (float)(t - t0), 0f, z), MathF.PI / 2f);
            Record($"fountain walk {speed:0.#}ms", seconds);
            perFrame = null;
            Remove(ids);
            WindField.Weather = calm;
            Pump(1.5);
        }

        // The garden fire pit, its emitter 0.6 m up as on the map. d from its middle.
        void Fire(IEnumerable<float> distances, double seconds, string label = "fire")
        {
            Vector3 pit = new(-60f, 0f, 60f);
            WindField.Weather = WindWeather.Steady(1.5f, 250f, 0.2f);
            Stand(pit + new Vector3(0f, 0f, -distances.First()), 0f);
            int id = AddEmitter("fire:fire_pit", pit + new Vector3(0f, 0.6f, 0f), 120f, 1f);
            Pump(4.0);
            foreach (float d in distances)
            {
                var feet = pit + new Vector3(0f, 0f, -d);
                Stand(feet, YawTo(feet, pit));
                Pump(2.5);
                Record($"{label} {d:0.#}m", seconds);
            }
            Remove(new[] { id });
            WindField.Weather = calm;
            Pump(1.5);
        }

        void Rain(float mmPerHour, string label, double seconds)
        {
            Stand(new Vector3(0f, 0f, -150f), 0f);
            world.UpdateAtmosphere(new WorldStateUpdate
            {
                Temperature = 15f, Humidity = 0.8f, AirPressure = 101325f, AirAbsorptionMultiplier = 1f,
                PrecipitationIntensity = 0.5f, RainRateMmPerHour = mmPerHour,
            });
            Pump(5.0);
            Record($"rain {label}", seconds);
            world.UpdateAtmosphere(new WorldStateUpdate { Temperature = 15f, Humidity = 0.6f, AirPressure = 101325f, AirAbsorptionMultiplier = 1f });
            Pump(3.0);
        }

        try
        {
            Pump(2.0);
            Record("silence", 2.0);
            var spots = new[] { 2f, 5f, 10f, 20f };
            if (set is "measure" or "all")
            {
                Tree(spots, 12.0);
                Fountain(spots, 10.0);
                Fire(spots, 12.0);
                Rain(Rainfall.ModerateRate, "street moderate", 12.0);
            }
            if (set is "render" or "all")
            {
                Tree(new[] { 3f }, renderSeconds, "render tree");
                Fountain(new[] { 4f }, renderSeconds, "render fountain");
                Fire(new[] { 3f }, renderSeconds, "render fire");
                Rain(Rainfall.ModerateRate, "render street moderate", renderSeconds);
                FountainWalk(18f, 1.1f);
            }
            if (set is "level")
            {
                // The total at 1, 5 and 20 m from each source's middle, spread or not.
                Tree(new[] { 1f, 5f, 20f }, 10.0, "level tree");
                Fire(new[] { 1f, 5f, 20f }, 10.0, "level fire");
            }
            Record("silence end", 1.0);
        }
        finally
        {
            facade.Dispose();
        }

        var sb = new StringBuilder("name,start,seconds,cpu_percent,mixer_load,hrtf_voices\n");
        foreach (var s in segments)
            sb.Append(CultureInfo.InvariantCulture, $"{s.Name},{s.Start:F3},{s.Seconds:F3},{s.CpuPercent:F1},{s.MixerLoad:F4},{s.Voices}\n");
        File.WriteAllText(Path.Combine(outDir, "segments.csv"), sb.ToString());
        Console.WriteLine($"Wrote {outDir}/capture.post.wav and segments.csv ({segments.Count} segments)");
        return 0;
    }

    private static string? Arg(string[] args, string key) => args.FirstOrDefault(x => x.StartsWith(key, StringComparison.Ordinal))?[key.Length..];
}
