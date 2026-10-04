using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// Round two of the bullets, after Cody heard round one (2026-10-04): the subsonic whizz rebuilt as
/// broadband wake noise, ricochets, and a round striking each material.
///
///   --bullet-round2 [out=DIR]
///
/// Every sound comes from the game's own calls (BulletFlyby.Sounds, Ricochet.TryBounce and
/// WhineSounds, BulletImpact.Sounds) and is rendered from its key as the client renders it, laid down
/// when it arrives (its delay plus its own distance over the speed of sound) and at the game's own
/// placement for its declared level (Loudness.Place, then the inverse law). ONE shared gain for every
/// file, never normalised per file. Dry and mono: the game adds direction, air, reflections, reverb.
/// The whizz "before" files are the old renderer (96 wandering sine partials), kept here only to be
/// compared.
/// </summary>
public static class BulletRound2Spike
{
    private const int Sr = TransientSynth.SampleRate;
    private static readonly Air Still = Air.Standard;
    private static float C => Still.SpeedOfSound;

    public static int Run(string[] args)
    {
        string dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..]
                     ?? LabPaths.InRepo("inbox", "bullets-round2-2026-10-04");
        Directory.CreateDirectory(dir);
        var readme = new List<string>();
        var rows = new List<string>();

        Console.WriteLine($"Writing to {dir}\n");

        // ── 1. The whizz, before and after ─────────────────────────────────────────────────────
        rows.Add("## 1. The subsonic whizz, before and after");
        rows.Add("");
        rows.Add("| file | what | flatness | tone over its third-octave (dB) | peak dBFS |");
        rows.Add("|---|---|---|---|---|");
        var whizzScenes = new (string Name, Vector3 Ear, string What)[]
        {
            ("1a-pistol45-30m-downrange-2m-off", new(2f, 0f, 30f), ".45 passing 2 m away, 30 m out"),
            ("1b-pistol45-30m-downrange-6m-off", new(6f, 0f, 30f), ".45 passing 6 m away"),
            ("1c-pistol45-15m-downrange-1m-off", new(1f, 0f, 15f), ".45 passing a metre away, 15 m out"),
        };
        var pistol = WeaponRegistry.ServicePistol;
        var path45 = FlyStraight(pistol, new Vector3(0f, 0f, 0.5f), Vector3.UnitZ, 3f);
        foreach (var (name, ear, what) in whizzScenes)
        {
            var sounds = BulletFlyby.Sounds(path45, ear, pistol, Still);
            foreach (bool after in new[] { false, true })
            {
                var laid = new List<(float At, float[] Pcm, float Gain)>();
                foreach (var s in sounds)
                {
                    if (!BulletFlyby.TryParseWhizz(s.SynthKey, out var w)) continue;
                    float r = Vector3.Distance(ear, s.Position);
                    laid.Add((s.DelaySeconds + r / C, after ? BulletFlyby.RenderWhizz(w, Sr, 1) : LegacyWhizz.Render(w, Sr, 1), Placed(s.LevelDb, r)));
                }
                var whizzOnly = Mix(laid, 0.05f, 0.05f, out _);
                var (flat, tone) = Spectrum.Tonality(Trim(whizzOnly), Sr);
                string file = $"{name}-whizz-{(after ? "after" : "before")}.wav";
                Write(dir, file, whizzOnly);
                rows.Add($"| {file} | {what}, the whizz alone ({(after ? "broadband wake noise" : "the old 96 sine partials")}) | {flat:F3} | {tone:F1} | {PeakDb(whizzOnly):F1} |");
                Console.WriteLine($"  {file,-58} flatness {flat:F3}  tone {tone:F1} dB  peak {PeakDb(whizzOnly):F1} dBFS");
            }
            // The new whizz with the report, as round one's files were.
            var full = new List<(float At, float[] Pcm, float Gain)>();
            foreach (var s in sounds)
            {
                if (!BulletFlyby.TryParseWhizz(s.SynthKey, out var w)) continue;
                float r = Vector3.Distance(ear, s.Position);
                full.Add((s.DelaySeconds + r / C, BulletFlyby.RenderWhizz(w, Sr, 1), Placed(s.LevelDb, r)));
            }
            AddReport(full, pistol, ear, Vector3.Zero + new Vector3(0f, 0f, 0.5f));
            var mixed = Mix(full, 0.2f, 0.3f, out _);
            string fileFull = $"{name}-with-report-after.wav";
            Write(dir, fileFull, mixed);
            rows.Add($"| {fileFull} | the same pass with its report, as in round one | | | {PeakDb(mixed):F1} |");
        }
        rows.Add("");

        // ── 2. Ricochets ────────────────────────────────────────────────────────────────────────
        rows.Add("## 2. Ricochets");
        rows.Add("");
        rows.Add("The shooter stands 20 m back, firing down onto a flat face (a concrete floor, a steel deck) so that the round meets it at the grazing angle given. "
               + "\"Side\" is 10 m to the side of where it strikes; \"downrange\" is 30 m on along the slug's way and 3 m off it. Each file is the report, the strike at the face "
               + "(and its chips), and the slug's whine (and its crack while it is still faster than sound).");
        rows.Add("");
        rows.Add("| file | round, face, angle | ricochet | sounds (key, declared dB at 1 m, arrives) | peak dBFS |");
        rows.Add("|---|---|---|---|---|");
        var ricochets = new (string Name, WeaponDefinition W, string Face, float Degrees, Vector3 FaceSize)[]
        {
            ("2a-pistol45-concrete-8deg", WeaponRegistry.ServicePistol, "Concrete", 8f, new Vector3(20f, 20f, 0.2f)),
            ("2b-glock-concrete-10deg", WeaponRegistry.Glock, "Concrete", 10f, new Vector3(20f, 20f, 0.2f)),
            ("2c-akm-concrete-6deg", WeaponRegistry.Akm, "Concrete", 6f, new Vector3(20f, 20f, 0.2f)),
            ("2d-pistol45-steel-12deg", WeaponRegistry.ServicePistol, "Metal", 12f, new Vector3(2f, 3f, 0.05f)),
            ("2e-akm-steel-10deg", WeaponRegistry.Akm, "Metal", 10f, new Vector3(2f, 3f, 0.05f)),
        };
        foreach (var (name, w, face, degrees, faceSize) in ricochets)
        {
            float g = degrees * MathF.PI / 180f;
            Vector3 strike = new(0f, 0f, 20f);
            Vector3 muzzle = strike - new Vector3(0f, -MathF.Sin(g), MathF.Cos(g)) * 20f;
            // Fly the round from the muzzle to the face, down the line at that angle.
            var leg0 = FlyStraight(w, muzzle, new Vector3(0f, -MathF.Sin(g), MathF.Cos(g)), 0.5f, untilY: 0f);
            var hitSample = leg0[^1];
            var slug = Slug.Of(w);
            bool bounced = false; Ricochet.Outcome o = default;
            for (int seed = 1; seed < 40 && !bounced; seed++)
                bounced = Ricochet.TryBounce(face, hitSample.Velocity, Vector3.UnitY, slug, new Random(seed), out o);
            if (!bounced) { Console.WriteLine($"  {name}: no ricochet at {degrees} degrees"); continue; }
            var leg1 = FlySlug(o.Slug, hitSample.Position + Vector3.UnitY * 0.002f, o.Velocity, hitSample.Seconds, 150f);
            float range = 0f;
            for (int i = 1; i < leg1.Count; i++) range += Vector3.Distance(leg1[i - 1].Position, leg1[i].Position);
            var strikeKey = BulletImpact.From(face, hitSample.Velocity.Length(), w, o.GrazeRadians, o.Kept, faceSize, face, 0f,
                                              slug.MassKg, slug.Across, slug.Length);
            Vector3 exit = Vector3.Normalize(o.Velocity);
            Vector3 flat = Vector3.Normalize(new Vector3(exit.X, 0f, exit.Z));
            Vector3 sideways = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, flat));
            var listeners = new (string Where, Vector3 Ear)[]
            {
                ("side", strike + sideways * 10f + new Vector3(0f, 1.7f, 0f)),
                ("downrange", strike + flat * 30f + sideways * 3f + new Vector3(0f, 1.7f, 0f)),
            };
            foreach (var (where, ear) in listeners)
            {
                var laid = new List<(float At, float[] Pcm, float Gain)>();
                var parts = new List<string>();
                var heard = new List<TransientSound>();
                heard.AddRange(BulletFlyby.Sounds(leg0, ear, w, Still));
                foreach (var s in BulletImpact.Sounds(strikeKey, hitSample.Position, hitSample.Position))
                {
                    var t = s; t.DelaySeconds += hitSample.Seconds; heard.Add(t);
                }
                heard.AddRange(BulletFlyby.CrackSounds(leg1, ear, o.Slug.Across, o.Slug.Length, Still));
                heard.AddRange(Ricochet.WhineSounds(hitSample.Position, o.Velocity, o.Slug, range, hitSample.Seconds, ear, Still));
                foreach (var s in heard)
                {
                    float r = Vector3.Distance(ear, s.Position);
                    laid.Add((s.DelaySeconds + r / C, Render(s, 1), Placed(s.LevelDb, r)));
                    parts.Add($"{Short(s.SynthKey)} {s.LevelDb:F0} dB +{(s.DelaySeconds + r / C) * 1000f:F0} ms");
                }
                AddReport(laid, w, ear, muzzle);
                parts.Add($"report {Loudness.MuzzleBlastDb(w):F0} dB +{Vector3.Distance(ear, muzzle) / C * 1000f:F0} ms");
                var mix = Mix(laid, 0.2f, 0.3f, out _);
                string file = $"{name}-{where}.wav";
                Write(dir, file, mix);
                string what = $"{w.DisplayName}, {face}, {degrees:F0} deg";
                string bounce = $"{hitSample.Velocity.Length():F0} m/s in, {o.Velocity.Length():F0} out at {Departure(o.Velocity):F1} deg off the face; tumbling {o.Slug.TumbleRadPerSec / (2 * MathF.PI):F0} rev/s; flies {range:F0} m";
                rows.Add($"| {file} | {what} | {bounce} | {string.Join("; ", parts)} | {PeakDb(mix):F1} |");
                Console.WriteLine($"  {file,-40} {PeakDb(mix),6:F1} dBFS  {bounce}");
            }
        }
        rows.Add("");

        // ── 3. A round striking each material ───────────────────────────────────────────────────
        rows.Add("## 3. A round striking each material, heard from 5 m and 30 m");
        rows.Add("");
        rows.Add("A 9 mm (Glock) from 10 m, and an AKM from 30 m on four of them. Walls are struck square at 1.2 m up, their chips landing on the concrete below; "
               + "the ground (dirt, grass, gravel, water, carpet) is struck at 45 degrees. The listener stands in front, at 5 or 30 m.");
        rows.Add("");
        rows.Add("| file | material, part | sounds (key, declared dB at 1 m) | length to -40 dB (ms) | peak dBFS |");
        rows.Add("|---|---|---|---|---|");
        var targets = new (string Material, string Part, Vector3 Size, bool Ground)[]
        {
            ("Concrete", "concrete wall 2 x 3 m, 20 cm", new(2f, 3f, 0.2f), false),
            ("Brick", "brick wall 2 x 3 m, 35 cm", new(2f, 3f, 0.35f), false),
            ("Marble", "stone wall 2 x 3 m, 20 cm", new(2f, 3f, 0.2f), false),
            ("Asphalt", "asphalt road", new(6f, 6f, 0.1f), true),
            ("Metal", "steel plate 1 x 1 m, 6 mm", new(1f, 1f, 0.006f), false),
            ("Metal", "the map's metal wall, 2 x 3 m, 5 cm", new(2f, 3f, 0.05f), false),
            ("Wood", "wooden door 0.9 x 2.1 m, 6 cm", new(0.9f, 2.1f, 0.06f), false),
            ("Plaster", "plaster wall 2 x 3 m, 12 cm (two 12.5 mm boards on studs)", new(2f, 3f, 0.12f), false),
            ("Dirt", "dirt ground", new(10f, 10f, 1f), true),
            ("Grass", "grass", new(10f, 10f, 1f), true),
            ("Gravel", "gravel", new(10f, 10f, 1f), true),
            ("Water", "water", new(10f, 10f, 1f), true),
            ("Carpet", "carpeted floor", new(4f, 4f, 0.02f), true),
        };
        var shots = new (WeaponDefinition W, float Range, string[] Only)[]
        {
            (WeaponRegistry.Glock, 10f, Array.Empty<string>()),
            (WeaponRegistry.Akm, 30f, new[] { "Concrete", "Metal", "Dirt", "Water" }),
        };
        int index = 0;
        foreach (var (w, shotRange, only) in shots)
            foreach (var (material, part, size, ground) in targets)
            {
                if (only.Length > 0 && !only.Contains(material)) continue;
                if (w == WeaponRegistry.Akm && part.StartsWith("the map", StringComparison.Ordinal)) continue;
                index++;
                float speed = FlyStraight(w, Vector3.Zero, Vector3.UnitZ, 0.5f, untilZ: shotRange)[^1].Velocity.Length();
                float graze = ground ? MathF.PI / 4f : MathF.PI / 2f;
                Vector3 at = ground ? Vector3.Zero : new Vector3(0f, 1.2f, 0f);
                Vector3 foot = ground ? at : new Vector3(0f, 0f, 0.3f);
                var hit = BulletImpact.From(material, speed, w, graze, 0f, size, ground ? material : "Concrete", ground ? 0f : 1.2f);
                foreach (float metres in new[] { 5f, 30f })
                {
                    Vector3 ear = new(0f, 1.7f, metres);
                    var laid = new List<(float At, float[] Pcm, float Gain)>();
                    var parts = new List<string>();
                    foreach (var s in BulletImpact.Sounds(hit, at, foot))
                    {
                        float r = Vector3.Distance(ear, s.Position);
                        laid.Add((r / C, Render(s, 1), Placed(s.LevelDb, r)));
                        parts.Add($"{Short(s.SynthKey)} {s.LevelDb:F0} dB");
                    }
                    var mix = Mix(laid, 0.1f, 0.2f, out float firstAt);
                    string slugName = part.StartsWith("the map", StringComparison.Ordinal) ? "metal-wall" : material.ToLowerInvariant();
                    string file = $"3{(char)('a' + index - 1)}-{w.Id}-{slugName}-{metres:F0}m.wav";
                    Write(dir, file, mix);
                    float length = LengthToMinus40(mix);
                    rows.Add($"| {file} | {w.DisplayName} at {speed:F0} m/s, {part} | {string.Join("; ", parts)} | {length:F0} | {PeakDb(mix):F1} |");
                    Console.WriteLine($"  {file,-34} {PeakDb(mix),6:F1} dBFS  {length,5:F0} ms  {string.Join("; ", parts)}");
                }
            }

        readme.Add("# Bullets, round 2, 2026-10-04");
        readme.Add("");
        readme.Add("Rendered by `OpenFPS.AudioLab --bullet-round2`. Every file is at ONE shared gain: the game's own full scale, each sound placed by the "
                 + "game's loudness law at the shipped compression and its distance. Never normalised per file. Dry and mono.");
        readme.Add("");
        readme.AddRange(rows);
        File.WriteAllLines(Path.Combine(dir, "README-tables.md"), readme);
        Console.WriteLine();
        foreach (var g in RawPeaks.GroupBy(r => r.Key))
            Console.WriteLine($"  full-scale use {g.Key,-18} raw peak {g.Max(r => r.Raw),5:F2} of full scale (min {g.Min(r => r.Raw):F2})");
        return 0;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static string Short(string key)
    {
        if (key.StartsWith(BulletImpact.HitPrefix, StringComparison.Ordinal)) return "hit:" + key.Split(':')[2];
        if (key.StartsWith(BulletImpact.DebrisPrefix, StringComparison.Ordinal)) return "debris:" + key.Split(':')[2];
        if (key.StartsWith(Ricochet.WhinePrefix, StringComparison.Ordinal)) return "whine:" + key.Split(':')[^1];
        if (key.StartsWith(BulletFlyby.WhizzPrefix, StringComparison.Ordinal)) return "whizz:" + key.Split(':')[^2];
        return key;
    }

    private static float Departure(Vector3 v) => MathF.Asin(Math.Clamp(Vector3.Normalize(v).Y, -1f, 1f)) * 180f / MathF.PI;

    private static float[] Render(TransientSound s, int seed)
    {
        if (BulletFlyby.TryParseCrack(s.SynthKey, out float T)) return BulletFlyby.RenderCrack(T, Sr);
        if (BulletFlyby.TryParseWhizz(s.SynthKey, out var wz)) return BulletFlyby.RenderWhizz(wz, Sr, seed);
        float raw = 0f;
        float[] pcm = BulletImpact.TryParseHit(s.SynthKey, out var h) ? BulletImpact.RenderHit(h, Sr, seed, out raw)
                    : BulletImpact.TryParseDebris(s.SynthKey, out var d) ? BulletImpact.RenderDebris(d, Sr, seed, out raw)
                    : Ricochet.TryParseWhine(s.SynthKey, out var wh) ? Ricochet.RenderWhine(wh, Sr, seed, out raw)
                    : Array.Empty<float>();
        if (raw > 0f) RawPeaks.Add((Short(s.SynthKey), raw, pcm.Length > 0 ? pcm.Max(MathF.Abs) : 0f));
        return pcm;
    }

    /// <summary>Each render's loudest sample against its full scale, before and after the clamp.</summary>
    private static readonly List<(string Key, float Raw, float Peak)> RawPeaks = new();

    private static void AddReport(List<(float At, float[] Pcm, float Gain)> laid, WeaponDefinition w, Vector3 ear, Vector3 muzzle)
    {
        float r = Vector3.Distance(ear, muzzle);
        laid.Add((r / C, WeaponSynth.MuzzleBlast(WeaponProfile.From(w), 1), Placed(Loudness.MuzzleBlastDb(w), r)));
    }

    /// <summary>The game's gain for a sound of this declared level heard this far off: its placement,
    /// then the engine's inverse law beyond the reference distance.</summary>
    private static float Placed(float levelDb, float distance)
    {
        var (gain, reference) = Loudness.Place(levelDb);
        return gain * MathF.Min(1f, reference / MathF.Max(0.01f, distance));
    }

    /// <summary>Lays the sounds down from <paramref name="lead"/> before the first, to
    /// <paramref name="tail"/> after the last.</summary>
    private static float[] Mix(List<(float At, float[] Pcm, float Gain)> laid, float lead, float tail, out float first)
    {
        first = laid.Count == 0 ? 0f : laid.Min(l => l.At);
        float origin = MathF.Min(0f, first - lead);
        if (first - lead > 0f && laid.Count > 0) origin = first - lead;
        float end = laid.Count == 0 ? 0.1f : laid.Max(l => l.At + l.Pcm.Length / (float)Sr) + tail;
        var mix = new float[Math.Max(1, (int)((end - origin) * Sr))];
        foreach (var (at, pcm, gain) in laid)
        {
            int o = (int)MathF.Round((at - origin) * Sr);
            for (int i = 0; i < pcm.Length && o + i < mix.Length; i++) if (o + i >= 0) mix[o + i] += pcm[i] * gain;
        }
        return mix;
    }

    private static float[] Trim(float[] x)
    {
        float peak = x.Max(MathF.Abs);
        int a = Array.FindIndex(x, v => MathF.Abs(v) > peak * 0.01f), b = Array.FindLastIndex(x, v => MathF.Abs(v) > peak * 0.01f);
        return a < 0 ? x : x[a..(b + 1)];
    }

    private static float PeakDb(float[] x) => 20f * MathF.Log10(MathF.Max(1e-9f, x.Max(MathF.Abs)));

    /// <summary>From the first sample within 40 dB of the peak to the last, ms (10 ms rms windows).</summary>
    private static float LengthToMinus40(float[] x)
    {
        int win = Sr / 100;
        var rms = new List<float>();
        for (int i = 0; i + win <= x.Length; i += win / 2)
        {
            double s = 0;
            for (int j = 0; j < win; j++) s += x[i + j] * x[i + j];
            rms.Add((float)Math.Sqrt(s / win));
        }
        if (rms.Count == 0) return 0f;
        float top = rms.Max();
        int a = rms.FindIndex(r => r > top * 0.01f), b = rms.FindLastIndex(r => r > top * 0.01f);
        return (b - a + 1) * win / 2f * 1000f / Sr;
    }

    /// <summary>A round flown along <paramref name="dir"/> from <paramref name="from"/> in still air,
    /// sampled every 10 ms, for <paramref name="seconds"/> or until it reaches a height or a range.</summary>
    private static List<FlightSample> FlyStraight(WeaponDefinition w, Vector3 from, Vector3 dir, float seconds,
                                                  float untilY = float.NegativeInfinity, float untilZ = float.PositiveInfinity)
    {
        var s = new BulletState { Position = from, Velocity = Vector3.Normalize(dir) * w.MuzzleVelocity };
        var path = new List<FlightSample> { new(0f, s.Position, s.Velocity) };
        float bc = ExternalBallistics.CoefficientOf(w);
        while (s.Seconds < seconds)
        {
            var before = s;
            ExternalBallistics.Advance(ref s, 0.001f, bc, Still, Vector3.Zero);
            if (s.Position.Y <= untilY || s.Position.Z >= untilZ)
            {
                float u = s.Position.Y <= untilY
                    ? (before.Position.Y - untilY) / MathF.Max(1e-6f, before.Position.Y - s.Position.Y)
                    : (untilZ - before.Position.Z) / MathF.Max(1e-6f, s.Position.Z - before.Position.Z);
                path.Add(new FlightSample(before.Seconds + u * 0.001f, Vector3.Lerp(before.Position, s.Position, u), Vector3.Lerp(before.Velocity, s.Velocity, u)));
                return path;
            }
            if (MathF.Round(s.Seconds * 1000f) % 10 == 0) path.Add(new FlightSample(s.Seconds, s.Position, s.Velocity));
        }
        return path;
    }

    private static List<FlightSample> FlySlug(Slug slug, Vector3 from, Vector3 velocity, float seconds, float maxRange)
    {
        var s = new BulletState { Position = from, Velocity = velocity, Seconds = seconds };
        var path = new List<FlightSample> { new(seconds, from, velocity) };
        while (s.Seconds < seconds + 2.5f && s.Velocity.Length() > 15f && s.Travelled < maxRange && s.Position.Y > -0.001f)
        {
            Ricochet.Advance(ref s, 0.01f, slug, Still, Vector3.Zero);
            path.Add(new FlightSample(s.Seconds, s.Position, s.Velocity));
        }
        return path;
    }

    private static void Write(string dir, string file, float[] samples)
    {
        using var fs = new FileStream(Path.Combine(dir, file), FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        int bytes = samples.Length * 2;
        w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Sr); w.Write(Sr * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(bytes);
        foreach (float v in samples) w.Write((short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue));
    }

    /// <summary>
    /// Round one's whizz renderer, word for word: 96 sine partials, log-normal about the shedding
    /// frequency, each wandering. Kept in the lab only, as the "before" of the before and after.
    /// </summary>
    private static class LegacyWhizz
    {
        private const float Crossfade = 0.0015f;

        private static (float From, float To) Stretch(BulletFlyby.Whizz w)
        {
            float span = Math.Clamp(6f * w.Miss, 5f, 40f);
            return (-MathF.Min(span, w.BeforeMetres), MathF.Min(span, w.AfterMetres));
        }

        private static float Heard(BulletFlyby.Whizz w, float x) => x / w.V + MathF.Sqrt(w.Miss * w.Miss + x * x) / w.SpeedOfSound;

        private static (float A, float B, float C, float D) Joins(BulletFlyby.Whizz w)
        {
            var (from, to) = Stretch(w);
            float b = w.Miss;
            return (from, Math.Clamp(-b, from, to), Math.Clamp(b, from, to), to);
        }

        private static float Envelope(BulletFlyby.Whizz w, float x)
        {
            float b = w.Miss, r = MathF.Sqrt(b * b + x * x);
            float mach = w.V / w.SpeedOfSound;
            float doppler = 1f / (1f + mach * x / r);
            float u = MathF.Max(1f, w.Speed);
            float atOneMetre = w.Density * u * u * u * w.Length * BulletFlyby.FluctuatingLift * BulletFlyby.Strouhal
                             / (4f * MathF.Sqrt(2f) * w.SpeedOfSound);
            return atOneMetre * (b / r) / r * doppler * doppler;
        }

        private static float Step(float t, float at)
        {
            if (t <= at - Crossfade) return 0f;
            if (t >= at + Crossfade) return 1f;
            return 0.5f - 0.5f * MathF.Cos(MathF.PI * (t - at + Crossfade) / (2f * Crossfade));
        }

        public static float[] Render(BulletFlyby.Whizz w, int sampleRate, int seed)
        {
            if (!BulletFlyby.TryPiece(w, out float start, out float end, out float xk)) return new float[16];
            float b = w.Miss, v = w.V, c = w.SpeedOfSound, mach = v / c;
            float rk = MathF.Sqrt(b * b + xk * xk);
            float fullScale = BulletFlyby.WhizzFullScalePascals(w);
            var (_, jb, jc, _) = Joins(w);
            float join1 = Heard(w, jb), join2 = Heard(w, jc);
            var rng = new Random(HashCode.Combine(w.Speed, w.MissDm, w.DiameterTenthMm, w.LengthTenthMm, w.SoundSpeed, w.BeforeMetres, w.AfterMetres, seed & 3));
            const int partials = 96;
            float f0 = BulletFlyby.Strouhal * MathF.Max(1f, w.Speed) / w.Diameter;
            var freq = new float[partials]; var phase = new float[partials];
            var depth = new float[partials]; var rate = new float[partials]; var wobble = new float[partials];
            for (int k = 0; k < partials; k++)
            {
                double g = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
                freq[k] = f0 * MathF.Pow(2f, (float)Math.Clamp(0.5 * g, -1.5, 1.5));
                phase[k] = (float)(rng.NextDouble() * 2 * Math.PI);
                rate[k] = freq[k] * (0.03f + 0.05f * (float)rng.NextDouble());
                depth[k] = 2.5f;
                wobble[k] = (float)(rng.NextDouble() * 2 * Math.PI);
            }
            float amp = MathF.Sqrt(2f / partials);
            float spin = MathF.Max(1f, w.Speed) / (30f * w.Diameter);
            float flutterHz = spin / 8f, flutterPhase = (float)(rng.NextDouble() * 2 * Math.PI);
            int n = Math.Max(16, (int)MathF.Ceiling((end - start) * sampleRate));
            var pcm = new float[n];
            float c2 = c * c, v2 = v * v;
            for (int i = 0; i < n; i++)
            {
                float t = start + i / (float)sampleRate;
                float tau = (c2 * t - MathF.Sqrt(c2 * v2 * t * t + b * b * (c2 - v2))) / (c2 - v2);
                float x = v * tau;
                float r = MathF.Sqrt(b * b + x * x);
                float doppler = 1f / (1f + mach * x / r);
                float carrier = 0f;
                for (int k = 0; k < partials; k++)
                {
                    float heardHz = freq[k] * doppler;
                    if (heardHz > 16000f || heardHz < 30f) continue;
                    float fade = heardHz < 12000f ? 1f : 0.5f + 0.5f * MathF.Cos(MathF.PI * (heardHz - 12000f) / 4000f);
                    float p = 2f * MathF.PI * freq[k] * tau + phase[k] + depth[k] * MathF.Sin(2f * MathF.PI * rate[k] * tau + wobble[k]);
                    carrier += fade * MathF.Sin(p);
                }
                carrier *= amp;
                float flutter = 1f + 0.3f * MathF.Sin(2f * MathF.PI * flutterHz * tau + flutterPhase);
                float s1 = Step(t, join1), s2 = Step(t, join2);
                float weight = w.Piece switch { 0 => 1f - s1, 1 => s1 - s2, _ => s2 };
                pcm[i] = Math.Clamp(Envelope(w, x) * carrier * flutter * rk / fullScale * weight, -1f, 1f);
            }
            return pcm;
        }
    }
}
