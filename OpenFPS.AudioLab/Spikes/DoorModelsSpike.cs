using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// The door models of 2026-10-05: every event of the glass front door, the glass pull door, the lift door, the
/// key in a lock, and the push openings of the knob and push-bar doors, rendered and measured the way the
/// push-bar door's round 8 was (<see cref="PushBarRefSpike.Clusters"/>), beside the recordings they are
/// measured against. The recordings are a yardstick only: nothing of them is played by the game.
///
///   --door-models [out=DIR] [only=TEXT] [stems=DIR] [refs]
///
/// out= writes the listening set: each file levelled by its own LAFmax at -24 dBFS (no peak over -1 dBFS),
/// and 00-tour.wav, every file in turn with a spoken label. only= keeps the renders whose name contains it.
/// refs measures the recordings too.
/// </summary>
public static class DoorModelsSpike
{
    public sealed record Render(string Name, string Label, Func<List<string>, float[]> Make, double PascalsAtFullScale);

    public static List<Render> All()
    {
        var list = new List<Render>();
        string[] chars = { "new", "standard", "worn", "old" };

        // The glass front door (glass-pushbar), each character: the bar pushed, the key-side pull, the close.
        for (int v = 0; v < GlassDoor.Variants; v++)
        {
            int vv = v;
            var door = new GlassDoor.Door { Kind = GlassDoor.Kind.PushBar, Variant = v, Seed = 1 + v };
            list.Add(new($"glass-front-bar-push-{chars[v]}", $"glass front door, {chars[v]}, pushed open by its bar from inside",
                r => Run<GlassDoor.Report>(r, rep => GlassDoor.RenderOpen(door, GlassDoor.Opening.Push, 48000, 1.1, rep)), GlassDoor.PascalsAtFullScale));
            list.Add(new($"glass-front-key-pull-{chars[v]}", $"glass front door, {chars[v]}, pulled open from outside with the key held, then the key let go",
                r => Run<GlassDoor.Report>(r, rep => GlassDoor.RenderOpen(door, GlassDoor.Opening.Key, 48000, 1.1, rep)), GlassDoor.PascalsAtFullScale));
            list.Add(new($"glass-front-close-{chars[v]}", $"glass front door, {chars[v]}, shut by its closer into the latch",
                r => Run<GlassDoor.Report>(r, rep => GlassDoor.RenderClose(door, 48000, rep)), GlassDoor.PascalsAtFullScale));
        }
        {
            var lam = new GlassDoor.Door { Kind = GlassDoor.Kind.PushBar, Glass = GlassDoor.Glazing.Laminated, Variant = 1, Seed = 2 };
            list.Add(new("glass-front-close-standard-laminated", "glass front door, standard, laminated glass, shut by its closer",
                r => Run<GlassDoor.Report>(r, rep => GlassDoor.RenderClose(lam, 48000, rep)), GlassDoor.PascalsAtFullScale));
        }
        // The glass pull door: pulled from one side, pushed from the other, shut by its closer (no latch).
        for (int v = 0; v < GlassDoor.Variants; v++)
        {
            var door = new GlassDoor.Door { Kind = GlassDoor.Kind.Pull, Variant = v, Seed = 1 + v };
            list.Add(new($"glass-pull-pull-{chars[v]}", $"glass shop door, {chars[v]}, pulled open by its handle",
                r => Run<GlassDoor.Report>(r, rep => GlassDoor.RenderOpen(door, GlassDoor.Opening.Pull, 48000, 1.0, rep)), GlassDoor.PascalsAtFullScale));
            list.Add(new($"glass-pull-push-{chars[v]}", $"glass shop door, {chars[v]}, pushed open from the other side",
                r => Run<GlassDoor.Report>(r, rep => GlassDoor.RenderOpen(door, GlassDoor.Opening.Push, 48000, 1.0, rep)), GlassDoor.PascalsAtFullScale));
            list.Add(new($"glass-pull-close-{chars[v]}", $"glass shop door, {chars[v]}, shut by its closer",
                r => Run<GlassDoor.Report>(r, rep => GlassDoor.RenderClose(door, 48000, rep)), GlassDoor.PascalsAtFullScale));
        }
        // The lift door: each character's opening and closing run, as the prefab's leaf at its 1.8 and 2.5 s.
        for (int v = 0; v < ElevatorDoor.Variants; v++)
        {
            var door = new ElevatorDoor.Door { Variant = v, Seed = 1 + v };
            list.Add(new($"lift-open-{chars[v]}", $"lift door, {chars[v]}, the operator opening it: coupler, rollers, stop",
                r => Run<ElevatorDoor.Report>(r, rep => ElevatorDoor.RenderOpen(door, 48000, 1.8, rep)), ElevatorDoor.PascalsAtFullScale));
            list.Add(new($"lift-close-{chars[v]}", $"lift door, {chars[v]}, the operator closing it: rollers, the creep, the leaves meeting, the lock",
                r => Run<ElevatorDoor.Report>(r, rep => ElevatorDoor.RenderClose(door, 48000, 2.5, rep)), ElevatorDoor.PascalsAtFullScale));
        }
        {
            var leaf = new ElevatorDoor.Door { Variant = 1, Seed = 2, Operator = false };
            list.Add(new("lift-close-standard-other-leaf", "lift door, standard, the other leaf of the pair closing, without the operator",
                r => Run<ElevatorDoor.Report>(r, rep => ElevatorDoor.RenderClose(leaf, 48000, 2.5, rep)), ElevatorDoor.PascalsAtFullScale));
        }
        // Push and pull on the approved doors: the knob door pushed from the stop's side beside its pull, and the
        // push-bar door pulled from the trim side beside its push.
        for (int v = 0; v < KnobDoor.Variants; v++)
        {
            var kd = new KnobDoor.Door { HingeWear = KnobDoor.WearOf(v), Seed = 1 + v };
            list.Add(new($"knob-push-v{v}", $"knob door, character {v}, turned and pushed open from the stop side",
                r => Run<KnobDoor.Report>(r, rep => KnobDoor.RenderOpen(kd, 48000, 0.9, rep, push: true)), KnobDoor.PascalsAtFullScale));
            list.Add(new($"knob-pull-v{v}", $"knob door, character {v}, turned and pulled open, the approved opening",
                r => Run<KnobDoor.Report>(r, rep => KnobDoor.RenderOpen(kd, 48000, 0.9, rep)), KnobDoor.PascalsAtFullScale));
        }
        for (int v = 0; v < PushBarDoor.Variants; v++)
        {
            var pd = new PushBarDoor.Door { Variant = v, Seed = 1 + v };
            list.Add(new($"pushbar-trim-pull-{chars[v]}", $"steel push bar door, {chars[v]}, pulled open from outside by its lever trim",
                r => Run<PushBarDoor.Report>(r, rep => PushBarDoor.RenderOpen(pd, 48000, 1.4, rep, pull: true)), PushBarDoor.PascalsAtFullScale));
            list.Add(new($"pushbar-bar-push-{chars[v]}", $"steel push bar door, {chars[v]}, pushed open by its bar, the approved opening",
                r => Run<PushBarDoor.Report>(r, rep => PushBarDoor.RenderOpen(pd, 48000, 1.4, rep)), PushBarDoor.PascalsAtFullScale));
        }
        // The key in the lock, in each kind of door it is set in, each character.
        foreach (var host in new[] { LockCylinder.Host.AluminiumStile, LockCylinder.Host.SteelDoor, LockCylinder.Host.WoodDoor })
            for (int v = 0; v < LockCylinder.Variants; v++)
            {
                if (host != LockCylinder.Host.AluminiumStile && v != 1) continue;
                var h = host; int vv = v;
                string where = host switch { LockCylinder.Host.SteelDoor => "a steel door", LockCylinder.Host.WoodDoor => "a wooden door", _ => "a glass front door's aluminium stile" };
                list.Add(new($"key-unlock-{h.ToString().ToLowerInvariant()}-{chars[v]}", $"a key unlocking {where}, {chars[v]} keyring",
                    r => Run<LockCylinder.Report>(r, rep => LockCylinder.RenderUnlock(h, vv, 48000, rep)), LockCylinder.PascalsAtFullScale));
                if (host == LockCylinder.Host.AluminiumStile && v == 1)
                    list.Add(new("key-unlock-aluminiumstile-standard-with-approach", "the same key, the ring first brought up to the lock",
                        r => Run<LockCylinder.Report>(r, rep => LockCylinder.RenderUnlock(h, vv, 48000, rep, approach: true)), LockCylinder.PascalsAtFullScale));
            }
        return list;
    }

    private static float[] Run<T>(List<string> log, Func<T, float[]> make) where T : new()
    {
        var rep = new T();
        var pcm = make(rep);
        log.Add(rep!.ToString()!);
        return pcm;
    }

    public static int Run(string[] args)
    {
        string? only = Arg(args, "only");
        string? outDir = Arg(args, "out");
        GlassDoor.StemFolder = Arg(args, "stems");
        LockCylinder.StemFolder = GlassDoor.StemFolder;
        ElevatorDoor.StemFolder = GlassDoor.StemFolder;
        var renders = All().Where(r => only == null || r.Name.Contains(only, StringComparison.Ordinal)).ToList();
        var made = new (Render R, float[] Pcm, string Log)[renders.Count];
        System.Threading.Tasks.Parallel.For(0, renders.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = GlassDoor.StemFolder != null ? 1 : 6 }, i =>
        {
            var log = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pcm = renders[i].Make(log);
            made[i] = (renders[i], pcm, $"({pcm.Length / 48000.0:F2} s, rendered in {sw.ElapsedMilliseconds} ms)\n" + string.Join("\n", log));
        });
        Console.WriteLine("Each render, dry at 1 m: peak and LAFmax, dB SPL; then its clusters of hits (loudest 5 ms re the render's loudest,");
        Console.WriteLine("hits ms:dB) and the loudest hit's first 30 ms by band, dB re its whole: <300 Hz, 300 Hz-1 kHz, 1-4 kHz, 4-16 kHz.");
        foreach (var (r, pcm, log) in made)
        {
            double fs = 20 * Math.Log10(r.PascalsAtFullScale / 2e-5);
            double peak = 20 * Math.Log10(Math.Max(1e-12, pcm.Max(Math.Abs))) + fs;
            double laf = PushBarRefSpike.LafDbfs(pcm) + fs;
            Console.WriteLine();
            Console.WriteLine($"{r.Name}  peak {peak:F1}  LAFmax {laf:F1}  {log}");
            Print(PushBarRefSpike.Clusters(pcm, 48000));
        }
        if (args.Contains("refs")) MeasureRecordings();
        if (outDir != null) WriteSet(outDir, made.Select(m => (m.R, m.Pcm)).ToList());
        return 0;
    }

    private static void Print(List<PushBarRefSpike.Cluster> cs)
    {
        foreach (var c in cs)
            Console.WriteLine($"    {c.Start,7:F3} s  {c.Level5,6:F1} dB  {c.Hits.Count,2} hits over {c.SpanMs,3:F0} ms   bands "
                              + string.Join(" ", c.Bands.Select(v => $"{v,6:F1}"))
                              + "\n        " + string.Join(" ", c.Hits.Take(24).Select(h => $"{h.Ms:F0}:{h.Db:F0}")));
    }

    /// <summary>The recordings the models answer to (inbox/door-types-2026-10-02/real), each event cut and
    /// measured as ours are.</summary>
    private static readonly (string Name, string File, double From, double To)[] RefEvents =
    {
        ("vaztur glass slam", "3-storefront-vaztur-iron-glass-slam-lock.wav", 0.0, 1.6),
        ("vaztur lock", "3-storefront-vaztur-iron-glass-slam-lock.wav", 1.6, 4.0),
        ("fossarts thumbturn deadbolt", "3-storefront-fossarts-thumbturn-deadbolt.wav", 0.0, 3.0),
        ("kraft glass closing", "4-pullglass-kraftaggregat-glass-closing.wav", 0.0, 6.0),
        ("kraft glass opening", "4-pullglass-kraftaggregat-glass-opening.wav", 0.0, 4.0),
        ("ifm library glass", "4-pullglass-ifm185-library-glass-aluminium-closer.wav", 0.0, 6.0),
        ("krystian lift doors", "7-elevator-krystianpawlowski-doors.wav", 0.0, 12.0),
        ("launchsite lift", "7-elevator-launchsite-open-close.wav", 0.0, 14.0),
        ("magedu lift inside", "7-elevator-magedu-inside-cabin.wav", 0.0, 12.0),
    };

    public static string RealDir()
    {
        string dir = LabPaths.InRepo("inbox", "door-types-2026-10-02", "real");
        return Directory.Exists(dir) ? dir : Path.Combine(LabPaths.Checkout, "inbox", "door-types-2026-10-02", "real");
    }

    private static void MeasureRecordings()
    {
        string dir = RealDir();
        Console.WriteLine();
        Console.WriteLine($"The recordings ({dir}), measured the same way (not calibrated: levels are re each event's loudest)");
        foreach (var (name, file, from, to) in RefEvents)
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path)) { Console.WriteLine($"  {name}: missing"); continue; }
            var (pcm, rate) = ReloadSpecSpike.ReadWav(path);
            var seg = pcm[(int)(from * rate)..Math.Min(pcm.Length, (int)(to * rate))];
            if (rate != 48000) seg = Resample(seg, rate, 48000);
            Console.WriteLine($"  {name} ({from:F1}-{to:F1} s)");
            Print(PushBarRefSpike.Clusters(seg, 48000));
        }
    }

    // ── The listening set ───────────────────────────────────────────────────────────────────────────

    private static void WriteSet(string dir, List<(Render R, float[] Pcm)> made)
    {
        Directory.CreateDirectory(dir);
        SpeakDir = dir;
        var tour = new List<float>();
        int n = 1;
        foreach (var (r, pcm) in made)
        {
            string file = $"{n:00}-{r.Name}.wav";
            var lev = Levelled(pcm);
            Write(Path.Combine(dir, file), lev);
            var label = Speak($"{n}. {r.Label}.");
            tour.AddRange(label);
            tour.AddRange(new float[(int)(0.35 * 48000)]);
            tour.AddRange(lev);
            tour.AddRange(new float[(int)(0.9 * 48000)]);
            n++;
        }
        Write(Path.Combine(dir, "00-tour.wav"), tour.ToArray());
        Console.WriteLine($"wrote {made.Count} renders and the tour to {dir}");
    }

    /// <summary>A spoken label from espeak-ng, at 48 kHz, levelled a little under the renders.</summary>
    private static string? SpeakDir;
    private static float[] Speak(string text)
    {
        try
        {
            string tmp = Path.Combine(SpeakDir ?? AppContext.BaseDirectory, $"doormodels-{Guid.NewGuid():N}.wav");
            var psi = new System.Diagnostics.ProcessStartInfo("espeak-ng", $"-s 165 -w \"{tmp}\" \"{text.Replace("\"", "")}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = System.Diagnostics.Process.Start(psi)!) p.WaitForExit();
            var (pcm, rate) = ReloadSpecSpike.ReadWav(tmp);
            File.Delete(tmp);
            var y = Resample(pcm, rate, 48000);
            double peak = Math.Max(1e-9, y.Max(Math.Abs));
            return y.Select(v => (float)(v / peak * 0.35)).ToArray();
        }
        catch { return new float[(int)(0.5 * 48000)]; }
    }

    /// <summary>Levelled by loudness: LAFmax at -24 dBFS, unless that puts a peak over -1 dBFS.</summary>
    public static float[] Levelled(float[] x)
    {
        double laf = PushBarRefSpike.LafDbfs(x), peak = 20 * Math.Log10(Math.Max(1e-12, x.Max(Math.Abs)));
        double g = Math.Min(Math.Pow(10, (-24 - laf) / 20), Math.Pow(10, (-1 - peak) / 20));
        return x.Select(v => (float)(v * g)).ToArray();
    }

    public static void Write(string path, float[] pcm)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(48000); w.Write(96000); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (float v in pcm) w.Write((short)Math.Clamp(v * 32767, -32768, 32767));
    }

    /// <summary>Band-limited resampling: a Blackman-windowed sinc, 32 taps a side.</summary>
    public static float[] Resample(float[] x, int from, int to)
    {
        if (from == to) return (float[])x.Clone();
        int n = (int)((long)x.Length * to / from);
        var y = new float[n];
        double fc = 0.45 * Math.Min(from, to) / from;
        for (int j = 0; j < n; j++)
        {
            double t = (double)j * from / to;
            int c = (int)Math.Floor(t);
            double acc = 0;
            for (int k = c - 31; k <= c + 32; k++)
            {
                if (k < 0 || k >= x.Length) continue;
                double d = t - k;
                double sinc = Math.Abs(d) < 1e-9 ? 2 * fc : Math.Sin(2 * Math.PI * fc * d) / (Math.PI * d);
                double u = (d + 32) / 64;
                acc += x[k] * sinc * (0.42 - 0.5 * Math.Cos(2 * Math.PI * u) + 0.08 * Math.Cos(4 * Math.PI * u));
            }
            y[j] = (float)acc;
        }
        return y;
    }

    private static string? Arg(string[] args, string name)
        => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
}
