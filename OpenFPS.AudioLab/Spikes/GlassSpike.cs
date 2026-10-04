using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --glass: the physical glass model (<see cref="GlassFracture"/>) against the hiss-and-tinkle it replaced.
///
///   --glass survey            every part and type at a few sizes, weapons and drops: peak dB SPL at a
///                             metre, render time, the census of pieces; what DeclaredDb is read from.
///   --glass round1 out=DIR    the listening set: a 6 mm annealed house window and a 10 mm tempered shop
///                             pane, each shot with a Glock and an AKM, heard at 5 and 20 m and from the
///                             street under a second-floor window; the old sounds of the same, and the raw
///                             renders at a metre (raw/) for measuring. One shared gain: the game's
///                             placement (Loudness at the shipped compression, 1/r, the travel time), then
///                             one common gain for every file so the loudest fits. Dry, mono, direct path.
/// </summary>
public static class GlassSpike
{
    private const int Rate = 48000;

    public static int Run(string[] args)
    {
        if (args.Contains("survey")) return Survey(args.Contains("quick"));
        if (args.Contains("kernels"))
        {
            foreach (double tau in new[] { 2e-6, 1e-5, 2e-5, 5e-5, 1e-4, 3e-4, 1e-3, 3e-3, 1e-2 })
            {
                var (peak, len, analytic) = GlassFracture.KernelInfo(tau, Rate);
                Console.WriteLine($"tau {tau * 1e6,8:F0} us: peak {peak:E3} (analytic {analytic:E3}), {len} samples");
            }
            return 0;
        }
        string? dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?.Substring(4);
        if (args.Contains("ringfit"))
        {
            // Shards (bottle and window glass, 3 and 6 mm, 3 to 8 cm, the size whose first modes are the 8-15 kHz lines in the recording) dropped 10 to 40 cm onto cement,
            // one after another, half a second apart: for ringclick.py to compare with the recording.
            AcousticRegistry.Initialize();
            var all = new List<float>();
            var rng = new Random(5);
            double keSum = 0, acSum = 0, pfSum = 0;
            for (int i = 0; i < 40; i++)
            {
                double lx = 0.03 + 0.05 * rng.NextDouble(), ly = lx * (0.4 + 0.5 * rng.NextDouble());
                double th = rng.NextDouble() < 0.5 ? 0.003 : 0.006, drop = 0.1 + 0.3 * rng.NextDouble();
                var pa = GlassFracture.RenderDrop(lx, ly, th, drop, "Concrete", Rate, i, out double pf);
                pfSum += pf;
                double ke = 2500 * lx * ly * 0.75 * th * 9.81 * drop;
                keSum += ke; acSum += GlassFracture.AcousticJoules(pa, Rate, false);
                all.AddRange(pa.Take(Rate / 2).Select(x => (float)(x / 50)));
            }
            WriteWav(dir ?? "ringfit.wav", all.ToArray());
            Console.WriteLine($"40 drops: KE {keSum:E2} J, sound {acSum:E2} J, efficiency {acSum / keSum:E2}; point-force estimate {pfSum:E2} J ({pfSum / keSum:E2})");
            return 0;
        }
        if (args.Contains("round1")) return Round1(dir ?? LabPaths.InRepo("inbox", "glass-round1-2026-10-04"));
        Console.WriteLine("--glass survey | --glass round1 [out=DIR]");
        return 1;
    }

    private static readonly (string Name, float W, float H, float T, GlassType Type)[] Panes =
    {
        ("house window, annealed 6 mm", 1.2f, 1.6f, 0.006f, GlassType.Annealed),
        ("shop pane, tempered 10 mm", 2.0f, 2.5f, 0.010f, GlassType.Tempered),
        ("glass wall prefab, tempered 6 mm", 2.0f, 3.0f, 0.006f, GlassType.Tempered),
        ("car side window, tempered 4 mm", 0.8f, 0.45f, 0.004f, GlassType.Tempered),
    };

    private static int Survey(bool quick)
    {
        AcousticRegistry.Initialize();
        var weapons = quick ? new[] { WeaponRegistry.Glock } : new[] { WeaponRegistry.Glock, WeaponRegistry.Akm, WeaponRegistry.Shotgun };
        Console.WriteLine($"{"key",-74} {"peak",6} {"LAFmax",7} {"len",5} {"ms",6}");
        foreach (var pane in quick ? Panes.Take(2).ToArray() : Panes)
            foreach (var w in weapons)
            {
                var (kg, pellets) = GlassFracture.BulletOf(w);
                bool shatters = GlassBreak.Shatters(pane.Type, w);
                var census = GlassFracture.Census(new GlassFracture.Spec(GlassFracture.Part.Break, pane.Type, pane.W, pane.H, pane.T, kg, w.MuzzleVelocity, pellets, 0.9f, "Concrete", 0));
                Console.WriteLine($"# {pane.Name}, {w.DisplayName} ({kg * 1000:F1} g at {w.MuzzleVelocity:F0} m/s x{pellets}): "
                                + (shatters ? $"shards {census.Shards}, slivers {census.Slivers}, clumps {census.Clumps}, dice {census.Dice}, falling {census.Falling}" : "a hole"));
                var parts = shatters ? new[] { GlassFracture.Part.Break, GlassFracture.Part.Land } : new[] { GlassFracture.Part.Hole };
                foreach (var part in parts)
                    foreach (var (drop, ground) in part == GlassFracture.Part.Land
                                 ? quick ? new[] { (0.9f, "Concrete"), (0.9f, "Grass") }
                                         : new[] { (0.9f, "Concrete"), (3.9f, "Concrete"), (0.9f, "Grass"), (0.9f, "Carpet"), (0.9f, "Asphalt") }
                                 : new[] { (0.9f, "Concrete") })
                        for (int v = 0; v < (quick ? 1 : 2); v++)
                        {
                            var spec = new GlassFracture.Spec(part, pane.Type, pane.W, pane.H, pane.T, kg, w.MuzzleVelocity, pellets, drop, ground, v);
                            string key = GlassFracture.Key(spec);
                            var sw = Stopwatch.StartNew();
                            var pcm = GlassFracture.RenderKey(key, Rate, out float db);
                            sw.Stop();
                            double laf = HeardLevelsSpike.LafMaxDbfs(pcm) + db;
                            var contacts = GlassFracture.Contacts(spec);
                            string eff = "";
                            if (part != GlassFracture.Part.Land)
                                eff = $"  sound {GlassFracture.AcousticJoules(GlassFracture.Render(spec, Rate), Rate, true):E2} J";
                            if (part == GlassFracture.Part.Land)
                            {
                                var pa = GlassFracture.Render(spec, Rate);
                                double ke = GlassFracture.ArrivingJoules(spec), ac = GlassFracture.AcousticJoules(pa, Rate, false);
                                double pf = GlassFracture.PointForceJoules(spec);
                                eff = $"  KE {ke:F0} J, sound {ac:E2} J, efficiency {ac / ke:E2}, point-force estimate {pf:E2} J ({pf / ke:E2})";
                            }
                            Console.WriteLine($"{key,-74} {db,6:F1} {laf,7:F1} {pcm.Length / (double)Rate,5:F2} {sw.Elapsed.TotalMilliseconds,6:F0}  declared {GlassFracture.DeclaredDb(spec):F0}"
                                            + $"  contacts {contacts.Contacts} over {contacts.First:F2}-{contacts.Last:F2} s, busiest {contacts.BusiestPerSecond:F0}/s" + eff);
                        }
            }
        return 0;
    }

    /// <summary>One listening position: where the listener's ear is, and the pane's bottom edge above the ground.</summary>
    private sealed record Scene(string Name, float Drop, Vector3 Ear);

    private static int Round1(string dir)
    {
        AcousticRegistry.Initialize();
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        float savedCompression = Loudness.DynamicRangeCompression;
        Loudness.DynamicRangeCompression = Loudness.DefaultCompression;

        var takes = new List<(string Name, float[] Pcm)>();
        var rawTakes = new List<(string Name, float[] Pa)>();
        var notes = new List<string>();
        int fileNo = 1;
        foreach (var pane in Panes.Take(2))
            foreach (var weapon in new[] { WeaponRegistry.Glock, WeaponRegistry.Akm })
            {
                string paneShort = pane.Type == GlassType.Annealed ? "house-annealed" : "shop-tempered";
                string gun = weapon == WeaponRegistry.Glock ? "glock" : "akm";
                // The pane in a wall facing +z, the ground at y = 0. Ground floor: a 0.9 m sill. The second
                // floor: its sill 3.9 m up. The listener 5 or 20 m out in front at ear height, or in the street
                // 4 m out from the foot of the wall.
                foreach (var scene in new[]
                         {
                             new Scene("5m", 0.9f, new Vector3(0, 1.6f, 5f)),
                             new Scene("20m", 0.9f, new Vector3(0, 1.6f, 20f)),
                             new Scene("street-below-2nd-floor", 3.9f, new Vector3(1f, 1.6f, 4f)),
                         })
                {
                    float centreY = scene.Drop + pane.H / 2;
                    var glass = new GlassPane(new Vector3(0, centreY, 0), new Vector2(pane.W, pane.H), Vector3.UnitZ, pane.Type, scene.Drop);
                    Span<GlassEvent> buf = stackalloc GlassEvent[48];
                    int n = GlassBreak.Resolve(glass, glass.Centre, weapon, 1, buf);
                    var events = new List<GlassEvent>();
                    for (int i = 0; i < n; i++) events.Add(buf[i]);

                    var after = GlassSound.From(events, glass, weapon, pane.T, "Concrete", 1);
                    var before = OldGlassSound.From(events, pane.Type, glass.Size, pane.T);
                    string stem = $"{fileNo:00}-{paneShort}-{gun}-{scene.Name}";
                    takes.Add(($"{stem}-after", Place(after, scene.Ear, out var lines, raw: rawTakes, rawName: $"{paneShort}-{gun}-{scene.Name}")));
                    notes.Add($"{stem}-after: " + string.Join("; ", lines));
                    takes.Add(($"{stem}-before", Place(before, scene.Ear, out lines)));
                    notes.Add($"{stem}-before: " + string.Join("; ", lines));
                    fileNo++;
                }
            }

        // Grounds: the house window from the 2nd floor with a Glock, landing on grass, asphalt, carpet.
        foreach (string ground in new[] { "Grass", "Asphalt", "Carpet" })
        {
            var pane = Panes[0];
            var glass = new GlassPane(new Vector3(0, 3.9f + pane.H / 2, 0), new Vector2(pane.W, pane.H), Vector3.UnitZ, pane.Type, 3.9f);
            Span<GlassEvent> buf = stackalloc GlassEvent[48];
            int n = GlassBreak.Resolve(glass, glass.Centre, WeaponRegistry.Glock, 1, buf);
            var events = new List<GlassEvent>();
            for (int i = 0; i < n; i++) events.Add(buf[i]);
            var after = GlassSound.From(events, glass, WeaponRegistry.Glock, pane.T, ground, 1);
            string stem = $"{fileNo:00}-house-annealed-glock-street-below-2nd-floor-onto-{ground.ToLowerInvariant()}";
            takes.Add(($"{stem}-after", Place(after, new Vector3(1f, 1.6f, 4f), out var lines, raw: rawTakes,
                                              rawName: $"house-annealed-glock-street-below-2nd-floor-onto-{ground.ToLowerInvariant()}")));
            notes.Add($"{stem}-after: " + string.Join("; ", lines));
            fileNo++;
        }

        // One gain for every file: the game's full scale, brought down only if anything would clip.
        float top = takes.Max(t => t.Pcm.Max(x => Math.Abs(x)));
        float common = top > 0.89f ? 0.89f / top : 1f;
        notes.Insert(0, $"common gain {20 * Math.Log10(common):F1} dB on every file (loudest sample before it {20 * Math.Log10(top):F1} dBFS)");
        foreach (var (name, pcm) in takes)
            WriteWav(Path.Combine(dir, name + ".wav"), pcm.Select(x => x * common).ToArray());
        // The raw renders, pascals at a metre over 500 Pa full scale (148 dB SPL peak), for measuring.
        foreach (var (name, pa) in rawTakes)
            WriteWav(Path.Combine(dir, "raw", name + ".wav"), pa.Select(x => x / 500f).ToArray());
        File.WriteAllLines(Path.Combine(dir, "levels.txt"), notes);
        foreach (var l in notes) Console.WriteLine(l);
        Loudness.DynamicRangeCompression = savedCompression;
        return 0;
    }

    /// <summary>The sounds of one break as the game places them for an ear: each rendered as the client
    /// renders it, at its own level (a model key at its render's own peak), its gain from Loudness, 1/r,
    /// and its travel time.</summary>
    private static float[] Place(List<TransientSound> sounds, Vector3 ear, out List<string> lines,
                                 List<(string, float[])>? raw = null, string rawName = "")
    {
        lines = new List<string>();
        var placed = new List<(int At, float[] Pcm, float Gain)>();
        double lead = 0.3;
        foreach (var s0 in sounds)
        {
            var own = new System.Collections.Concurrent.ConcurrentDictionary<string, float>();
            float[] pcm;
            var s = s0;
            if (!string.IsNullOrEmpty(s.SynthKey) && s.SynthKey.StartsWith(GlassFracture.KeyPrefix, StringComparison.Ordinal))
            {
                pcm = OpenFPS.Client.Core.WorldAudioPlayer.RenderDoorKey(s.SynthKey, own);
                s = OpenFPS.Client.Core.WorldAudioPlayer.AtOwnLevel(s, own);
                if (raw != null)
                {
                    GlassFracture.TryParseKey(s.SynthKey, out var spec);
                    float scale = (float)(2e-5 * Math.Pow(10, s.LevelDb / 20));
                    raw.Add(($"{rawName}-{spec.Part.ToString().ToLowerInvariant()}", pcm.Select(x => x * scale).ToArray()));
                }
            }
            else pcm = TransientSynth.Render(s, 1);
            float d = Vector3.Distance(ear, s.Position);
            var (gain, reference) = Loudness.Place(s.LevelDb);
            float g = Loudness.RenderedGain(gain, reference, Loudness.AudibleRange(s.LevelDb), d);
            double at = lead + s.DelaySeconds + d / 343.0;
            placed.Add(((int)(at * Rate), pcm, g));
            string what = string.IsNullOrEmpty(s.SynthKey) ? $"{s.Character} {s.Hz:F0} Hz" : s.SynthKey;
            lines.Add($"{what} {s.LevelDb:F1} dB at {d:F1} m, +{s.DelaySeconds:F2} s, gain {20 * Math.Log10(Math.Max(1e-9, g)):F1} dB");
        }
        int len = placed.Max(p => p.At + p.Pcm.Length) + Rate / 4;
        var y = new float[len];
        foreach (var (at, pcm, g) in placed)
            for (int i = 0; i < pcm.Length; i++) y[at + i] += pcm[i] * g;
        return y;
    }

    private static void WriteWav(string path, float[] pcm)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (float v in pcm) w.Write((short)Math.Clamp(v * 32767f, -32768f, 32767f));
    }

    /// <summary>The mapping this replaced (main before 2026-10-04, ImpactAcoustics.cs), kept here only to
    /// render the "before" files: a hiss for the break and single-mode rings for the pieces.</summary>
    private static class OldGlassSound
    {
        private const float ReferencePaneVolume = 1.2f * 1.6f * 0.006f;

        public static List<TransientSound> From(IEnumerable<GlassEvent> events, GlassType type, Vector2 paneSize, float thicknessMetres)
        {
            var material = AcousticRegistry.GetProperties("Glass");
            var sounds = new List<TransientSound>();
            float thickness = Math.Clamp(thicknessMetres, 0.002f, 0.030f);
            float volume = MathF.Max(1e-5f, paneSize.X * paneSize.Y * thickness);
            float sizeDb = 10f * MathF.Log10(volume / ReferencePaneVolume);
            float shardPitch = MathF.Sqrt(0.006f / thickness);
            float area = MathF.Max(0.01f, paneSize.X * paneSize.Y);
            float span = MathF.Sqrt(area / (1.2f * 1.6f));
            float sizePitch = Math.Clamp(1f / MathF.Sqrt(span), 0.45f, 2.2f);
            float sizeLength = Math.Clamp(span, 0.5f, 2.5f);
            foreach (var e in events)
            {
                float reference = e.Kind switch
                {
                    GlassEventKind.Shatter => 96f, GlassEventKind.Puncture => 82f, GlassEventKind.Shard => 90f, _ => 92f,
                };
                float baseDb = reference + sizeDb + 20f * MathF.Log10(MathF.Max(0.01f, e.Volume));
                switch (e.Kind)
                {
                    case GlassEventKind.Puncture:
                        sounds.Add(new TransientSound { Character = SoundCharacter.Knock, DelaySeconds = e.DelaySeconds, Position = e.Position,
                                                        LevelDb = baseDb, Hz = 3200f * e.Pitch, DecaySeconds = 0.03f, Noisiness = 0.7f });
                        break;
                    case GlassEventKind.Shatter:
                        sounds.Add(new TransientSound { Character = SoundCharacter.Hiss, DelaySeconds = e.DelaySeconds, Position = e.Position,
                                                        LevelDb = baseDb, Hz = (type == GlassType.Laminated ? 900f : 2800f) * sizePitch,
                                                        DecaySeconds = (type == GlassType.Laminated ? 0.12f : 0.28f) * sizeLength, Noisiness = 1f });
                        if (type == GlassType.Annealed)
                        {
                            float hz = PanelAcoustics.RingHz(material, paneSize.X, paneSize.Y, thickness);
                            if (hz > 0f)
                                sounds.Add(new TransientSound { Character = SoundCharacter.Ring, DelaySeconds = e.DelaySeconds, Position = e.Position,
                                                                LevelDb = baseDb - 10f, Hz = hz,
                                                                DecaySeconds = PanelAcoustics.RingSeconds(material, hz) * 0.3f, Noisiness = 0.3f });
                        }
                        break;
                    case GlassEventKind.Shard:
                        sounds.Add(new TransientSound { Character = SoundCharacter.Ring, DelaySeconds = e.DelaySeconds, Position = e.Position,
                                                        LevelDb = baseDb, Hz = 6400f * e.Pitch * shardPitch * sizePitch, DecaySeconds = 0.07f, Noisiness = 0.18f });
                        break;
                    default:
                        sounds.Add(new TransientSound { Character = SoundCharacter.Ring, DelaySeconds = e.DelaySeconds, Position = e.Position,
                                                        LevelDb = baseDb, Hz = 3200f * e.Pitch * shardPitch * sizePitch, DecaySeconds = 0.09f, Noisiness = 0.2f });
                        break;
                }
            }
            return sounds;
        }
    }
}
