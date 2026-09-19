using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Breathing, rendered on its own so it can be judged on its own.
///
///   --breath [effort=0..1] [seconds=40] [out=path]
///
/// Written because of a report that took six sessions to get to: *"it is 2 different bangs so it
/// makes me think it's breathing in and out... I don't hear the breathing either... can you play the
/// breathing sound for me so I can hear it and I'll tell you?"*
///
/// Everything about that fits. Two alternating sounds, an inhale brighter and shorter than the
/// exhale; they start after effort and go on for the best part of a minute after it stops, which is
/// what <see cref="Breathing.RecoverySeconds"/> is for; and they were in the log all along as
/// `recv 'breath out' ... Hiss 507 Hz 36 dB`. If a breath renders as a thump rather than as air, a
/// listener hears exactly what was described: banging, and no breathing.
///
/// So this runs the real <see cref="Breathing"/> model at a given effort, renders every breath it
/// takes through the real <see cref="TransientSynth"/>, and lays them out at their own times and
/// their own levels. Nothing is a stub. It also prints what each one WAS, so "that is not a breath"
/// can be answered with the numbers that made it.
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

        // Run hard for a third of the time, then stand still. The tail is the point: breathing
        // outlasts the running, which is what puts breaths seconds after you have stopped moving.
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

        // ...and lay them out as they happened, each at its own level against the others.
        var mix = new float[(int)((seconds + 2f) * Sr)];
        foreach (var (at, b) in taken)
        {
            var one = TransientSynth.Render(new TransientSound
            {
                Character = SoundCharacter.Hiss,
                LevelDb = b.LevelDb, Hz = b.Hz, DecaySeconds = b.DecaySeconds, Noisiness = 1f,
            }, seed: (int)(at * 1000f));

            // TransientSynth normalises what it renders, so the LEVEL has to be applied here — the
            // same way the mixer applies it, as a gain against the loudest breath there can be.
            float gain = MathF.Pow(10f, (b.LevelDb - Breathing.MaxLevelDb) / 20f);
            int start = (int)(at * Sr);
            for (int i = 0; i < one.Length && start + i < mix.Length; i++) mix[start + i] += one[i] * gain;
        }

        float peak = 0f;
        foreach (var v in mix) peak = MathF.Max(peak, MathF.Abs(v));
        if (peak > 0f) { float g = 0.89f / peak; for (int i = 0; i < mix.Length; i++) mix[i] *= g; }

        // ── What shape is it, before anybody plays it ───────────────────────────────────────────
        //
        // The rule this project learned from the footsteps (`synthesis-failures`): measure the band
        // balance BEFORE listening, because "that does not sound like X" is a question about the
        // spectrum and the ear is slow at answering it. A breath is TURBULENCE — air tearing past a
        // narrow opening — and turbulence is broadband. If most of the energy is in one band, what is
        // being rendered is not a breath, it is a thump with a breath's name on it.
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
