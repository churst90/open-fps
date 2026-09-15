using System;
using System.Collections.Generic;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>The rendered result: the three sources and the path the car took.</summary>
public sealed class VehicleRender
{
    public required float[] Exhaust { get; init; }
    public required float[] Intake { get; init; }
    public required float[] Tyres { get; init; }
    /// <summary>Diagnostic: the summed pressure at the valve ends, BEFORE the pipes. If the source is
    /// lumpy and the output is not, the network is smoothing the life out of it.</summary>
    public required float[] Port { get; init; }
    /// <summary>Distance travelled at each sample, so the caller can place the car along a road.</summary>
    public required float[] Distance { get; init; }
    /// <summary>Engine speed at each sample — ground truth for the instruments.</summary>
    public required float[] Rpm { get; init; }
    public required int SampleRate { get; init; }
    public float Seconds => Exhaust.Length / (float)SampleRate;
    public required List<string> Log { get; init; }
    /// <summary>Sample index at which each drive order began.</summary>
    public required int[] OrderStart { get; init; }
    /// <summary>Sound pressure level at one metre of each buffer's loudest second, dB SPL, before
    /// normalisation — what the game should place the emitter at.</summary>
    public float ExhaustDb { get; init; }
    public float IntakeDb { get; init; }
    /// <summary>The pascals per full-scale unit the buffers were normalised by.</summary>
    public float PascalsPerUnit { get; init; }
}

/// <summary>
/// A vehicle, rendered offline from its mechanism: the engine (<see cref="EngineSynth"/>) driven
/// through a gearbox and a car (<see cref="Driveline"/>) by a scripted driver, plus the tyres.
///
/// This is the bench and the demo. The game does not use it — a vehicle in the world runs the same
/// engine live inside an FMOD DSP, following the speed the server reports.
/// </summary>
public static class VehicleSynth
{
    public const int SampleRate = 44100;

    /// <summary>Speed of sound in exhaust gas at a temperature, m/s, for anyone printing resonances.</summary>
    public static float GasSoundSpeed(float celsius) => Gas.SoundSpeed(celsius + 273.15f, Gas.GammaExhaust);

    /// <summary>Renders a whole drive. Deterministic given the seed.</summary>
    public static VehicleRender Render(VehicleProfile v, IReadOnlyList<DriveOrder> orders, int seed = 11)
    {
        var rng = new Random(seed);
        var log = new List<string>();
        float total = 0f;
        foreach (var o in orders) total += o.Seconds;
        int n = (int)(SampleRate * total);

        var exhaust = new float[n];
        var intake = new float[n];
        var tyres = new float[n];
        var distance = new float[n];
        var port = new float[n];
        var rpmTrace = new float[n];

        var engine = new EngineSynth(v.Engine, SampleRate, seed);
        var driveline = new Driveline(v);
        var driver = new Driver(driveline, engine);
        foreach (var line in engine.Describe()) log.Add("  " + line);

        float dt = 1f / SampleRate;
        float tyreLp = 0f, tyreHp = 0f, tyreHpPrev = 0f;
        double treadPhase = 0;
        var orderStart = new int[orders.Count];
        int at = 0;
        for (int oi = 0; oi < orders.Count; oi++)
        {
            var order = orders[oi];
            orderStart[oi] = at;
            int len = (int)(SampleRate * order.Seconds);
            float phase = 0f;
            string target = order.TargetSpeed <= 0 ? ""
                : order.Action is DriverAction.Revving or DriverAction.Holding ? $" to {order.TargetSpeed:F0} rpm"
                : $" to {order.TargetSpeed * 3.6f:F0} km/h";
            log.Add($"{at / (float)SampleRate,6:F1}s  {order.Action}{target}");
            float peakRpm = 0f, minRpm = float.MaxValue, minQ = 1f;
            int lastGear = driveline.Gear;
            for (int i = 0; i < len && at < n; i++, at++, phase += dt)
            {
                driver.Apply(order, phase, dt);
                driveline.Step(engine, dt);
                exhaust[at] = engine.Exhaust;
                intake[at] = engine.Intake + engine.Block;
                port[at] = engine.PortSum;
                tyres[at] = Tyre(v.Tyres, driveline.Speed, rng, ref tyreLp, ref tyreHp, ref tyreHpPrev, ref treadPhase);
                distance[at] = driveline.Distance;
                rpmTrace[at] = engine.Rpm;
                peakRpm = MathF.Max(peakRpm, engine.Rpm);
                if (phase > 0.8f && engine.Rpm > 50f) minRpm = MathF.Min(minRpm, engine.Rpm);
                minQ = MathF.Min(minQ, engine.LastBurnQuality);
                if (driveline.Gear != lastGear && driveline.Gear != 0)
                {
                    log.Add($"{at / (float)SampleRate,6:F1}s  into {driveline.Gear} at {engine.Rpm:F0} rpm, {driveline.Speed * 3.6f:F0} km/h");
                    lastGear = driveline.Gear;
                }
                else if (driveline.Gear != lastGear) lastGear = driveline.Gear;
            }
            if (order.Action == DriverAction.Revving)
                log[^1] += $"  (reached {peakRpm:F0})";
            else if (order.Action == DriverAction.Idling && minRpm < float.MaxValue)
                log[^1] += $"  ({minRpm:F0}-{peakRpm:F0} rpm, hunting {peakRpm - minRpm:F0}, weakest burn {minQ:F2}, MAP {engine.ManifoldBar:F2} bar, port peak {engine.PortPeak / 1e5f:F2} bar)";
            else if (order.Action == DriverAction.Holding)
                log[^1] += $"  (MAP {engine.ManifoldBar:F2} bar, port peak {engine.PortPeak / 1e5f:F2} bar, throttle {engine.Throttle:F2})";
            else if (order.Action is DriverAction.Accelerating or DriverAction.Cruising or DriverAction.Coasting or DriverAction.Braking)
                log[^1] += $"  ({driveline.Speed * 3.6f:F0} km/h, {engine.Rpm:F0} rpm in {driveline.Gear})";
        }

        // Levels before normalising, so the game knows how loud each source really is.
        float exDb = LoudestSecondDb(exhaust);
        float inDb = LoudestSecondDb(intake);
        log.Add($"  exhaust {exDb:F0} dB SPL at 1 m in its loudest second, intake+block {inDb:F0} dB");

        // ONE scale factor across all three, so the balance the physics found survives. The exhaust
        // sets it; the front of the car is scaled by the same factor and soft-limited, because one
        // gulp of intake on a snapped throttle must not decide the level of the whole render.
        float shared = 0f;
        foreach (var s in exhaust) shared = MathF.Max(shared, MathF.Abs(s));
        for (int i = 0; i < intake.Length; i++) intake[i] = shared * MathF.Tanh(intake[i] / MathF.Max(1e-6f, shared));
        // Tyres are synthesized in arbitrary units; place them against the exhaust by level.
        float tyreTarget = shared * MathF.Pow(10f, (v.Tyres.ReferenceDb - 96f) / 20f);
        float tyrePeak = 0f;
        foreach (var s in tyres) tyrePeak = MathF.Max(tyrePeak, MathF.Abs(s));
        if (tyrePeak > 1e-9f) Scale(tyres, tyreTarget / tyrePeak);
        float scale = shared > 1e-9f ? 0.95f / shared : 1f;

        return new VehicleRender
        {
            Exhaust = Scale(exhaust, scale),
            Intake = Scale(intake, scale),
            Tyres = Scale(tyres, scale),
            Port = port,
            Distance = distance,
            Rpm = rpmTrace,
            SampleRate = SampleRate,
            Log = log,
            OrderStart = orderStart,
            ExhaustDb = exDb,
            IntakeDb = inDb,
            PascalsPerUnit = 1f / scale,
        };
    }

    /// <summary>dB SPL of the loudest one-second RMS window of a buffer in pascals.</summary>
    public static float LoudestSecondDb(float[] pa)
    {
        int w = SampleRate;
        if (pa.Length < w) w = pa.Length;
        double best = 0, acc = 0;
        for (int i = 0; i < pa.Length; i++)
        {
            acc += pa[i] * pa[i];
            if (i >= w) acc -= pa[i - w] * pa[i - w];
            if (i >= w - 1) best = Math.Max(best, acc / w);
        }
        double rms = Math.Sqrt(Math.Max(1e-20, best));
        return (float)(20 * Math.Log10(rms / 2e-5));
    }

    // ── Tyres ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tyre on asphalt: broadband roar plus the tread blocks going past. The roar is the tread
    /// deforming over the aggregate, rising about 30 log10(v); the tone is the blocks striking the
    /// road at speed over their spacing, which is why tyre noise rises in pitch as a car accelerates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Tyre(TyreProfile t, float speed, Random rng,
                             ref float lp, ref float hp, ref float hpPrev, ref double treadPhase)
    {
        if (speed < 0.3f) { lp = hp = hpPrev = 0f; return 0f; }
        float level = MathF.Pow(speed / 20f, 1.5f);
        float noise = (float)(rng.NextDouble() * 2 - 1);
        float cutoff = MathHelper.Lerp(0.06f, 0.34f, Math.Clamp(speed / 40f, 0f, 1f));
        lp += cutoff * (noise - lp);
        float roar = lp * (0.55f + 0.45f * t.SurfaceRoughness);
        float blockHz = speed / (2f * MathF.PI * 0.337f) * t.TreadBlocks;
        treadPhase += 2.0 * Math.PI * blockHz / SampleRate;
        float tone = (float)(Math.Sin(treadPhase) * 0.34 + Math.Sin(treadPhase * 2) * 0.16);
        tone *= 1f - t.SurfaceRoughness * 0.55f;
        float mix = (roar * 1.4f + tone) * level * 0.3f;
        float y = 0.992f * (hpPrev + mix - hp);
        hp = mix; hpPrev = y;
        return MathF.Tanh(y * 0.8f);
    }

    private static float[] Scale(float[] buf, float k)
    {
        for (int i = 0; i < buf.Length; i++) buf[i] *= k;
        return buf;
    }

    /// <summary>16-bit mono WAV, for auditioning outside the game.</summary>
    public static byte[] ToWav16(float[] samples) => WeaponSynth.ToWav16(samples, SampleRate);
}
