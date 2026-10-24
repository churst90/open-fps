using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using OpenFPS.Audio.Data;
using OpenFPS.Audio.Fmod;
using OpenFPS.Sound;
using OpenFPS.Sound.Core;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --never-started [n=3000] [rate=150] [kinds=a,b] [out=DIR]: footstep recordings started through the real mixer, n of
/// each kind, with delays scattered across the parent clock's edge (within the next block, on it, a block or two
/// after), with and without pitch and fade points, and counted by the provider's start check (FmodAudioProvider.Starts.cs):
/// how many never left their first sample, per thousand. The footsteps are read from ASSETS/SOUNDS beside the lab's
/// binary (a link to the checkout's will do). OPENFPS_HRTF=0 runs it without the binaural stage.
/// </summary>
public static class NeverStartedSpike
{
    /// <summary>One kind of start: its delays (ms, drawn by <c>Delay</c>), its pitch range, and whether it is a
    /// one-shot (fade points at its start) or a reflection (another group, fade points too). A control is started
    /// so that it cannot move (pitch 0): every one of them must be counted, or the check is blind.</summary>
    private sealed record Kind(string Name, Func<Random, float> Delay, float PitchLow, float PitchHigh, bool OneShot, bool Reflection,
                               bool Control = false);

    private static string? Arg(string[] args, string key)
    {
        foreach (var a in args) if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return a[(key.Length + 1)..];
        return null;
    }

    public static int Run(string[] args)
    {
        int n = int.Parse(Arg(args, "n") ?? "3000", CultureInfo.InvariantCulture);
        double rate = double.Parse(Arg(args, "rate") ?? "150", CultureInfo.InvariantCulture);
        string outDir = Arg(args, "out") ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".cache", "openfps-build", "lib6a-start", "never-started");
        Directory.CreateDirectory(outDir);
        System.Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", Path.Combine(outDir, "mix.wav"));

        string root = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS");
        string steps = Path.Combine(root, "FOOTSTEPS");
        if (!Directory.Exists(steps)) { Console.WriteLine($"FAIL: no footsteps at {steps} (link the checkout's ASSETS beside the lab)"); return 1; }
        var files = Directory.EnumerateFiles(steps, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToList();
        // Every hundredth: the bank's surfaces and shoes, few enough to load before the count starts.
        var ids = files.Where((_, i) => i % 100 == 0).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).ToList();

        int block = 1024;
        float blockMs = block * 1000f / 48000f;
        // Within the next block (a start the mixer reaches already past), on a block's edge to the sample, and a
        // block or two after: all on the parent's clock as the provider reads it.
        float Edge(Random r) => r.Next(3) switch
        {
            0 => (float)r.NextDouble() * blockMs,
            1 => blockMs * r.Next(1, 4) + (r.Next(-2, 3) / 48f),
            _ => (float)r.NextDouble() * 3f * blockMs,
        };
        var kinds = new List<Kind>
        {
            new("now, one-shot", _ => 0f, 1f, 1f, true, false),
            new("edge, one-shot", Edge, 1f, 1f, true, false),
            new("edge, one-shot, pitch", Edge, 0.85f, 1.18f, true, false),
            new("edge, no fades", Edge, 1f, 1f, false, false),
            new("edge, no fades, pitch", Edge, 0.85f, 1.18f, false, false),
            new("now, no fades", _ => 0f, 1f, 1f, false, false),
            new("edge, reflection, pitch", Edge, 0.85f, 1.18f, true, true),
            new("control: pitch 0", Edge, 0f, 0f, true, false, Control: true),
        };
        string? only = Arg(args, "kinds");
        if (only != null)
        {
            var wanted = only.Split(',').Select(int.Parse).ToHashSet();
            kinds = kinds.Where((_, i) => wanted.Contains(i)).ToList();
        }

        var provider = new FmodAudioProvider { Hearing = OpenFPS.Sound.Hearing.HearingSettings.FromEnvironment() };
        if (!provider.Initialize()) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        provider.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);

        int id = 1_000_000;
        var rnd = new Random(6061);
        var pass = Stopwatch.StartNew();
        void Pump(double seconds)
        {
            var until = Stopwatch.StartNew();
            while (until.Elapsed.TotalSeconds < seconds) { provider.Update(); System.Threading.Thread.Sleep(4); }
        }
        SpatialEmitter Step(string sound, float delayMs, float pitch, bool oneShot, bool reflection, float volume) => new()
        {
            EntityId = ++id, Seed = id, SoundId = sound, Type = EmitterType.WorldLocked, Mode = PlaybackMode.Single,
            Position = new Vector3((float)rnd.NextDouble() * 12f - 6f, 0.1f, (float)rnd.NextDouble() * 12f - 6f),
            Range = 60f, MinDistance = 1f, Volume = volume, Pitch = pitch, DelayMs = delayMs,
            IsEvent = oneShot, IsReflection = reflection, Essential = !reflection,
        };

        // Loaded first, so a start waits on nothing but the mixer; these are not counted.
        foreach (var s in ids) provider.PlaySpatialSound(Step(s, 0f, 1f, true, false, 0.01f));
        Pump(3.0);
        var before = provider.StartCheck;

        Console.WriteLine($"never-started: {ids.Count} footstep files, {n} starts a kind at {rate:F0} a second, "
                        + $"binaural {(System.Environment.GetEnvironmentVariable("OPENFPS_HRTF") == "0" ? "off" : "on if loaded")}");
        Console.WriteLine($"  {"kind",-26} {"started",8} {"watched",8} {"judged",8} {"stuck",6} {"per 1000",9}");
        int total = 0;
        bool controlBlind = false;
        foreach (var kind in kinds)
        {
            var at = provider.StartCheck;
            double interval = 1.0 / rate, next = pass.Elapsed.TotalSeconds;
            int count = kind.Control ? Math.Max(1, n / 20) : n;
            for (int i = 0; i < count; i++)
            {
                while (pass.Elapsed.TotalSeconds < next) { provider.Update(); System.Threading.Thread.Sleep(1); }
                next += interval;
                float pitch = kind.PitchLow + (float)rnd.NextDouble() * (kind.PitchHigh - kind.PitchLow);
                provider.PlaySpatialSound(Step(ids[rnd.Next(ids.Count)], kind.Delay(rnd), pitch, kind.OneShot, kind.Reflection, 0.2f));
            }
            // Every start judged: its delay, the margin, and a step's length.
            Pump(2.5);
            var now = provider.StartCheck;
            int watched = now.Watched - at.Watched, judged = now.Checked - at.Checked, stuck = now.NeverStarted - at.NeverStarted;
            if (kind.Control) controlBlind |= stuck != count;
            else total += stuck;
            Console.WriteLine($"  {kind.Name,-26} {count,8} {watched,8} {judged,8} {stuck,6} {(watched > 0 ? stuck * 1000.0 / watched : 0),9:F2}");
        }
        var end = provider.StartCheck;
        Console.WriteLine($"  all: watched {end.Watched - before.Watched}, judged {end.Checked - before.Checked}, "
                        + $"never started {end.NeverStarted - before.NeverStarted} (warm-up {before.NeverStarted})");
        provider.Dispose();
        if (controlBlind) { Console.WriteLine("FAIL: the control was not all counted"); return 1; }
        return total == 0 ? 0 : 2;
    }
}
