using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Aircraft;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Aircraft, heard from the ground.
///
///   --aircraft [preset ...] [alt=m] [speed=m/s] [offset=m] [sec=s] [lever=0..1] [descend=0..1]
///
/// Renders each aircraft flying a straight line past a listener standing on grass, and writes one
/// WAV per preset. The flyover is done the honest way: the machine is integrated in ITS time, and
/// each sample is deposited at the moment it ARRIVES — emission time plus the path over the speed of
/// sound — with the inverse-distance gain and the air's absorption for that path. Doppler is not
/// applied; it happens, because the path is shortening. A second arrival off the ground, a little
/// later and a little weaker, is what gives a flyover its slow comb.
/// </summary>
public static class AircraftSpike
{
    private const int Sr = VehicleSynth.SampleRate;

    /// <summary>
    /// --aircraft steady [preset ...]: the synth alone, the listener fixed beside it, ten seconds.
    /// Any comb that moves has to come from the machine, since nothing in the geometry does. Reports
    /// how much the spectrum's fine structure wanders from frame to frame between 300 Hz and 4 kHz
    /// (the flange index: the mean over 1/12-octave bands of each band's standard deviation over
    /// time, dB), for the preset as it is and with one engine.
    /// </summary>
    static int Steady(string[] args)
    {
        var presets = args.Where(a => AircraftProfile.Presets.ContainsKey(a)).ToList();
        if (presets.Count == 0) presets = AircraftProfile.Presets.Keys.ToList();
        Console.WriteLine($"{"preset",-14} {"engines",7} {"flange",7}   {"single",7}");
        foreach (var key in presets)
        {
            var p = AircraftProfile.Presets[key]();
            float a = FlangeIndex(p), b = p.Engines > 1 ? FlangeIndex(p with { Engines = 1 }) : a;
            if (p.TailRotor != null)
                foreach (float hz in new[] { 170.5f, 341f, 682f })
                    Console.WriteLine($"   {hz:F0} Hz: level swings {Swing(p, hz):F1} dB with the tail rotor, {Swing(p with { TailRotor = null }, hz):F1} without");
            string tail = p.TailRotor != null ? $"   no tail rotor {FlangeIndex(p with { TailRotor = null }):F2}" : "";
            Console.WriteLine($"{key,-14} {p.Engines,7} {a,7:F2}   {b,7:F2}{tail}");
        }
        return 0;
    }

    /// <summary>The spread (max minus min, 10th to 90th percentile) of the level in a 6 Hz band
    /// round <paramref name="hz"/>, over 250 ms windows, steady listener.</summary>
    static float Swing(AircraftProfile p, float hz)
    {
        var syn = new AircraftSynth(p, Sr, 3);
        syn.PlaceAtLever(0.7f); syn.Lever = 0.7f;
        syn.SetListener(new System.Numerics.Vector3(60f, -40f, 10f));
        int n = Sr * 14, N = 16384;
        var x = new float[n];
        for (int i = 0; i < n; i++) { syn.Step(); x[i] = syn.Total; }
        var lv = new List<double>();
        var re = new double[N]; var im = new double[N];
        for (int s0 = Sr * 2; s0 + N <= n; s0 += Sr / 4)
        {
            for (int i = 0; i < N; i++) { re[i] = x[s0 + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N)); im[i] = 0; }
            Fft(re, im);
            double e = 1e-30;
            for (int k = (int)((hz - 3) * N / Sr); k <= (int)((hz + 3) * N / Sr); k++) e += re[k] * re[k] + im[k] * im[k];
            lv.Add(10 * Math.Log10(e));
        }
        lv.Sort();
        return (float)(lv[(int)(lv.Count * 0.9)] - lv[(int)(lv.Count * 0.1)]);
    }

    static float FlangeIndex(AircraftProfile p)
    {
        var syn = new AircraftSynth(p, Sr, 3);
        syn.PlaceAtLever(0.7f);
        syn.Lever = 0.7f;
        syn.SetListener(new System.Numerics.Vector3(60f, -40f, 10f));
        int n = Sr * 12;
        var x = new float[n];
        for (int i = 0; i < n; i++) { syn.Step(); x[i] = syn.Total; }
        const int N = 4096, hop = 2048;
        var bands = new List<(int lo, int hi)>();
        for (double f = 300; f < 4000; f *= Math.Pow(2, 1.0 / 12))
            bands.Add(((int)(f * N / Sr), Math.Max((int)(f * N / Sr) + 1, (int)(f * Math.Pow(2, 1.0 / 12) * N / Sr))));
        var series = bands.Select(_ => new List<double>()).ToList();
        var re = new double[N]; var im = new double[N];
        for (int s0 = Sr * 2; s0 + N <= n; s0 += hop)
        {
            for (int i = 0; i < N; i++) { re[i] = x[s0 + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N)); im[i] = 0; }
            Fft(re, im);
            for (int b = 0; b < bands.Count; b++)
            {
                double e = 1e-30;
                for (int k = bands[b].lo; k < bands[b].hi; k++) e += re[k] * re[k] + im[k] * im[k];
                series[b].Add(10 * Math.Log10(e));
            }
        }
        return (float)series.Average(sr => { double m = sr.Average(); return Math.Sqrt(sr.Average(v => (v - m) * (v - m))); });
    }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            for (int i = 0; i < n; i += len)
                for (int k = 0; k < len / 2; k++)
                {
                    double c = Math.Cos(ang * k), s = Math.Sin(ang * k);
                    double ur = re[i + k], ui = im[i + k];
                    double vr = re[i + k + len / 2] * c - im[i + k + len / 2] * s;
                    double vi = re[i + k + len / 2] * s + im[i + k + len / 2] * c;
                    re[i + k] = ur + vr; im[i + k] = ui + vi;
                    re[i + k + len / 2] = ur - vr; im[i + k + len / 2] = ui - vi;
                }
        }
    }

    public static int Run(string[] args)
    {
        if (args.Contains("steady")) return Steady(args);
        AcousticRegistry.Initialize();
        float alt = Arg(args, "alt", -1f), speed = Arg(args, "speed", -1f), offset = Arg(args, "offset", 60f);
        float seconds = Arg(args, "sec", 18f), lever = Arg(args, "lever", -1f), descend = Arg(args, "descend", -1f);
        var presets = args.Where(a => AircraftProfile.Presets.ContainsKey(a)).ToList();
        if (presets.Count == 0) presets = AircraftProfile.Presets.Keys.ToList();

        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "AIRCRAFT");
        Directory.CreateDirectory(dir);
        Console.WriteLine("\n  Aircraft, flying past a listener on the ground.\n");

        foreach (var key in presets)
        {
            var p = AircraftProfile.ByName(key);
            // A sensible pass for each kind, unless told otherwise.
            float h = alt > 0 ? alt : p.Power switch
            {
                AircraftPower.Turbofan => 400f, AircraftPower.Turboprop => 300f,
                AircraftPower.Turboshaft => 90f, _ => 150f,
            };
            float v = speed > 0 ? speed : p.Power switch
            {
                AircraftPower.Turbofan => 95f, AircraftPower.Turboprop => 110f,
                AircraftPower.Turboshaft => 45f, _ => 50f,
            };
            float lv = lever >= 0 ? lever : (p.Power == AircraftPower.Turboshaft ? 0.8f : 1f);
            float ds = descend >= 0 ? descend : (p.Power == AircraftPower.Turboshaft ? 0.7f : 0f);

            var synth = new AircraftSynth(p, Sr, 5) { Lever = lv, Descending = ds };
            Console.WriteLine($"  {key}");
            foreach (var line in synth.Describe()) Console.WriteLine($"    {line}");
            Console.WriteLine($"    pass: {h:F0} m up, {offset:F0} m to the side, {v:F0} m/s ({v * 3.6f:F0} km/h), {seconds:F0} s");

            var (wav, peakDb, closestDb) = Flyover(synth, h, offset, v, seconds);
            string path = Path.Combine(dir, $"aircraft_{key}.wav");
            File.WriteAllBytes(path, VehicleSynth.ToWav16(wav));
            Console.WriteLine($"    at the ear: {closestDb:F0} dB at the closest point, peak {peakDb:F0} dB; wrote {path}\n");
        }
        return 0;
    }

    /// <summary>
    /// What each mechanism is worth as the engines spool up, at a fixed bearing.
    ///
    /// "The whine is there but it gets quieter when it spools up" is a statement about a BALANCE, and
    /// a balance cannot be read off a constant. The whine's own level rises with the spool (it goes
    /// as the cube of it) — but so does everything else, and the jets go as the EIGHTH power of exit
    /// velocity, which is the fastest-rising thing on the aeroplane. If the jets gain more decibels
    /// per notch of lever than the tone does, the tone is being buried while getting louder, and
    /// that is heard exactly as "it stops when it spools up".
    ///
    /// So this sweeps the lever and reports, at each setting, every mechanism's own level AND what
    /// the whine is worth against the rest of the machine — by rendering the same aeroplane twice,
    /// once with the tone and once with it silenced, and differencing. The last column is the number
    /// that decides whether it is audible.
    ///
    /// Two bearings, because they are two different aeroplanes to a listener: AHEAD is an aircraft
    /// coming towards you, where the fan and the compressor radiate out of the inlet; ASTERN is one
    /// that has gone past, which is jets and nothing else.
    ///
    ///   --spool [preset ...] [alt=m]
    /// </summary>
    public static int Spool(string[] args)
    {
        var presets = args.Where(a => AircraftProfile.Presets.ContainsKey(a)).ToList();
        if (presets.Count == 0) presets = AircraftProfile.Presets.Keys.ToList();
        float alt = Arg(args, "alt", 300f);

        foreach (var key in presets)
        {
            var p = AircraftProfile.ByName(key);
            Console.WriteLine($"\n  {key} — {p.Name}.");
            // What the machine declares, against what it makes. The same check `--voice-levels`
            // does for a vehicle, and for the same reason: SourceLevelDb sets BOTH where the
            // emitter is placed AND what one full-scale sample means inside the voice, and the two
            // pull opposite ways, so a mis-declaration never cancels. An aeroplane that declares
            // itself louder than it is comes out QUIETER, by more than half the error.
            float loudest = float.NegativeInfinity;
            foreach (string bear in new[] { "ahead", "astern" })
            {
                var at0 = new Vector3(0f, -alt, bear == "ahead" ? 1000f : -1000f);
                var (_, _, _, t0, _, _, _) = Hold(p, at0, 1f);
                loudest = MathF.Max(loudest, t0);
            }
            Console.WriteLine($"    declares {p.SourceLevelDb:F0} dB at 1 m; loudest bearing at full power measures "
                            + $"{loudest:F1} — {loudest - p.SourceLevelDb:+0.0;-0.0;0.0} dB");
            if (p.Turbine == null) { Console.WriteLine(); continue; }
            Console.WriteLine("    Levels are dB SPL at one metre in the machine's frame.\n");
            Console.WriteLine("    bearing   lever   spool    blades      jet     core    total    whine worth   blade tone");

            foreach (string bearing in new[] { "ahead", "astern" })
            {
                // The listener a kilometre out and `alt` below, ahead of or behind the nose. What
                // matters to every directivity in here is the DIRECTION, not the distance.
                var at = new Vector3(0f, -alt, bearing == "ahead" ? 1000f : -1000f);
                foreach (float lever in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
                {
                    var (bl, jt, co, tot, spool, bpf, bpfDb) = Hold(p, at, lever);
                    // The same aeroplane with the tone taken out. Everything else — the spool, the
                    // random streams, the order of operations — is identical, so the difference in
                    // the total is the tone and nothing else.
                    var mute = p with { Turbine = p.Turbine with { WhineDb = -200f } };
                    var (_, _, _, totNoWhine, _, _, _) = Hold(mute, at, lever);
                    float worth = tot - totNoWhine;
                    Console.WriteLine($"    {bearing,-8}  {lever,5:F2}  {spool,5:F2}  {bl,8:F1} {jt,8:F1} {co,8:F1} {tot,8:F1}      {worth,6:F1} dB"
                                    + $"   {bpf,5:F0} Hz {bpfDb - bl,+6:F1} dB");
                }
            }
            Console.WriteLine();
        }
        return 0;
    }

    /// <summary>Holds one aircraft at one lever setting and one bearing, and meters a second of it.</summary>
    private static (float Blades, float Jet, float Core, float Total, float Spool, float Bpf, float BpfDb)
        Hold(AircraftProfile p, Vector3 listener, float lever)
    {
        var s = new AircraftSynth(p, Sr, 5) { Lever = lever };
        s.SetListener(listener);
        s.PlaceAtLever(lever);
        // FOUR seconds, not half of one. A blade row's speed follows its target on a half-second
        // time constant — a rotor's inertia is enormous against anything driving it — so a short
        // settle meters a fan that is still accelerating, and a tone whose frequency is sliding has
        // no line in a spectrum at all. The first version of this measurement reported the blade
        // rate forty-eight decibels under the row's own energy for that reason and nothing else.
        for (int i = 0; i < Sr * 4; i++) s.Step();
        // How much of the blade row is in its BLADE-PASSING TONE, as against everything else it
        // makes. This is the column that answers "the whine stops when it spools up": a fan whose
        // tips have gone supersonic moves its energy out of the blade rate and down into the shaft
        // harmonics — the buzz-saw — and a listener hears the clean tone go away even though the
        // row got louder. Measured with a Goertzel at the blade rate, not assumed.
        var row = p.Turbine?.Fan ?? p.Propeller;
        float rpm = p.Turbine?.Fan != null ? p.Turbine.Fan.RpmMax * s.Spool : s.Rpm;
        float bpf = row != null && rpm > 1f ? row.Blades * rpm / 60f : 0f;
        // A BANK of bins across a few per cent either side of the blade rate, not one bin at it.
        //
        // A twin's two fans are trimmed half a per cent apart, which at two kilohertz is ten hertz
        // — ten whole bins of a one-second window — so a single Goertzel at the nominal rate sits
        // BETWEEN the two lines and sees neither. It read the tone twenty-five decibels under the
        // row when the row was radiating it at nine, and that is an artefact of the instrument and
        // not of the aeroplane. Both lines, and the modulation sidebands they beat into, land
        // inside this band.
        const int Bins = 41;
        var cw = new double[Bins];
        var g1 = new double[Bins];
        var g2 = new double[Bins];
        for (int i = 0; i < Bins; i++)
        {
            float f = bpf * (0.97f + 0.06f * i / (Bins - 1));
            cw[i] = 2 * Math.Cos(2 * Math.PI * f / Sr);
        }

        double b = 0, j = 0, c = 0, t = 0;
        for (int i = 0; i < Sr; i++)
        {
            s.Step();
            b += (double)s.Blades * s.Blades; j += (double)s.Jet * s.Jet;
            c += (double)s.Core * s.Core;     t += (double)s.Total * s.Total;
            for (int k = 0; k < Bins; k++)
            {
                double g0 = s.Blades + cw[k] * g1[k] - g2[k];
                g2[k] = g1[k]; g1[k] = g0;
            }
        }
        static float D(double sum) => 20f * MathF.Log10(MathF.Max(1e-12f, MathF.Sqrt((float)(sum / Sr))) / 20e-6f);
        // The loudest line in the band, as an RMS: a sinusoid of amplitude A has RMS A/sqrt(2).
        // The loudest rather than the sum, because the bins overlap and summing them would count
        // one line several times over.
        double best = 0;
        for (int k = 0; k < Bins; k++)
        {
            double m = Math.Sqrt(g1[k] * g1[k] + g2[k] * g2[k] - cw[k] * g1[k] * g2[k]) * 2.0 / Sr;
            if (m > best) best = m;
        }
        float bpfRms = (float)(best / Math.Sqrt(2));
        float bpfDb = 20f * MathF.Log10(MathF.Max(1e-12f, bpfRms) / 20e-6f);
        return (D(b), D(j), D(c), D(t), s.Spool, bpf, bpfDb);
    }

    /// <summary>
    /// The aircraft flies along +x at a height, offset to one side, passing abeam the listener
    /// half way through. Returns the received signal normalised for playback, plus the level at the
    /// ear when it was closest and the peak.
    /// </summary>

    /// <summary>
    /// An arrival, heard from beside the runway: the approach, the flare, the wheels, the rollout.
    ///
    /// The flight path is the only input. The aeroplane comes down a three-degree slope at its
    /// approach speed, the descent stops at the runway, and at the instant it does the wheels —
    /// which have been stationary for the whole flight — meet concrete going past at seventy metres
    /// a second. Nothing here says "play a screech": the touchdown is the path reaching the ground,
    /// and the sound is a tyre model at a hundred per cent slip for as long as the wheel's inertia
    /// takes to be paid off, which is <see cref="LandingGearSpec.SpinUpSeconds"/> and nothing else.
    ///
    /// The POWER comes off the path too, the same way the game reads it: coming down is idle, and
    /// on the ground it is idle with the reversers doing the stopping.
    ///
    ///   --landing [preset ...] [offset=m] [speed=m/s] [sec=s]
    /// </summary>
    public static int Landing(string[] args)
    {
        AcousticRegistry.Initialize();
        var presets = args.Where(a => AircraftProfile.Presets.ContainsKey(a)).ToList();
        if (presets.Count == 0)
            presets = AircraftProfile.Presets.Where(kv => kv.Value().Gear != null).Select(kv => kv.Key).ToList();
        float offset = Arg(args, "offset", 120f), seconds = Arg(args, "sec", 22f);

        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "AIRCRAFT");
        Directory.CreateDirectory(dir);
        Console.WriteLine("\n  Landings, heard from beside the runway.\n");

        foreach (var key in presets)
        {
            var p = AircraftProfile.ByName(key);
            if (p.Gear is not { } gear) { Console.WriteLine($"  {key}: no undercarriage declared.\n"); continue; }
            float vApp = Arg(args, "speed", p.ApproachSpeedMps > 0f ? p.ApproachSpeedMps : p.CruiseSpeedMps * 0.62f);
            Console.WriteLine($"  {key}");
            foreach (var line in new AircraftSynth(p, Sr, 5).Describe()) Console.WriteLine($"    {line}");
            Console.WriteLine($"    approach {vApp:F0} m/s ({vApp * 1.944f:F0} kt) on 3 degrees, {offset:F0} m to the side; "
                            + $"spin-up {gear.SpinUpSeconds(vApp) * 1000f:F0} ms");

            var (wav, peakDb, touchDb) = Arrival(p, vApp, offset, seconds);
            string path = Path.Combine(dir, $"landing_{key}.wav");
            File.WriteAllBytes(path, VehicleSynth.ToWav16(wav));
            Console.WriteLine($"    at the ear: peak {peakDb:F0} dB, {touchDb:F0} dB over the touchdown second; wrote {path}\n");
        }
        return 0;
    }

    private static (float[] Wav, float PeakDb, float TouchDb) Arrival(
        AircraftProfile p, float vApp, float offset, float seconds)
    {
        const float c = 343f;
        int n = (int)(seconds * Sr);
        var ear = new Vector3(0f, 1.6f, 0f);
        var earImage = new Vector3(0f, -1.6f, 0f);
        var outBuf = new float[n + Sr * 4];
        // Touchdown two fifths of the way through, so there is approach before it and rollout after.
        float tTouch = seconds * 0.4f;
        float slope = MathF.Tan(3f * MathF.PI / 180f);

        var synth = new AircraftSynth(p, Sr, 5);
        synth.SetListener(new Vector3(0f, -vApp * tTouch * slope, -vApp * tTouch));
        // On approach: idle, the air doing the work. Started there rather than spooling down to it.
        synth.Lever = 0.12f;
        synth.PlaceAtLever(0.12f);
        for (int i = 0; i < 3 * Sr; i++) synth.Step();

        float lp1 = 0f, lp2 = 0f, glp1 = 0f, glp2 = 0f;
        double touchP2 = 0; int touchCount = 0;
        bool touched = false;
        float peak = 0f;

        for (int k = 0; k < n; k++)
        {
            float t = k / (float)Sr;
            // Down the slope to the threshold, then along the runway, slowing on the brakes and the
            // reversers at a quarter of a g — which is what a wet-day landing rollout is.
            float before = t - tTouch;
            float x, alt, v;
            if (before < 0f) { v = vApp; x = before * vApp; alt = 1.2f + (-before) * vApp * slope; }
            else
            {
                v = MathF.Max(6f, vApp - 2.5f * before);
                x = (vApp * before) - 1.25f * before * before;
                alt = 1.2f;
            }

            if (!touched && before >= 0f)
            {
                touched = true;
                synth.Touchdown(v);
                // Thrust reverse: the levers come up out of idle as soon as the wheels are on.
                synth.Lever = 0.55f;
            }
            if (touched) synth.GroundSpeed = v;

            var pos = new Vector3(x, alt, offset);
            if ((k & 63) == 0)
            {
                Vector3 d = ear - pos;
                synth.SetListener(new Vector3(-d.Z, d.Y, d.X));
            }
            synth.Step();
            float s = synth.Total;

            float r = Vector3.Distance(pos, ear);
            float fc = AirCorner(r);
            float a = 1f - MathF.Exp(-2f * MathF.PI * fc / Sr);
            lp1 += a * (s - lp1); lp2 += a * (lp1 - lp2);
            float direct = lp2 / MathF.Max(1f, r);
            Deposit(outBuf, (t + r / c) * Sr, direct);
            if (before >= 0f && before < 1f) { touchP2 += direct * direct; touchCount++; }

            // Concrete, not grass: a runway reflects nearly all of it.
            float rg = Vector3.Distance(pos, earImage);
            float fg = AirCorner(rg) * 0.85f;
            float ag = 1f - MathF.Exp(-2f * MathF.PI * fg / Sr);
            glp1 += ag * (s - glp1); glp2 += ag * (glp1 - glp2);
            Deposit(outBuf, (t + rg / c) * Sr, 0.9f * glp2 / MathF.Max(1f, rg));
        }

        foreach (var xv in outBuf) peak = MathF.Max(peak, MathF.Abs(xv));
        float peakDb = 20f * MathF.Log10(MathF.Max(1e-9f, peak) / 2e-5f);
        float touchDb = 10f * MathF.Log10((float)Math.Max(1e-20, touchP2 / Math.Max(1, touchCount)) / (2e-5f * 2e-5f));
        var wav = new float[n + Sr * 2];
        float g = peak > 1e-9f ? 0.89f / peak : 0f;
        for (int i = 0; i < wav.Length; i++) wav[i] = outBuf[i] * g;
        return (wav, peakDb, touchDb);
    }

    private static (float[] Wav, float PeakDb, float ClosestDb) Flyover(AircraftSynth synth, float alt, float offset, float speed, float seconds)
    {
        const float c = 343f;
        int n = (int)(seconds * Sr);
        var ear = new Vector3(0f, 1.6f, 0f);
        var earImage = new Vector3(0f, -1.6f, 0f);            // the ground's mirror of the ear
        var outBuf = new float[n + Sr * 4];
        float half = seconds * 0.5f;

        // Warm the machine up out of earshot first: a piston engine has to crank and settle, a
        // turbine has to spool.
        int warm = (int)(3f * Sr);
        synth.SetListener(new Vector3(0f, -alt, -speed * half));
        for (int i = 0; i < warm; i++) synth.Step();

        float lp1 = 0f, lp2 = 0f, glp1 = 0f, glp2 = 0f;
        double closestP2 = 0; int closeCount = 0;
        float rMin = float.MaxValue;
        for (int k = 0; k < n; k++)
        {
            float t = k / (float)Sr;
            var pos = new Vector3((t - half) * speed, alt, offset);
            if ((k & 63) == 0)
            {
                // The listener in the aircraft's frame: forward is +x here, up +y, starboard -z.
                Vector3 d = ear - pos;
                synth.SetListener(new Vector3(-d.Z, d.Y, d.X));
            }
            synth.Step();
            float s = synth.Total;

            // Direct path.
            float r = Vector3.Distance(pos, ear);
            rMin = MathF.Min(rMin, r);
            float fc = AirCorner(r);
            float a = 1f - MathF.Exp(-2f * MathF.PI * fc / Sr);
            lp1 += a * (s - lp1); lp2 += a * (lp1 - lp2);
            float direct = lp2 / MathF.Max(1f, r);
            Deposit(outBuf, (t + r / c) * Sr, direct);
            if (MathF.Abs(t - half) < 0.5f) { closestP2 += direct * direct; closeCount++; }

            // Off the ground: grass keeps about 70 % of the pressure at grazing incidence and
            // rather less of the top.
            float rg = Vector3.Distance(pos, earImage);
            float fg = AirCorner(rg) * 0.6f;
            float ag = 1f - MathF.Exp(-2f * MathF.PI * fg / Sr);
            glp1 += ag * (s - glp1); glp2 += ag * (glp1 - glp2);
            Deposit(outBuf, (t + rg / c) * Sr, 0.7f * glp2 / MathF.Max(1f, rg));
        }

        float peak = 0f;
        foreach (var x in outBuf) peak = MathF.Max(peak, MathF.Abs(x));
        float peakDb = 20f * MathF.Log10(MathF.Max(1e-9f, peak) / 2e-5f);
        float closestDb = 10f * MathF.Log10((float)Math.Max(1e-20, closestP2 / Math.Max(1, closeCount)) / (2e-5f * 2e-5f));
        var wav = new float[n + Sr * 2];
        float g = peak > 1e-9f ? 0.89f / peak : 0f;
        for (int i = 0; i < wav.Length; i++) wav[i] = outBuf[i] * g;
        return (wav, peakDb, closestDb);
    }

    /// <summary>Where the air has taken 3 dB off, for a path of this length: about 4 kHz at a
    /// hundred metres, 2 kHz at three hundred, 1 kHz at a kilometre (20 C, 50 % humidity).</summary>
    private static float AirCorner(float r)
        => Math.Clamp(4000f * MathF.Pow(100f / MathF.Max(1f, r), 0.59f), 300f, 18000f);

    /// <summary>Adds a sample at a fractional position, split linearly across the two slots.</summary>
    private static void Deposit(float[] buf, float at, float v)
    {
        int i = (int)at;
        float f = at - i;
        if (i < 0 || i + 1 >= buf.Length) return;
        buf[i] += v * (1f - f);
        buf[i + 1] += v * f;
    }

    private static float Arg(string[] args, string key, float fallback)
    {
        foreach (var a in args)
        {
            int eq = a.IndexOf('=');
            if (eq > 0 && a[..eq] == key && float.TryParse(a[(eq + 1)..], out float v)) return v;
        }
        return fallback;
    }
}
