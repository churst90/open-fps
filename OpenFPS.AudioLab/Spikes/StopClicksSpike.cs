using System.Numerics;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Data;
using OpenFPS.Audio.Fmod;
using OpenFPS.Materials;
using OpenFPS.Sound;
using OpenFPS.Sound.Core;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --stop-clicks [old] [out=DIR]: whether a voice ends without a click by every way the game lets one go, through the
/// whole mixer and Steam Audio. A 440 Hz tone 2 m off is started and, 0.8 s later, let go by one path: the budget
/// dropping a one-shot that lost its slot, the budget letting go of a continuous voice that lost its slot, its owner
/// stopping a budget voice, the registry's Stop, StopNow and two Retires, a direct voice stopped, the same played by a DSP
/// (as every engine, machine and talker is), a voice replaced under its id, the world left while it plays and left faded out, the binaural pool letting it go, and the program closing.
/// Each end is measured in the master capture: how long the fall took (-1 to -60 dB), and the step, the largest
/// prediction residual of the tone (x[n] - 2cos(w)x[n-1] + x[n-2]: zero for a steady sine, the jump itself at a cut)
/// within 5 ms of its end, against the tone's own level, in dB; beside it the largest over the whole fall. A step 60 dB under the voice is the depth
/// of the fade's own floor and the level the mixer already takes for silence (FmodAudioProvider.HeadSilentVolume); a cut
/// stands within a few dB of the voice itself. old: every stop outright, as before 2026-10-17 (LabLevers.HardStops).
/// </summary>
public static class StopClicksSpike
{
    private static readonly OpenFPS.Sound.Hearing.HearingSettings LabHearing = OpenFPS.Sound.Hearing.HearingSettings.FromEnvironment();

    private const int Rate = 48000;
    private const float ToneHz = 440f;

    /// <summary>The step a path may leave, dB under the voice's own level.</summary>
    public const double ThresholdDb = -60.0;

    public static int Run(string[] args)
    {
        string outDir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..] ?? "/tmp/openfps-stop-clicks";
        bool old = args.Contains("old");
        Directory.CreateDirectory(outDir);
        if (!args.Contains("debug")) Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string wav = System.IO.Path.Combine(outDir, "capture-float.wav");
        System.Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", System.IO.Path.Combine(outDir, "mix.wav"));
        System.Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", wav);
        System.Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        System.Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        AcousticRegistry.Initialize();
        var provider = new FmodAudioProvider { Hearing = LabHearing, Lab = new LabLevers { HardStops = old } };
        string sounds = LabPaths.Sounds();
        var facade = new AudioEngineFacade(provider, paths: new AudioDataPaths(sounds, null, null));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        facade.InitializeSteppedByHand(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        var tone = new float[Rate * 3];
        for (int i = 0; i < tone.Length; i++) tone[i] = 0.3f * MathF.Sin(2f * MathF.PI * ToneHz * i / Rate);
        facade.RegisterSynthesisedSoundFloat("lab:tone", tone, Rate);
        facade.RegisterSynthesisedSoundFloat("lab:hush", new float[Rate * 3], Rate);
        var ear = new Vector3(0f, 1.6f, 0f);
        facade.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
        var registry = new VoiceRegistry { Mixer = facade };

        SpatialEmitter Tone(int id, bool oneShot = false) => new()
        {
            EntityId = id, SoundId = "lab:tone", Type = EmitterType.WorldLocked,
            Mode = oneShot ? PlaybackMode.Single : PlaybackMode.LoopOne, IsEvent = oneShot,
            Position = ear + new Vector3(1.4f, 0f, 1.4f), Volume = 0.5f, MinDistance = 1f, Range = 200f, Pitch = 1f,
            EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
        };
        // Silent, and louder than the tone by the budget's score: what takes its slot.
        // The same tone made by a DSP (SynthProcessor), on a channel FMOD plays from a unit as it plays every engine,
        // machine and talker: its fade and its stop are the same calls on a different kind of channel.
        SpatialEmitter Synth(int id) => Tone(id) with
        {
            SoundId = "lab:synth", IsSynth = true, SynthWave = SynthWaveType.Sine, SynthFrequency = ToneHz,
            SynthPulseWidth = 500f, SynthFilterCutoff = 1f, Volume = 0.15f,
        };
        SpatialEmitter Hush(int id, bool oneShot = false) => Tone(id, oneShot) with
        {
            SoundId = "lab:hush", Position = ear + new Vector3(0f, 0f, 1f), Volume = 1f,
        };
        void Pump(double seconds)
        {
            double until = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < until) { registry.Fades(); facade.StepByHand(); Thread.Sleep(2); }
        }

        var names = new List<string>();
        int id = 7100;
        // Starts a tone, lets it play, lets it go by one path, and gives it time to end.
        void Path(string name, Action<int> start, Action<int> letGo)
        {
            id++;
            start(id);
            Pump(0.8);
            names.Add(name);
            letGo(id);
            Pump(0.7);
        }
        try
        {
            Pump(0.5);
            Path("budget: a one-shot losing its slot", i => { facade.BudgetForLab = 1; facade.Submit(Tone(i, oneShot: true)); },
                 i => facade.Submit(Hush(i + 500, oneShot: true)));
            facade.StopSound(id + 500); facade.BudgetForLab = null; Pump(0.3);
            Path("budget: a continuous voice losing its slot", i => { facade.BudgetForLab = 1; facade.Submit(Tone(i)); },
                 i => facade.Submit(Hush(i + 500)));
            facade.StopSound(id + 500); facade.StopSound(id); facade.BudgetForLab = null; Pump(0.3);
            Path("budget: stopped by its owner", i => facade.Submit(Tone(i)), i => facade.StopSound(i));
            Path("registry Stop (an owner's stop)", i => registry.Sound(Tone(i)), i => registry.Stop(i));
            Path("registry StopNow", i => registry.Sound(Tone(i)), i => registry.StopNow(i));
            Path("registry Retire, voice fade", i => registry.Sound(Tone(i)), i => registry.Retire(i, VoiceFade.Voice));
            Path("registry Retire, engine fade", i => registry.Sound(Tone(i)), i => registry.Retire(i, VoiceFade.Engine));
            Path("a direct voice stopped (echoes, rain)", i => facade.PlayPhysicalSoundDirect(Tone(i)), i => facade.StopSound(i));
            Path("a synthesised voice stopped (a DSP)", i => facade.PlayPhysicalSoundDirect(Synth(i)), i => facade.StopSound(i));
            Path("a voice replaced under its id", i => facade.PlayPhysicalSoundDirect(Tone(i)), i => facade.PlayPhysicalSoundDirect(Hush(i)));
            facade.StopSound(id); Pump(0.3);
            Path("leaving the world, connected", i => registry.Sound(Tone(i)), _ => facade.StopAllWorldSounds());
            // As ClientGameSession.Leave: the world faded out over half a second, linear in decibels, then left, then
            // brought back up for the menu.
            Path("leaving the world, faded out", i => registry.Sound(Tone(i)), _ =>
            {
                const double Fade = 0.5;
                double from = clock.Elapsed.TotalSeconds;
                for (double t = 0; t < Fade; t = clock.Elapsed.TotalSeconds - from)
                {
                    facade.SetWorldFade(MathF.Pow(10f, -60f * (float)(t / Fade) / 20f));
                    registry.Fades(); facade.StepByHand(); Thread.Sleep(2);
                }
                facade.SetWorldFade(0f);
                facade.StopAllWorldSounds();
                facade.SetWorldFade(1f);
            });
            Path("the binaural pool letting it go", i => facade.PlayPhysicalSoundDirect(Tone(i)), _ => provider.HoldBinauralVoicesForLab(10));
            provider.ReleaseHeldBinauralVoicesForLab();
            Pump(0.3);
            facade.PlayPhysicalSoundDirect(Tone(++id));
            Pump(0.8);
            names.Add("the program closing");
        }
        finally { facade.Dispose(); }

        var (l, r, sr) = OpenFPS.AudioLab.Spikes.ClapRoomSpike.ReadStereo(wav);
        var tones = Segments(l, r, sr);
        Console.WriteLine($"  every stop {(old ? "outright (old)" : "as the game makes it")}; a step under {ThresholdDb:F0} dB is not heard");
        if (tones.Count != names.Count) Console.WriteLine($"  WARNING: {tones.Count} tones in the capture for {names.Count} paths");
        Console.WriteLine("  path                                       | fall -1 to -60 dB | step at the end | worst in the fall | the tone's own   (dB re the voice)");
        int loud = 0;
        for (int k = 0; k < Math.Min(names.Count, tones.Count); k++)
        {
            var m = Measure(l, r, sr, tones[k].Start, tones[k].End);
            bool heard = m.StepDb > ThresholdDb;
            if (heard) loud++;
            Console.WriteLine($"  {names[k],-42} | {m.FallMs,8:F0} ms       | {m.StepDb,6:F1}{(heard ? " CLICK" : ""),-8} | {m.FallDb,6:F1}            | {m.FloorDb,6:F1}");
        }
        Console.WriteLine(loud == 0 ? "  no path ends in a step" : $"  {loud} path(s) end in a step");
        return 0;
    }

    /// <summary>Each tone in the capture, in order: from where it rises out of silence to its last sample that is not
    /// zero. Found in the capture itself rather than by the clock, which the capture does not start on.</summary>
    internal static List<(int Start, int End)> Segments(float[] l, float[] r, int sr)
    {
        int n = Math.Min(l.Length, r.Length), w = sr / 1000;
        var list = new List<(int, int)>();
        int start = -1, lastHeard = -1;
        for (int i = 0; i + w <= n; i += w)
        {
            double pk = 0;
            for (int j = i; j < i + w; j++) pk = Math.Max(pk, Math.Abs(l[j]) + Math.Abs(r[j]));
            if (pk >= 1e-5)
            {
                if (start < 0 && pk > 1e-2) start = i;
                if (start >= 0) lastHeard = i + w;
            }
            else if (start >= 0 && i - lastHeard > 50 * w)
            {
                int end = lastHeard;
                while (end > start && Math.Abs(l[end - 1]) + Math.Abs(r[end - 1]) < 1e-9) end--;
                list.Add((start, end));
                start = -1;
            }
        }
        if (start >= 0)
        {
            int end = n;
            while (end > start && Math.Abs(l[end - 1]) + Math.Abs(r[end - 1]) < 1e-9) end--;
            list.Add((start, end));
        }
        return list;
    }

    /// <summary>
    /// One tone's end: its level while steady (100 to 500 ms in), the fall from -1 to -60 dB under it, the largest
    /// prediction residual within 5 ms of its last sample (the step at its end; at the file's own end, where what follows
    /// is nothing, its last sample is a step to nothing) and over its whole fall (the corners of a fade drawn in steps),
    /// and the residual while it was steady; in dB re its level.
    /// </summary>
    internal static (double FallMs, double StepDb, double FallDb, double FloorDb) Measure(float[] l, float[] r, int sr, int start, int end)
    {
        double c = 2 * Math.Cos(2 * Math.PI * ToneHz / sr);
        int n = Math.Min(l.Length, r.Length);
        double Res(int i) => Math.Abs(l[i] - c * l[i - 1] + l[i - 2]) + Math.Abs(r[i] - c * r[i - 1] + r[i - 2]);
        int a0 = Math.Max(2, Math.Min(end, start + sr / 10)), a1 = Math.Min(end, start + sr / 2);
        double level = 1e-12, own = 0;
        for (int i = a0; i < a1; i++)
        {
            level = Math.Max(level, Math.Abs(l[i]) + Math.Abs(r[i]));
            own = Math.Max(own, Res(i));
        }
        int w = sr / 1000, fallFrom = -1, fallTo = end;
        for (int i = a1; i + w <= end; i += w)
        {
            double pk = 0;
            for (int j = i; j < i + w; j++) pk = Math.Max(pk, Math.Abs(l[j]) + Math.Abs(r[j]));
            if (fallFrom < 0 && pk < level * 0.891) fallFrom = i;
            if (fallFrom >= 0 && pk < level * 1e-3) { fallTo = i; break; }
        }
        if (fallFrom < 0) fallFrom = Math.Max(a1, end - w);
        double step = 0, fall = 0;
        for (int i = Math.Max(2, fallFrom - 5 * w); i < Math.Min(n, end + 5 * w); i++)
        {
            fall = Math.Max(fall, Res(i));
            if (i >= end - 5 * w) step = Math.Max(step, Res(i));
        }
        if (end >= n) step = Math.Max(step, Math.Abs(l[n - 1]) + Math.Abs(r[n - 1]));
        static double Db(double x, double re) => 20 * Math.Log10(x / re + 1e-12);
        return ((fallTo - fallFrom) * 1000.0 / sr, Db(step, level), Db(Math.Max(fall, step), level), Db(own, level));
    }
}
