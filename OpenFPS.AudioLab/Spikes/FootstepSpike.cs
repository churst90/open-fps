using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// Footsteps, synthesized from what the foot and the ground are made of.
///
/// Renders a walk across each surface in each kind of shoe and writes them out, plus the numbers that
/// decide how each one sounds — the contact time, the corner it implies, how much of the roughness
/// the sole flows into, and whether the floor rings.
///
/// Command line:
///   --footsteps                          every surface, every shoe, walking
///   --footsteps concrete gravel          only those surfaces
///   --footsteps shoe=dress run steps=8   one shoe, running, a longer walk
///   --footsteps table                    just the numbers, no rendering
/// </summary>
public static class FootstepSpike
{
    private static readonly string[] Surfaces = { "Concrete", "Wood", "Grass", "Gravel", "Dirt", "Metal", "Carpet" };

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();

        string? compare = args.FirstOrDefault(a => a.StartsWith("compare=", StringComparison.OrdinalIgnoreCase))?[8..];
        if (compare != null) return Compare(compare, args);

        var surfaces = args.Where(a => !a.StartsWith("--") && AcousticRegistry.IsKnown(a)).ToList();
        if (surfaces.Count == 0) surfaces.AddRange(Surfaces);

        string? onlyShoe = args.FirstOrDefault(a => a.StartsWith("shoe=", StringComparison.OrdinalIgnoreCase))?[5..];
        var shoes = onlyShoe != null
            ? new List<Shoe> { Shoe.ByName(onlyShoe) }
            : Shoe.Presets.Values.Select(f => f()).ToList();

        bool running = args.Contains("run");
        float mass = Arg(args, "kg", 78f);
        int steps = (int)Arg(args, "steps", 6f);
        float speed = running ? 4.2f : 1.4f;

        Table(surfaces, shoes, mass, speed);
        if (args.Contains("table")) return 0;

        string dir = Path.Combine(AppContext.BaseDirectory, "logs", "footsteps");
        Directory.CreateDirectory(dir);
        Console.WriteLine($"\n  Rendering {(running ? "a run" : "a walk")} of {steps} steps, {mass:F0} kg.\n");

        foreach (var shoe in shoes)
        {
            foreach (string surface in surfaces)
            {
                var walk = Walk(surface, shoe, mass, speed, steps, running);
                string name = $"{surface.ToLowerInvariant()}_{shoe.Name.Replace(' ', '_')}{(running ? "_run" : "")}.wav";
                File.WriteAllBytes(Path.Combine(dir, name), ToWav16(walk, Footsteps.SampleRate));
                Console.WriteLine($"    {name,-34}  {walk.Length / (float)Footsteps.SampleRate,5:F1}s");
            }
        }

        Console.WriteLine($"\n  -> {dir}");
        Console.WriteLine("  Play one with:  paplay <file>\n");
        return 0;
    }

    /// <summary>
    /// The numbers that decide the sound, before any of it is rendered.
    ///
    /// This is the table worth arguing with: if the contact time for a trainer on concrete is not
    /// several times a leather heel's, the model is wrong and no amount of listening will fix it.
    /// </summary>
    private static void Table(List<string> surfaces, List<Shoe> shoes, float mass, float speed)
    {
        Console.WriteLine("\n  What sets the sound. tau = how long the heel is in contact; the impact can hold");
        Console.WriteLine("  nothing above 1/tau. conform = how much of the grit the sole flows into rather");
        Console.WriteLine("  than rattling over it. ring = the floor's own note, if it has one.\n");
        Console.WriteLine("    surface     shoe        tau ms  thump Hz  tread Hz  grain Hz   scuff   shoe Hz  floor Hz   level dB");

        float mEff = mass * Footsteps.EffectiveMassFraction;
        float v = MathF.Max(0.15f, speed * Footsteps.HeelVelocityRatio);

        foreach (string surface in surfaces)
        {
            var ground = AcousticRegistry.GetProperties(surface);
            float span = Footsteps.FreeSpanM(surface), thick = Footsteps.DeckThicknessM(surface);
            float ringHz = span > 0f ? PanelAcoustics.RingHz(ground, span, span * 2.4f, thick) : 0f;

            foreach (var shoe in shoes)
            {
                var sole = AcousticRegistry.GetProperties(shoe.SoleMaterial);
                float e = Footsteps.ContactModulus(sole, ground);
                float tau = Footsteps.ContactSeconds(mEff + shoe.MassKg, shoe.HeelRadiusM, e, v);
                float conform = MathF.Max(Footsteps.Conformity(sole), shoe.TreadGrip);
                // The grain's own contact, which is where everything audible on a hard floor lives.
                var (grainMm, coverage) = Footsteps.ContactTexture(surface);
                float grainHz = Footsteps.ImpactCornerHz(
                    Footsteps.GrainContactSeconds(tau, shoe.HeelRadiusM, grainMm));
                float scuff = coverage * (1f - conform);
                // The two scales between the heel and the grit: the sole's own lumps, and the sole
                // as a struck panel. Both live in 60-400 Hz, which is where a real footstep keeps
                // its body and where the model measured a hole against a recording.
                float treadHz = Footsteps.ImpactCornerHz(
                    Footsteps.GrainContactSeconds(tau, shoe.HeelRadiusM, shoe.TreadBlockM * 1000f));
                float shoeHz = PanelAcoustics.RingHz(sole, shoe.SoleRadiusM * 1.15f,
                                                     shoe.SoleRadiusM * 1.15f * 2.5f, shoe.SoleThicknessM);
                float db = Footsteps.MeasuredLevelDb(new Footstep
                {
                    Surface = surface, Shoe = shoe, BodyMassKg = mass, SpeedMps = speed, Seed = 1,
                });

                Console.WriteLine($"    {surface,-11} {shoe.Name,-11} {tau * 1000f,6:F1}  {Footsteps.ImpactCornerHz(tau),8:F0}  {treadHz,8:F0}  {grainHz,8:F0}"
                                + $"   {scuff,5:F2}   {(shoeHz > 0f ? shoeHz.ToString("F0") : "—"),7}   {(ringHz > 0f ? ringHz.ToString("F0") : "—"),8}   {db,8:F0}");
            }
        }
        Console.WriteLine();
    }

    /// <summary>
    /// The synthesised step against a REAL one, band by band.
    ///
    /// The instrument this whole thing should have had before anybody was asked to listen. A model
    /// can have every parameter defensible and still be unlistenable, and the difference shows up
    /// here in about a second: a band that is fifteen decibels out is a missing mechanism, not a
    /// matter of taste.
    ///
    /// Point it at a folder of single-footstep WAVs — `tools/split_footsteps.py` makes them out of a
    /// recording of somebody walking.
    ///
    ///   --footsteps compare=inbox/"foot steps sounds"/split/concrete_walk [surface=Concrete] [shoe=sneaker]
    /// </summary>
    private static int Compare(string dir, string[] args)
    {
        var files = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.wav").OrderBy(f => f).ToArray()
            : Array.Empty<string>();
        if (files.Length == 0)
        {
            Console.WriteLine($"  no .wav files in {dir}");
            return 1;
        }

        string surface = args.FirstOrDefault(a => a.StartsWith("surface=", StringComparison.OrdinalIgnoreCase))?[8..] ?? "Concrete";
        string shoeKey = args.FirstOrDefault(a => a.StartsWith("shoe=", StringComparison.OrdinalIgnoreCase))?[5..] ?? "sneaker";
        var shoe = Shoe.ByName(shoeKey);
        float mass = Arg(args, "kg", 78f);
        float speed = args.Contains("run") ? 4.2f : 1.4f;

        // The real thing: every step in the folder, averaged. One step is one sample of a random
        // process — which grit it happened to land on — and averaging is what turns that into a
        // measurement of the surface rather than of one footfall.
        var realAcc = new double[Spectrum.BandCount];
        int used = 0;
        foreach (string f in files)
        {
            var x = OpenFPS.Client.AudioEngine.Core.WeaponSynth.ReadWav16Mono(File.ReadAllBytes(f));
            if (x.Length < 512) continue;
            var e = Spectrum.BandEnergy(x, Footsteps.SampleRate);
            for (int i = 0; i < e.Length; i++) realAcc[i] += e[i];
            used++;
        }
        var real = Normalise(realAcc);

        // ...and the same measurement of the model, over as many steps.
        var synthAcc = new double[Spectrum.BandCount];
        for (int seed = 0; seed < Math.Max(8, Math.Min(used, 24)); seed++)
        {
            var buf = Footsteps.Render(new Footstep
            {
                Surface = surface, Shoe = shoe, BodyMassKg = mass, SpeedMps = speed, Seed = seed,
            });
            var e = Spectrum.BandEnergy(buf, Footsteps.SampleRate);
            for (int i = 0; i < e.Length; i++) synthAcc[i] += e[i];
        }
        var synth = Normalise(synthAcc);

        Console.WriteLine($"\n  {used} real step(s) from {Path.GetFileName(dir)}  vs  synthesised {surface}, {shoe.Name}");
        Console.WriteLine("  Both normalised to their own total, so this is SHAPE and not level.\n");
        Console.WriteLine("    band              real    synth     gap");
        float worst = 0f; int worstBand = 0;
        for (int i = 0; i < Spectrum.BandCount; i++)
        {
            float gap = synth[i] - real[i];
            if (MathF.Abs(gap) > MathF.Abs(worst)) { worst = gap; worstBand = i; }
            Console.WriteLine($"    {Spectrum.BandName(i),-14} {real[i],6:F1}   {synth[i],6:F1}   {gap,+6:F1}");
        }
        Console.WriteLine($"\n  worst band: {Spectrum.BandName(worstBand)} at {worst:+0.0;-0.0} dB");
        Console.WriteLine("  (positive means the model has too much there)\n");
        return 0;
    }

    private static float[] Normalise(double[] energy)
    {
        double total = 0;
        foreach (double v in energy) total += v;
        if (total <= 0) total = 1e-12;
        var db = new float[energy.Length];
        for (int i = 0; i < energy.Length; i++)
            db[i] = 10f * MathF.Log10((float)Math.Max(energy[i] / total, 1e-9));
        return db;
    }

    /// <summary>A few steps, alternating feet, with the cadence the speed implies.</summary>
    private static float[] Walk(string surface, Shoe shoe, float mass, float speed, int steps, bool running)
    {
        // Cadence from the gait: a stride is about 0.8 of the walker's height, so step time is
        // roughly stride over speed. A walk is about two steps a second, a run nearly three.
        float stride = running ? 1.5f : 0.75f;
        float period = Math.Clamp(stride / MathF.Max(0.3f, speed), 0.18f, 1.2f);

        var rendered = new List<(int At, float[] Buf)>();
        int longest = 0;
        for (int i = 0; i < steps; i++)
        {
            // A little jitter, because nobody walks to a metronome and a perfectly even walk is the
            // single most artificial thing a game does with footsteps.
            var rng = new Random(i * 31 + 7);
            float at = i * period * (0.94f + 0.12f * (float)rng.NextDouble());
            var buf = Footsteps.Render(new Footstep
            {
                Surface = surface, Shoe = shoe, BodyMassKg = mass, SpeedMps = speed, Seed = i,
            });
            int start = (int)(at * Footsteps.SampleRate);
            rendered.Add((start, buf));
            longest = Math.Max(longest, start + buf.Length);
        }

        var mix = new float[longest];
        foreach (var (start, buf) in rendered)
            for (int i = 0; i < buf.Length && start + i < mix.Length; i++)
                mix[start + i] += buf[i];

        // Peak-normalised for the file only. The LEVEL lives on the emitter, from
        // Footsteps.MeasuredLevelDb — a WAV has no absolute scale and normalising here would be a
        // lie if the game read it back.
        float peak = 0f;
        foreach (float x in mix) peak = MathF.Max(peak, MathF.Abs(x));
        if (peak > 1e-6f)
        {
            float g = 0.89f / peak;
            for (int i = 0; i < mix.Length; i++) mix[i] *= g;
        }
        return mix;
    }

    private static float Arg(string[] args, string name, float fallback)
    {
        foreach (var a in args)
            if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)
                && float.TryParse(a[(name.Length + 1)..], out float x)) return x;
        return fallback;
    }

    private static byte[] ToWav16(float[] samples, int rate)
    {
        var pcm = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)(Math.Clamp(samples[i], -1f, 1f) * 32767f);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF".ToCharArray()); w.Write(36 + pcm.Length);
        w.Write("WAVE".ToCharArray()); w.Write("fmt ".ToCharArray());
        w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(pcm.Length); w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }
}
