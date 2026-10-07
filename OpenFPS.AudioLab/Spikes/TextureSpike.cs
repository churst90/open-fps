using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --textures: the sound-texture statistics listeners recognise a texture by (McDermott and Simoncelli
/// 2011; <see cref="TextureStatistics"/>, the twin of tools/texture_stats.py), and the texture round's
/// listening files.
///
///   --textures stats FILE...                 each file's summary features (any WAV: 16/24/32-bit or
///                                            float, any rate, mixed to mono, brought to 48 kHz)
///   --textures wave FILE...                  each file's 4-16 kHz kurtosis and crest in 10 ms windows
///   --textures compare REF... -- FILE...     the references' range and each file against it (* outside)
///   --textures render out=DIR [before=DIR] [sec=30]
///        the fountain from 2, 8 and 20 m south of its kerb, and a park tree in a 3, 6 and 10 m/s wind
///        from 5 m off its trunk, as stereo float WAVs at the level the game plays them. With before=, the
///        same places for the previous models' raw renders found there (water_park_fountain.wav and
///        foliage_park_tree_w{3,6,10}.wav from an older build's --nature render; one point each).
///
/// THE MAPPING TO GAME LEVEL (the one RainSpike.GameDb states): each source's pressure at a metre is
/// divided by its voice's full scale (its declared level plus the fleet's 16 dB headroom: a voice with
/// more headroom renders that much lower and the channel gives it back), multiplied by the loudness
/// law's gain at the default /levels (Loudness.Place with the source's extent, compression 0.45) and by
/// 1/r past the reference distance, then by the provider chain's measured +5.5 dB (RainSpike.ProviderDb,
/// --rain live). The air's three bands (AudioPhysics.AirLossDb) for each source's own distance. Placed
/// by equal-power panning on its azimuth, the listener facing north, east to the right, with √2 so
/// each ear carries the source's whole level as the binaural voice does: NOT the game's HRTF, and no
/// reflections or reverb. A fountain's taps are each placed from their own place on the city map.
/// </summary>
public static class TextureSpike
{
    private const int Rate = 48000;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        var rest = args.Where(a => !a.StartsWith("--textures", StringComparison.Ordinal)).ToList();
        if (rest.Contains("stats"))
        {
            foreach (var f in rest.Where(a => a != "stats" && !a.Contains('=')))
            {
                var s = TextureStatistics.Analyse(ReadMono48k(f)).Summary();
                Console.WriteLine($"{Path.GetFileName(f)}: " + string.Join(", ", s.Select(kv => $"{kv.Key} {kv.Value:F3}")));
            }
            return 0;
        }
        if (rest.Contains("wave"))
        {
            foreach (var f in rest.Where(a => a != "wave" && !a.Contains('=')))
            {
                var (k, c) = TextureStatistics.Waveform(ReadMono48k(f));
                Console.WriteLine($"{Path.GetFileName(f)}: 4-16 kHz in 10 ms: kurtosis {k:F2}, crest median {c:F1} dB");
            }
            return 0;
        }
        if (rest.Contains("compare"))
        {
            var files = rest.Where(a => a != "compare" && !a.Contains('=')).ToList();
            int split = files.IndexOf("--");
            if (split < 0) { Console.WriteLine("compare REF... -- FILE..."); return 1; }
            var refs = files.Take(split).Select(f => (Path.GetFileName(f), TextureStatistics.Analyse(ReadMono48k(f)).Summary())).ToList();
            var models = files.Skip(split + 1).Select(f => (Path.GetFileName(f), TextureStatistics.Analyse(ReadMono48k(f)).Summary())).ToList();
            Compare(refs, models);
            return 0;
        }
        if (rest.Contains("render"))
        {
            string dir = Arg(rest, "out=") ?? "/tmp/openfps-textures";
            Directory.CreateDirectory(dir);
            float sec = float.Parse(Arg(rest, "sec=") ?? "30", CultureInfo.InvariantCulture);
            Render(dir, Arg(rest, "before="), sec);
            return 0;
        }
        Console.WriteLine("--textures stats FILE... | wave FILE... | compare REF... -- FILE... | render out=DIR [before=DIR] [sec=30]");
        return 1;
    }

    public static void Compare(List<(string Name, Dictionary<string, double> S)> refs, List<(string Name, Dictionary<string, double> S)> models)
    {
        var keys = refs[0].S.Keys.ToList();
        int w = refs.Concat(models).Max(r => r.Name.Length) + 2;
        Console.WriteLine(new string(' ', w) + string.Join(" ", keys.Select(k => k.PadLeft(12))));
        foreach (var (n, s) in refs) Console.WriteLine(n.PadRight(w) + string.Join(" ", keys.Select(k => $"{s[k],12:F3}")));
        var lo = keys.ToDictionary(k => k, k => refs.Min(r => r.S[k]));
        var hi = keys.ToDictionary(k => k, k => refs.Max(r => r.S[k]));
        Console.WriteLine("refs min".PadRight(w) + string.Join(" ", keys.Select(k => $"{lo[k],12:F3}")));
        Console.WriteLine("refs max".PadRight(w) + string.Join(" ", keys.Select(k => $"{hi[k],12:F3}")));
        foreach (var (n, s) in models)
        {
            int inside = keys.Count(k => s[k] >= lo[k] && s[k] <= hi[k]);
            Console.WriteLine(n.PadRight(w) + string.Join(" ", keys.Select(k => $"{s[k],11:F3}{(s[k] >= lo[k] && s[k] <= hi[k] ? ' ' : '*')}"))
                              + $"   {inside}/{keys.Count} inside");
        }
    }

    // ── Game-level renders ─────────────────────────────────────────────────────────────────────────

    private sealed record Source(float[] Pascals, Vector3 At, float LevelDb, float Extent);

    private static void Render(string dir, string? before, float sec)
    {
        int n = (int)(sec * Rate);
        var fountain = WaterFeatureSpec.ByName("park_fountain");
        var (centre, taps) = FountainOnTheMap();
        Console.WriteLine($"fountain at {centre}, {taps.Count} taps: " + string.Join("; ", taps.Select(t => $"{t - centre}")));

        // Every tap rendered by the one synth, each placed where the map puts it.
        var synth = new FallingWaterSynth(fountain, Rate, 7);
        var tapPa = new float[synth.TapCount][];
        for (int t = 0; t < tapPa.Length; t++) tapPa[t] = new float[n];
        Span<float> frame = stackalloc float[synth.TapCount];
        const int block = 256;
        for (int i = 0; i < n; i += block)
        {
            synth.Wind = WindField.SpeedAt(0f, fountain.WindHeightMetres, 0f, i / (double)Rate);
            synth.Control(block / (float)Rate);
            for (int k = i; k < Math.Min(n, i + block); k++)
            {
                synth.NextTaps(frame);
                for (int t = 0; t < tapPa.Length; t++) tapPa[t][k] = frame[t];
            }
        }
        const float half = 5.5f, oldHalf = 4f;
        foreach (float d in new[] { 2f, 8f, 20f })
        {
            var ear = new Vector3(centre.X, 1.7f, centre.Z - half - d);
            var sources = taps.Select((p, t) => new Source(tapPa[t], p, fountain.SourceLevelDb, fountain.Taps[t].ExtentMetres)).ToList();
            Write(Path.Combine(dir, $"fountain_after_{d:0}m.wav"), sources, ear, n);
            if (before != null && File.Exists(Path.Combine(before, "water_park_fountain.wav")))
            {
                // Round 3's fountain: one emitter at (0, 0.7, +1.6) from the middle of an 8 m basin,
                // declared 72 dB with a 3 m extent.
                var old = ReadRawPascals(Path.Combine(before, "water_park_fountain.wav"), n);
                var oldEar = new Vector3(centre.X, 1.7f, centre.Z - oldHalf - d);
                Write(Path.Combine(dir, $"fountain_before_{d:0}m.wav"),
                      new List<Source> { new(old, centre + new Vector3(0f, 0.7f, 1.6f), 72f, 3f) }, oldEar, n);
            }
        }

        // A park tree, 5 m off its trunk to the south-west: the crown's middle 7 m up.
        var treeSpec = FoliageSpec.ByName("park_tree");
        var treeAt = new Vector3(3f, treeSpec.CrownHeightMetres, 4f);
        var treeEar = new Vector3(0f, 1.7f, 0f);
        float savedMean = WindField.MeanSpeed;
        foreach (float wind in new[] { 3f, 6f, 10f })
        {
            WindField.MeanSpeed = wind;
            var tree = new FoliageSynth(treeSpec, Rate, 7);
            var pa = new float[n];
            for (int i = 0; i < n; i += block)
            {
                tree.ReadWind(0f, 0f, i / (double)Rate);
                tree.Control(block / (float)Rate);
                for (int k = i; k < Math.Min(n, i + block); k++) pa[k] = tree.Next();
            }
            Write(Path.Combine(dir, $"tree_after_{wind:0}ms.wav"),
                  new List<Source> { new(pa, treeAt, treeSpec.SourceLevelDb, treeSpec.ExtentMetres) }, treeEar, n);
            string old = before == null ? "" : Path.Combine(before, $"foliage_park_tree_w{wind:0}.wav");
            if (before != null && File.Exists(old))
                Write(Path.Combine(dir, $"tree_before_{wind:0}ms.wav"),
                      new List<Source> { new(ReadRawPascals(old, n), treeAt, 48f, 4f) }, treeEar, n);
        }
        WindField.MeanSpeed = savedMean;
    }

    private static void Write(string path, List<Source> sources, Vector3 ear, int n)
    {
        var left = new float[n];
        var right = new float[n];
        foreach (var s in sources)
        {
            var to = s.At - ear;
            float dist = MathF.Max(0.5f, to.Length());
            var (gain, reference) = Loudness.Place(s.LevelDb, s.Extent);
            float fullScale = 20e-6f * MathF.Pow(10f, (s.LevelDb + VehicleProfile.PeakHeadroomDb) / 20f);
            float amp = gain * MathF.Min(1f, reference / dist) / fullScale * MathF.Pow(10f, (float)RainSpike.ProviderDb / 20f);
            var x = (float[])s.Pascals.Clone();
            var air = AudioPhysics.AirLossDb(dist, 50f, 15f, 1013.25f);
            RainSpike.ThreeEq(x, MathF.Pow(10f, air.Low / 20f), MathF.Pow(10f, air.Mid / 20f), MathF.Pow(10f, air.High / 20f));
            float az = MathF.Atan2(to.X, to.Z);
            float pan = MathF.Sin(az);
            float gl = MathF.Cos((pan + 1f) * MathF.PI / 4f) * 1.4142135f * amp, gr = MathF.Sin((pan + 1f) * MathF.PI / 4f) * 1.4142135f * amp;
            for (int i = 0; i < n; i++) { left[i] += x[i] * gl; right[i] += x[i] * gr; }
        }
        RainSpike.WriteStereo(path, left, right);
        double e = 0; float peak = 0;
        for (int i = 0; i < n; i++) { e += left[i] * (double)left[i] + right[i] * (double)right[i]; peak = MathF.Max(peak, MathF.Max(MathF.Abs(left[i]), MathF.Abs(right[i]))); }
        Console.WriteLine($"  wrote {Path.GetFileName(path)}: rms {10 * Math.Log10(e / (2.0 * n) + 1e-30):F1} dBFS, peak {20 * Math.Log10(peak + 1e-30):F1} dBFS, " +
                          $"{sources.Count} source(s), nearest {sources.Min(s => (s.At - ear).Length()):F1} m");
    }

    /// <summary>The fountain's middle and its tap emitters, read from the city map as the server lays it.</summary>
    private static (Vector3 Centre, List<Vector3> Taps) FountainOnTheMap()
    {
        var map = JsonDocument.Parse(File.ReadAllText(LabPaths.Server("maps", "city.json"))).RootElement;
        var taps = new SortedDictionary<int, Vector3>();
        Vector3 centre = new(-325f, 0f, 227f);
        foreach (var e in map.GetProperty("Entities").EnumerateArray())
        {
            string id = e.GetProperty("PrefabId").GetString()!;
            var p = e.GetProperty("Position");
            var at = new Vector3(p.GetProperty("X").GetSingle(), p.GetProperty("Y").GetSingle(), p.GetProperty("Z").GetSingle());
            if (id.StartsWith("elm_fountain_water_", StringComparison.Ordinal)) taps[int.Parse(id[19..], CultureInfo.InvariantCulture)] = at;
            if (id == "concrete_floor" && e.TryGetProperty("Name", out var nm) && nm.GetString() == "Elm Park fountain bowl") centre = at with { Y = 0f };
        }
        return (centre, taps.Values.ToList());
    }

    // ── Files ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A --nature render (16-bit, 0.1 of full scale a pascal) back to pascals, looped or cut to n.</summary>
    private static float[] ReadRawPascals(string path, int n)
    {
        var (x, sr) = ReadWav(path);
        if (sr != Rate) x = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.Resample(x, sr, Rate);
        var y = new float[n];
        for (int i = 0; i < n; i++) y[i] = x[i % x.Length] / 0.1f;
        return y;
    }

    public static float[] ReadMono48k(string path)
    {
        var (x, sr) = ReadWav(path);
        return sr == Rate ? x : OpenFPS.Client.AudioEngine.Fmod.MixerQuality.Resample(x, sr, Rate);
    }

    /// <summary>Any PCM or float WAV, mixed to mono.</summary>
    public static (float[] Pcm, int Rate) ReadWav(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        r.ReadBytes(12);
        int channels = 1, rate = 44100, bits = 16, format = 1;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            string id = new string(r.ReadChars(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                format = r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                if (size > 16) r.ReadBytes(size - 16);
                if (format == unchecked((short)0xFFFE)) format = bits == 32 ? 3 : 1;   // extensible: assume the common cases
            }
            else if (id == "data")
            {
                int bytes = bits / 8;
                int frames = size / (bytes * channels);
                var pcm = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    float s = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        s += (format, bits) switch
                        {
                            (3, 32) => r.ReadSingle(),
                            (_, 16) => r.ReadInt16() / 32768f,
                            (_, 24) => ((r.ReadByte() | (r.ReadByte() << 8) | (r.ReadByte() << 16)) << 8 >> 8) / 8388608f,
                            (_, 32) => r.ReadInt32() / 2147483648f,
                            _ => throw new InvalidDataException($"{path}: format {format}, {bits}-bit"),
                        };
                    }
                    pcm[i] = s / channels;
                }
                return (pcm, rate);
            }
            else r.ReadBytes(size + (size & 1));
        }
        throw new InvalidDataException($"{path}: no data chunk");
    }

    private static string? Arg(List<string> args, string prefix)
        => args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
}
