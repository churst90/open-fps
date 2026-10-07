using System.Numerics;
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
    /// <summary>The block on its own (knock, valvetrain, accessory and turbo whine), also folded into
    /// <see cref="Intake"/>: apart, it tells a quiet mechanical layer from one buried by the intake.</summary>
    public required float[] Block { get; init; }
    /// <summary>Diagnostic: the summed pressure at the valve ends, before the pipes. A lumpy source
    /// and a smooth output means the network is smoothing the life out of it.</summary>
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
/// <see cref="Render"/> is the bench and the demo: in the world the engine runs live in
/// EngineVoiceState, following the speed the server reports. The tyre functions here
/// (<see cref="Tyre"/>, <see cref="WheelSqueal"/>, <see cref="TreadTone"/>) are the live voices' too,
/// and run inside the render pool per sample.
/// </summary>
public static class VehicleSynth
{
    /// <summary>The bench's rate: the rate the game mixes at, so a bench render is what the live voice makes.</summary>
    public const int SampleRate = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.DefaultRate;

    /// <summary>Renders a whole drive. Deterministic given the seed.</summary>
    /// <param name="v">The vehicle.</param>
    /// <param name="orders">The drive, order by order.</param>
    /// <param name="seed">Seeds the engine and the tyres.</param>
    /// <param name="listener">Where the bench stands, in the machine's frame (x across, y up, z
    /// forward, origin at the exhaust). Null sums every tailpipe at one point, as dead behind the car.</param>
    public static VehicleRender Render(VehicleProfile v, IReadOnlyList<DriveOrder> orders, int seed = 11, Vector3? listener = null)
    {
        var rng = new Random(seed);
        var log = new List<string>();
        float total = 0f;
        foreach (var o in orders) total += o.Seconds;
        int n = (int)(SampleRate * total);

        var exhaust = new float[n];
        var intake = new float[n];
        var tyres = new float[n];
        var block = new float[n];
        var distance = new float[n];
        var port = new float[n];
        var rpmTrace = new float[n];

        var engine = new EngineSynth(v.Engine, SampleRate, seed);
        if (listener is { } standing) engine.SetListener(standing);
        var driveline = new Driveline(v);
        var driver = new Driver(driveline, engine);
        // The car's body, driven by the exhaust (what shakes a floorpan), as EngineVoiceState drives
        // it: the bench and the live voice must not disagree.
        var body = new BodyResonator(v.Body ?? VehicleBody.None, SampleRate);
        foreach (var line in engine.Describe()) log.Add("  " + line);

        float dt = 1f / SampleRate;
        double shellEnergy = 0, pipeEnergy = 0;
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
                exhaust[at] = engine.Exhaust + body.Process(engine.Exhaust);
                shellEnergy += engine.ExhaustShell * engine.ExhaustShell;
                pipeEnergy += engine.ExhaustPipe * engine.ExhaustPipe;
                intake[at] = engine.Intake + engine.Block;
                block[at] = engine.Block;
                port[at] = engine.PortSum;
                // The bench drives straight, so every squeal in a render is longitudinal: wheelspin, a
                // shift, or a lock-up.
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

        // Levels before normalising. The can against the pipe is printed: a layer nobody can hear is
        // either too quiet or not there, and only a number tells which.
        if (shellEnergy > 0f)
            log.Add($"  muffler case {10.0 * Math.Log10(shellEnergy / Math.Max(1e-20, pipeEnergy)):F1} dB "
                  + $"against the pipe (ShellLevel {v.Engine.Exhaust.Muffler.ShellLevel:G3})");

        float exDb = LoudestSecondDb(exhaust);
        float inDb = LoudestSecondDb(intake);
        log.Add($"  exhaust {exDb:F0} dB SPL at 1 m in its loudest second, intake+block {inDb:F0} dB");

        // One scale factor across all three, so the physics' balance survives. The exhaust sets it; the
        // front is soft-limited, so one gulp of intake cannot decide the whole render's level.
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
            Block = Scale(block, scale),
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
    /// Everything one tyre needs between samples. A struct passed by ref: this runs per sample per car
    /// on the render threads, where a class would be a pointer chase and a collection.
    /// </summary>
    public struct TyreVoice
    {
        public float Lp, Hp, HpPrev;
        public double TreadPhase;
        // The scrub's two-pole resonators: without resonance a squeal is a hiss with the top off.
        public float R1, R2, R1b, R2b;
        public float SlipSmooth;
        public float SlideLp;
        /// <summary>The anchored rolling path's high-pass, which takes off the bass a real tyre
        /// does not make (see <see cref="RollingHighPassHz"/>).</summary>
        public float RollHp;
        /// <summary>The anchored rolling path's second lowpass pole.</summary>
        public float RollLp;
        /// <summary>The rolling path's filters at the rate this voice runs at, found on its first sample.</summary>
        internal RollingFilters? Rolling;
        /// <summary>The squeal's note itself (see <see cref="StickSlipVoice"/>).</summary>
        public StickSlipVoice Tone;
    }

    /// <summary>
    /// The squeal as the oscillation it is: tread elements stick, load, let go and pull into step, a
    /// limit cycle with harmonics whose pitch wanders with load, slip speed and heat. Noise through a
    /// resonance had the colour without the coherence (a modulation index of 0.49 on an airliner's
    /// touchdown): gravel on an airliner, a pitched hiss on a car (Cody, 2026-10-07: "airliner sounds
    /// like gravel, not a squeal ... kind of like the cars, they need to squeal").
    ///
    /// Shaped on one real screech ("car screech sound effect", Cody's trash, 3.0-4.3 s): the
    /// fundamental near 1 kHz wanders 7 % (standard deviation) between 25 ms steps; the harmonics stand
    /// 10, 21, 28 and 32 dB under it. The noise through the resonances, the scrub, stays under it at a
    /// tenth of the power.
    /// </summary>
    public struct StickSlipVoice
    {
        public double Phase;
        public float W1, W2;
        /// <summary>The noise resonators' RMS at the last pitch, per unit of input, which the tone is
        /// made to equal.</summary>
        public float Norm;
        public int Tick;
    }

    /// <summary>The tone's share of the squeal's power is ToneShare squared; the scrub's ScrubShare squared.</summary>
    private const float ToneShare = 0.954f, ScrubShare = 0.3f;
    /// <summary>The harmonics re the fundamental (measured: -10, -21, -28, -32 dB).</summary>
    private const float H2 = 0.316f, H3 = 0.089f, H4 = 0.040f, H5 = 0.025f;
    /// <summary>The unit tone's RMS.</summary>
    private static readonly float ToneRms = MathF.Sqrt((1f + H2 * H2 + H3 * H3 + H4 * H4 + H5 * H5) / 2f);

    /// <summary>
    /// One sample of the stick-slip note at <paramref name="hz"/>, unit RMS. The pitch wanders on the
    /// tyre's own noise, low-passed twice at <see cref="RollingFilters.WanderHz"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float StickSlipTone(ref StickSlipVoice s, float hz, float noise, RollingFilters rates)
    {
        s.W1 += rates.WanderStep * (noise - s.W1);
        s.W2 += rates.WanderStep * (s.W1 - s.W2);
        float f = hz * (1f + rates.WanderGain * s.W2);
        s.Phase += f / rates.Rate;
        if (s.Phase >= 1.0) s.Phase -= Math.Floor(s.Phase);
        float th = (float)(s.Phase * 2.0 * Math.PI);
        float nyq = rates.Rate * 0.45f;
        float y = MathF.Sin(th);
        if (2f * f < nyq) y += H2 * MathF.Sin(2f * th);
        if (3f * f < nyq) y += H3 * MathF.Sin(3f * th);
        if (4f * f < nyq) y += H4 * MathF.Sin(4f * th);
        if (5f * f < nyq) y += H5 * MathF.Sin(5f * th);
        return y / ToneRms;
    }

    /// <summary>
    /// The RMS of the noise squeal (noise of variance 1/3 through the resonator at the note, and 0.45 of
    /// the one at its octave), from the two-pole resonators' own variance:
    /// g^2 s^2 (1 + c2) / ((1 - c2) ((1 + c2)^2 - c1^2)).
    /// </summary>
    private static float ResonatorRms(float hz, float q, float rate)
    {
        static float Var(float hz, float q, float rate)
        {
            Coefficients(hz, q, rate, out float c1, out float c2, out float g);
            return g * g / 3f * (1f + c2) / MathF.Max(1e-12f, (1f - c2) * ((1f + c2) * (1f + c2) - c1 * c1));
        }
        return MathF.Sqrt(Var(hz, q, rate) + 0.45f * 0.45f * Var(hz * 2f, q * 0.7f, rate));
    }

    /// <summary>
    /// Where the anchored rolling roar is cut below. Tyre/road noise is a band round 1 kHz: in the
    /// CNOSSOS-EU light-vehicle spectrum the 250 Hz octave is 12 dB under the 1 kHz one, and above the
    /// peak it falls about 10 dB an octave, which is the lowpass taken twice (once left the 8 kHz octave
    /// 6 dB under the peak, where it should be twenty-odd: a hiss).
    /// </summary>
    public const float RollingHighPassHz = 400f;

    /// <summary>
    /// The variance of white noise through the rolling lowpass (coefficient <paramref name="a"/>)
    /// twice and the high-pass, over the input's, integrated from the responses so the declared level
    /// is what the roar makes wherever the lowpass is.
    /// </summary>
    internal static float RollingBandGain(float a, float rate = SampleRate)
    {
        float b = 1f - MathF.Exp(-2f * MathF.PI * RollingHighPassHz / rate);
        const int N = 512;
        double sum = 0;
        for (int k = 0; k < N; k++)
        {
            double w = Math.PI * (k + 0.5) / N;
            var z1 = System.Numerics.Complex.FromPolarCoordinates(1.0, -w);
            var lp = a / (1.0 - (1.0 - a) * z1);
            lp *= lp;
            var hp = 1.0 - b / (1.0 - (1.0 - b) * z1);
            sum += System.Numerics.Complex.Abs(lp * hp) * System.Numerics.Complex.Abs(lp * hp);
        }
        return (float)(sum / N);
    }

    private const float RollA0 = 0.06f, RollA1 = 0.34f;

    /// <summary>
    /// The tyre's filters at one sample rate, and the gains of <see cref="RollingBandGain"/> over the
    /// lowpass's range there. Made once per rate: the mixer runs at whatever the device does.
    /// </summary>
    internal sealed class RollingFilters
    {
        public readonly float Rate, HpAlpha;
        private readonly float[] _table = new float[65];
        private readonly float[] _alpha = new float[65];

        /// <summary>The tyre's per-sample coefficients, chosen at 44.1 kHz, at this rate (At44k): the
        /// demand's attack and release (0.0016, 0.00035), the slip velocity's (0.0016), the locked
        /// wheel's low-pass (0.10) and the output's DC high-pass pole (0.992).</summary>
        public readonly float Attack, Release, SlipStep, SlideStep, HpPole;

        /// <summary>How fast a squeal's pitch wanders, Hz, and how far: the step of the twice
        /// low-passed noise it wanders on, and the gain that makes its standard deviation
        /// <see cref="WanderStd"/> (measured here rather than assumed).</summary>
        public const float WanderHz = 6f, WanderStd = 0.05f;
        public readonly float WanderStep, WanderGain;

        private RollingFilters(float rate)
        {
            Rate = rate;
            HpAlpha = 1f - MathF.Exp(-2f * MathF.PI * RollingHighPassHz / rate);
            // The roar's low-pass is quoted as its 44.1 kHz step (RollA0..RollA1): the same corner at any rate.
            for (int i = 0; i < _table.Length; i++)
            {
                _alpha[i] = At44k.Step(RollA0 + (RollA1 - RollA0) * i / (_table.Length - 1), rate);
                _table[i] = RollingBandGain(_alpha[i], rate);
            }
            Attack = At44k.Step(0.0016f, rate); Release = At44k.Step(0.00035f, rate);
            SlipStep = At44k.Step(0.0016f, rate); SlideStep = At44k.Step(0.10f, rate);
            HpPole = At44k.Decay(0.992f, rate);
            WanderStep = 1f - MathF.Exp(-2f * MathF.PI * WanderHz / rate);
            var rng = new Random(1);
            double w1 = 0, w2 = 0, sum = 0; int n = 0;
            for (int i = 0; i < (int)(rate * 20); i++)
            {
                w1 += WanderStep * ((rng.NextDouble() * 2 - 1) - w1);
                w2 += WanderStep * (w1 - w2);
                if (i > rate) { sum += w2 * w2; n++; }
            }
            WanderGain = WanderStd / (float)Math.Max(1e-9, Math.Sqrt(sum / Math.Max(1, n)));
        }

        /// <summary>The roar low-pass's step at this rate for a step quoted at 44.1 kHz.</summary>
        public float Alpha(float quoted) => Lookup(_alpha, quoted);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<float, RollingFilters> _byRate = new();
        public static RollingFilters At(float rate) => _byRate.GetOrAdd(rate, static r => new RollingFilters(r));

        /// <summary>The band's gain for the low-pass quoted (at 44.1 kHz) as <paramref name="a"/>.</summary>
        public float Band(float a) => Lookup(_table, a);

        private static float Lookup(float[] t, float a)
        {
            float x = Math.Clamp((a - RollA0) / (RollA1 - RollA0), 0f, 1f) * (t.Length - 1);
            int i = Math.Min((int)x, t.Length - 2);
            float f = x - i;
            return t[i] + (t[i + 1] - t[i]) * f;
        }
    }

    /// <summary>
    /// Tyre on asphalt: rolling, sliding, or somewhere between.
    ///
    /// Rolling is broadband roar (the tread deforming over the aggregate, rising about 30 log10(v))
    /// plus the tread blocks' tone, which rises in pitch with speed; a slick has no blocks and no tone.
    /// Sliding is stick-slip in the same rubber (<see cref="StickSlipVoice"/>): a note that rises as the
    /// tyre is worked harder, collapsing past the limit into a locked wheel's broadband roar. Chirp,
    /// squeal and skid are one continuum sampled at three demands (TyreFriction), not three sounds.
    /// </summary>
    /// <param name="t">The tyre.</param>
    /// <param name="speed">Road speed, m/s.</param>
    /// <param name="slip">Fraction of available grip in use. See <see cref="TyreFriction.Demand"/>.</param>
    /// <param name="rng">The voice's noise source.</param>
    /// <param name="v">This tyre's state between samples.</param>
    /// <param name="rollingPa">
    /// The RMS pressure, pascals at 1 m, this call's rolling noise should make at 20 m/s, before the
    /// caller's own gain; zero or less keeps the unanchored rolling level, which the aircraft's wheels use.
    /// </param>
    /// <param name="rollingRadius">The tyre's rolling radius, metres, which sets how fast its tread
    /// blocks pass. The aircraft's wheels, which declare none, keep the 0.337 m of a 255/40R19.</param>
    /// <param name="sliding">Squeal made elsewhere, wheel by wheel (<see cref="WheelSqueal"/>), to go
    /// out through this voice's output stage with its rolling noise. A caller that passes it passes
    /// no <paramref name="slip"/> of its own, so the sliding is not made twice.</param>
    /// <param name="sampleRate">The rate the caller runs at, which sets the tread tone, the rolling
    /// high-pass and the squeal's resonators in hertz.</param>
    /// <param name="squealScale">The share of the <paramref name="slip"/>'s squeal and slide this call
    /// makes, as pressure: one wheel of an axle whose slip is all that is known (CabinPaths' corners,
    /// each 1/sqrt(wheels) so the axle's wheels together squeal as loud as the axle did).</param>
    /// <param name="toneScale">How much of the tread tone this call makes, as amplitude. Zero leaves it
    /// out: CabinPaths' wheels each roll and squeal for themselves, and the tread tone, one tone in phase
    /// on every tyre of a size, is played once from under the floor (<see cref="TreadTone"/>).</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Tyre(TyreProfile t, float speed, float slip, Random rng, ref TyreVoice v, float rollingPa = 0f,
                             float rollingRadius = 0.337f, float sliding = 0f, float sampleRate = SampleRate,
                             float squealScale = 1f, float toneScale = 1f)
    {
        // A tyre lets go on the instant and settles over a couple of hundred milliseconds; a stepped
        // demand would click at every corner entry.
        var rates = v.Rolling;
        if (rates == null || rates.Rate != sampleRate) v.Rolling = rates = RollingFilters.At(sampleRate);
        float k = slip > v.SlipSmooth ? rates.Attack : rates.Release;
        v.SlipSmooth += (slip - v.SlipSmooth) * k;
        float demand = v.SlipSmooth;

        if (speed < 0.3f && demand < TyreFriction.SquealOnset && sliding == 0f)
        { v.Lp = v.Hp = v.HpPrev = 0f; return 0f; }

        float noise = (float)(rng.NextDouble() * 2 - 1);

        // ── Rolling ──
        float level = MathF.Pow(MathF.Max(speed, 0.3f) / 20f, 1.5f);
        // Quoted as the step at 44.1 kHz (RollingFilters.Alpha takes it to this rate).
        float quoted = MathHelper.Lerp(0.06f, 0.34f, Math.Clamp(speed / 40f, 0f, 1f));
        float cutoff = rates.Alpha(quoted);
        v.Lp += cutoff * (noise - v.Lp);
        float roar = v.Lp * (0.55f + 0.45f * t.SurfaceRoughness);
        float tone = 0f;
        if (t.TreadBlocks > 0)
        {
            float blockHz = speed / (2f * MathF.PI * MathF.Max(0.05f, rollingRadius)) * t.TreadBlocks;
            v.TreadPhase += 2.0 * Math.PI * blockHz / sampleRate;
            if (v.TreadPhase > 2.0 * Math.PI) v.TreadPhase -= 2.0 * Math.PI;
            tone = (float)(Math.Sin(v.TreadPhase) * 0.34 + Math.Sin(v.TreadPhase * 2) * 0.16);
            tone *= 1f - t.SurfaceRoughness * 0.55f;
        }
        // The roar's normalisation below is from the untouched tone's level; only the tone's share moves.
        float toneOut = tone * toneScale;
        float mix;
        if (rollingPa > 0f)
        {
            // Anchored: roar and tread tone each normalised to unit RMS, mixed in the unanchored path's
            // proportion at 20 m/s, at the declared pressure times the speed law, over the output
            // stage's small-signal gain so that is the level that leaves this function.
            var rolling = rates;
            v.RollLp += cutoff * (roar - v.RollLp);
            v.RollHp += rolling.HpAlpha * (v.RollLp - v.RollHp);
            float band = v.RollLp - v.RollHp;
            float bandRms = MathF.Sqrt(rolling.Band(quoted) / 3f) * (0.55f + 0.45f * t.SurfaceRoughness);
            float roarW = 1.4f * (0.55f + 0.45f * t.SurfaceRoughness) * 0.19f;
            float toneW = t.TreadBlocks > 0 ? 0.2657f * (1f - t.SurfaceRoughness * 0.55f) : 0f;
            float norm = MathF.Sqrt(roarW * roarW + toneW * toneW);
            float toneRms = 0.2657f * (1f - t.SurfaceRoughness * 0.55f);
            float unit = (roarW / norm) * band / MathF.Max(1e-6f, bandRms)
                       + (toneW > 0f ? (toneW / norm) * toneOut / toneRms : 0f);
            mix = unit * rollingPa * level / OutputGain;
        }
        else mix = (roar * 1.4f + toneOut) * level * 0.3f;

        // ── Sliding ──
        float squeal = TyreFriction.SquealAmount(demand);
        float skid = TyreFriction.SkidAmount(demand);
        if (squeal > 1e-3f || skid > 1e-3f)
        {
            // A stationary wheel cannot squeal however hard it is pushed; past walking pace only the
            // demand matters.
            float rub = Math.Clamp(speed / 12f, 0f, 1f);

            if (squeal > 1e-3f)
            {
                float hz = t.SquealHz * TyreFriction.SquealPitch(demand);
                float amp = Level(t.SquealDb) * squeal * rub * SquealProminence * squealScale;
                // The note (StickSlipTone) over the scrub: the noise through two poles at the
                // fundamental and one at the second harmonic, a tenth of the power.
                float sq = Resonate(ref v.R1, ref v.R2, noise, hz, t.SquealQ, sampleRate)
                         + Resonate(ref v.R1b, ref v.R2b, noise, hz * 2f, t.SquealQ * 0.7f, sampleRate) * 0.45f;
                if ((v.Tone.Tick++ & 63) == 0) v.Tone.Norm = ResonatorRms(hz, t.SquealQ, sampleRate);
                float note = StickSlipTone(ref v.Tone, hz, noise, rates);
                mix += (ScrubShare * sq + ToneShare * v.Tone.Norm * note) * amp;
            }

            if (skid > 1e-3f)
            {
                // A locked wheel is broadband and dark: torn rather than tapped, well below the squeal.
                v.SlideLp += rates.SlideStep * (noise - v.SlideLp);
                mix += v.SlideLp * Level(t.SquealDb) * skid * rub * 1.6f * SquealProminence * squealScale;
            }
        }

        mix += sliding;
        float y = rates.HpPole * (v.HpPrev + mix - v.Hp);
        v.Hp = mix; v.HpPrev = y;

        // The knee must stay out of the way: at a drive of 0.8 a full squeal came out equal to a gentle
        // scrub, and at 0.13 it still sounded clipped at the source. A gentle drive with the range
        // restored after keeps the waveform and leaves the tanh for the rare extreme.
        return MathF.Tanh(y * 0.05f) * 26f;
    }

    /// <summary>
    /// The tread tone of an anchored <see cref="Tyre"/> call alone, as it leaves that call (its output
    /// stage is linear at rolling levels): the same tone, phase and weight, for <paramref name="rollingPa"/>
    /// at <paramref name="speed"/>. For a voice that plays an axle's tread tone apart from its roar.
    /// </summary>
    public static float TreadTone(TyreProfile t, float speed, float rollingPa, float rollingRadius, ref double phase,
                                  float sampleRate = SampleRate)
    {
        if (t.TreadBlocks <= 0 || rollingPa <= 0f || speed < 0.3f) return 0f;
        float level = MathF.Pow(MathF.Max(speed, 0.3f) / 20f, 1.5f);
        float blockHz = speed / (2f * MathF.PI * MathF.Max(0.05f, rollingRadius)) * t.TreadBlocks;
        phase += 2.0 * Math.PI * blockHz / sampleRate;
        if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
        float tone = (float)(Math.Sin(phase) * 0.34 + Math.Sin(phase * 2) * 0.16) * (1f - t.SurfaceRoughness * 0.55f);
        float roarW = 1.4f * (0.55f + 0.45f * t.SurfaceRoughness) * 0.19f;
        float toneW = 0.2657f * (1f - t.SurfaceRoughness * 0.55f);
        float norm = MathF.Sqrt(roarW * roarW + toneW * toneW);
        return (toneW / norm) * tone / toneW * rollingPa * level;
    }

    /// <summary>The output stage's gain for small signals: tanh(0.05 y) x 26.</summary>
    private const float OutputGain = 0.05f * 26f;

    /// <summary>
    /// How much louder a squeal is rendered than its sound pressure alone would suggest. A squeal sits
    /// at 600 Hz-4 kHz, where the ear is ten-odd decibels more sensitive than in an engine's octaves,
    /// so pressure taken as loudness buried it: a full squeal moved a sports car's voice by 0.6 dB at
    /// unity, 5 at 2.6, 8.5 at twenty; then backed off a quarter by ear.
    /// TODO: weight the whole mix as the ear does, and retire this.
    /// </summary>
    public const float SquealProminence = 15f;

    /// <summary>One wheel's squeal: its smoothed demand and slip velocity, and its resonators.</summary>
    public struct WheelSquealVoice
    {
        public float Demand, SlipVelocity;
        public float R1, R2, R1b, R2b, SlideLp;
        // Refreshed every 64 samples: the pitch follows a demand smoothed over tens of milliseconds.
        public float C1, C2, G, C1b, C2b, Gb;
        public int Tick;
        /// <summary>The squeal's note itself (see <see cref="StickSlipVoice"/>).</summary>
        public StickSlipVoice Tone;
        /// <summary>The per-sample coefficients at this voice's rate, found on its first sample.</summary>
        internal RollingFilters? Rates;
    }

    /// <summary>
    /// One tyre's squeal and slide, from that wheel's own state, before the output stage of the voice
    /// it goes out through (pass it to <see cref="Tyre"/> as <c>sliding</c>).
    ///
    /// The stick-slip resonance at <see cref="TyreProfile.SquealHz"/> with its harmonic: measured
    /// cornering squeal peaks round 1.2 and 2.5 kHz, and a stiffer block or lower friction raises it
    /// (Tan Li, NOISE-CON 2019). The demand's continuum is <see cref="TyreFriction"/>'s.
    ///
    /// The level follows the frictional power in the sliding part of the patch: p^2 ~ s (Fz / Fz0) Vs,
    /// with s the sliding share of the contact length from the brush model with a parabolic pressure
    /// distribution, s = 1 - (1 - d)^(1/3), 1 past the limit (Pacejka, Tire and Vehicle Dynamics, 2nd
    /// ed. 2006, 3.2), and Vs = u sqrt(kappa^2 + tan^2 alpha). So a stationary wheel cannot squeal, the
    /// loaded outside front squeals first, and a locked wheel at speed is far louder than one at its
    /// cornering limit.
    /// </summary>
    /// <param name="t">The tyre.</param>
    /// <param name="demand">The wheel's share of its grip in use (1 the limit), as the server sends it.</param>
    /// <param name="slipVelocity">Vs, m/s.</param>
    /// <param name="loadShare">Fz / Fz0.</param>
    /// <param name="referenceSlipVelocity">The Vs at which a tyre at the limit gives
    /// <see cref="TyreProfile.SquealDb"/>'s share for one wheel.</param>
    /// <param name="wheels">How many wheels the vehicle has: <see cref="TyreProfile.SquealDb"/> is the
    /// level the two axle voices together made at the limit, so each of n wheels gets 2/n of its power.</param>
    /// <param name="rng">The voice's noise source.</param>
    /// <param name="v">This wheel's state between samples.</param>
    /// <param name="stickSlip">The surface's stick-slip, 0..1 (RoadSurfaces.StickSlipOf): at 1 a full
    /// slide stays a squeal (TyreFriction.SkidAmount).</param>
    /// <param name="sampleRate">The rate the caller runs at, which sets the resonators in hertz.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float WheelSqueal(TyreProfile t, float demand, float slipVelocity, float loadShare, float referenceSlipVelocity,
                                    int wheels, Random rng, ref WheelSquealVoice v, float stickSlip = 1f,
                                    float sampleRate = SampleRate)
    {
        // Smoothed as in Tyre: quick to let go, slow to settle.
        var rates = v.Rates;
        if (rates == null || rates.Rate != sampleRate) v.Rates = rates = RollingFilters.At(sampleRate);
        float k = demand > v.Demand ? rates.Attack : rates.Release;
        v.Demand += (demand - v.Demand) * k;
        v.SlipVelocity += (slipVelocity - v.SlipVelocity) * rates.SlipStep;
        float d = v.Demand;
        float squeal = TyreFriction.SquealAmount(d, stickSlip);
        float skid = TyreFriction.SkidAmount(d, stickSlip);
        if (squeal <= 1e-3f && skid <= 1e-3f) { v.R1 = v.R2 = v.R1b = v.R2b = v.SlideLp = 0f; v.Tick = 0; return 0f; }

        float sliding = d >= 1f ? 1f : 1f - MathF.Cbrt(1f - d);
        float power = sliding * MathF.Max(0f, loadShare) * MathF.Max(0f, v.SlipVelocity) / MathF.Max(1e-3f, referenceSlipVelocity);
        float amp = Level(t.SquealDb) * SquealProminence * MathF.Sqrt(2f / MathF.Max(1, wheels) * power);
        float noise = (float)(rng.NextDouble() * 2 - 1);
        float mix = 0f;
        if (squeal > 1e-3f)
        {
            float hz = t.SquealHz * TyreFriction.SquealPitch(d);
            if ((v.Tick++ & 63) == 0)
            {
                Coefficients(hz, t.SquealQ, sampleRate, out v.C1, out v.C2, out v.G);
                Coefficients(hz * 2f, t.SquealQ * 0.7f, sampleRate, out v.C1b, out v.C2b, out v.Gb);
                v.Tone.Norm = ResonatorRms(hz, t.SquealQ, sampleRate);
            }
            float y1 = noise * v.G + v.C1 * v.R1 - v.C2 * v.R2;
            v.R2 = v.R1; v.R1 = y1;
            float y2 = noise * v.Gb + v.C1b * v.R1b - v.C2b * v.R2b;
            v.R2b = v.R1b; v.R1b = y2;
            // The note (StickSlipTone) over the scrub, a tenth of the power.
            float tone = StickSlipTone(ref v.Tone, hz, noise, rates);
            mix += (ScrubShare * (y1 + y2 * 0.45f) + ToneShare * v.Tone.Norm * tone) * amp * squeal;
        }
        if (skid > 1e-3f)
        {
            v.SlideLp += rates.SlideStep * (noise - v.SlideLp);
            mix += v.SlideLp * amp * skid * 1.6f;
        }
        return mix;
    }

    /// <summary>The coefficients <see cref="Resonate"/> computes each sample, computed once.</summary>
    private static void Coefficients(float hz, float q, float rate, out float c1, out float c2, out float g)
    {
        float w = 2f * MathF.PI * Math.Clamp(hz, 40f, rate * 0.45f) / rate;
        float r = MathF.Exp(-w / (2f * MathF.Max(0.5f, q)));
        c1 = 2f * r * MathF.Cos(w);
        c2 = r * r;
        g = 1f - r;
    }

    /// <summary>The squeal level relative to the rolling noise, as a linear factor. Both are quoted
    /// in dB at a metre, so the difference between them is the only thing that matters.</summary>
    private static float Level(float squealDb) => MathF.Pow(10f, (squealDb - 96f) / 20f) * 6f;

    /// <summary>One two-pole resonator, driven by noise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Resonate(ref float z1, ref float z2, float x, float hz, float q, float rate)
    {
        float w = 2f * MathF.PI * Math.Clamp(hz, 40f, rate * 0.45f) / rate;
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
