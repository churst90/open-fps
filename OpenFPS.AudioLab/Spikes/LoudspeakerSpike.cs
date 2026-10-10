using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Signals;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --loudspeaker bench|game [out=DIR]: the loudspeaker model (LoudspeakerSpec, LoudspeakerChain, Radiator).
///
///   bench  each preset's numbers against its datasheet (response, level, coverage, directivity index),
///          and its program rendered on the axis at a metre, as DIR/bench-PRESET.wav (float, the buffer
///          the game plays) with the levels in DIR/bench.csv.
///   game   the whole client path (a ClientAudioSystem over the facade over the FmodAudioProvider, at
///          the default /levels and the master), captured to DIR/capture.wav with DIR/segments.csv for
///          tools/game_levels.py: the PA at 5 m on the axis and behind, 30 m and 100 m down a street,
///          the megaphone at 3 m on the axis and behind and shouted into, and each as it was before.
/// </summary>
public static class LoudspeakerSpike
{
    private const string PaProgram = "ANNOUNCE/st_louis_welcome";
    private const string MegaphoneProgram = "BEACONS/megaphone";

    public static int Run(string[] args)
    {
        string outDir = Arg(args, "out=") ?? "/tmp/ofps-speaker/out";
        Directory.CreateDirectory(outDir);
        if (args.Contains("game")) return Game(outDir);
        return Bench(outDir);
    }

    // ── The model on its own ─────────────────────────────────────────────────────────────────────

    private static int Bench(string outDir)
    {
        int rate = MixerQuality.DefaultRate;
        var csv = new StringBuilder("preset,program,spl_gated_db,peak_db,level_db,clipped,excursion,limit_db,radiated_db,ms\n");
        foreach (var (preset, program) in new[] { ("pa_horn", PaProgram), ("megaphone", MegaphoneProgram), ("megaphone_shouted", MegaphoneProgram), ("megaphone", PaProgram) })
        {
            var spec = LoudspeakerSpec.ByName(preset);
            Console.WriteLine($"\n== {preset}: {spec.Name}");
            Console.WriteLine($"   mouth {spec.MouthDiameterMetres * 1000:F0} mm -> cutoff {spec.FlareCutoffHz:F0} Hz; driver f0 {spec.DriverResonanceHz:F0} Hz Q {spec.DriverQ:F2}; "
                            + $"rated {spec.RatedWatts:F0} W = {spec.RatedSplDb:F1} dB at 1 m (sine, band average)");

            // The small-signal response against the datasheet's band.
            double[] thirds = { 125, 160, 200, 250, 315, 400, 450, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6000, 6300, 8000, 10000, 12500, 16000 };
            var h = LoudspeakerChain.Response(spec, rate, thirds);
            double peak = h.Max();
            Console.WriteLine("   response, dB re its peak: " + string.Join(" ", thirds.Select((f, i) => $"{f:0}:{20 * Math.Log10(h[i] / peak):+0.0;-0.0}")));
            Console.WriteLine($"   1 W at 1 kHz: {LoudspeakerChain.SineSplDb(spec, rate, 1000, 1):F1} dB; rated power at 1 kHz: {LoudspeakerChain.SineSplDb(spec, rate, 1000, spec.RatedWatts):F1} dB");

            var rad = Radiator.For(spec);
            Console.WriteLine("   beam, dB per band at 0/30/60/90/120/150/180 degrees:");
            for (int b = 0; b < Radiator.Bands; b++)
            {
                var row = new[] { 0, 30, 60, 90, 120, 150, 180 }.Select(d => 20 * Math.Log10(Math.Max(1e-6, rad.Gain(b, d * MathF.PI / 180f))));
                Console.WriteLine($"     {Radiator.BandHz[b],5:0} Hz: " + string.Join(" ", row.Select(v => $"{v,6:F1}")) + $"   DI {rad.DirectivityIndexDb(b):F1} dB");
            }
            float cover = 0f;
            for (float d = 0; d <= 180; d += 0.5f) if (rad.Gain(3, d * MathF.PI / 180f) >= 0.5012f) cover = d;
            Console.WriteLine($"   coverage at -6 dB in the 2 kHz octave: {2 * cover:F0} degrees");

            if (!ReadWav(LabPaths.Sounds(program + ".wav"), out var mono, out int srcRate)) { Console.WriteLine($"   no {program}"); continue; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var r = LoudspeakerChain.Render(preset + "/" + program, spec, mono, srcRate, rate, loop: program == MegaphoneProgram);
            long ms = clock.ElapsedMilliseconds;
            Console.WriteLine($"   {program}: {r.SplDb:F1} dB SPL at 1 m (gated), peaks {r.PeakSplDb:F1}, declared {r.LevelDb:F1}; clipped {r.ClippedShare:P1}, "
                            + $"diaphragm {r.MaxExcursion:F2} of its travel, compressor {r.MaxLimitDb:F1} dB; room fed {20 * Math.Log10(r.RadiatedGain):F1} dB; {ms} ms");
            Console.WriteLine("   program's energy by band, dB re the loudest: "
                            + string.Join(" ", r.BandEnergy.Select((e, i) => $"{Radiator.BandHz[i]:0}:{10 * Math.Log10(e / r.BandEnergy.Max()):F1}")));
            string name = $"bench-{preset}-{Path.GetFileName(program)}.wav";
            WriteWav(Path.Combine(outDir, name), r.Pcm, r.Rate);
            csv.Append(CultureInfo.InvariantCulture, $"{preset},{program},{r.SplDb:F2},{r.PeakSplDb:F2},{r.LevelDb:F2},{r.ClippedShare:F4},{r.MaxExcursion:F3},{r.MaxLimitDb:F2},{20 * Math.Log10(r.RadiatedGain):F2},{ms}\n");
        }
        File.WriteAllText(Path.Combine(outDir, "bench.csv"), csv.ToString());

        // What one beam unit costs the mixer: a mono block of 1,024, as FMOD hands it over.
        {
            var unit = new RadiatorState();
            unit.Configure(rate);
            var block = new float[1024];
            var output = new float[1024];
            var rng = new Random(1);
            for (int i = 0; i < block.Length; i++) block[i] = (float)(rng.NextDouble() * 2 - 1);
            for (int k = 0; k < 2000; k++) unit.Process(block, output, 1);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            const int Blocks = 20000;
            for (int k = 0; k < Blocks; k++) unit.Process(block, output, 1);
            double us = timer.Elapsed.TotalMilliseconds * 1000.0 / Blocks;
            Console.WriteLine($"\nbeam unit: {us:F1} us per 1,024-sample block = {us / (1024.0 / rate * 1e6) * 100:F2} % of one core in real time");
        }

        // What the old prefab placed the recording as: full scale out to 12 m at gain 1.
        foreach (var (program, minDistance) in new[] { (PaProgram, 12f), (MegaphoneProgram, 3f) })
        {
            if (!ReadWav(LabPaths.Sounds(program + ".wav"), out var mono, out int srcRate)) continue;
            float gated = OpenFPS.Common.Hearing.Timbre.GatedRms(mono, srcRate);
            float fullScale = Loudness.RenderCeilingDb + 20f * MathF.Log10(minDistance);
            Console.WriteLine($"\nbefore: {program} at gain 1, reference {minDistance} m: full scale stood for {fullScale:F1} dB at 1 m, "
                            + $"its speech ({gated:F1} dBFS gated) for {fullScale + gated:F1} dB SPL at 1 m");
        }
        Console.WriteLine($"\nWrote {outDir}/bench-*.wav and bench.csv");
        return 0;
    }

    // ── Through the game ─────────────────────────────────────────────────────────────────────────

    private sealed record Segment(string Name, double Start, double Seconds);

    private static int Game(string outDir)
    {
        AcousticRegistry.Initialize();
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

        // Ids BirdLife settles nothing on (its hash of 9 and 10 is between a pigeon's and a crow's), so the
        // captures are the speakers alone.
        void Solid(int id, Vector3 centre, Vector3 size, string material) => world.RegisterDefinition(new EntityDefinition
        {
            EntityId = id, Type = EntityType.StaticObject,
            Transform = new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
            Material = new MaterialComponent { Material = material },
        });
        // An asphalt street between two rows of brick fronts, 16 m apart and 12 m high, 260 m long.
        Solid(1, new Vector3(0f, -0.5f, 100f), new Vector3(600f, 1f, 600f), "Asphalt");
        Solid(9, new Vector3(-8.25f, 6f, 100f), new Vector3(0.5f, 12f, 260f), "Brick");
        Solid(10, new Vector3(8.25f, 6f, 100f), new Vector3(0.5f, 12f, 260f), "Brick");

        var segments = new List<Segment>();
        int nextId = 700;            // a multiple of 7: a repeater's first turn is not staggered
        void Pump(double seconds)
        {
            var until = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < until)
            {
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
        // An emitter as a prefab makes one: the recording, and either the speaker it plays through or
        // the old cone and reference distance.
        int Speaker(string program, Vector3 at, Vector3 aim, string? loudspeaker, PlaybackMode mode, float repeat,
                    float range, float minDistance, float coneIn, float coneOut, float coneVol)
        {
            int id = nextId; nextId += 7;
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.Beacon,
                Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
            };
            def.SoundEmitter = new SoundEmitterComponent
            {
                SoundId = program, Mode = mode, RepeatIntervalSeconds = repeat, Volume = 1f, Range = range,
                MinDistance = minDistance, Direction = Vector3.Normalize(aim),
                ConeInsideAngle = coneIn, ConeOutsideAngle = coneOut, ConeOutsideVolume = coneVol,
                Loudspeaker = loudspeaker ?? "",
            };
            world.RegisterDefinition(def);
            return id;
        }
        void Remove(int id)
        {
            world.RemoveEntities(new[] { id });
            audio.ForgetEntity(id);
        }
        Vector3 Ear(Vector3 feet) => feet + new Vector3(0f, player.EyeHeight, 0f);

        try
        {
            Record("silence start", 1.0);
            var pole = new Vector3(0f, 4f, 0f);
            foreach (bool old in new[] { false, true })
            {
                string tag = old ? "old" : "new";
                foreach (var (name, feet, behind) in new[]
                {
                    ("pa 5m on axis", new Vector3(0f, 0f, 5f), false),
                    ("pa 5m behind", new Vector3(0f, 0f, 5f), true),
                    ("pa 30m on axis", new Vector3(0f, 0f, 30f), false),
                    ("pa 100m down the street", new Vector3(0f, 0f, 100f), false),
                })
                {
                    Stand(feet, pole);
                    var toEar = Ear(feet) - pole;
                    var aim = behind ? -toEar : toEar;
                    int id = old
                        ? Speaker(PaProgram, pole, aim, null, PlaybackMode.Single, 4f, 400f, 12f, 90f, 240f, 0.35f)
                        : Speaker(PaProgram, pole, aim, "pa_horn", PlaybackMode.Single, 4f, 400f, 12f, 360f, 360f, 1f);
                    Pump(old ? 0.3 : 1.0);         // the new one's first render
                    Record($"{tag} {name}", 8.0);
                    Remove(id);
                    Pump(2.0);
                }
                foreach (var (name, preset, behind) in new[]
                {
                    ("megaphone 3m on axis", "megaphone", false),
                    ("megaphone 3m behind", "megaphone", true),
                    ("megaphone 3m shouted", "megaphone_shouted", false),
                })
                {
                    if (old && preset == "megaphone_shouted") continue;    // the old one had no drive to push
                    var mouth = new Vector3(30f, 1.6f, 40f);
                    var feet = new Vector3(30f, 0f, 43f);
                    Stand(feet, mouth);
                    var toEar = Ear(feet) - mouth;
                    var aim = behind ? -toEar : toEar;
                    int id = old
                        ? Speaker(MegaphoneProgram, mouth, aim, null, PlaybackMode.LoopOne, 0f, 25f, 3f, 40f, 110f, 0.05f)
                        : Speaker(MegaphoneProgram, mouth, aim, preset, PlaybackMode.LoopOne, 0f, 25f, 3f, 360f, 360f, 1f);
                    Pump(old ? 0.3 : 1.5);
                    Record($"{tag} {name}", 8.0);
                    Remove(id);
                    Pump(2.0);
                }
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
        Console.WriteLine($"Wrote {wav} and segments.csv ({segments.Count} segments); /levels {Loudness.DynamicRangeCompression:F2}");
        return 0;
    }

    // ── Files ────────────────────────────────────────────────────────────────────────────────────

    private static string? Arg(string[] args, string prefix)
        => args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    /// <summary>A PCM16 or float WAV, mixed to mono.</summary>
    private static bool ReadWav(string path, out float[] mono, out int rate)
    {
        mono = Array.Empty<float>(); rate = 0;
        if (!File.Exists(path)) return false;
        using var r = new BinaryReader(File.OpenRead(path));
        if (new string(r.ReadChars(4)) != "RIFF") return false;
        r.ReadInt32();
        if (new string(r.ReadChars(4)) != "WAVE") return false;
        int channels = 1, bits = 16, format = 1;
        while (r.BaseStream.Position < r.BaseStream.Length - 8)
        {
            string id = new string(r.ReadChars(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                format = r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32();
                r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                if (size > 16) r.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                int frames = size / (channels * bits / 8);
                mono = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    float s = 0;
                    for (int c = 0; c < channels; c++)
                        s += format == 3 ? r.ReadSingle() : bits == 16 ? r.ReadInt16() / 32768f : r.ReadInt32() / 2147483648f;
                    mono[i] = s / channels;
                }
                return true;
            }
            else r.ReadBytes(size + (size & 1));
        }
        return false;
    }

    private static void WriteWav(string path, float[] samples, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = samples.Length * 4;
        w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)3); w.Write((short)1);
        w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)32);
        w.Write("data".ToCharArray()); w.Write(bytes);
        foreach (float v in samples) w.Write(v);
    }
}
