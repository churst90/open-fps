using System;
using System.Collections.Generic;
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
    /// <summary>The BLOCK on its own — combustion knock, valvetrain clatter, accessory and turbo
    /// whine. It is normally folded into <see cref="Intake"/>, which makes it impossible to tell
    /// whether the mechanical layer is quiet or simply buried under a much louder intake. It is the
    /// whole character of a diesel and it had never been separable.</summary>
    public required float[] Block { get; init; }
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
    /// <summary>The bench's rate: the rate the game mixes at, so a bench render is what the live voice makes.</summary>
    public const int SampleRate = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.DefaultRate;

    /// <summary>Renders a whole drive. Deterministic given the seed.</summary>
    /// <param name="listener">Where the bench stands, in the machine's frame (x across, y up, z
    /// forward, origin at the exhaust). Null sums every tailpipe at one point, which is what a
    /// listener dead behind the car hears and what every render did before tailpipes had positions.</param>
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
        // The car the engine is bolted into. Driven by the EXHAUST rather than by the finished mix,
        // because that is what physically shakes a floorpan — and because EngineVoiceState drives it
        // the same way, so the offline render and the game's live voice cannot disagree about what a
        // car sounds like. They are two renderers of one model and this is the seam where that shows.
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
        // How loud the can is against the pipe. Printed because "I could not tell the three apart"
        // is only answerable by a number: a layer nobody can hear is either too quiet or not there.
        if (shellEnergy > 0f)
            log.Add($"  muffler case {10.0 * Math.Log10(shellEnergy / Math.Max(1e-20, pipeEnergy)):F1} dB "
                  + $"against the pipe (ShellLevel {v.Engine.Exhaust.Muffler.ShellLevel:G3})");

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
        /// <summary>The anchored rolling path's high-pass, which takes off the bass a real tyre
        /// does not make (see <see cref="RollingHighPassHz"/>).</summary>
        public float RollHp;
        /// <summary>And its second lowpass pole.</summary>
        public float RollLp;
        /// <summary>The rolling path's filters at the rate this voice runs at, found on its first sample.</summary>
        internal RollingFilters? Rolling;
    }

    /// <summary>
    /// Where the anchored rolling roar is cut below. Tyre/road noise is a band round 1 kHz: in the
    /// CNOSSOS-EU light-vehicle spectrum the 250 Hz octave is twelve decibels under the 1 kHz one, and
    /// the lowpass alone left it flat all the way down, a rumble the tyre does not make. Above the
    /// peak the same spectrum falls about ten decibels an octave, which is the lowpass taken twice;
    /// once, it left the 8 kHz octave six decibels under the peak where it should be twenty-odd, and
    /// that was a hiss.
    /// </summary>
    public const float RollingHighPassHz = 400f;

    /// <summary>
    /// The variance of white noise through the rolling lowpass (coefficient <paramref name="a"/>)
    /// twice and the high-pass, over the input's. Integrated from the two filters' responses rather than
    /// assumed, so the declared level is the level the roar really makes wherever the lowpass is.
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
    /// The anchored rolling path's high-pass at one sample rate, and the gains of
    /// <see cref="RollingBandGain"/> over the lowpass's range at that rate. Made once per rate: the
    /// mixer runs at whatever the device does, and 400 Hz is 400 Hz at 48 kHz too.
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

        private RollingFilters(float rate)
        {
            Rate = rate;
            HpAlpha = 1f - MathF.Exp(-2f * MathF.PI * RollingHighPassHz / rate);
            // The roar's low-pass is quoted as its 44.1 kHz step (RollA0..RollA1), so it is the same
            // corner at any rate: each quoted step, its step at this rate, and the band's gain there.
            for (int i = 0; i < _table.Length; i++)
            {
                _alpha[i] = At44k.Step(RollA0 + (RollA1 - RollA0) * i / (_table.Length - 1), rate);
                _table[i] = RollingBandGain(_alpha[i], rate);
            }
            Attack = At44k.Step(0.0016f, rate); Release = At44k.Step(0.00035f, rate);
            SlipStep = At44k.Step(0.0016f, rate); SlideStep = At44k.Step(0.10f, rate);
            HpPole = At44k.Decay(0.992f, rate);
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
    /// <param name="rollingPa">
    /// The RMS pressure, pascals at 1 m, this call's rolling noise should make at 20 m/s, before the
    /// caller's own gain; zero or less keeps the old unanchored rolling level, which the aircraft's
    /// wheels still use.
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
    /// <param name="toneScale">The share of the tread tone this call makes, as amplitude. The tread tone
    /// is one tone: every tyre of a size turns at the same rate from the same phase, so the wheels' tones
    /// add as amplitudes, not powers. A wheel standing for its share of an axle (CabinPaths' corners,
    /// whose noise is its share in power) passes the square root of that share here, so the wheels'
    /// tones together are the axle's tone and their roar the axle's roar.</param>
    public static float Tyre(TyreProfile t, float speed, float slip, Random rng, ref TyreVoice v, float rollingPa = 0f,
                             float rollingRadius = 0.337f, float sliding = 0f, float sampleRate = SampleRate,
                             float squealScale = 1f, float toneScale = 1f)
    {
        // The demand is smoothed, and asymmetrically: a tyre lets go quickly and settles slowly, so
        // a squeal starts on the instant and dies away over a couple of hundred milliseconds. Stepping
        // it would make every corner entry a click.
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
            // Anchored: the roar is normalised to unit RMS from the filters' own response, the tread
            // tone likewise, and the two are mixed in the proportion the unanchored path always had
            // them at 20 m/s so the character of each tyre is unchanged. What comes out is the
            // declared pressure times the speed law, divided by the output stage's small-signal gain
            // (see the return) so that it is the level that leaves this function.
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
            // How fast the rubber is actually being dragged across the road decides how much of this
            // there is: a stationary wheel cannot squeal, however hard it is being pushed, and the
            // same demand at eighty is far louder than at ten. Saturating, because past walking pace
            // the mechanism is fully established and only the demand matters.
            float rub = Math.Clamp(speed / 12f, 0f, 1f);

            if (squeal > 1e-3f)
            {
                float hz = t.SquealHz * TyreFriction.SquealPitch(demand);
                float amp = Level(t.SquealDb) * squeal * rub * SquealProminence * squealScale;
                // Two poles at the fundamental and one at the second harmonic. Real squeal is rich —
                // the release is a snap, not a sine — and the octave is most of what makes it read as
                // rubber rather than as a test tone.
                float sq = Resonate(ref v.R1, ref v.R2, noise, hz, t.SquealQ, sampleRate)
                         + Resonate(ref v.R1b, ref v.R2b, noise, hz * 2f, t.SquealQ * 0.7f, sampleRate) * 0.45f;
                mix += sq * amp;
            }

            if (skid > 1e-3f)
            {
                // A locked wheel is broadband and DARK: the tread is being torn rather than tapped,
                // and the energy sits well below the squeal it replaced.
                v.SlideLp += rates.SlideStep * (noise - v.SlideLp);
                mix += v.SlideLp * Level(t.SquealDb) * skid * rub * 1.6f * SquealProminence * squealScale;
            }
        }

        mix += sliding;
        float y = rates.HpPole * (v.HpPrev + mix - v.Hp);
        v.Hp = mix; v.HpPrev = y;

        // Shaped, not clipped, and with room above. At a drive of 0.8 a full squeal comes out of the
        // tanh at exactly the value a gentle scrub does — the shaper erases the whole difference
        // between a tyre working and a tyre screaming, and no amount of turning the layer up
        // afterwards can put it back. A gentle knee keeps the quiet case and gives the loud one
        // somewhere to go.
        // The knee has to stay OUT OF THE WAY. At a drive of 0.13 a full squeal sits well up the
        // curve, so the level comes from saturation rather than from gain, and it sounds clipped at
        // the source. A gentler drive with the range restored afterwards gives the same loudness
        // with the waveform intact, and keeps
        // the tanh for what it is for, which is catching the rare extreme rather than shaping the
        // normal case.
        return MathF.Tanh(y * 0.05f) * 26f;
    }

    /// <summary>The output stage's gain for small signals: tanh(0.05 y) x 26.</summary>
    private const float OutputGain = 0.05f * 26f;

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

    /// <summary>One wheel's squeal: its smoothed demand and slip velocity, and its resonators.</summary>
    public struct WheelSquealVoice
    {
        public float Demand, SlipVelocity;
        public float R1, R2, R1b, R2b, SlideLp;
        // The resonators' coefficients, refreshed every 64 samples: the pitch moves with the demand,
        // which is smoothed over tens of milliseconds, so per-sample exp and cos buy nothing.
        public float C1, C2, G, C1b, C2b, Gb;
        public int Tick;
        /// <summary>The per-sample coefficients at this voice's rate, found on its first sample.</summary>
        internal RollingFilters? Rates;
    }

    /// <summary>
    /// One tyre's squeal and slide, from that wheel's own state, before the output stage of the voice
    /// it goes out through (pass it to <see cref="Tyre"/> as <c>sliding</c>).
    ///
    /// The mechanism is the one <see cref="Tyre"/> describes: tread elements in the sliding part of
    /// the contact patch stick, deflect, let go and snap back, a relaxation oscillation at the
    /// element's stick-slip resonance (<see cref="TyreProfile.SquealHz"/>), its harmonic beside it.
    /// Measured squeal sits there: peaks round 1.2 and 2.5 kHz in drum tests of cornering, and a
    /// stiffer tread block or lower friction raises the note (Tan Li, "Tire Braking/Cornering Noise
    /// Analysis: Stick/Slip Mechanism", NOISE-CON 2019). Where along the demand the note starts,
    /// peaks and gives way to the broadband slide of a locked wheel is <see cref="TyreFriction"/>'s
    /// continuum, as it is for the axle voice.
    ///
    /// How much there is follows the frictional power in the sliding part of the patch, taken as
    /// what is radiated in a fixed proportion:
    ///
    ///   p^2 ~ s (Fz / Fz0) Vs
    ///
    /// s, the sliding share of the contact length, from the brush model with a parabolic pressure
    /// distribution, where the force share is d = 1 - (1 - s)^3 so s = 1 - (1 - d)^(1/3), and 1 past
    /// the limit (Pacejka, Tire and Vehicle Dynamics, 2nd ed. 2006, section 3.2); Fz / Fz0 the wheel's
    /// load over its static load; Vs the speed the rubber is dragged over the road, u sqrt(kappa^2 +
    /// tan^2 alpha). So a stationary wheel cannot squeal however hard it is pushed, the loaded outside
    /// front of a corner squeals before the light inside one, and a locked wheel at speed, dragged at
    /// the whole road speed, is far louder than a tyre at its cornering limit.
    /// </summary>
    /// <param name="demand">The wheel's share of its grip in use (1 the limit), as the server sends it.</param>
    /// <param name="slipVelocity">Vs, m/s.</param>
    /// <param name="loadShare">Fz / Fz0.</param>
    /// <param name="referenceSlipVelocity">The Vs at which a tyre at the limit gives
    /// <see cref="TyreProfile.SquealDb"/>'s share for one wheel.</param>
    /// <param name="wheels">How many wheels the vehicle has: <see cref="TyreProfile.SquealDb"/> is the
    /// level the two axle voices together made at the limit, so each of n wheels gets 2/n of its power.</param>
    /// <param name="sampleRate">The rate the caller runs at, which sets the resonators in hertz.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float WheelSqueal(TyreProfile t, float demand, float slipVelocity, float loadShare, float referenceSlipVelocity,
                                    int wheels, Random rng, ref WheelSquealVoice v, float stickSlip = 1f,
                                    float sampleRate = SampleRate)
    {
        // Smoothed as the axle voice smooths its demand: a tyre lets go on the instant and settles
        // over a couple of hundred milliseconds.
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
            if ((v.Tick++ & 63) == 0)
            {
                float hz = t.SquealHz * TyreFriction.SquealPitch(d);
                Coefficients(hz, t.SquealQ, sampleRate, out v.C1, out v.C2, out v.G);
                Coefficients(hz * 2f, t.SquealQ * 0.7f, sampleRate, out v.C1b, out v.C2b, out v.Gb);
            }
            float y1 = noise * v.G + v.C1 * v.R1 - v.C2 * v.R2;
            v.R2 = v.R1; v.R1 = y1;
            float y2 = noise * v.Gb + v.C1b * v.R1b - v.C2b * v.R2b;
            v.R2b = v.R1b; v.R1b = y2;
            mix += (y1 + y2 * 0.45f) * amp * squeal;
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

    /// <summary>
    /// One two-pole resonator, driven by noise. The cheapest thing that rings, and ringing is the
    /// entire point: a squeal is a resonance being excited, not a filtered hiss.
    /// </summary>
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
