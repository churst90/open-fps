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
        var tyreVoice = default(TyreVoice);
        float lastSpeed = 0f, chirp = 0f;
        int tyreGear = 0;
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
                // What the tyres are being asked for, from the car's own motion. Straight-line only
                // here — the bench drives in a straight line — so the lateral term is zero and every
                // squeal in a render is longitudinal: wheelspin, a shift, or a lock-up under braking.
                float aLong = (driveline.Speed - lastSpeed) / dt;
                lastSpeed = driveline.Speed;
                if (driveline.Gear != tyreGear && tyreGear >= 1 && driveline.Gear >= 1)
                {
                    float from = driveline.Ratio(tyreGear), to = driveline.Ratio(driveline.Gear);
                    if (from > 0f && to > 0f)
                        chirp = MathF.Max(chirp, TyreFriction.ShiftChirp(from / to, engine.Throttle));
                }
                tyreGear = driveline.Gear;
                chirp *= 0.99985f;   // about a tenth of a second for the driveline to resolve it
                float slip = TyreFriction.Demand(aLong, 0f, v.Tyres.PeakGripG) + chirp;
                tyres[at] = Tyre(v.Tyres, driveline.Speed, slip, rng, ref tyreVoice);
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
    /// Everything one tyre needs to remember between samples. A struct passed by ref, because this
    /// runs per sample per car inside a mixer callback and a class would be a pointer chase and a
    /// collection in the one place neither is affordable.
    /// </summary>
    public struct TyreVoice
    {
        public float Lp, Hp, HpPrev;
        public double TreadPhase;
        // The stick-slip resonator: a two-pole bandpass, which is the cheapest thing that actually
        // RINGS. A filtered-noise squeal without resonance is a hiss with the top taken off.
        public float R1, R2, R1b, R2b;
        public float SlipSmooth;
        public float SlideLp;
    }

    /// <summary>
    /// Tyre on asphalt: rolling, sliding, or somewhere between.
    ///
    /// ROLLING is broadband roar plus the tread blocks going past — the roar is the tread deforming
    /// over the aggregate, rising about 30 log10(v), and the tone is the blocks striking the road at
    /// speed over their spacing, which is why tyre noise rises in pitch as a car accelerates. A slick
    /// has no blocks and so has no tone at all, which falls out of TreadBlocks being zero rather than
    /// being a case anyone writes.
    ///
    /// SLIDING is a different mechanism in the same rubber. A tread element grips, deflects as the
    /// contact patch moves under it, releases, and snaps back at its own resonance; thousands of them
    /// doing it slightly out of step is the squeal. It is a NOTE, which is why a car at the limit
    /// sings rather than just getting louder, and it rises in pitch as the tyre is worked harder
    /// because each element completes its cycle faster.
    ///
    /// Past the limit the patch slides continuously, the stick-slip cycle stops being periodic, and
    /// the note collapses into the broadband roar of a locked wheel. Chirp, squeal and skid are one
    /// continuum sampled at three demands — see TyreFriction — not three sounds to be triggered.
    /// </summary>
    /// <param name="slip">Fraction of available grip in use. See <see cref="TyreFriction.Demand"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Tyre(TyreProfile t, float speed, float slip, Random rng, ref TyreVoice v)
    {
        // The demand is smoothed, and asymmetrically: a tyre lets go quickly and settles slowly, so
        // a squeal starts on the instant and dies away over a couple of hundred milliseconds. Stepping
        // it would make every corner entry a click.
        float k = slip > v.SlipSmooth ? 0.0016f : 0.00035f;
        v.SlipSmooth += (slip - v.SlipSmooth) * k;
        float demand = v.SlipSmooth;

        if (speed < 0.3f && demand < TyreFriction.SquealOnset)
        { v.Lp = v.Hp = v.HpPrev = 0f; return 0f; }

        float noise = (float)(rng.NextDouble() * 2 - 1);

        // ── Rolling ──
        float level = MathF.Pow(MathF.Max(speed, 0.3f) / 20f, 1.5f);
        float cutoff = MathHelper.Lerp(0.06f, 0.34f, Math.Clamp(speed / 40f, 0f, 1f));
        v.Lp += cutoff * (noise - v.Lp);
        float roar = v.Lp * (0.55f + 0.45f * t.SurfaceRoughness);
        float tone = 0f;
        if (t.TreadBlocks > 0)
        {
            float blockHz = speed / (2f * MathF.PI * 0.337f) * t.TreadBlocks;
            v.TreadPhase += 2.0 * Math.PI * blockHz / SampleRate;
            if (v.TreadPhase > 2.0 * Math.PI) v.TreadPhase -= 2.0 * Math.PI;
            tone = (float)(Math.Sin(v.TreadPhase) * 0.34 + Math.Sin(v.TreadPhase * 2) * 0.16);
            tone *= 1f - t.SurfaceRoughness * 0.55f;
        }
        float mix = (roar * 1.4f + tone) * level * 0.3f;

        // ── Sliding ──
        float squeal = TyreFriction.SquealAmount(demand);
        float skid = TyreFriction.SkidAmount(demand);
        if (squeal > 1e-3f || skid > 1e-3f)
        {
            // How fast the rubber is actually being dragged across the road decides how much of this
            // there is: a stationary wheel cannot squeal, however hard it is being pushed, and the
            // same demand at eighty is far louder than at ten. Saturating, because past walking pace
            // the mechanism is fully established and only the demand matters.
            float rub = Math.Clamp(speed / 12f, 0f, 1f);

            if (squeal > 1e-3f)
            {
                float hz = t.SquealHz * TyreFriction.SquealPitch(demand);
                float amp = Level(t.SquealDb) * squeal * rub * SquealProminence;
                // Two poles at the fundamental and one at the second harmonic. Real squeal is rich —
                // the release is a snap, not a sine — and the octave is most of what makes it read as
                // rubber rather than as a test tone.
                float sq = Resonate(ref v.R1, ref v.R2, noise, hz, t.SquealQ)
                         + Resonate(ref v.R1b, ref v.R2b, noise, hz * 2f, t.SquealQ * 0.7f) * 0.45f;
                mix += sq * amp;
            }

            if (skid > 1e-3f)
            {
                // A locked wheel is broadband and DARK: the tread is being torn rather than tapped,
                // and the energy sits well below the squeal it replaced.
                v.SlideLp += 0.10f * (noise - v.SlideLp);
                mix += v.SlideLp * Level(t.SquealDb) * skid * rub * 1.6f * SquealProminence;
            }
        }

        float y = 0.992f * (v.HpPrev + mix - v.Hp);
        v.Hp = mix; v.HpPrev = y;

        // Shaped, not clipped, and with room above. At a drive of 0.8 a full squeal came out of the
        // tanh at exactly the value a gentle scrub came out at — the shaper was erasing the whole
        // difference between a tyre working and a tyre screaming, and no amount of turning the layer
        // up afterwards could put it back. A gentle knee keeps the quiet case where it was and gives
        // the loud one somewhere to go.
        // The knee has to stay OUT OF THE WAY. At a drive of 0.13 a full squeal sat well up the
        // curve, so the level was coming from saturation rather than from gain — and a listener
        // described exactly that: "it sounds like it clips from the source". A gentler drive with
        // the range restored afterwards leaves the same loudness with the waveform intact, and keeps
        // the tanh for what it is for, which is catching the rare extreme rather than shaping the
        // normal case.
        return MathF.Tanh(y * 0.05f) * 26f;
    }

    /// <summary>
    /// How much louder a squeal is rendered than its sound pressure alone would suggest.
    ///
    /// Not a fudge, and worth writing down. A squealing tyre sits between 600 Hz and 4 kHz, which is
    /// where human hearing is at its most sensitive; an engine's energy is mostly an octave or two
    /// below that, where the ear is ten-odd decibels less sensitive. So a 92 dB squeal against a
    /// 116 dB engine is NOT twenty-four decibels down to a listener, and treating sound pressure as
    /// though it were loudness buried the squeal completely — measuring it showed a full squeal
    /// changing a sports car's voice by six tenths of a decibel.
    ///
    /// The right answer in the long run is to weight the whole mix the way an ear does. Until then
    /// this puts the one band where that error is largest back where a listener would put it.
    ///
    /// Twenty is large and was arrived at by measurement rather than by taste: rendering the whole
    /// engine voice with and without slip, a full squeal moved a sports car by six tenths of a
    /// decibel at unity, five at 2.6, and eight and a half at twenty — then backed off a quarter
    /// from there, on the ear that said twenty was too much.
    /// </summary>
    public const float SquealProminence = 15f;

    /// <summary>The squeal level relative to the rolling noise, as a linear factor. Both are quoted
    /// in dB at a metre, so the difference between them is the only thing that matters.</summary>
    private static float Level(float squealDb) => MathF.Pow(10f, (squealDb - 96f) / 20f) * 6f;

    /// <summary>
    /// One two-pole resonator, driven by noise. The cheapest thing that rings, and ringing is the
    /// entire point: a squeal is a resonance being excited, not a filtered hiss.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Resonate(ref float z1, ref float z2, float x, float hz, float q)
    {
        float w = 2f * MathF.PI * Math.Clamp(hz, 40f, SampleRate * 0.45f) / SampleRate;
        float r = MathF.Exp(-w / (2f * MathF.Max(0.5f, q)));
        float y = x * (1f - r) + 2f * r * MathF.Cos(w) * z1 - r * r * z2;
        z2 = z1; z1 = y;
        return y;
    }

    private static float[] Scale(float[] buf, float k)
    {
        for (int i = 0; i < buf.Length; i++) buf[i] *= k;
        return buf;
    }

    /// <summary>16-bit mono WAV, for auditioning outside the game.</summary>
    public static byte[] ToWav16(float[] samples) => WeaponSynth.ToWav16(samples, SampleRate);
}
