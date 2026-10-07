using System.Globalization;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// --breath [effort=0..1] [seconds=40] [out=path]: the real <see cref="Breathing"/> model at an effort,
/// every breath rendered through the real <see cref="TransientSynth"/> and laid out at its own time and
/// level, each one's parameters printed. Breaths go on long after effort stops
/// (<see cref="Breathing.RecoverySeconds"/>). docs/CLIENT_NOTES.md, "A breath is turbulence".
/// </summary>
public static class BreathSpike
{
    private const int Sr = TransientSynth.SampleRate;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        float effort = Num(args, "effort", 1f);
        float seconds = Num(args, "seconds", 40f);
        string outPath = Str(args, "out") ?? "/tmp/openfps-breath.wav";

        var lungs = new Breathing();
        var taken = new List<(float At, Breath B)>();

        // Hard for a third of the time, then still: breathing outlasts the running.
        const float dt = 1f / 60f;
        float runUntil = seconds / 3f;
        for (float t = 0; t < seconds; t += dt)
        {
            float speed = t < runUntil ? PhysicsConstants.SprintSpeed * effort : 0f;
            if (lungs.Update(speed, dt, out var breath) && breath.Taken) taken.Add((t, breath));
        }

        Console.WriteLine($"\n  Breathing at effort {effort:P0}: ran {runUntil:F0} s, then stood still.");
        Console.WriteLine($"  {taken.Count} breath(s) in {seconds:F0} s. Exertion ended at {lungs.Exertion:P0}.\n");
        Console.WriteLine("       when      what     level     centre   decay");
        foreach (var (at, b) in taken)
            Console.WriteLine($"    {at,7:F2} s   {(b.IsInhale ? "in " : "out")}    {b.LevelDb,5:F1} dB   "
                            + $"{b.Hz,5:F0} Hz   {b.DecaySeconds * 1000f,4:F0} ms");

        var mix = new float[(int)((seconds + 2f) * Sr)];
        foreach (var (at, b) in taken)
        {
            var one = TransientSynth.Render(new TransientSound
            {
                Character = SoundCharacter.Hiss,
                LevelDb = b.LevelDb, Hz = b.Hz, DecaySeconds = b.DecaySeconds, Noisiness = 1f,
            }, seed: (int)(at * 1000f));

            // TransientSynth normalises its render, so the level is applied here as the mixer does,
            // against the loudest breath there can be.
            float gain = MathF.Pow(10f, (b.LevelDb - Breathing.MaxLevelDb) / 20f);
            int start = (int)(at * Sr);
            for (int i = 0; i < one.Length && start + i < mix.Length; i++) mix[start + i] += one[i] * gain;
        }

        float peak = 0f;
        foreach (var v in mix) peak = MathF.Max(peak, MathF.Abs(v));
        if (peak > 0f) { float g = 0.89f / peak; for (int i = 0; i < mix.Length; i++) mix[i] *= g; }

        // ── Band balance, measured before anybody listens ───────────────────────────────────────
        // A breath is turbulence and broadband; most of its energy in one band is a thump, not a breath.
        var exhale = TransientSynth.Render(new TransientSound
        {
            Character = SoundCharacter.Hiss, LevelDb = 50f, Hz = 500f, DecaySeconds = 0.3f, Noisiness = 1f,
        }, seed: 7);
        var bands = Spectrum.BandsDb(exhale, Sr);
        Console.WriteLine("\n  One exhale, as rendered - dB against the whole:");
        for (int i = 0; i < Spectrum.BandCount; i++)
            Console.WriteLine($"      {Spectrum.BandName(i),-14} {bands[i],6:F1}");

        File.WriteAllBytes(outPath, WeaponSynth.ToWav16(mix, Sr));
        Console.WriteLine($"\n  wrote {outPath}\n");
        return 0;
    }

    private static string? Str(string[] args, string name)
    {
        foreach (var a in args) if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return a[(name.Length + 1)..];
        return null;
    }

    private static float Num(string[] args, string name, float fallback)
        => float.TryParse(Str(args, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
