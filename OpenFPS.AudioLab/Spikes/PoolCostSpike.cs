using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// What the engine render pool costs with a street's worth of live voices, and whether a change to the
/// synthesis moved a single sample (--pool-cost).
///
///   --pool-cost cpu [voices=32] [sec=10]   the real EngineRenderPool and a mixer taking blocks in real
///                                          time; cores used by the process, starves
///   --pool-cost offline [voices=32] [sec=4] the same voices rendered on one thread: core-seconds per second
///   --pool-cost render DIR                 a fixed set of scenes, Produce/Consume as the game does, written
///                                          as raw float32 with a hash each
///   --pool-cost render DIR wide            and every vehicle preset revving, and the engine-built machines
///   --pool-cost diff DIR_A DIR_B           the largest difference between two render sets, dBFS
///   --pool-cost machines [sec=4]           what each standing physical voice the street has costs
///   ptracer                                lets eu-stack attach (Yama ptrace_scope 1) and prints the pid:
///                                          the only profiler here that sees JIT frames and libm alike
///
/// Voices are built as FmodAudioProvider builds them: CompensateLevel on, placed at speed, the
/// listener in the machine's frame. The --engine-cost bench leaves the level compensation off.
/// </summary>
public static class PoolCostSpike
{
    private const int Rate = MixerQuality.DefaultRate, Block = 1024;

    /// <summary>A street: cars, a bus, vans, a truck, a bike.</summary>
    private static readonly string[] StreetMix =
    {
        "i4_compact", "i4_midsize", "boxer4_street", "i6_street", "v6", "transit_bus", "pickup_v8",
        "i4_compact", "school_bus", "step_van", "i4_midsize", "diesel_truck", "sportbike", "police_v8",
        "i4_sport_street", "mail_truck",
    };

    public static int Run(string[] args)
    {
        var rest = args.Where(a => a != "--pool-cost").ToArray();
        int voices = IntArg(rest, "voices", 32);
        // Lets a sampler that is not this process's parent (eu-stack) attach under Yama ptrace_scope 1.
        if (rest.Contains("ptracer") && OperatingSystem.IsLinux())
        {
            prctl(0x59616d61, ulong.MaxValue, 0, 0, 0);
            Console.WriteLine($"pid {Environment.ProcessId}");
        }
        if (rest.Contains("cpu")) return Cpu(voices, IntArg(rest, "sec", 10));
        if (rest.Contains("offline")) return Offline(voices, IntArg(rest, "sec", 4));
        if (rest.Contains("machines")) return Machines(IntArg(rest, "sec", 4));
        if (rest.Contains("render")) return RenderSet(rest.SkipWhile(a => a != "render").Skip(1).First(), rest.Contains("wide"));
        if (rest.Contains("diff"))
        {
            var dirs = rest.SkipWhile(a => a != "diff").Skip(1).Take(2).ToArray();
            return Diff(dirs[0], dirs[1]);
        }
        Console.WriteLine("--pool-cost cpu|offline|machines|render DIR [wide]|diff A B [voices=N] [sec=S] [ptracer]");
        return 2;
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    private static int IntArg(string[] args, string key, int fallback)
    {
        foreach (var a in args)
            if (a.StartsWith(key + "=") && int.TryParse(a[(key.Length + 1)..], out int v)) return v;
        return fallback;
    }

    /// <summary>One street voice: a speed and a listener 5-60 m off, as the provider makes it.</summary>
    private static EngineVoiceState StreetVoice(int i)
    {
        string key = StreetMix[i % StreetMix.Length];
        float speed = 4f + (i * 7 % 11) * 1.5f;                  // 4-19 m/s
        var v = new EngineVoiceState(MachineRegistry.VehicleFor(key), Rate, 1000 + i)
        {
            TargetSpeed = speed,
            CompensateLevel = true,
        };
        v.PlaceAtSpeed(speed);
        float d = 5f + (i * 13 % 56);
        float a = i * 0.7f;
        v.SetListener(new Vector3(MathF.Sin(a) * d, 1.6f, MathF.Cos(a) * d));
        return v;
    }

    private static int Cpu(int count, int seconds)
    {
        var list = Enumerable.Range(0, count).Select(StreetVoice).ToList();
        var snapshot = list.Cast<IRenderedVoice>().ToList();
        var proc = Process.GetCurrentProcess();
        using var pool = new EngineRenderPool(() => new List<IRenderedVoice>(snapshot));

        // The mixer: a block from every voice each 21 ms, on the clock, as FMOD calls the DSPs.
        var buf = new float[Block];
        double blockSec = Block / (double)Rate;
        var clock = Stopwatch.StartNew();
        long blocks = 0;
        double warm = 3.0;
        TimeSpan cpuAtStart = TimeSpan.Zero;
        double wallAtStart = 0;
        int starvesAtStart = 0;
        bool measuring = false;
        while (clock.Elapsed.TotalSeconds < warm + seconds)
        {
            double due = blocks * blockSec;
            double now = clock.Elapsed.TotalSeconds;
            if (now < due) { Thread.Sleep(Math.Max(0, (int)((due - now) * 1000))); continue; }
            foreach (var v in list) v.Consume(buf);
            blocks++;
            if (!measuring && clock.Elapsed.TotalSeconds >= warm)
            {
                measuring = true;
                proc.Refresh();
                cpuAtStart = proc.TotalProcessorTime;
                wallAtStart = clock.Elapsed.TotalSeconds;
                starvesAtStart = list.Sum(v => v.Starves);
            }
        }
        proc.Refresh();
        double cpu = (proc.TotalProcessorTime - cpuAtStart).TotalSeconds;
        double wall = clock.Elapsed.TotalSeconds - wallAtStart;
        int starves = list.Sum(v => v.Starves) - starvesAtStart;
        Console.WriteLine($"\n  {count} street voices through the render pool, {Environment.ProcessorCount} cores seen, {wall:F1} s measured after {warm:F0} s.");
        Console.WriteLine($"    cores used by the process   {cpu / wall,6:F2}");
        Console.WriteLine($"    starved blocks              {starves,6}");
        return 0;
    }

    private static int Offline(int count, int seconds)
    {
        var list = Enumerable.Range(0, count).Select(StreetVoice).ToList();
        var buf = new float[Block];
        // A second for the JIT and the pipes to settle, then the clock.
        for (int b = 0; b < Rate / Block; b++)
            foreach (var v in list) { v.Produce(); v.Consume(buf); }
        int blocks = seconds * Rate / Block;
        var sw = Stopwatch.StartNew();
        for (int b = 0; b < blocks; b++)
            foreach (var v in list) { v.Produce(); v.Consume(buf); }
        sw.Stop();
        double audio = blocks * Block / (double)Rate;
        Console.WriteLine($"\n  {count} street voices on one thread: {sw.Elapsed.TotalSeconds / audio:F3} core-seconds per second of sound"
                          + $" ({sw.Elapsed.TotalSeconds * 1000 / audio / count:F1} ms per voice-second).");
        return 0;
    }

    /// <summary>What each standing physical voice the street has costs, one at a time.</summary>
    private static int Machines(int seconds)
    {
        var makers = new (string Name, Func<PhysicalVoiceState> Make)[]
        {
            ("machine:mower_push", () => new MachineVoiceState(SmallMachineSpec.ByName("mower_push"), Rate, 11, 11 * 31 + 7)),
            ("machine:mower_riding", () => new MachineVoiceState(SmallMachineSpec.ByName("mower_riding"), Rate, 12, 12 * 31 + 7)),
            ("machine:ac_condenser", () => new MachineVoiceState(SmallMachineSpec.ByName("ac_condenser"), Rate, 13, 13 * 31 + 7)),
            ("machine:ac_window", () => new MachineVoiceState(SmallMachineSpec.ByName("ac_window"), Rate, 14, 14 * 31 + 7)),
            ("gate:crossing_gate", () => new GateVoiceState(CrossingGateSpec.ByName("crossing_gate"), Rate, closed: true)),
            ("bell:crossing_gong", () => new BellVoiceState(ModelLibrary.Bell("crossing_gong"), Rate, 15 * 13 + 5)),
        };
        var buf = new float[Block];
        Console.WriteLine();
        foreach (var (name, make) in makers)
        {
            var v = make();
            v.SetListener(new Vector3(3f, 1.6f, 8f));
            for (int b = 0; b < Rate / Block; b++) { v.Produce(); v.Consume(buf); }
            int blocks = seconds * Rate / Block;
            var sw = Stopwatch.StartNew();
            for (int b = 0; b < blocks; b++) { v.Produce(); v.Consume(buf); }
            double audio = blocks * Block / (double)Rate;
            Console.WriteLine($"    {name,-24} {sw.Elapsed.TotalSeconds * 1000 / audio,6:F1} ms per voice-second");
        }
        return 0;
    }

    /// <summary>A scene: one vehicle, what it is doing, where the listener stands.</summary>
    private sealed record Scene(string Name, string Preset, Func<double, float> Speed, Vector3? Listener, bool Inside = false);

    private static readonly Scene[] Scenes =
    {
        new("v8_idle", "v8_muscle", _ => 0f, new Vector3(1f, 1f, -3f)),
        new("v8_rev", "v8_muscle", t => t < 4 ? (float)(t * 8) : 32f - (float)(t - 4) * 6f, new Vector3(2f, 1.2f, 4f)),
        new("i4_cruise_distant", "i4_compact", _ => 14f, new Vector3(40f, 1.6f, -45f)),
        new("bus_idle", "transit_bus", _ => 0f, new Vector3(-3f, 1.6f, 2f)),
        new("bus_pullaway", "transit_bus", t => t < 1.5 ? 0f : (float)((t - 1.5) * 2.5), new Vector3(-3f, 1.6f, 8f)),
        new("truck_distant", "diesel_truck", _ => 22f, new Vector3(-70f, 1.6f, 20f)),
        new("bike_rev", "sportbike", t => (float)(t * 9), new Vector3(1f, 1f, -2f)),
        new("f1_lap", "f1_v10", t => 40f + (float)(25 * Math.Sin(t)), new Vector3(10f, 1f, 0f)),
        new("pickup_inside", "pickup_v8", t => (float)Math.Min(25, t * 5), null, Inside: true),
    };

    private static int RenderSet(string dir, bool wide)
    {
        Directory.CreateDirectory(dir);
        if (wide) RenderWide(dir);
        const double seconds = 6.0;
        foreach (var s in Scenes)
        {
            var v = new EngineVoiceState(MachineRegistry.VehicleFor(s.Preset), Rate, 7)
            {
                TargetSpeed = s.Speed(0),
                CompensateLevel = true,
                Interior = s.Inside,
            };
            v.PlaceAtSpeed(s.Speed(0));
            if (s.Listener is { } l) v.SetListener(l);
            int blocks = (int)(seconds * Rate / Block);
            var all = new float[blocks * Block];
            var buf = new float[Block];
            for (int b = 0; b < blocks; b++)
            {
                v.TargetSpeed = s.Speed(b * Block / (double)Rate);
                v.Produce();
                v.Consume(buf);
                buf.CopyTo(all, b * Block);
            }
            var bytes = new byte[all.Length * 4];
            Buffer.BlockCopy(all, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(dir, s.Name + ".f32"), bytes);
            double ms = all.Sum(x => (double)x * x) / all.Length;
            string hash = Convert.ToHexString(SHA256.HashData(bytes))[..16];
            Console.WriteLine($"    {s.Name,-20} {10 * Math.Log10(ms + 1e-30),7:F1} dBFS RMS  starves {v.Starves}  {hash}");
        }
        return 0;
    }

    /// <summary>Every vehicle preset revving through its gears, and the physical voices built on an
    /// engine (mowers, a piston aeroplane), three seconds each.</summary>
    private static void RenderWide(string dir)
    {
        const double seconds = 3.0;
        int blocks = (int)(seconds * Rate / Block);
        foreach (var key in VehicleProfile.Presets.Keys.OrderBy(k => k))
        {
            var v = new EngineVoiceState(MachineRegistry.VehicleFor(key), Rate, 3) { CompensateLevel = true };
            v.PlaceAtSpeed(2f);
            v.SetListener(new Vector3(4f, 1.4f, -6f));
            var all = new float[blocks * Block];
            var buf = new float[Block];
            for (int b = 0; b < blocks; b++)
            {
                v.TargetSpeed = 2f + 9f * (float)(b * Block / (double)Rate);
                v.Produce();
                v.Consume(buf);
                buf.CopyTo(all, b * Block);
            }
            Write(dir, "vehicle_" + key, all);
        }
        var physical = new (string Name, Func<PhysicalVoiceState> Make)[]
        {
            ("mower_push", () => new MachineVoiceState(SmallMachineSpec.ByName("mower_push"), Rate, 11, 11 * 31 + 7)),
            ("mower_riding", () => new MachineVoiceState(SmallMachineSpec.ByName("mower_riding"), Rate, 12, 12 * 31 + 7)),
            ("ac_condenser", () => new MachineVoiceState(SmallMachineSpec.ByName("ac_condenser"), Rate, 13, 13 * 31 + 7)),
            ("piston_single", () => new AircraftVoiceState(AircraftProfile.ByName("piston_single"), Rate, 5)),
        };
        foreach (var (name, make) in physical)
        {
            var v = make();
            v.SetListener(new Vector3(3f, 1.6f, 8f));
            var all = new float[blocks * Block];
            var buf = new float[Block];
            for (int b = 0; b < blocks; b++) { v.Produce(); v.Consume(buf); buf.CopyTo(all, b * Block); }
            Write(dir, "physical_" + name, all);
        }
    }

    private static void Write(string dir, string name, float[] all)
    {
        var bytes = new byte[all.Length * 4];
        Buffer.BlockCopy(all, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(Path.Combine(dir, name + ".f32"), bytes);
    }

    private static int Diff(string a, string b)
    {
        double worst = 0;
        foreach (var fa in Directory.GetFiles(a, "*.f32").OrderBy(f => f))
        {
            string fb = Path.Combine(b, Path.GetFileName(fa));
            if (!File.Exists(fb)) { Console.WriteLine($"    {Path.GetFileName(fa)}: missing in {b}"); continue; }
            var xa = Floats(fa);
            var xb = Floats(fb);
            int n = Math.Min(xa.Length, xb.Length), differ = 0;
            double maxDiff = 0;
            for (int i = 0; i < n; i++)
            {
                double d = Math.Abs((double)xa[i] - xb[i]);
                if (BitConverter.SingleToInt32Bits(xa[i]) != BitConverter.SingleToInt32Bits(xb[i])) differ++;
                maxDiff = Math.Max(maxDiff, d);
            }
            worst = Math.Max(worst, maxDiff);
            string verdict = differ == 0 && xa.Length == xb.Length ? "bit-identical" : $"{differ} samples differ, largest {20 * Math.Log10(maxDiff + 1e-30):F1} dBFS";
            Console.WriteLine($"    {Path.GetFileNameWithoutExtension(fa),-20} {verdict}");
        }
        Console.WriteLine(worst == 0 ? "\n  Every render bit-identical." : $"\n  Largest difference anywhere: {20 * Math.Log10(worst):F1} dBFS.");
        return 0;
    }

    private static float[] Floats(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var f = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, f, 0, bytes.Length);
        return f;
    }
}
