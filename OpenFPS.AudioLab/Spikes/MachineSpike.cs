using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// What machines there are, and what they are made of.
///
/// A machine is a parts list — an engine, an exhaust somewhere behind an intake, a body, a set of
/// tyres — and until now that list lived only in C#, where an author could read it and not write it.
/// This prints the list for every machine the game knows, and writes any of them out as the JSON a
/// map author would edit.
///
/// Command line:
///   --machines                 what there is, one line each
///   --machines v8_muscle       one machine, part by part
///   --machines export=DIR      the whole library written out as parts lists
/// </summary>
public static class MachineSpike
{
    public static int Run(string[] args)
    {
        string? exportTo = args.FirstOrDefault(a => a.StartsWith("export=", StringComparison.OrdinalIgnoreCase))?[7..];
        if (exportTo != null) return Export(exportTo);

        string? only = args.FirstOrDefault(a => !a.StartsWith("--") && MachineRegistry.Knows(a));
        if (only != null) return Describe(only);

        Console.WriteLine("\n  Machines. * is authored — a file, not a factory function.\n");
        Console.WriteLine("    machine              parts  engine                             level dB  name");
        foreach (string id in MachineRegistry.Ids.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var def = MachineRegistry.Find(id)!;
            var v = MachineRegistry.VehicleFor(id);
            string mark = MachineRegistry.Authored.ContainsKey(id) ? "*" : " ";
            Console.WriteLine($"  {mark} {id,-18}  {def.Parts.Count,5}  {v.Engine.Name,-33}  {v.SourceLevelDb,8:F0}  {v.Name}");
        }
        Console.WriteLine($"\n  {MachineRegistry.Ids.Count()} machine(s), {MachineRegistry.Authored.Count} of them authored.");
        Console.WriteLine("  --machines <id> for one of them; --machines export=DIR to write them out.\n");
        return 0;
    }

    private static int Describe(string id)
    {
        var def = MachineRegistry.Find(id)!;
        Console.WriteLine($"\n  {id}: {def.Name}");
        if (def.Base.Length > 0) Console.WriteLine($"  based on {def.Base}");
        Console.WriteLine();
        foreach (var p in def.Parts)
        {
            string at = p.At == System.Numerics.Vector3.Zero
                ? "" : $" at ({p.At.X:F2}, {p.At.Y:F2}, {p.At.Z:F2})";
            string profile = p.Profile.Length > 0 ? $" {p.Profile}" : "";
            string material = p.Material.Length > 0 ? $" of {p.Material}" : "";
            string level = p.LevelDb > 0f ? $" at {p.LevelDb:F0} dB" : "";
            Console.WriteLine($"    {p.Model,-9}{profile}{material}{at}{level}");
            if (p.Series.Count > 0)
                Console.WriteLine($"      series: {string.Join(", ", p.Series.Select(x => x.ToString("G4")))}");
            foreach (var kv in p.Settings.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"      {kv.Key} = {kv.Value:G6}");
        }
        Console.WriteLine();
        Console.WriteLine(MachineRegistry.ToJson(def));
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// A machine driving past you, first as one voice and then as two.
    ///
    /// The thing being auditioned is GEOMETRY, not timbre: with one voice a car is a point somewhere
    /// between its two ends; with two, the intake goes past you before the exhaust does, which is
    /// most of how a listener knows which way something is pointing and how long it is. The level is
    /// identical either way by construction (the taps sum to the single voice), so anything you can
    /// hear between the two passes is the rig.
    ///
    /// Command line: --machine-pass [id] [kmh=..] [side=..] [one] [two] [hard]
    ///
    /// `hard` accelerates the machine through the pass instead of holding a speed, which is worth
    /// knowing about before judging how loud anything is: a declared source level is measured at FULL
    /// LOAD, and an engine cruising at a steady 50 km/h in a tall gear is doing a fraction of that
    /// work. A car that idles past you quietly and shouts when it is opened up is not a bug.
    /// </summary>
    public static int Pass(string[] args)
    {
        string id = args.FirstOrDefault(a => !a.StartsWith("--") && MachineRegistry.Knows(a)) ?? "v8_muscle";
        float kmh = Arg(args, "kmh", 50f), side = Arg(args, "side", 5f);
        bool onlyOne = args.Contains("one"), onlyTwo = args.Contains("two"), hard = args.Contains("hard");

        AcousticRegistry.Initialize();
        var v = MachineRegistry.VehicleFor(id);
        float separation = Vector3.Distance(
            new Vector3(0f, v.ExhaustHeight, v.ExhaustOffsetZ),
            new Vector3(0f, v.IntakeHeight, v.IntakeOffsetZ));

        Console.WriteLine($"\n  {v.Name} — {v.Engine.Name}");
        Console.WriteLine($"  Outlets {separation:F2} m apart: the airbox at z {v.IntakeOffsetZ:+0.00;-0.00} and "
                        + $"{v.IntakeHeight:F2} m up, the tailpipe at z {v.ExhaustOffsetZ:+0.00;-0.00} and {v.ExhaustHeight:F2} m up.");
        Console.WriteLine($"  They subtend {Localisation.SubtendedDegrees(separation, side):F0} degrees at {side:F0} m, "
                        + $"and merge into one source past {Localisation.MergingDistance(separation):F0} m.\n");

        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("  (live playback unavailable)"); return 1; }
        try
        {
            var ear = new Vector3(0f, 1.7f, 0f);
            provider.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
            for (int i = 0; i < 30; i++) { provider.Update(); Thread.Sleep(8); }

            if (!onlyTwo) Drive(provider, v, id, ear, kmh, side, split: false, hard);
            if (!onlyOne) Drive(provider, v, id, ear, kmh, side, split: true, hard);
        }
        finally { provider.Dispose(); }
        return 0;
    }

    private const int CarId = -91500;
    private const int IntakeId = -91501;

    private static void Drive(FmodAudioProvider provider, VehicleProfile v, string key,
                              Vector3 ear, float kmh, float side, bool split, bool hard = false)
    {
        float speed = kmh / 3.6f;
        // Opened up: the driver chases a speed well past the one it starts at, so the engine is under
        // load and changing gear as it goes by — which is the state a declared source level describes
        // and the state anybody notices a vehicle in.
        float top = hard ? speed * 3f : speed;
        float from = -130f, to = 130f;
        var level = v.SourceLevelDb;
        var (gain, reference) = Loudness.Place(level);
        float range = Loudness.AudibleRange(level);

        Console.WriteLine((split
            ? "  ── TWO voices: the airbox at the front, the tailpipe at the back."
            : "  ── ONE voice: the whole machine at a point between its ends.")
            + (hard ? $"  Accelerating to {top * 3.6f:F0} km/h." : "  Steady speed."));

        // Where each outlet is. Heard as one thing, the voice sits between them and nearer the
        // exhaust — VehicleProfile.ExhaustEmitterBias — which is the compromise the split removes.
        Vector3 Rear(Vector3 p) => p + new Vector3(0f, v.ExhaustHeight,
            v.ExhaustOffsetZ * (split ? 1f : VehicleProfile.ExhaustEmitterBias));
        Vector3 Front(Vector3 p) => p + new Vector3(0f, v.IntakeHeight, v.IntakeOffsetZ);

        SpatialEmitter Exhaust(Vector3 p, Vector3 vel) => new()
        {
            EntityId = CarId, SoundId = "engine:" + key, IsSynth = true, EngineKey = key,
            EngineSpeed = speed, EngineRunning = true,
            Type = EmitterType.WorldLocked, Mode = PlaybackMode.LoopOne,
            Position = Rear(p), ApparentPosition = Rear(p), Velocity = vel,
            Volume = gain, Range = range, MinDistance = MathF.Max(reference, 3f), Pitch = 1f,
            TargetRegionId = AcousticConstants.GlobalRegionId, EnableReverb = true,
        };
        SpatialEmitter Intake(Vector3 p, Vector3 vel) => new()
        {
            EntityId = IntakeId, SoundId = "engine-intake", IsSynth = true, IntakeOfEntity = CarId,
            Type = EmitterType.WorldLocked, Mode = PlaybackMode.LoopOne,
            Position = Front(p), ApparentPosition = Front(p), Velocity = vel,
            Volume = gain, Range = range, MinDistance = MathF.Max(reference, 3f), Pitch = 1f,
            TargetRegionId = AcousticConstants.GlobalRegionId, EnableReverb = true,
        };

        var pos = new Vector3(side, 0.6f, from);
        var vel = new Vector3(0f, 0f, speed);
        provider.PlaySpatialSound(Exhaust(pos, vel));
        // A moment for the engine to prime before the other half of it asks for audio.
        for (int i = 0; i < 20; i++) { provider.Update(); Thread.Sleep(8); }
        if (split) provider.PlaySpatialSound(Intake(pos, vel));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        double last = 0; float lastReport = -10f;
        while (pos.Z < to)
        {
            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)(now - last); last = now;
            if (hard) speed = MathF.Min(top, speed + 2.4f * dt);
            vel = new Vector3(0f, 0f, speed);
            pos = new Vector3(side, 0.6f, pos.Z + speed * dt);
            provider.UpdateSpatialAttributes(Exhaust(pos, vel));
            if (split) provider.UpdateSpatialAttributes(Intake(pos, vel));
            if (now - lastReport >= 1.5)
            {
                lastReport = (float)now;
                float d = Vector3.Distance(pos, ear);
                // The TRUE separation, not this pass's voice placement: the question being asked
                // is what the machine's ends subtend, which does not depend on how it is voiced.
                float sep = Vector3.Distance(
                    new Vector3(0f, v.ExhaustHeight, v.ExhaustOffsetZ),
                    new Vector3(0f, v.IntakeHeight, v.IntakeOffsetZ));
                Console.WriteLine($"     {d,5:F0} m away, outlets {Localisation.SubtendedDegrees(sep, d),4:F0} degrees apart");
            }
            provider.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
            provider.Update();
            Thread.Sleep(6);
        }
        if (split) provider.StopSound(IntakeId);
        provider.StopSound(CarId);
        for (int i = 0; i < 60; i++) { provider.Update(); Thread.Sleep(8); }
        Console.WriteLine();
    }

    /// <summary>
    /// How loud a machine actually is at a speed — and what that becomes at a distance.
    ///
    /// The declared `SourceLevelDb` every preset carries is measured at FULL LOAD, which is the right
    /// anchor for the mix and the wrong number to have in your head when a car cruises past. This
    /// renders the game's own live voice at a steady speed, measures the pressure it makes, and then
    /// walks it out through the mixer's own distance law (Loudness.RenderedGain) so the two questions
    /// — "how loud is this thing" and "what will I hear" — are answered with one set of numbers.
    ///
    /// Command line: --machine-levels [id ...] [kmh=..]
    /// </summary>
    public static int Levels(string[] args)
    {
        float kmh = Arg(args, "kmh", 50f);
        var ids = args.Where(a => !a.StartsWith("--") && MachineRegistry.Knows(a)).ToList();
        if (ids.Count == 0) ids = MachineRegistry.Ids.ToList();

        Console.WriteLine($"\n  What each machine makes at a steady {kmh:F0} km/h, and what reaches a listener.");
        Console.WriteLine("  full = its declared level at FULL LOAD, 1 m. cruise = measured here, 1 m.");
        Console.WriteLine("  The dBFS columns are what the mixer renders at that distance.\n");
        Console.WriteLine("    machine            full dB   idle dB   cruise dB   under      5 m     20 m     50 m    130 m");
        foreach (string id in ids)
        {
            var v = MachineRegistry.VehicleFor(id);
            float idleDb = CruiseLevelDb(v, 0f);
            float cruiseDb = CruiseLevelDb(v, kmh / 3.6f);
            // Placed the way the game places it: a machine is as big as the distance between the
            // ends it radiates from, and the gain is paid down as the reference widens.
            float extent = Vector3.Distance(
                new Vector3(0f, v.ExhaustHeight, v.ExhaustOffsetZ),
                new Vector3(0f, v.IntakeHeight, v.IntakeOffsetZ));
            var (gain, reference) = Loudness.Place(v.SourceLevelDb, extent);
            float range = Loudness.AudibleRange(v.SourceLevelDb);
            // The voice renders the pressure it actually makes, so the cruise/full difference is
            // already IN the signal; the placement below is the same for both.
            float under = cruiseDb - v.SourceLevelDb;
            string At(float d)
            {
                float g = Loudness.RenderedGain(gain, reference, range, d);
                if (g <= 0f) return "   —  ";
                // Rendered gain, the voice's own headroom, and how far under full load it is running.
                return $"{20f * MathF.Log10(g) - VehicleProfile.PeakHeadroomDb + under,6:F0}";
            }
            Console.WriteLine($"    {id,-16}  {v.SourceLevelDb,7:F0}   {idleDb,7:F0}   {cruiseDb,9:F0}   {under,5:F0}   {At(5f)}   {At(20f)}   {At(50f)}   {At(130f)}");
        }
        Console.WriteLine("\n  dBFS is RMS. A steady cruise is a long way under full load, by design and by physics.\n");
        return 0;
    }

    /// <summary>The RMS the game's live voice makes at a steady speed, as dB SPL at one metre.</summary>
    private static float CruiseLevelDb(VehicleProfile v, float metresPerSecond)
    {
        var voice = new OpenFPS.Client.AudioEngine.Fmod.EngineVoiceState(v, 44100f, 11)
        { TargetSpeed = metresPerSecond };
        voice.PlaceAtSpeed(metresPerSecond);
        var block = new float[1024];
        double sum = 0; int n = 0;
        for (int b = 0; b < 44100 * 2 / 1024; b++)
        {
            voice.Produce();
            voice.Consume(block);
            if (b < 10) continue;                     // the envelope fading in
            foreach (float x in block) { sum += x * x; n++; }
        }
        float rms = MathF.Sqrt((float)(sum / Math.Max(1, n)));
        // Back into pascals: the voice divides by the pressure that maps to full scale.
        float pa = rms * v.PascalsAtFullScale;
        return 20f * MathF.Log10(MathF.Max(1e-9f, pa) / 20e-6f);
    }

    private static float Arg(string[] args, string name, float fallback)
    {
        foreach (var a in args)
            if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)
                && float.TryParse(a[(name.Length + 1)..], out float x)) return x;
        return fallback;
    }

    /// <summary>
    /// Writes the built-in library out as parts lists.
    ///
    /// On demand rather than checked in, deliberately. An exported file is a COPY of what C# says,
    /// and an authored machine overrides the built-in of the same name — so a library exported into
    /// the game's own machines/ folder would quietly freeze every car at the numbers it had on the
    /// day it was written. Export it somewhere to read, copy the one you want to change.
    /// </summary>
    private static int Export(string dir)
    {
        Directory.CreateDirectory(dir);
        int n = 0;
        foreach (string id in VehicleProfile.Presets.Keys)
        {
            var def = MachineRegistry.Describe(VehicleProfile.ByName(id), id);
            File.WriteAllText(Path.Combine(dir, id + ".json"), MachineRegistry.ToJson(def));
            n++;
        }
        Console.WriteLine($"\n  {n} machine(s) written to {Path.GetFullPath(dir)}");
        Console.WriteLine("  A copy of the library, not the library: drop one into the game's machines/");
        Console.WriteLine("  folder only if you mean to override it.\n");
        return 0;
    }
}
