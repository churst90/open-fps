using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.AudioEngine.Core.Rail;
using OpenFPS.Client.AudioEngine.Core.Signals;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The render fingerprint (docs/SOUND_LIBRARY_BOUNDARY.md, stage 0): a fixed set of deterministic offline
/// renders, made as the game's models make them, hashed and compared with
/// OpenFPS.Tests/LibraryBoundary/render-fingerprints.tsv, so the library's moves change no bit of a sound.
///
/// Bits depend on the platform's libm too, so the file stores a fingerprint of the maths
/// (<see cref="MathsProbe"/>): where it matches, every render must match to the bit; elsewhere renders are
/// compared by level every 4096 samples, and the test says so.
///
/// An intended change to a sound regenerates in the same commit, saying why:
/// OPENFPS_FINGERPRINT_WRITE=1 dotnet test --filter RenderFingerprintTests.
/// </summary>
public class RenderFingerprintTests
{
    private const int Rate = 48000;
    private readonly ITestOutputHelper _o;
    public RenderFingerprintTests(ITestOutputHelper o) => _o = o;

    /// <summary>Every render, by name. Each returns its buffers; all of them are hashed, in order.</summary>
    private static readonly Dictionary<string, Func<float[][]>> Renders = new()
    {
        // A car's engine at three steady speeds on the dyno, from the start: the whole vehicle bench.
        ["engine.v8_muscle"] = () =>
        {
            var r = VehicleSynth.Render(VehicleProfile.V8Muscle, new List<DriveOrder>
            {
                new(DriverAction.Cranking, 0.5f), new(DriverAction.Idling, 1f),
                new(DriverAction.Holding, 0.7f, 1500f, 0.5f), new(DriverAction.Holding, 0.7f, 3000f, 0.7f),
                new(DriverAction.Holding, 0.7f, 4500f, 1f),
            }, seed: 11);
            return new[] { r.Exhaust, r.Intake, r.Tyres, r.Block };
        },

        // Each door model, one event each.
        ["door.knob.close"] = () => new[] { KnobDoor.RenderGameClose(new KnobDoor.Door { Width = 1.1f, HingeWear = KnobDoor.WearOf(0), Seed = 1 }, Rate, KnobDoor.Shut.Normal) },
        ["door.knob.open"] = () => new[] { KnobDoor.RenderOpen(new KnobDoor.Door { Width = 1.1f, HingeWear = KnobDoor.WearOf(0), Seed = 1 }, Rate, 0.9) },
        ["door.pushbar.open"] = () => new[] { PushBarDoor.RenderOpen(new PushBarDoor.Door { Variant = 0, Seed = 1 }, Rate, 1.4) },
        ["door.sliding.close"] = () => new[] { SlidingDoor.RenderClose(new SlidingDoor.Door { Kind = SlidingDoor.Kind.Patio, Variant = 1, Seed = 2 }, Rate, 1.4) },
        ["door.glass.close"] = () => new[] { GlassDoor.RenderClose(new GlassDoor.Door { Kind = GlassDoor.Kind.Pull, Variant = 1, Seed = 2 }, Rate) },
        ["door.elevator.open"] = () => new[] { ElevatorDoor.RenderOpen(new ElevatorDoor.Door { Variant = 1, Seed = 2 }, Rate, 1.8) },
        ["door.lock.unlock"] = () => new[] { LockCylinder.RenderUnlock(LockCylinder.Host.AluminiumStile, 1, Rate) },
        ["door.car.close"] = () => new[] { CarDoor.Render(true, Rate, 3) },
        ["window.car.down"] = () => new[] { CarWindow.Render(0, 0f, 1f, Rate) },

        // Rain on three surfaces, a square metre a metre away, at 8 mm/h.
        ["rain.asphalt"] = () => new[] { Rain(new RainLayer { Kind = RainSurfaceKind.Hard, Material = "Asphalt", ModulusGPa = 3f }) },
        ["rain.steel"] = () => new[] { Rain(new RainLayer { Kind = RainSurfaceKind.Plate, Material = "Metal", ModulusGPa = 200f,
                                                            Plate = new RainPlate("Metal", 0.0007f, 1.2f, 0.6f) }) },
        ["rain.puddle"] = () => new[] { Rain(new RainLayer { Kind = RainSurfaceKind.Pool, Material = "Water", ModulusGPa = 2.2f }) },

        // A siren on wail, two seconds.
        ["siren.patrol.wail"] = () =>
        {
            var s = new ElectronicSiren(SirenSpec.ByName("patrol"), Rate) { Mode = SirenMode.Wail };
            var x = new float[2 * Rate];
            for (int i = 0; i < x.Length; i++) { s.Step(); x[i] = s.Output; }
            return new[] { x };
        },

        // A light-rail set passing at 15 m/s: every source of the train, summed, for three seconds.
        ["train.light_rail.pass"] = () =>
        {
            var train = new TrainSynth(TrainProfile.ByName("light_rail"), Rate, 41) { Speed = 15f, Notch = 4f };
            train.Place(-15.0 * 1.5);
            var x = new float[3 * Rate];
            for (int i = 0; i < x.Length; i++)
            {
                train.Step();
                float sum = 0f;
                foreach (var source in train.Sources) sum += source.Out;
                x[i] = sum;
            }
            return new[] { x };
        },

        // Thunder from a ground flash 1.5 km away: the game's renderer, on a pinned thread count and rate.
        ["thunder.ground_1500"] = () =>
        {
            var strike = new LightningStrike(31, FlashKind.CloudToGround, new Vector3(1800f, 5000f, -200f), new Vector3(1500f, 0f, 0f),
                                             LightningPhysics.GroundFlashEnergyMedian, 1);
            var parts = Thunder.Render(strike, new Vector3(5f, 1.7f, 5f), new Thunder.Air(12f, 0.9f, 1013.25f, new Vector3(8f, 0f, 3f)),
                                       new Thunder.Options { Threads = 2, SampleRate = 24000 });
            return parts.SelectMany(p => new[] { new[] { p.StartSeconds, p.SampleRate, p.Direction.X, p.Direction.Y, p.Direction.Z }, p.Pressure }).ToArray();
        },

        // A clap, dry: the source of the clap in the traced room (the room itself needs Steam Audio).
        ["clap.dry"] = () => new[] { Applause.RenderClap(Rate, 1) },
    };

    private static float[] Rain(RainLayer surface)
    {
        var synth = new RainSynth(Rate, 9) { Patch = new RainPatch { Layers = new[] { surface.Single() }, ReferenceDistance = 1f }, RainRate = 8f };
        var x = new float[2 * Rate];
        for (int i = 0; i < x.Length; i++) x[i] = synth.Next();
        return x;
    }

    public static IEnumerable<object[]> Names() => Renders.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Names))]
    public void RenderIsUnchanged(string name)
    {
        float[][] buffers = RenderPinned(Renders[name]);
        foreach (var b in buffers)
            foreach (float v in b)
                if (!float.IsFinite(v)) Assert.Fail($"{name}: a sample is not finite");
        string hash = Hash(buffers);
        string envelope = Envelope(buffers);
        string maths = MathsProbe();
        int samples = buffers.Sum(b => b.Length);

        var stored = Read();
        if (Environment.GetEnvironmentVariable("OPENFPS_FINGERPRINT_WRITE") == "1")
        {
            if (stored.Maths != maths) stored.Rows.Clear();   // hashes from other maths would never match here
            stored.Maths = maths;
            stored.Rows[name] = (hash, samples, envelope);
            Write(stored);
            _o.WriteLine($"{name}: written {hash}");
            return;
        }

        Assert.True(stored.Rows.TryGetValue(name, out var row),
            $"{name} has no stored fingerprint: run with OPENFPS_FINGERPRINT_WRITE=1 and commit the file.");
        if (stored.Maths == maths)
        {
            Assert.True(row.Hash == hash,
                $"{name} renders differently from its stored fingerprint ({samples} samples, {row.Samples} stored; "
                + $"levels {Compare(row.Envelope, envelope).Describe()}). If the change is intended, regenerate in the same "
                + "commit with the reason (docs/SOUND_LIBRARY_BOUNDARY.md, stage 0).");
            _o.WriteLine($"{name}: {hash}, bit for bit");
            return;
        }

        // Another maths library: the bits can differ where libm rounds differently, so compare levels.
        Assert.Equal(row.Samples, samples);
        var diff = Compare(row.Envelope, envelope);
        _o.WriteLine($"{name}: the maths differ from the machine the fingerprints were made on; levels {diff.Describe()}");
        Assert.True(diff.Within, $"{name} renders at different levels from its stored fingerprint: {diff.Describe()}");
    }

    /// <summary>The lab levers a render reads, held at their game values while it runs.</summary>
    private static float[][] RenderPinned(Func<float[][]> render)
    {
        bool jet = EngineSynth.ValveJetNoise, diesel = EngineSynth.DebugLegacyDiesel, rigid = EngineSynth.DebugRigidValves;
        int solo = EngineSynth.DebugSoloTailpipe;
        double keeper = KnobDoor.KeeperBendsStrike;
        var stems = (KnobDoor.StemFolder, PushBarDoor.StemFolder, SlidingDoor.StemFolder, GlassDoor.StemFolder,
                     ElevatorDoor.StemFolder, LockCylinder.StemFolder);
        try
        {
            EngineSynth.ValveJetNoise = true;
            EngineSynth.DebugLegacyDiesel = false;
            EngineSynth.DebugRigidValves = false;
            EngineSynth.DebugSoloTailpipe = -1;
            KnobDoor.KeeperBendsStrike = 0.3;
            KnobDoor.StemFolder = PushBarDoor.StemFolder = SlidingDoor.StemFolder = GlassDoor.StemFolder
                = ElevatorDoor.StemFolder = LockCylinder.StemFolder = null;
            return render();
        }
        finally
        {
            EngineSynth.ValveJetNoise = jet;
            EngineSynth.DebugLegacyDiesel = diesel;
            EngineSynth.DebugRigidValves = rigid;
            EngineSynth.DebugSoloTailpipe = solo;
            KnobDoor.KeeperBendsStrike = keeper;
            (KnobDoor.StemFolder, PushBarDoor.StemFolder, SlidingDoor.StemFolder, GlassDoor.StemFolder,
             ElevatorDoor.StemFolder, LockCylinder.StemFolder) = stems;
        }
    }

    internal static string Hash(float[][] buffers)
    {
        using var sha = SHA256.Create();
        foreach (var b in buffers)
        {
            sha.TransformBlock(BitConverter.GetBytes(b.Length), 0, 4, null, 0);
            byte[] bytes = MemoryMarshal.AsBytes(b.AsSpan()).ToArray();
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);   // never on the machines this runs on
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!, 0, 16).ToLowerInvariant();
    }

    /// <summary>Each buffer's level every 4096 samples, dB, to 0.01 dB; buffers separated by ';'.</summary>
    internal static string Envelope(float[][] buffers)
        => string.Join(";", buffers.Select(b =>
        {
            var levels = new List<string>();
            for (int at = 0; at < b.Length; at += 4096)
            {
                double e = 0;
                int n = Math.Min(4096, b.Length - at);
                for (int i = 0; i < n; i++) e += b[at + i] * (double)b[at + i];
                levels.Add((10 * Math.Log10(e / n + 1e-30)).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return string.Join(",", levels);
        }));

    internal readonly record struct LevelDiff(bool Within, double Worst, int Blocks)
    {
        public string Describe() => Blocks == 0 ? "not comparable" : $"worst block {Worst:F2} dB apart over {Blocks} blocks";
    }

    /// <summary>Within: every block within 40 dB of its buffer's loudest is within 1 dB of the stored one.</summary>
    internal static LevelDiff Compare(string stored, string now)
    {
        var a = stored.Split(';');
        var b = now.Split(';');
        if (a.Length != b.Length) return new LevelDiff(false, double.PositiveInfinity, 0);
        double worst = 0;
        int blocks = 0;
        bool within = true;
        for (int k = 0; k < a.Length; k++)
        {
            var x = a[k].Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var y = b[k].Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (x.Length != y.Length) return new LevelDiff(false, double.PositiveInfinity, blocks);
            double loudest = x.Length == 0 ? 0 : x.Max();
            for (int i = 0; i < x.Length; i++)
            {
                if (x[i] < loudest - 40) continue;
                double d = Math.Abs(x[i] - y[i]);
                worst = Math.Max(worst, d);
                blocks++;
                if (d > 1.0) within = false;
            }
        }
        return new LevelDiff(within, worst, blocks);
    }

    /// <summary>A fingerprint of this machine's floating-point maths: the bits of every libm function the
    /// models call over a spread of arguments, and the runtime's vector width.</summary>
    internal static string MathsProbe()
    {
        var sb = new StringBuilder();
        sb.Append(Vector<float>.Count).Append(';');
        using var sha = SHA256.Create();
        var bytes = new List<byte>();
        for (int i = 0; i < 4096; i++)
        {
            float x = -60f + i * (120f / 4096f) + 0.0123f;
            float u = x / 61f;                 // in (-1, 1)
            float p = MathF.Abs(x) + 1e-3f;
            double xd = x * 1.0000001, pd = p * 1.0000001;
            foreach (float f in new[]
            {
                MathF.Sin(x), MathF.Cos(x), MathF.Tan(x), MathF.Exp(x / 8f), MathF.Log(p), MathF.Log10(p), MathF.Log2(p),
                MathF.Pow(p, 1.37f), MathF.Pow(0.5f + p / 64f, -2.3f), MathF.Tanh(x / 10f), MathF.Atan(x), MathF.Atan2(x, 3f),
                MathF.Asin(u), MathF.Acos(u), MathF.Cbrt(x), MathF.Sinh(x / 10f), MathF.Cosh(x / 10f), MathF.Sqrt(p),
                MathF.Exp(-p),
            })
                bytes.AddRange(BitConverter.GetBytes(f));
            foreach (double d in new[]
            {
                Math.Sin(xd), Math.Cos(xd), Math.Tan(xd), Math.Exp(xd / 8), Math.Log(pd), Math.Log10(pd), Math.Log2(pd),
                Math.Pow(pd, 1.37), Math.Tanh(xd / 10), Math.Atan(xd), Math.Atan2(xd, 3), Math.Asin(u), Math.Acos(u),
                Math.Cbrt(xd), Math.Sinh(xd / 10), Math.Cosh(xd / 10), Math.Sqrt(pd), Math.Exp(-pd),
            })
                bytes.AddRange(BitConverter.GetBytes(d));
        }
        sb.Append(Convert.ToHexString(SHA256.HashData(bytes.ToArray()), 0, 12).ToLowerInvariant());
        return sb.ToString();
    }

    // ── The stored file ─────────────────────────────────────────────────────────────────────────

    private sealed class Stored
    {
        public string Maths = "";
        public readonly SortedDictionary<string, (string Hash, int Samples, string Envelope)> Rows = new(StringComparer.Ordinal);
    }

    private static string FilePath([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "LibraryBoundary", "render-fingerprints.tsv");

    private static Stored Read()
    {
        var s = new Stored();
        if (!File.Exists(FilePath())) return s;
        foreach (var line in File.ReadAllLines(FilePath()))
        {
            if (line.StartsWith("# maths\t", StringComparison.Ordinal)) { s.Maths = line.Substring(8); continue; }
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("name\t", StringComparison.Ordinal)) continue;
            var c = line.Split('\t');
            s.Rows[c[0]] = (c[1], int.Parse(c[2]), c[3]);
        }
        return s;
    }

    private static void Write(Stored s)
    {
        var sb = new StringBuilder();
        sb.Append("# The render fingerprint: OpenFPS.Tests/RenderFingerprintTests.cs, docs/SOUND_LIBRARY_BOUNDARY.md stage 0.\n");
        sb.Append("# Regenerate only in the commit that changes a sound on purpose, and say why there.\n");
        sb.Append("# maths\t").Append(s.Maths).Append('\n');
        sb.Append("name\thash\tsamples\tlevels\n");
        foreach (var (name, r) in s.Rows) sb.Append(name).Append('\t').Append(r.Hash).Append('\t').Append(r.Samples).Append('\t').Append(r.Envelope).Append('\n');
        File.WriteAllText(FilePath(), sb.ToString());
    }

    [Fact]
    public void EveryRenderIsStoredAndNothingElse()
    {
        if (Environment.GetEnvironmentVariable("OPENFPS_FINGERPRINT_WRITE") == "1") return;
        var stored = Read();
        Assert.Equal(Renders.Keys.OrderBy(k => k, StringComparer.Ordinal), stored.Rows.Keys);
    }

    [Fact]
    public void EveryRenderIsTheSameTwice()
    {
        // The same render twice in one process, bit for bit, on the cheap renders (the doors and engine share
        // these code paths and seeding).
        foreach (var name in new[] { "rain.steel", "siren.patrol.wail", "clap.dry", "door.car.close" })
            Assert.Equal(Hash(RenderPinned(Renders[name])), Hash(RenderPinned(Renders[name])));
    }
}
