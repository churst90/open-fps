using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// Thunder measured before anyone listens: ground and cloud flashes at 0.1, 0.5, 1, 3, 8 and 15 km,
/// heard in an open field and in a city street, each rendered by the same code the client plays.
///
/// --thunder [out=DIR] [seed=N] [city=x,z] [nowav]
///
/// For every case: the peak level at the ear (dB SPL, from the pressure the model computes), how long
/// it lasts above -20 dB of its loudest (125 ms Fast envelope), the spectral centroid, the 1/3-octave
/// band it peaks in, octave-band levels over that span, and the level the game would play it at under
/// the loudness law. Then binaural WAVs: each part of the thunder through Steam Audio's HRTF from its
/// own direction, once at the level the game would place it (one gain for the whole set) and once
/// normalised, for its colour at every distance.
/// </summary>
public static class ThunderSpike
{
    private const int OutRate = 48000;
    private const float ProxyMetres = OpenFPS.Client.Core.WorldAudioPlayer.SkyProxyMetres;

    public static int Run(string[] args)
    {
        string? S(string k) => args.FirstOrDefault(a => a.StartsWith(k + "=", StringComparison.Ordinal))?[(k.Length + 1)..];
        string outDir = S("out") ?? Path.Combine(LabPaths.Repo, "inbox", "thunder-2026-10-05");
        int seed = int.TryParse(S("seed"), out int sd) ? sd : 7;
        bool wav = !args.Contains("nowav");
        Directory.CreateDirectory(outDir);
        AcousticRegistry.Initialize();

        var air = new Thunder.Air(15f, 0.8f, 1013.25f, Vector3.Zero);
        var report = new List<string>();
        void Say(string s) { Console.WriteLine(s); report.Add(s); }

        Say("THUNDER, measured from the model (OpenFPS.Client.Core/AudioEngine/Core/Thunder.cs)");
        Say($"air {air.TemperatureC} C, {air.Humidity * 100:F0} % RH, still; listener 1.7 m up, ground reflection {Thunder.GroundReflection}; seed {seed}");
        Say($"source: {Thunder.SourcePa} Pa at {Thunder.SourceMetres} m for a {Thunder.SourcePeakHz} Hz wave (E = {Thunder.SourceEnergyPerMetre():0} J/m); first strokes here take {LightningPhysics.GroundFlashEnergyMedian:0} J/m");
        Say($"Few's peak frequency at that energy: {LightningPhysics.PeakFrequencyHz(LightningPhysics.GroundFlashEnergyMedian):F0} Hz; N-wave {LightningPhysics.NWaveSeconds(LightningPhysics.GroundFlashEnergyMedian) * 1000:F2} ms");
        float c = AudioPhysics.SpeedOfSoundAt(air.TemperatureC);
        float a2 = Thunder.AmplitudeAt2m(LightningPhysics.GroundFlashEnergyMedian, c);
        float t0 = LightningPhysics.NWaveSeconds(LightningPhysics.GroundFlashEnergyMedian, c);
        Say($"N-wave lengthening (weak shock): x{Thunder.LengthenedSeconds(t0, a2, 1000f, c) / t0:F3} at 1 km, x{Thunder.LengthenedSeconds(t0, a2, 10000f, c) / t0:F3} at 10 km");
        Say($"shadow edge, still air: source 1 km up {Thunder.ShadowEdgeMetres(1000f, 15f) / 1000:F1} km, 3 km up {Thunder.ShadowEdgeMetres(3000f, 15f) / 1000:F1} km, 5 km up {Thunder.ShadowEdgeMetres(5000f, 15f) / 1000:F1} km");
        Say("");

        // ── The channel: does it come out as Hill measured? ────────────────────────────────────
        {
            var defl = new List<float>(); var ratio = new List<float>(); var len = new List<float>();
            for (int i = 0; i < 200; i++)
            {
                var s = Ground(1000f, 45f, seed + i, 4);
                var ch = LightningChannel.Build(s);
                defl.Add(ch.MeanDeflectionDegrees());
                float mainLen = 0f; var m = ch.Paths[0];
                for (int k = 1; k < m.Length; k++) mainLen += Vector3.Distance(m[k - 1], m[k]);
                ratio.Add(mainLen / Vector3.Distance(s.From, s.To));
                len.Add(ch.TotalLength());
            }
            Say($"channel, 200 ground flashes: mean deflection {defl.Average():F1} deg (Hill 1968: 16.3); main channel {ratio.Average():F2} x the straight line; total with branches {len.Average() / 1000:F1} km (Lacroix 2019: about 8 km of return stroke for a 5 km drop)");
            Say("");
        }

        var ear = new Vector3(0f, 1.7f, 0f);
        var distances = new[] { 100f, 500f, 1000f, 3000f, 8000f, 15000f };
        // ── The storm: how often, and what ───────────────────────────────────────────────────────
        {
            var sched = new LightningSchedule(seed);
            var got = new List<LightningStrike>();
            var sky = new StormSky(OpenFPS.Common.Components.WeatherType.Storm, 1f, new Vector3(12f, 0f, -6f));
            for (float t = 0f; t < 10f * 3600f; t += 1f) sched.Advance(1f, sky, got);
            var cg = got.Where(s => s.Kind == FlashKind.CloudToGround).ToList();
            var near = cg.Select(s => new Vector2(s.To.X, s.To.Z).Length()).ToList();
            Say($"ten hours of Storm: {got.Count / 600f:F2} flashes a minute (cell peak {LightningPhysics.StormCellPeakFlashesPerMinute}, mean over a cell's life half that); "
              + $"{cg.Count * 100f / Math.Max(1, got.Count):F0} % to ground; {cg.Average(s => s.Strokes):F1} strokes a ground flash; ground strikes within 1 km of the map centre {near.Count(d => d < 1000f)}, "
              + $"1-5 km {near.Count(d => d >= 1000f && d < 5000f)}, 5-15 km {near.Count(d => d >= 5000f && d < 15000f)}, further {near.Count(d => d >= 15000f)}");
            Say("");
        }

        Say("open field                         peak     Lpk    span-20  centroid  peak1/3   LAeq   octave Leq over the span, dB SPL                         game peak dBFS  render");
        Say("case                               Pa       dB     s        Hz        Hz        dB     16   31   63   125  250  500  1k   2k   4k   8k       45%    100%    ms");
        var cases = new List<(string Name, LightningStrike Strike)>();
        foreach (float d in distances) cases.Add(($"cg-{Km(d)}", Ground(d, 45f, seed, 4)));
        foreach (float d in distances) cases.Add(($"ic-{Km(d)}", Cloud(d, 45f, seed)));

        var rendered = new List<(string Name, List<Thunder.Part> Parts)>();
        foreach (var (name, strike) in cases)
        {
            var sw = Stopwatch.StartNew();
            var parts = Thunder.Render(strike, ear, air);
            sw.Stop();
            rendered.Add((name, parts));
            Say(Row(name, parts, sw.ElapsedMilliseconds));
        }
        Say("");

        // A straight vertical channel 100 m away, no ground: the cylinder Lacroix normalises against.
        {
            var straight = new LightningStrike(seed, FlashKind.CloudToGround, new Vector3(100f, 5000f, 0f), new Vector3(100f, 0f, 0f), LightningPhysics.GroundFlashEnergyMedian, 1);
            var ch = new LightningChannel();
            var line = new List<Vector3>();
            for (float y = 0f; y <= 5000f; y += LightningPhysics.StepMetres) line.Add(new Vector3(100f, y, 0f));
            ch.Paths.Add(line.ToArray()); ch.EnergyShares.Add(1f);
            foreach (float r in new[] { 2f, 10f, 100f, 1000f })
            {
                var e2 = new Vector3(100f - r, 1.7f, 0f);
                var parts = Thunder.Render(straight, ch, e2, air, new Thunder.Options { Ground = false, MaxParts = 1, SampleRate = 48000, Plain = true });
                float pk = parts.Count > 0 ? parts.Max(p => p.PeakPa) : 0f;
                Say($"straight vertical channel, single stroke, free field, {r,5:F0} m: peak {pk,8:F2} Pa ({Db(pk):F1} dB); cylinder from 650 Pa at 2 m would be {650f * MathF.Sqrt(2f / r):F1} Pa");
            }
            Say("");
        }

        // ── A city street ───────────────────────────────────────────────────────────────────────
        string? at = S("city") ?? "8,-67";
        var cityRows = new List<(string Name, float[] L, float[] R)>();
        WorldSnapshot? world = null;
        try
        {
            string root = LabPaths.Server() + Path.DirectorySeparatorChar;
            (world, _) = SirenRouteSpike.Load(root + "maps/city.json", root + "prefabs", "none", 0f);
        }
        catch (Exception ex) { Say($"city: could not load ({ex.Message}); skipped"); }
        if (world != null)
        {
            var xz = at.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var cityEar = new Vector3(xz[0], 1.7f, xz[1]);
            var boxes = SteamAudioScene.BoxesFromWorld(world);
            var solids = boxes.Select(b => new EarlyReflections.Solid(b.Center, b.Size, b.Rotation, b.Material)).ToList();
            Say($"city street at ({xz[0]}, {xz[1]}) (Main Street canyon), {solids.Count} solids: each part placed {ProxyMetres:F0} m out in its direction, as the client places it;");
            Say("  blocked parts take the diffraction over the edge (Maekawa bands); copies as the client gives a sky sound: the strongest "
              + $"{OpenFPS.Client.Core.WorldAudioPlayer.MaxSkyRoomEchoes} mirrors inside 80 ms and up to two first-order facades after it.");
            foreach (float d in new[] { 100f, 1000f, 3000f, 8000f })
            {
                var strike = Ground(d, 45f, seed, 4).Offset(new Vector3(cityEar.X, 0f, cityEar.Z));
                var parts = Thunder.Render(strike, cityEar, air);
                var (l, r, notes) = CityBinaural(parts, cityEar, solids, c);
                cityRows.Add(($"city-cg-{Km(d)}", l, r));
                Say($"  cg {Km(d),6}: {notes}");
            }
            Say("");
        }

        // ── WAVs ─────────────────────────────────────────────────────────────────────────────────
        if (wav)
        {
            // One gain for the set at game level: the loudest file's peak at -1 dBFS.
            var gameSet = new List<(string Name, float[] L, float[] R)>();
            foreach (var (name, parts) in rendered)
            {
                var (l, r) = Binaural(parts, gameLevel: true);
                gameSet.Add((name, l, r));
            }
            gameSet.AddRange(cityRows);
            float loud = 1e-9f;
            foreach (var (_, l, r) in gameSet) loud = MathF.Max(loud, MathF.Max(l.Max(MathF.Abs), r.Max(MathF.Abs)));
            float g = 0.89f / loud;
            foreach (var (name, l, r) in gameSet)
                WriteStereo(Path.Combine(outDir, $"{name}-game-level-binaural.wav"), l, r, g);
            foreach (var (name, parts) in rendered)
            {
                var (l, r) = Binaural(parts, gameLevel: false);
                float pk = MathF.Max(1e-9f, MathF.Max(l.Max(MathF.Abs), r.Max(MathF.Abs)));
                WriteStereo(Path.Combine(outDir, $"{name}-normalised-binaural.wav"), l, r, 0.89f / pk);
            }
            Say($"game-level set: one gain for every file ({20f * MathF.Log10(g):F1} dB), so the files keep the game's level differences; the normalised set brings each to -1 dBFS.");
        }
        File.WriteAllLines(Path.Combine(outDir, "measurements.txt"), report);
        Console.WriteLine($"\nwritten to {outDir}");
        return 0;
    }

    // ── Strikes ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A ground flash striking <paramref name="d"/> metres away on a bearing (degrees east of north).</summary>
    internal static LightningStrike Ground(float d, float bearing, int seed, int strokes)
    {
        float b = bearing * MathF.PI / 180f;
        var to = new Vector3(d * MathF.Sin(b), 0f, d * MathF.Cos(b));
        var rng = new Random(seed);
        var from = to + new Vector3(LightningPhysics.Gaussian(rng) * 800f, 5000f, LightningPhysics.Gaussian(rng) * 800f);
        return new LightningStrike(seed, FlashKind.CloudToGround, from, to, LightningPhysics.GroundFlashEnergyMedian, strokes);
    }

    /// <summary>A cloud flash centred <paramref name="d"/> metres away (horizontally), from 6 km up to 9 km, 6 km across the bearing.</summary>
    internal static LightningStrike Cloud(float d, float bearing, int seed)
    {
        float b = bearing * MathF.PI / 180f;
        var mid = new Vector3(d * MathF.Sin(b), 0f, d * MathF.Cos(b));
        var across = new Vector3(MathF.Cos(b), 0f, -MathF.Sin(b)) * 3000f;
        // From the lower charge (6 km) to the upper (9 km), 6 km apart across the ground.
        return new LightningStrike(seed, FlashKind.IntraCloud, mid - across + new Vector3(0f, 6000f, 0f), mid + across + new Vector3(0f, 9000f, 0f),
                                   LightningPhysics.GroundFlashEnergyMedian * LightningPhysics.CloudFlashEnergyShare, 1);
    }

    private static string Km(float d) => d < 1000f ? $"{d / 1000f:0.0}km" : $"{d / 1000f:0}km";

    // ── Measurement ─────────────────────────────────────────────────────────────────────────

    private static float Db(float pa) => 20f * MathF.Log10(MathF.Max(1e-9f, pa) / Thunder.ReferencePa);

    private static readonly float[] Octaves = { 16f, 31.5f, 63f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f };
    private static readonly float[] AWeight = { -56.7f, -39.4f, -26.2f, -16.1f, -8.6f, -3.2f, 0f, 1.2f, 1.0f, -1.1f };

    internal readonly record struct Measures(float PeakPa, float Span20, float Centroid, float PeakThird, float LAeq, float[] OctaveDb, float GameDbfs);

    internal static Measures Measure(List<Thunder.Part> parts)
    {
        var (p, fs, _) = Thunder.Mix(parts);
        if (p.Length == 0) return new Measures(0, 0, 0, 0, 0, new float[Octaves.Length], -999f);
        float peak = p.Max(MathF.Abs);
        // Fast (125 ms) envelope on p^2.
        var env = new float[p.Length];
        float a = 1f - MathF.Exp(-1f / (0.125f * fs)), e = 0f, emax = 0f;
        for (int i = 0; i < p.Length; i++) { e += a * (p[i] * p[i] - e); env[i] = e; emax = MathF.Max(emax, e); }
        int first = Array.FindIndex(env, v => v >= emax * 0.01f), last = Array.FindLastIndex(env, v => v >= emax * 0.01f);
        float span = (last - first) / (float)fs;
        // Welch over the span.
        int n = 8192; while (n > Math.Max(256, last - first)) n >>= 1;
        var psd = new double[n / 2 + 1]; int segs = 0;
        var win = new double[n];
        for (int i = 0; i < n; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
        double wsum = win.Sum(w => w * w);
        for (int s = first; s + n <= Math.Max(first + n, last) && s + n <= p.Length; s += n / 2)
        {
            var buf = new System.Numerics.Complex[n];
            for (int i = 0; i < n; i++) buf[i] = p[s + i] * win[i];
            Spectrum.Fft(buf);
            for (int k = 0; k <= n / 2; k++) psd[k] += buf[k].Magnitude * buf[k].Magnitude;
            segs++;
        }
        if (segs == 0) return new Measures(peak, span, 0, 0, 0, new float[Octaves.Length], -999f);
        // Mean square pressure per bin over the span: one-sided, Hann-corrected.
        double binHz = fs / (double)n;
        for (int k = 0; k <= n / 2; k++) psd[k] = psd[k] / segs / wsum * (k == 0 || k == n / 2 ? 1 : 2) / n * n;
        double num = 0, den = 0;
        for (int k = 1; k <= n / 2; k++) { double f = k * binHz; if (f < 10) continue; num += f * psd[k]; den += psd[k]; }
        float centroid = (float)(num / Math.Max(1e-30, den));
        float BandDb(double lo, double hi)
        {
            double sum = 0;
            for (int k = 1; k <= n / 2; k++) { double f = k * binHz; if (f >= lo && f < hi) sum += psd[k]; }
            return (float)(10 * Math.Log10(Math.Max(1e-30, sum) / (Thunder.ReferencePa * Thunder.ReferencePa)));
        }
        var oct = Octaves.Select(f => BandDb(f / Math.Sqrt(2), f * Math.Sqrt(2))).ToArray();
        double la = 0;
        for (int i = 0; i < oct.Length; i++) la += Math.Pow(10, (oct[i] + AWeight[i]) / 10);
        float bestThird = 0f, bestDb = -999f;
        for (int i = 0; i < 30; i++)
        {
            double fc = 10 * Math.Pow(2, i / 3.0);
            if (fc * 1.12 > fs / 2) break;
            float db = BandDb(fc / 1.12, fc * 1.12);
            if (db > bestDb) { bestDb = db; bestThird = (float)fc; }
        }
        // The game's level: each part placed ProxyMetres out at its own peak level, through the law.
        float game = 0f;
        foreach (var part in parts) game = MathF.Max(game, GameGain(part));
        return new Measures(peak, span, centroid, bestThird, (float)(10 * Math.Log10(Math.Max(1e-30, la))), oct, 20f * MathF.Log10(MathF.Max(1e-9f, game)));
    }

    /// <summary>What the game plays a part's peak at, linear full scale: the level declared for the
    /// proxy (its peak at the ear, carried out to ProxyMetres), through Loudness.Place and the mixer's
    /// distance law at ProxyMetres. The same arithmetic WorldAudioPlayer does.</summary>
    internal static float GameGain(Thunder.Part part)
    {
        float level = WorldAudioPlayerThunderLevel(part.PeakDb);
        var placed = Loudness.Place(level);
        return Loudness.RenderedGain(placed.Gain, placed.ReferenceDistance, MathF.Min(3000f, Loudness.AudibleRange(level)), ProxyMetres);
    }

    internal static float WorldAudioPlayerThunderLevel(float peakDbAtEar) => OpenFPS.Client.Core.WorldAudioPlayer.SkyLevelDb(peakDbAtEar);

    private static string Row(string name, List<Thunder.Part> parts, long ms)
    {
        var m = Measure(parts);
        string oct = string.Join(" ", m.OctaveDb.Select(v => v < 0 ? "  - " : $"{v,4:F0}"));
        // The same at /levels 100 (no compression), where the ceiling falls to about 89 dB at the ear.
        float shipped = Loudness.DynamicRangeCompression;
        Loudness.DynamicRangeCompression = 1f;
        float real = 0f;
        foreach (var part in parts) real = MathF.Max(real, GameGain(part));
        Loudness.DynamicRangeCompression = shipped;
        return $"{name,-12} parts {parts.Count}, first {(parts.Count > 0 ? parts.Min(p => p.StartSeconds) : 0f),5:F1} s  "
             + $"{m.PeakPa,8:F3} {Db(m.PeakPa),6:F1} {m.Span20,7:F1}  {m.Centroid,7:F0}  {m.PeakThird,7:F0}  {m.LAeq,6:F1}  {oct}  {m.GameDbfs,6:F1} {20f * MathF.Log10(MathF.Max(1e-9f, real)),6:F1}  {ms,5}";
    }

    // ── Binaural ────────────────────────────────────────────────────────────────────────────

    private static float[] To48k(Thunder.Part p)
    {
        if (p.SampleRate == OutRate) return p.Pressure;
        int n = (int)((long)p.Pressure.Length * OutRate / p.SampleRate);
        var y = new float[n];
        double step = p.SampleRate / (double)OutRate;
        for (int i = 0; i < n; i++)
        {
            double x = i * step; int k = (int)x; float f = (float)(x - k);
            float a = p.Pressure[Math.Min(k, p.Pressure.Length - 1)], b = p.Pressure[Math.Min(k + 1, p.Pressure.Length - 1)];
            y[i] = a + (b - a) * f;
        }
        return y;
    }

    /// <summary>Every part through the HRTF from its own direction, listener facing north.</summary>
    private static (float[] L, float[] R) Binaural(List<Thunder.Part> parts, bool gameLevel)
    {
        float start = parts.Count == 0 ? 0f : parts.Min(p => p.StartSeconds);
        float end = parts.Count == 0 ? 1f : parts.Max(p => p.StartSeconds + p.Seconds);
        end = MathF.Min(end, start + 45f);
        int n = (int)((end - start + 0.5f) * OutRate);
        var l = new float[n]; var r = new float[n];
        foreach (var p in parts)
        {
            var mono = To48k(p);
            float g = gameLevel ? GameGain(p) / p.PeakPa : 1f;
            var (pl, pr) = Hrtf(mono, p.Direction);
            int off = (int)((p.StartSeconds - start) * OutRate);
            for (int i = 0; i < pl.Length && off + i < n; i++) { l[off + i] += pl[i] * g; r[off + i] += pr[i] * g; }
        }
        return (l, r);
    }

    private static (float[] L, float[] R, string Notes) CityBinaural(List<Thunder.Part> parts, Vector3 ear, List<EarlyReflections.Solid> solids, float c)
    {
        float start = parts.Count == 0 ? 0f : parts.Min(p => p.StartSeconds);
        float end = parts.Count == 0 ? 1f : parts.Max(p => p.StartSeconds + p.Seconds) + 1f;
        end = MathF.Min(end, start + 45f);
        int n = (int)((end - start + 0.5f) * OutRate);
        var l = new float[n]; var r = new float[n];
        var notes = new List<string>();
        var into = new List<EarlyReflections.Arrival>();
        foreach (var p in parts)
        {
            var mono = To48k(p);
            float g = GameGain(p) / p.PeakPa;
            var proxy = ear + p.Direction * ProxyMetres;
            // Blocked on the way? The worst single box's path difference, and where the sound leaves it.
            float worst = 0f; Vector3 edge = proxy;
            foreach (var s in solids)
                if (Diffraction.PathDifferenceAroundBox(s.Center, s.Size, s.Rotation, proxy, ear, out float pd, out var e) && pd > worst)
                { worst = pd; edge = e; }
            var (lo, mid, hi) = worst > 0f ? Diffraction.BandGains(worst, c) : (1f, 1f, 1f);
            var direct = ThreeBand(mono, lo, mid, hi);
            var dir = Vector3.Normalize(edge - ear);
            var (pl, pr) = Hrtf(direct, dir);
            int off = (int)((p.StartSeconds - start) * OutRate);
            for (int i = 0; i < pl.Length && off + i < n; i++) { l[off + i] += pl[i] * g; r[off + i] += pr[i] * g; }
            string elev = $"{MathF.Asin(p.Direction.Y) * 180f / MathF.PI:F0} deg up";
            string note = worst > 0f ? $"part {elev} blocked, {worst:F1} m over the edge (bands {Db20(lo):F0}/{Db20(mid):F0}/{Db20(hi):F0} dB)" : $"part {elev} in plain view";
            // The copies the client gives a sky sound: the strongest mirrors inside the room window
            // (WorldAudioPlayer.PlanRoomEchoes, at most MaxSkyRoomEchoes, no washes), and up to two
            // first-order facades later than that. Each is placed at its image and the mixer's 1/r
            // beyond the 40 m reference does the rest, as in the game.
            float trim = OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.CopiesTrim;
            EarlyReflections.Find(proxy, ear, solids, into, c, maxOrder: 2, keep: OpenFPS.Client.Core.WorldAudioPlayer.MaxRoomEchoes * 2,
                                  maxExtraPathMetres: OpenFPS.Client.Core.WorldAudioPlayer.RoomEchoWindowSeconds * c);
            var plan = new List<OpenFPS.Client.Core.WorldAudioPlayer.RoomEcho>();
            OpenFPS.Client.Core.WorldAudioPlayer.PlanRoomEchoes(into, proxy, ear, ProxyMetres, ProxyMetres, trim, audible: true, plan);
            var echoes = new List<(EarlyReflections.Arrival A, float Rel)>();
            foreach (var e in plan)
            {
                if (e.InVoice || e.MirrorGain < ImageSource.MinGain) continue;
                echoes.Add((e.Arrival, e.MirrorGain * ProxyMetres / MathF.Max(ProxyMetres, e.Arrival.PathLength)));
                if (echoes.Count >= OpenFPS.Client.Core.WorldAudioPlayer.MaxSkyRoomEchoes) break;
            }
            EarlyReflections.Find(proxy, ear, solids, into, c, maxOrder: 1, separateFirst: true);
            int late = 0;
            foreach (var a in into.OrderByDescending(a => a.GainMid))
            {
                if (a.ExtraDelaySeconds <= OpenFPS.Client.Core.WorldAudioPlayer.RoomEchoWindowSeconds || a.GainMid < ImageSource.EchoAudibleRatio) continue;
                float placed = EarlyReflections.PlacedCopyGain(a.GainMid, a.PathLength, ProxyMetres, ProxyMetres) * trim;
                if (placed < ImageSource.MinGain) continue;
                echoes.Add((a, placed * ProxyMetres / MathF.Max(ProxyMetres, a.PathLength)));
                if (++late >= 2) break;
            }
            foreach (var (a, rel) in echoes)
            {
                var eDir = Vector3.Normalize(a.ImagePosition - ear);
                var copy = ThreeBand(mono, a.GainLow / MathF.Max(1e-4f, a.GainMid), 1f, a.GainHigh / MathF.Max(1e-4f, a.GainMid));
                var (el, er) = Hrtf(copy, eDir);
                int eo = off + (int)(a.ExtraDelaySeconds * OutRate);
                float eg = g * rel;
                for (int i = 0; i < el.Length && eo + i < n; i++) { l[eo + i] += el[i] * eg; r[eo + i] += er[i] * eg; }
                note += $"; echo order {a.Order} {a.ExtraDelaySeconds * 1000f:F0} ms at {Db20(rel):F0} dB";
            }
            notes.Add(note);
        }
        return (l, r, string.Join(" | ", notes));
    }

    private static float Db20(float g) => 20f * MathF.Log10(MathF.Max(1e-6f, g));

    /// <summary>Three bands split at 500 Hz and 3 kHz (one-pole), each scaled: the game's three-band path gains.</summary>
    private static float[] ThreeBand(float[] x, float lo, float mid, float hi)
    {
        if (lo == 1f && mid == 1f && hi == 1f) return x;
        var y = new float[x.Length];
        float a1 = 1f - MathF.Exp(-2f * MathF.PI * 500f / OutRate), a2 = 1f - MathF.Exp(-2f * MathF.PI * 3000f / OutRate);
        float s1 = 0f, s2 = 0f;
        for (int i = 0; i < x.Length; i++)
        {
            s1 += a1 * (x[i] - s1); s2 += a2 * (x[i] - s2);
            float low = s1, high = x[i] - s2, band = s2 - s1;
            y[i] = low * lo + band * mid + high * hi;
        }
        return y;
    }

    private static (float[] L, float[] R) Hrtf(float[] mono, Vector3 dir)
    {
        const int frame = 1024;
        var cs = Phonon.DefaultContextSettings();
        Phonon.iplContextCreate(ref cs, out IntPtr ctx);
        var au = new Phonon.IPLAudioSettings { samplingRate = OutRate, frameSize = frame };
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);
        var es = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
        Phonon.iplBinauralEffectCreate(ctx, ref au, ref es, out IntPtr eff);
        var inBuf = new Phonon.IPLAudioBuffer(); var outBuf = new Phonon.IPLAudioBuffer();
        Phonon.iplAudioBufferAllocate(ctx, 1, frame, ref inBuf);
        Phonon.iplAudioBufferAllocate(ctx, 2, frame, ref outBuf);
        // Listener facing north (+z): Steam Audio's frame is +x right (east), +y up, -z forward.
        var d = new Phonon.IPLVector3 { x = dir.X, y = dir.Y, z = -dir.Z };
        int frames = (mono.Length + frame - 1) / frame + 1;
        var l = new float[frames * frame]; var r = new float[frames * frame];
        var m = new float[frame]; var st = new float[frame * 2];
        for (int f = 0; f < frames; f++)
        {
            Array.Clear(m);
            int at = f * frame;
            if (at < mono.Length) Array.Copy(mono, at, m, 0, Math.Min(frame, mono.Length - at));
            Phonon.iplAudioBufferDeinterleave(ctx, m, ref inBuf);
            var prm = new Phonon.IPLBinauralEffectParams { direction = d, interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR, spatialBlend = 1f, hrtf = hrtf, peakDelays = IntPtr.Zero };
            Phonon.iplBinauralEffectApply(eff, ref prm, ref inBuf, ref outBuf);
            Phonon.iplAudioBufferInterleave(ctx, ref outBuf, st);
            for (int i = 0; i < frame; i++) { l[at + i] = st[2 * i]; r[at + i] = st[2 * i + 1]; }
        }
        Phonon.iplAudioBufferFree(ctx, ref inBuf); Phonon.iplAudioBufferFree(ctx, ref outBuf);
        Phonon.iplBinauralEffectRelease(ref eff); Phonon.iplHRTFRelease(ref hrtf); Phonon.iplContextRelease(ref ctx);
        return (l, r);
    }

    private static void WriteStereo(string path, float[] l, float[] r, float gain)
    {
        var a = new float[l.Length]; var b = new float[r.Length];
        for (int i = 0; i < a.Length; i++) { a[i] = Math.Clamp(l[i] * gain, -1f, 1f); b[i] = Math.Clamp(r[i] * gain, -1f, 1f); }
        File.WriteAllBytes(path, OpenFPS.Client.Core.AudioEngine.Fmod.CrossingSpike.ToWav16Stereo(a, b, OutRate));
    }
}
