using System;
using System.Collections.Generic;
using System.Linq;
using FMOD;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;

/// <summary>
/// --heard-levels [d=1.5] [wav=DIR]: what level each everyday world sound actually reaches the listener
/// at, against what the server declares for it.
///
/// Every world sound is placed by <see cref="Loudness.Place"/> from the level the server sends, and that
/// gain is applied to the buffer the client renders or decodes. The convention (Speech.LevelDb) is that a
/// sound's level is its buffer's FULL SCALE at one metre. So what reaches the ear is the placement plus
/// where the buffer's own loudness sits under full scale: a peak-normalised crack sits far further under
/// it than a loudness-normalised line of speech does. This takes each buffer exactly as the client gets
/// it (door models through their RenderKey, a car door and a knock through their renders, a footstep take
/// from its bank with its TakeLevels correction, a pedestrian's line brought to the speech loudness), and
/// reports its LAFmax (A-weighted, 125 ms) at <c>d</c> metres on the direct path, at the shipped
/// compression and at 1.0 ("real", where rendered dBFS plus the ceiling is dB SPL).
///
/// For the door models it also reports the model's own physical LAFmax at a metre (its pressure, before
/// RenderKey normalised it): at 1.0 a door should be heard at that, less 20 log d.
///
/// With wav=DIR it writes each buffer, as the client would play it at 1.0 and d metres, as a WAV (with a
/// common gain so the loudest fits), so the files stand to each other as they do in the game.
/// </summary>
public static class HeardLevelsSpike
{
    private sealed record Source(string Name, float DeclaredDb, float[] Buffer, double? ModelLafDb, double? ModelPeakDb)
    {
        /// <summary>The level it is placed at, where the client puts its own in place of the declared one.</summary>
        public float? PlacedDb { get; init; }
        public float Level => PlacedDb ?? DeclaredDb;
    }

    /// <summary>
    /// --heard-levels survey: every door key the game sends (knob doors 1.1 and 1.4 m wide, every
    /// character and way of shutting; push-bar and sliding doors, every character), rendered in pressure,
    /// with its LAFmax and its peak at a metre: what the declared full-scale levels are read from.
    /// only=TEXT keeps the keys that contain it (only=patio).
    /// </summary>
    public static int Survey(string? only = null)
    {
        var keys = new List<string>();
        foreach (float w in new[] { 1.1f, 1.4f })
            for (int v = 0; v < KnobDoor.Variants; v++)
            {
                keys.Add(KnobDoor.Key(false, KnobDoor.Construction.HollowCore, v, 0.9f, KnobDoor.Shut.Normal, w, 2.1f));
                foreach (var how in new[] { KnobDoor.Shut.Gentle, KnobDoor.Shut.Normal, KnobDoor.Shut.Hard })
                    keys.Add(KnobDoor.Key(true, KnobDoor.Construction.HollowCore, v, 0.9f, how, w, 2.1f));
            }
        for (int v = 0; v < PushBarDoor.Variants; v++)
            foreach (bool c in new[] { false, true })
                keys.Add(PushBarDoor.Key(c, v, 1.4f, 1.0f, 2.1f));
        for (int v = 0; v < SlidingDoor.Variants; v++)
            foreach (bool c in new[] { false, true })
            {
                keys.Add(SlidingDoor.Key(SlidingDoor.Kind.Patio, c, v, 1.4f, 1.0f, 2.1f));
                keys.Add(SlidingDoor.Key(SlidingDoor.Kind.Automatic, c, v, SlidingDoor.AutomaticSeconds(1.0f, !c), 1.0f, 2.1f));
            }
        if (only != null) keys.RemoveAll(k => !k.Contains(only, StringComparison.Ordinal));
        var rows = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        System.Threading.Tasks.Parallel.ForEach(keys, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, key =>
        {
            float[] raw = RawOf(key, 48000);
            double fs = 20 * Math.Log10(20.0 / 2e-5);
            rows[key] = $"{key,-44} LAFmax {LafMaxDbfs(raw) + fs,6:F1}  peak {PeakDbfs(raw) + fs,6:F1}";
            Console.Error.WriteLine(rows[key]);
        });
        foreach (var k in keys) Console.WriteLine(rows[k]);
        return 0;
    }

    /// <summary>A door key's render in pressure, in units of the model's PascalsAtFullScale (20 Pa for all three).</summary>
    private static float[] RawOf(string key, int rate)
    {
        if (KnobDoor.TryParseKey(key, out bool closing, out var kd, out float swing, out var how))
            return closing ? KnobDoor.RenderGameClose(kd, rate, how) : KnobDoor.RenderOpen(kd, rate, swing);
        if (PushBarDoor.TryParseKey(key, out closing, out var pd, out swing))
            return closing ? PushBarDoor.RenderClose(pd, rate) : PushBarDoor.RenderOpen(pd, rate, swing);
        if (SlidingDoor.TryParseKey(key, out closing, out var sd, out swing))
            return closing ? SlidingDoor.RenderClose(sd, rate, swing) : SlidingDoor.RenderOpen(sd, rate, swing);
        throw new ArgumentException(key);
    }

    public static int Run(string[] args)
    {
        if (args.Contains("survey")) return Survey(args.FirstOrDefault(a => a.StartsWith("only=", StringComparison.Ordinal))?.Substring(5));
        float d = float.TryParse(args.FirstOrDefault(a => a.StartsWith("d=", StringComparison.Ordinal))?.Substring(2),
                                 System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dd) ? dd : 1.5f;
        string? wavDir = args.FirstOrDefault(a => a.StartsWith("wav=", StringComparison.Ordinal))?.Substring(4);
        const int Rate = 48000;
        var sources = new List<Source>();

        // ── The door models, as the server names them and the client renders them ───────────────────
        foreach (var how in new[] { KnobDoor.Shut.Gentle, KnobDoor.Shut.Normal, KnobDoor.Shut.Hard, KnobDoor.Shut.Slam })
        {
            for (int v = 0; v < KnobDoor.Variants; v += 3)
            {
                string key = KnobDoor.Key(true, KnobDoor.Construction.HollowCore, v, 0.9f, how, 0.9f, 2.1f);
                AddModel(sources, $"knob close {how.ToString().ToLowerInvariant()} v{v}", KnobDoor.CloseLevelDb(how), key, KnobDoor.PascalsAtFullScale, Rate);
            }
        }
        for (int v = 0; v < KnobDoor.Variants; v += 3)
            AddModel(sources, $"knob open v{v}", KnobDoor.OpenLevelDb,
                     KnobDoor.Key(false, KnobDoor.Construction.HollowCore, v, 0.9f, KnobDoor.Shut.Normal, 0.9f, 2.1f), KnobDoor.PascalsAtFullScale, Rate);
        for (int v = 0; v < PushBarDoor.Variants; v++)
        {
            AddModel(sources, $"push-bar open v{v}", PushBarDoor.OpenLevelDb(v), PushBarDoor.Key(false, v, 1.4f, 1.0f, 2.1f), PushBarDoor.PascalsAtFullScale, Rate);
            AddModel(sources, $"push-bar close v{v}", PushBarDoor.CloseLevelDb(v), PushBarDoor.Key(true, v, 1.4f, 1.0f, 2.1f), PushBarDoor.PascalsAtFullScale, Rate);
        }
        foreach (var kind in new[] { SlidingDoor.Kind.Patio, SlidingDoor.Kind.Automatic })
            foreach (int v in new[] { 1, 3 })
            {
                float open = kind == SlidingDoor.Kind.Patio ? 1.4f : SlidingDoor.AutomaticSeconds(1.0f, true);
                float shut = kind == SlidingDoor.Kind.Patio ? 1.4f : SlidingDoor.AutomaticSeconds(1.0f, false);
                string k = kind == SlidingDoor.Kind.Patio ? "patio" : "auto";
                AddModel(sources, $"{k} open v{v}", SlidingDoor.OpenLevelDb(kind, v), SlidingDoor.Key(kind, false, v, open, kind == SlidingDoor.Kind.Patio ? 0.9f : 1.0f, 2.1f), SlidingDoor.PascalsAtFullScale, Rate);
                AddModel(sources, $"{k} close v{v}", SlidingDoor.CloseLevelDb(kind, v), SlidingDoor.Key(kind, true, v, shut, kind == SlidingDoor.Kind.Patio ? 0.9f : 1.0f, 2.1f), SlidingDoor.PascalsAtFullScale, Rate);
            }

        // ── For comparison: other things the game plays ──────────────────────────────────────────────
        // A car door at the levels the server declared in today's session (DoorAcoustics: 77 open, 88 shut).
        sources.Add(new Source("car door open", 77f, CarDoor.Render(false, Rate, 1), null, null));
        sources.Add(new Source("car door close", 88f, CarDoor.Render(true, Rate, 1), null, null));
        sources.Add(new Source("knock x3 (Shift+E)", DoorKnock.LevelDb, DoorKnock.Render(3, Rate, 1), null, null));

        Factory.System_Create(out FMOD.System sys);
        sys.setOutput(OUTPUTTYPE.NOSOUND);
        sys.init(32, INITFLAGS.NORMAL, IntPtr.Zero);
        var bank = new GranularBank(sys);

        // Footsteps: the concrete and wood banks, each take with its TakeLevels correction, as your own
        // step is placed (Loudness.FootstepDb). The median take by LAFmax stands for the bank.
        foreach (string surface in new[] { "Concrete", "Wood", "Carpet" })
        {
            string dir = OpenFPS.AudioLab.LabPaths.Sounds("FOOTSTEPS", surface);
            var takes = new List<(double Laf, float[] Pcm)>();
            foreach (var f in System.IO.Directory.GetFiles(dir, "*.ogg").OrderBy(x => x).Take(40))
            {
                if (!bank.TryDecode(f, out var pcm, out int ch, out int rate) || pcm.Length == 0) continue;
                pcm = Mono(pcm, ch);
                if (rate != Rate) pcm = OpenFPS.Client.Core.WorldAudioPlayer.Resample(pcm, rate, Rate);
                float g = TakeLevels.GainFor(f);
                for (int i = 0; i < pcm.Length; i++) pcm[i] *= g;
                takes.Add((LafMaxDbfs(pcm), pcm));
            }
            if (takes.Count == 0) { Console.WriteLine($"no {surface} footsteps decoded from {dir}"); continue; }
            var median = takes.OrderBy(t => t.Laf).ElementAt(takes.Count / 2);
            sources.Add(new Source($"footstep {surface.ToLowerInvariant()} (median of {takes.Count})", Loudness.FootstepDb, median.Pcm, null, null));
        }

        // A pedestrian's greeting at normal effort, brought to the speech loudness as SpokenLine does.
        foreach (var take in Speech.Takes.Where(t => t.Line.StartsWith("greet", StringComparison.Ordinal)).Take(3))
        {
            string id = OpenFPS.AudioLab.LabPaths.Sounds("VOICES", take.Voice, take.Line + ".ogg");
            if (!bank.TryDecode(id, out var pcm, out int ch, out int rate) || pcm.Length == 0) continue;
            pcm = Mono(pcm, ch);
            if (rate != Rate) pcm = OpenFPS.Client.Core.WorldAudioPlayer.Resample(pcm, rate, Rate);
            double lufs = Speech.LoudnessLufs(pcm);
            float gain = (float)Math.Pow(10.0, (Speech.BufferLoudnessLufs - lufs) / 20.0);
            for (int i = 0; i < pcm.Length; i++) pcm[i] = Math.Clamp(pcm[i] * gain, -1f, 1f);
            sources.Add(new Source($"speech {take.Voice}/{take.Line}", Speech.LevelDb(Speech.NormalDb), pcm, null, null));
        }
        sys.release();

        // ── The table ────────────────────────────────────────────────────────────────────────────────
        float saved = Loudness.DynamicRangeCompression;
        Console.WriteLine($"At {d:F1} m on the direct path. buf = the buffer's own LAFmax and peak, dBFS. "
                        + "0.45 / 1.0 = rendered LAFmax, dBFS; SPL@1.0 = that plus the ceiling at 1.0 (dB SPL heard).");
        Console.WriteLine($"Ceilings: 0.45 -> {Ceiling(0.45f):F1} dB, 1.0 -> {Ceiling(1f):F1} dB.");
        Console.WriteLine($"{"source",-44} {"decl",6} {"bufLAF",7} {"bufPk",6} {"@0.45",7} {"@1.0",7} {"SPL@1.0",8} {"model@1m",9} {"model@d",8} {"short",6}");
        var renderedAt1 = new List<(string, float[])>();
        foreach (var s in sources)
        {
            double laf = LafMaxDbfs(s.Buffer), pk = PeakDbfs(s.Buffer);
            double r045 = Rendered(s.Level, 0.45f, d) + laf;
            double r1 = Rendered(s.Level, 1f, d) + laf;
            double spl = r1 + Ceiling(1f);
            string model = s.ModelLafDb is double m ? $"{m,9:F1} {m - 20 * Math.Log10(d),8:F1} {m - 20 * Math.Log10(d) - spl,6:F1}" : "";
            Console.WriteLine($"{s.Name,-44} {s.DeclaredDb,6:F1} {laf,7:F1} {pk,6:F1} {r045,7:F1} {r1,7:F1} {spl,8:F1} {model}");
            if (s.ModelPeakDb is double p)
                Console.WriteLine($"{"",-44} placed at its own peak, {p:F1} dB (the server declared {s.DeclaredDb:F1})");
            Loudness.DynamicRangeCompression = 1f;
            float g = (float)Math.Pow(10, Rendered(s.Level, 1f, d) / 20);
            renderedAt1.Add((s.Name, s.Buffer.Select(x => x * g).ToArray()));
        }
        Loudness.DynamicRangeCompression = saved;

        if (wavDir != null)
        {
            System.IO.Directory.CreateDirectory(wavDir);
            float top = renderedAt1.Max(r => r.Item2.Max(x => Math.Abs(x)));
            float common = top > 0.9f ? 0.9f / top : 1f;
            Console.WriteLine($"WAVs at a common gain of {20 * Math.Log10(common):F1} dB");
            foreach (var (name, pcm) in renderedAt1)
            {
                string file = System.IO.Path.Combine(wavDir, new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()) + ".wav");
                WriteWav(file, pcm.Select(x => x * common).ToArray(), Rate);
            }
        }
        return 0;
    }

    /// <summary>A door model's key as the client plays it: its render through WorldAudioPlayer.RenderDoorKey,
    /// placed at the level WorldAudioPlayer.AtOwnLevel gives it (the render's own peak), the server's figure
    /// shown beside it. The model's LAFmax at a metre is the buffer's LAFmax over its full scale.</summary>
    private static void AddModel(List<Source> list, string name, float declared, string key, double pascalsAtFullScale, int rate)
    {
        var own = new System.Collections.Concurrent.ConcurrentDictionary<string, float>();
        float[] buf = OpenFPS.Client.Core.WorldAudioPlayer.RenderDoorKey(key, own);
        float placed = OpenFPS.Client.Core.WorldAudioPlayer.AtOwnLevel(new TransientSound { SynthKey = key, LevelDb = declared }, own).LevelDb;
        list.Add(new Source(name, declared, buf, LafMaxDbfs(buf) + placed, placed) { PlacedDb = placed });
    }

    private static float Ceiling(float c)
    {
        float saved = Loudness.DynamicRangeCompression;
        Loudness.DynamicRangeCompression = c;
        float r = Loudness.RenderCeilingDb;
        Loudness.DynamicRangeCompression = saved;
        return r;
    }

    /// <summary>The direct path's gain at <paramref name="d"/>, dB: the placement and the mixer's 1/r.</summary>
    private static double Rendered(float levelDb, float c, float d)
    {
        float saved = Loudness.DynamicRangeCompression;
        Loudness.DynamicRangeCompression = c;
        var (gain, reference) = Loudness.Place(levelDb);
        float g = Loudness.RenderedGain(gain, reference, Loudness.AudibleRange(levelDb), d);
        Loudness.DynamicRangeCompression = saved;
        return 20 * Math.Log10(Math.Max(1e-12, g));
    }

    private static float[] Mono(float[] pcm, int channels)
    {
        if (channels <= 1) return pcm;
        var m = new float[pcm.Length / channels];
        for (int i = 0; i < m.Length; i++)
        {
            float s = 0; for (int c = 0; c < channels; c++) s += pcm[i * channels + c];
            m[i] = s / channels;
        }
        return m;
    }

    private static double PeakDbfs(float[] x) => 20 * Math.Log10(Math.Max(1e-12, x.Max(v => Math.Abs((double)v))));

    /// <summary>LAFmax of a 48 kHz buffer, dB re full scale: A-weighted, 125 ms exponential (a full-scale
    /// 1 kHz sine reads -3).</summary>
    public static double LafMaxDbfs(float[] x)
    {
        double[] b = { 0.234301792299513, -0.468603584599026, -0.234301792299513, 0.937207168598053, -0.234301792299513, -0.468603584599026, 0.234301792299513 };
        double[] a = { 1.0, -4.113043408775871, 6.553121752655047, -4.990849294163381, 1.785737302937573, -0.246190595319487, 0.011224250033231 };
        var xs = new double[7]; var ys = new double[7];
        double k = Math.Exp(-1 / (0.125 * 48000)), e = 0, max = 0;
        foreach (float s in x)
        {
            Array.Copy(xs, 0, xs, 1, 6); xs[0] = s;
            double y = 0;
            for (int i = 0; i < 7; i++) y += b[i] * xs[i];
            for (int i = 1; i < 7; i++) y -= a[i] * ys[i - 1];
            Array.Copy(ys, 0, ys, 1, 6); ys[0] = y;
            e = k * e + (1 - k) * y * y;
            max = Math.Max(max, e);
        }
        return 10 * Math.Log10(Math.Max(1e-24, max));
    }

    private static void WriteWav(string path, float[] pcm, int rate)
    {
        using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (float v in pcm) w.Write((short)Math.Clamp(v * 32767f, -32768f, 32767f));
    }
}
