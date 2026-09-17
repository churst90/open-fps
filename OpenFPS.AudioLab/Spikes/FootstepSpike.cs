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
        Console.WriteLine("    surface     shoe        tau ms  thump Hz  grain Hz   conform   scuff   ring Hz   level dB");

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
                float db = Footsteps.MeasuredLevelDb(new Footstep
                {
                    Surface = surface, Shoe = shoe, BodyMassKg = mass, SpeedMps = speed, Seed = 1,
                });

                Console.WriteLine($"    {surface,-11} {shoe.Name,-11} {tau * 1000f,6:F1}  {Footsteps.ImpactCornerHz(tau),8:F0}  {grainHz,8:F0}"
                                + $"   {conform,7:F2}   {scuff,5:F2}   {(ringHz > 0f ? ringHz.ToString("F0") : "—"),7}   {db,8:F0}");
            }
        }
        Console.WriteLine();
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
