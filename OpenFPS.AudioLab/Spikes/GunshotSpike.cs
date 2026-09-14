using System;
using System.IO;
using System.Numerics;
using Thread = System.Threading.Thread;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Renders a synthesized weapon, writes the layers out to audition, and plays a shot from a series of
/// distances so the crack-to-report gap can be heard doing its job.
///
/// The gap is the point. A supersonic round makes two sounds — the crack as it passes you, and the
/// report chasing it at the speed of sound — and the delay between them is d·(1/c − 1/v), about 1.7 ms
/// per metre for a rifle. That is a direct readout of range, available to a player who cannot see the
/// shooter, and it falls out of the physics rather than being a designed cue bolted on afterwards.
///
/// Headless it checks the timing and the rendered waveforms. `--gunshot-live` fires the shots.
/// </summary>
public static class GunshotSpike
{
    public static int Run(bool interactive, string outDir)
    {
        // Into the asset tree, because the live half plays them back through the ordinary sound path.
        outDir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "WEAPONS");
        AcousticRegistry.Initialize();
        var rifle = WeaponProfile.Rifle;
        var pistol = WeaponProfile.Pistol;
        float c = AudioPhysics.SpeedOfSound;

        Console.WriteLine($"  speed of sound {c:F0} m/s");
        Console.WriteLine($"  {rifle.Name}: {rifle.MuzzleVelocity:F0} m/s, " +
                          $"Mach {rifle.MuzzleVelocity / c:F2}, " +
                          $"cone half-angle {Ballistics.MachConeAngle(rifle.MuzzleVelocity, c) * 180 / MathF.PI:F0}°");
        Console.WriteLine($"  {pistol.Name}: {pistol.MuzzleVelocity:F0} m/s, " +
                          $"Mach {pistol.MuzzleVelocity / c:F2} — subsonic, so no crack");

        // --- The distance cue, as a table -------------------------------------------------------
        Console.WriteLine("\n  range    crack at   report at      gap   (gap read back as range)");
        bool timingOk = true;
        foreach (float d in new[] { 25f, 50f, 100f, 200f, 400f })
        {
            float crack = Ballistics.CrackArrival(d, 1.5f, rifle.MuzzleVelocity, c);
            float report = Ballistics.ReportArrival(d, c);
            float gap = Ballistics.CrackToReportSeconds(d, rifle.MuzzleVelocity, c);
            float back = Ballistics.DistanceFromCrackToReport(gap, rifle.MuzzleVelocity, c);
            Console.WriteLine($"  {d,5:F0} m   {crack * 1000,7:F1} ms  {report * 1000,7:F1} ms  " +
                              $"{gap * 1000,6:F1} ms   {back,6:F1} m");
            if (crack >= report) timingOk = false;
            if (MathF.Abs(back - d) > 0.5f) timingOk = false;
        }

        // --- Render the layers ------------------------------------------------------------------
        Directory.CreateDirectory(outDir);
        var blast = WeaponSynth.MuzzleBlast(rifle);
        var crackNear = WeaponSynth.SupersonicCrack(rifle, 1.5f);
        var crackFar = WeaponSynth.SupersonicCrack(rifle, 18f);
        var action = WeaponSynth.MechanicalAction(rifle);
        var pistolBlast = WeaponSynth.MuzzleBlast(pistol);
        var pistolCrack = WeaponSynth.SupersonicCrack(pistol, 1.5f);

        Write(outDir, "rifle_blast.wav", blast);
        Write(outDir, "rifle_crack_near.wav", crackNear);
        Write(outDir, "rifle_crack_far.wav", crackFar);
        Write(outDir, "rifle_action.wav", action);
        Write(outDir, "pistol_blast.wav", pistolBlast);

        Console.WriteLine();
        bool ok = timingOk;
        ok &= Check("the crack always arrives before the report", timingOk);
        ok &= Check("the muzzle blast rendered", blast.Length > 1000 && Peak(blast) > 0.5f);
        ok &= Check("a supersonic round cracks", crackNear.Length > 8 && Peak(crackNear) > 0.5f);
        ok &= Check("a subsonic round does not", pistolCrack.Length == 0);
        // A near miss is a short sharp tear; a distant one is longer and duller.
        ok &= Check("a distant crack is longer than a near one", crackFar.Length > crackNear.Length);
        ok &= Check("the action layer is quieter than the blast", Peak(action) < Peak(blast));
        // Dry means dry: nothing here may ring on after the decay, or it would fight the engine's reverb.
        ok &= Check("the blast is dry — it has decayed to nothing by the end",
                    TailLevel(blast) < 0.02f);
        ok &= Check("the crack is dry too", TailLevel(crackNear) < 0.02f);

        Console.WriteLine($"\n  wrote {outDir}");

        if (interactive)
        {
            int rc = Fire(rifle, blast, crackNear, action, c);
            if (rc != 0) ok = false;
        }

        Console.WriteLine(ok
            ? "RESULT: PASS — a dry synthesized shot, and a crack-to-report gap that encodes range."
            : "RESULT: FAIL — see the unmet conditions above.");
        return ok ? 0 : 1;
    }

    /// <summary>Plays the shot from several ranges through the real provider, crack first.</summary>
    private static int Fire(WeaponProfile w, float[] blast, float[] crack, float[] action, float c)
    {
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("  (live playback unavailable)"); return 1; }
        try
        {
            provider.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
            Console.WriteLine("\n  LIVE. HEADPHONES. The same shot from 25 m, 100 m and 400 m.");
            Console.WriteLine("  Listen for the gap opening up between the crack and the thump.");

            foreach (float d in new[] { 25f, 100f, 400f })
            {
                float gapMs = Ballistics.CrackToReportSeconds(d, w.MuzzleVelocity, c) * 1000f;
                Console.WriteLine($"    {d,4:F0} m — gap {gapMs:F0} ms");

                // The crack happens AT the listener, just off to one side. The report happens at the
                // shooter, out in front. Two different places, which is half of what sells it.
                PlayOneShot(provider, -9001, "WEAPONS/rifle_crack_near", new Vector3(1.2f, 0f, 0.4f), 0.85f, 40f);
                Sleep(provider, gapMs);
                PlayOneShot(provider, -9002, "WEAPONS/rifle_blast", new Vector3(0f, 0f, d), 1.0f, 900f);
                Sleep(provider, 900);
            }
            return 0;
        }
        finally { provider.Dispose(); }
    }

    /// <summary>Plays one rendered layer through the ordinary spatial path — the same one the game
    /// uses. The layers were written to ASSETS a moment ago, which is also how they would ship: rendered
    /// once, then treated as any other one-shot.</summary>
    private static void PlayOneShot(FmodAudioProvider provider, int id, string soundId, Vector3 at,
                                    float volume, float range)
    {
        provider.PlaySpatialSound(new SpatialEmitter
        {
            EntityId = id,
            SoundId = soundId,
            Type = EmitterType.WorldLocked,
            Mode = PlaybackMode.Single,
            Position = at,
            Volume = volume,
            Range = range,
            MinDistance = 1.5f,
            Pitch = 1.0f,
            TargetRegionId = AcousticConstants.GlobalRegionId,
            EnableReverb = true,
            IsEvent = true
        });
    }

    private static void Sleep(FmodAudioProvider provider, float ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { provider.Update(); Thread.Sleep(4); }
    }

    private static void Write(string dir, string name, float[] pcm)
    {
        if (pcm.Length == 0) return;
        File.WriteAllBytes(Path.Combine(dir, name), WeaponSynth.ToWav16(pcm));
        Console.WriteLine($"    {name,-24} {pcm.Length / (float)WeaponSynth.SampleRate * 1000,6:F1} ms  peak {Peak(pcm):F2}");
    }

    private static float Peak(float[] v)
    {
        float m = 0f;
        foreach (var s in v) m = MathF.Max(m, MathF.Abs(s));
        return m;
    }

    /// <summary>Peak over the last tenth of the buffer — what is still ringing when it should be over.</summary>
    private static float TailLevel(float[] v)
    {
        if (v.Length < 20) return 0f;
        float m = 0f;
        for (int i = v.Length - v.Length / 10; i < v.Length; i++) m = MathF.Max(m, MathF.Abs(v[i]));
        return m;
    }

    private static bool Check(string what, bool held)
    {
        Console.WriteLine($"  [{(held ? "PASS" : "FAIL")}] {what}");
        return held;
    }
}
