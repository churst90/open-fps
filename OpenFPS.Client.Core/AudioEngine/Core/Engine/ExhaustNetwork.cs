using System.Numerics;
using OpenFPS.Common;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Engine;

/// <summary>
/// The exhaust system as a network of pipes that meet each other, built from an
/// <see cref="ExhaustSpec"/>: one primary per cylinder into its collector, the collectors joined
/// (or not) by the crossover, then each branch runs mid-pipe, muffler and tailpipe to an open end.
///
/// A junction is not a mixer: part of a wave reflects and the rest divides among the other pipes,
/// including back up other primaries to closed valves. That cross-talk is why four pipes of four
/// lengths make a forest of resonances, and why a cast manifold and long-tubes sound different.
///
/// The muffler is not a filter either: a chambered can is two area steps and a delay, which gives the
/// textbook expansion chamber's loss, 10 log(1 + (m - 1/m)^2 sin^2(kL) / 4); an absorptive one is a pipe
/// with heavy frequency-dependent loss; a resonator is a neck into a closed volume. All are built from
/// the same pipes and junctions.
/// </summary>
internal sealed class ExhaustNetwork
{
    private readonly EngineProfile _e;
    private readonly ExhaustSpec _x;
    private readonly float _rate;
    private readonly float _spreadSlew;
    private readonly int _n;

    private readonly Pipe[] _primary;
    private readonly int[][] _groups;
    private readonly Pipe[] _collector;             // one per group, collector to the merge
    private readonly Pipe? _crossTube;              // H-pipe balance tube
    private readonly Branch[] _branch;
    private readonly float[] _valveArrived;
    private readonly float[] _scratchA = new float[20], _scratchY = new float[20], _scratchB = new float[20];
    private readonly float _airDensity = 1.2f;
    private float _flowLossFraction;
    private float _tailK = 293f;

    /// <summary>
    /// The turbine, between the manifold and the downpipe: a bladed rotor across the gas path. It
    /// takes energy out (the pulse energy spins the compressor, so a turbo note is a rush, not beats),
    /// reflects what it neither passes nor absorbs back up the manifold (blocking without reflecting
    /// would make the manifold an open end), and scatters the top: the low end passes, the scattering
    /// sets in above a couple of kilohertz, where the blade passages stop being short.
    /// </summary>
    private sealed class Turbine
    {
        private readonly float _alpha;
        private float _lp;

        /// <summary>Fraction of a LOW-frequency wave that reaches the downpipe.</summary>
        private readonly float _pass;
        /// <summary>How much of what is stopped comes back up the manifold rather than becoming
        /// shaft work. A rotor is a poor absorber and a fair mirror.</summary>
        private readonly float _reflect;

        public Turbine(float rate, float cornerHz, float pass, float reflect)
        {
            _alpha = OnePole.AlphaFor(cornerHz, rate);
            _pass = pass;
            _reflect = reflect;
        }

        /// <summary>Splits a wave heading into the downpipe into what gets through and what comes
        /// back. The remainder — neither transmitted nor reflected — is the shaft work.</summary>
        public (float Through, float Back) Split(float incoming)
        {
            _lp += _alpha * (incoming - _lp);
            float through = _lp * _pass;
            return (through, (incoming - through) * _reflect);
        }
    }

    /// <summary>One run from the merge point to the open air.</summary>
    private sealed class Branch
    {
        public readonly List<Pipe> Chain = new();
        /// <summary>Side branches attached at the junction AFTER chain pipe i: a Helmholtz neck+cavity.</summary>
        public readonly Dictionary<int, (Pipe Neck, Pipe Cavity)> Resonators = new();
        public required OpenEnd End;
        public required JetNoise Jet;
        /// <summary>Null on a naturally aspirated engine — there is nothing in the way.</summary>
        public Turbine? Turbine;
        public float ExitVelocity, MeanVelocity;
        public float Radiated;

        /// <summary>Which entries in <see cref="Chain"/> are muffler chambers — the pipes with the
        /// can's steel around them, and therefore the ones whose pressure drives it.</summary>
        public readonly List<int> ChamberIndices = new();

        /// <summary>The can, as metal. Null when the muffler has no shell worth modelling.</summary>
        public BodyResonator? Shell;

        /// <summary>What the shell radiated this sample, pascals at one metre. Diagnostic.</summary>
        public float ShellRadiated;


        /// <summary>Where this pipe leaves the car, machine frame, relative to the exhaust part.</summary>
        public Vector3 Exit;

        /// <summary>The ring that delays this pipe by its extra distance to the listener against the
        /// nearest pipe, in samples. See <see cref="SetListener"/>.</summary>
        public float[] Path = Array.Empty<float>();
        public int PathAt;
        public float PathSamples, PathTarget;
        /// <summary>Spherical spreading against the part's centre: one over the ratio of this pipe's
        /// distance to the listener and the centre's. Unity in the far field.</summary>
        public float Spread = 1f, SpreadTarget = 1f;
    }

    public ExhaustNetwork(EngineProfile e, float rate, int seed = 3)
    {
        _e = e;
        _x = e.Exhaust;
        _rate = rate;
        _spreadSlew = At44k.Increment(0.0005f, rate);   // 2000 samples at 44.1 kHz, full scale
        _n = e.Cylinders;
        _groups = e.CollectorGroups;
        _valveArrived = new float[_n];

        float steep = Math.Clamp(_x.Steepening, 0f, 1.5f);
        float wall = MathF.Max(0.2f, _x.WallLossMultiplier);

        // ── Primaries ───────────────────────────────────────────────────────────────────────
        // Unequal, front to back down each bank, unless the profile lists them explicitly.
        _primary = new Pipe[_n];
        float primArea = Circle(_x.PrimaryDiameterMm);
        for (int c = 0; c < _n; c++)
        {
            float L;
            if (_x.PrimaryLengthsMetres != null && c < _x.PrimaryLengthsMetres.Length)
                L = _x.PrimaryLengthsMetres[c];
            else
            {
                // Position along the bank decides the length; a spread of 0 makes them equal.
                int bank = e.Bank[c];
                int inBank = 0, ofBank = 0;
                for (int k = 0; k < _n; k++) if (e.Bank[k] == bank) { if (k < c) inBank++; ofBank++; }
                float pos = ofBank <= 1 ? 0.5f : inBank / (float)(ofBank - 1);
                L = _x.PrimaryLengthMetres * (1f + _x.PrimarySpread * (pos - 0.5f) * 2f * 0.5f);
                // A little deterministic scatter on top, so two pipes never quite match.
                L *= 1f + 0.015f * MathF.Sin(c * 2.399f);
            }
            _primary[c] = new Pipe(L, primArea, rate, wall, steep);
        }

        // ── Collectors and the merge ────────────────────────────────────────────────────────
        int g = _groups.Length;
        float colArea = Circle(_x.CollectorDiameterMm);
        _collector = new Pipe[g];
        for (int i = 0; i < g; i++)
            _collector[i] = new Pipe(_x.CollectorPipeMetres * (1f + 0.04f * i), colArea, rate, wall, steep);

        var cross = _x.Crossover;
        if (g != 2 && (cross == CrossoverKind.HPipe || cross == CrossoverKind.XPipe))
            cross = g == 1 ? CrossoverKind.Merged : CrossoverKind.None;
        _crossover = cross;
        if (cross == CrossoverKind.HPipe)
            _crossTube = new Pipe(_x.CrossoverTubeMetres, colArea * Math.Clamp(_x.CrossoverArea, 0.05f, 1.5f), rate, wall, steep * 0.5f);

        int branches = cross == CrossoverKind.Merged ? 1 : g;
        _branch = new Branch[branches];
        float tailArea = Circle(_x.TailpipeDiameterMm);
        for (int b = 0; b < branches; b++)
        {
            var br = new Branch
            {
                End = new OpenEnd(rate),
                Jet = new JetNoise(rate, _x.TailpipeDiameterMm * 1e-3f, seed + 17 * b),
                // A radial turbine takes about 6 dB off a passing wave across the plane-wave range and
                // scatters only above a couple of kilohertz (Tiikoja and Abom: 5-10 dB transmission
                // loss); half of what is stopped comes back up the manifold. A 260 Hz corner passing a
                // third left a straight-piped diesel pickup all rumble and turbo whine.
                // Turbocharged only: a blower has nothing in the exhaust, and a turbine would take
                // 17 dB off a supercharged V8.
                Turbine = e.Induction == Induction.Turbocharged
                        ? new Turbine(rate, 2500f, 0.5f, 0.5f) : null,
            };
            // Mid pipe from the merge to the muffler.
            br.Chain.Add(new Pipe(_x.MidPipeMetres * (1f + 0.03f * b), tailArea, rate, wall, steep));
            BuildMuffler(br, _x.Muffler, tailArea, wall, steep);
            // The can, once the chambers it wraps are known.
            if (_x.Muffler.Shell != null && _x.Muffler.ShellLevel > 0f && br.ChamberIndices.Count > 0)
                br.Shell = new BodyResonator(_x.Muffler.Shell, rate);
            // Tailpipe: two branches get two lengths, and if only one is given the second is 9% longer.
            float tailL = _x.TailpipeMetres.Length > b ? _x.TailpipeMetres[b]
                        : _x.TailpipeMetres[0] * (1f + 0.09f * b);
            br.Chain.Add(new Pipe(tailL, tailArea, rate, wall, steep));
            var exits = _x.TailpipeExitsMetres;
            br.Exit = exits != null && b < exits.Length ? exits[b] : Vector3.Zero;
            if (br.Exit != Vector3.Zero) _hasExits = true;
            _branch[b] = br;
        }
        if (_hasExits)
        {
            // Long enough for two metres of spacing at any rate this will be run at.
            int len = 64;
            while (len < 0.006f * rate) len <<= 1;
            foreach (var br in _branch) br.Path = new float[len];
        }

        UpdateGas(_x.GasCelsiusIdle + 273.15f, 0f);
    }

    private readonly CrossoverKind _crossover;

    /// <summary>True when the profile places its tailpipes apart; false means one point.</summary>
    private readonly bool _hasExits;
    /// <summary>True once anyone has said where the listener is.</summary>
    private bool _listenerKnown;

    /// <summary>
    /// The most a pipe's path delay may move per sample. A real pass-by's differential Doppler stays
    /// under half a per cent; the cap keeps a game-thread jump (a teleport, a respawn) from a chirp.
    /// </summary>
    private const float MaxPathSlew = 0.005f;

    /// <summary>
    /// Tells the network where the listener stands, in the machine's frame (x across, y up, z
    /// forward, origin at the exhaust part), so each tailpipe can radiate from its own place.
    ///
    /// A single-point sum is right only dead behind the car: on an even-firing V10 the banks are
    /// anti-phase at the bank firing rate, so the sum cancelled the fundamental and left a siren (order
    /// 2.5 read 12-19 dB under order 5 on the sum, level with it on one pipe). Each branch gets the
    /// delay of its extra distance to the listener against the nearest pipe, and the spreading ratio
    /// up close; on the centre line far off this is the single sum exactly. Called a few hundred times
    /// a second at most; the delays slew, never step.
    /// </summary>
    public void SetListener(Vector3 machineFrame)
    {
        if (!_hasExits) return;
        float r = machineFrame.Length();
        if (r < 0.05f) return;                    // standing in the tailpipe: nothing to say
        // Air, not exhaust gas: the extra distance is travelled outside the pipe.
        const float airC = 343f;
        float nearest = float.MaxValue;
        foreach (var br in _branch) nearest = MathF.Min(nearest, Vector3.Distance(machineFrame, br.Exit));
        foreach (var br in _branch)
        {
            float path = Vector3.Distance(machineFrame, br.Exit);
            br.PathTarget = Math.Clamp((path - nearest) / airC * _rate, 0f, br.Path.Length - 3f);
            // Beyond a metre the spreading ratio is within a few per cent of one.
            br.SpreadTarget = r > 1f ? Math.Clamp(r / MathF.Max(0.1f, path), 0.25f, 4f) : 1f;
        }
        _listenerKnown = true;
    }

    private static float Circle(float diameterMm)
    {
        float r = diameterMm * 0.5e-3f;
        return MathF.PI * r * r;
    }

    /// <summary>
    /// Builds the muffler's internals onto the branch chain. Every section steepens with the profile's
    /// <see cref="ExhaustSpec.Steepening"/>: steepening belongs to the gas and the wave, not the pipe,
    /// and a chamber's wider area has already dropped the pressure.
    /// </summary>
    private void BuildMuffler(Branch br, MufflerSpec m, float pipeArea, float wall, float steep)
    {
        switch (m.Kind)
        {
            case MufflerKind.None:
                return;

            case MufflerKind.Chambered:
            case MufflerKind.Baffled:
            {
                float canArea = pipeArea * MathF.Max(1.5f, m.ExpansionRatio);
                for (int i = 0; i < m.ChamberLengthsMetres.Length; i++)
                {
                    // Baffles take energy off every internal reflection and broaden the notches: a
                    // clean expansion chamber rings, a Flowmaster does not.
                    var chamber = new Pipe(m.ChamberLengthsMetres[i], canArea, _rate, wall, steep);
                    float baffle = Math.Clamp(m.BaffleLoss, 0f, 0.95f);
                    chamber.SetExtraLoss(1f - 0.5f * baffle, OnePole.AlphaFor(MathHelper.Lerp(12000f, 1500f, baffle), _rate));
                    br.Chain.Add(chamber);
                    // Its pressure is what shakes the case.
                    br.ChamberIndices.Add(br.Chain.Count - 1);
                    // Between chambers, a short passage through the partition at pipe area.
                    if (i + 1 < m.ChamberLengthsMetres.Length)
                        br.Chain.Add(new Pipe(0.04f, pipeArea, _rate, wall, steep));
                }
                if (m.Kind == MufflerKind.Baffled)
                {
                    AddAbsorptive(br, m, pipeArea, wall, steep);
                    if (m.ResonatorHz > 0f) AddResonator(br, m, pipeArea, wall);
                }
                return;
            }

            case MufflerKind.Absorptive:
                AddAbsorptive(br, m, pipeArea, wall, steep);
                if (m.ResonatorHz > 0f) AddResonator(br, m, pipeArea, wall);
                return;
        }
    }

    /// <summary>A perforated tube in packing: the pipe continues at its own area, and loses its top
    /// progressively along the length. Absorption 0.6 takes the corner down to about 700 Hz.</summary>
    private void AddAbsorptive(Branch br, MufflerSpec m, float pipeArea, float wall, float steep)
    {
        var p = new Pipe(m.AbsorptiveLengthMetres, pipeArea, _rate, wall, steep);
        float a = Math.Clamp(m.Absorption, 0f, 1f);
        float corner = MathHelper.Lerp(9000f, 450f, MathF.Pow(a, 0.8f));
        p.SetExtraLoss(1f - 0.35f * a, OnePole.AlphaFor(corner, _rate));
        br.Chain.Add(p);
    }

    /// <summary>A Helmholtz resonator hung off the pipe after the last element added: a neck into a
    /// closed cavity, sized to speak at the requested frequency in gas at the tailpipe temperature.</summary>
    private void AddResonator(Branch br, MufflerSpec m, float pipeArea, float wall)
    {
        // f = c/(2 pi) sqrt(S / (V L')). Pick a neck of 30 mm diameter and 60 mm length and solve for V.
        float neckArea = Circle(30f);
        float neckL = 0.06f;
        float neckEff = neckL + 1.7f * MathF.Sqrt(neckArea / MathF.PI);
        float c = Gas.SoundSpeed(273.15f + _x.GasCelsiusIdle * _x.TailCooling + 20f, Gas.GammaExhaust);
        float w = 2f * MathF.PI * MathF.Max(20f, m.ResonatorHz);
        float V = c * c * neckArea / (w * w * neckEff);
        // A wide short pipe, closed, well under a quarter wave so it behaves as a compliance.
        float cavArea = pipeArea * 6f;
        float cavL = MathF.Max(0.03f, V / cavArea);
        var neck = new Pipe(neckL, neckArea, _rate, wall * 2f, 0f);
        var cavity = new Pipe(cavL, cavArea, _rate, wall * 2f, 0f);
        float q = MathF.Max(1f, m.ResonatorQ);
        cavity.SetExtraLoss(1f - 0.5f / q, 1f);
        br.Resonators[br.Chain.Count - 1] = (neck, cavity);
    }

    /// <summary>Characteristic impedance of a cylinder's primary, Pa s/m^3 — the valve boundary needs it.</summary>
    public float PrimaryImpedance(int cyl) => _primary[cyl].Impedance;
    public float PrimarySoundSpeed(int cyl) => _primary[cyl].SoundSpeed;

    /// <summary>The wave arriving back at the valve end of a cylinder's primary. Once per sample.</summary>
    public float ArrivedAtValve(int cyl) => _valveArrived[cyl] = _primary[cyl].ArriveNear();
    /// <summary>The wave the valve sends down its primary. Once per sample, after ArrivedAtValve.</summary>
    public void PushFromValve(int cyl, float p)
    {
        _primary[cyl].PushForward(p);
        _portSumAcc += _valveArrived[cyl] + p;
    }
    private float _portSumAcc;
    /// <summary>Sum of the pressures at every valve end this sample, pascals — the source before the
    /// pipes have their say. Diagnostic.</summary>
    public float PortSum { get; private set; }

    /// <summary>Radiated pressure at one metre from all tailpipes, pascals, this sample.</summary>
    public float Radiated { get; private set; }

    /// <summary>How much of <see cref="Radiated"/> came off the muffler case rather than out of the
    /// pipe. Diagnostic.</summary>
    public float ShellRadiated { get; private set; }

    /// <summary>The rest of it, out of the pipes. Report the can against this: against
    /// <see cref="Radiated"/>, which includes the can, a six-fold change in it read as three decibels.</summary>
    public float PipeRadiated { get; private set; }

    /// <summary>
    /// Retunes every pipe for the gas now in the system. Called a few hundred times a second, not
    /// per sample: it has square roots in it.
    /// </summary>
    /// <param name="portKelvin">Gas temperature at the port.</param>
    /// <param name="massFlowKgPerS">Mean exhaust mass flow of the whole engine.</param>
    public void UpdateGas(float portKelvin, float massFlowKgPerS)
    {
        float ambient = 293f;
        float tailK = ambient + (portKelvin - ambient) * _x.TailCooling;
        _tailK = tailK;
        int branches = _branch.Length;
        float flowPerBranch = massFlowKgPerS / branches;

        // Temperature falls along the run; each pipe gets the temperature at its position.
        float primK = portKelvin;
        float colK = MathHelper.Lerp(portKelvin, tailK, 0.25f);
        for (int c = 0; c < _n; c++)
        {
            float rho = Gas.Density(Gas.Atmosphere, primK);
            float cs = Gas.SoundSpeed(primK, Gas.GammaExhaust);
            // The mean Mach number, which is what the loss and delay corrections want.
            float mach = massFlowKgPerS / _n / (rho * cs * _primary[c].Area);
            _primary[c].SetGas(primK, Gas.GammaExhaust, mach);
        }
        for (int i = 0; i < _collector.Length; i++)
        {
            float rho = Gas.Density(Gas.Atmosphere, colK);
            float cs = Gas.SoundSpeed(colK, Gas.GammaExhaust);
            float share = massFlowKgPerS * _groups[i].Length / _n;
            _collector[i].SetGas(colK, Gas.GammaExhaust, share / (rho * cs * _collector[i].Area));
        }
        _crossTube?.SetGas(colK, Gas.GammaExhaust, 0f);

        float fullLoadFlow = FullLoadMassFlow();
        _flowLossFraction = Math.Clamp(_x.FlowLoss * massFlowKgPerS / MathF.Max(1e-3f, fullLoadFlow), 0f, 1.5f);

        foreach (var br in _branch)
        {
            int count = br.Chain.Count;
            for (int i = 0; i < count; i++)
            {
                float t = MathHelper.Lerp(colK, tailK, (i + 0.5f) / count);
                var p = br.Chain[i];
                float rho = Gas.Density(Gas.Atmosphere, t);
                float cs = Gas.SoundSpeed(t, Gas.GammaExhaust);
                float mach = flowPerBranch / (rho * cs * p.Area);
                p.SetGas(t, Gas.GammaExhaust, mach);
                if (br.Resonators.TryGetValue(i, out var res))
                {
                    res.Neck.SetGas(t, Gas.GammaExhaust, 0f);
                    res.Cavity.SetGas(t, Gas.GammaExhaust, 0f);
                }
            }
            var tail = br.Chain[^1];
            float rhoT = Gas.Density(Gas.Atmosphere, tailK);
            float cT = Gas.SoundSpeed(tailK, Gas.GammaExhaust);
            br.MeanVelocity = flowPerBranch / (rhoT * tail.Area);
            br.End.Configure(tail.Radius, cT, rhoT, tail.Area, br.MeanVelocity / cT);
        }
    }

    /// <summary>Exhaust mass flow at redline and full throttle, for scaling the flow losses.</summary>
    private float FullLoadMassFlow()
    {
        // Displacement per second at redline times the density of intake air, times 0.9 VE.
        float cyclesPerSec = _e.RedlineRpm / 60f / (_e.Strokes == 2 ? 1f : 2f);
        return _e.DisplacementLitres * 1e-3f * cyclesPerSec * 1.18f * 0.9f;
    }

    /// <summary>One sample through everything downstream of the valves. Call after every cylinder
    /// has done its ArrivedAtValve / PushFromValve for this sample.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Step()
    {
        PortSum = _portSumAcc;
        _portSumAcc = 0f;
        var a = _scratchA.AsSpan();
        var y = _scratchY.AsSpan();
        var b = _scratchB.AsSpan();
        float loss = _flowLossFraction;

        // ── Collectors: each group's primaries meet their collector pipe ─────────────────────
        for (int gi = 0; gi < _groups.Length; gi++)
        {
            var group = _groups[gi];
            int n = group.Length;
            float ySum = 0f;
            for (int k = 0; k < n; k++)
            {
                var p = _primary[group[k]];
                a[k] = p.ArriveFar();
                y[k] = p.Admittance;
                ySum += y[k];
            }
            a[n] = _collector[gi].ArriveNear();
            y[n] = _collector[gi].Admittance;
            Junction.Scatter(a[..(n + 1)], y[..(n + 1)], b[..(n + 1)], loss * ySum * 0.5f);
            for (int k = 0; k < n; k++) _primary[group[k]].PushBackward(b[k]);
            _collector[gi].PushForward(b[n]);
        }

        // ── The merge: collectors into branches ──────────────────────────────────────────────
        switch (_crossover)
        {
            case CrossoverKind.None:
                for (int gi = 0; gi < _groups.Length; gi++)
                {
                    var col = _collector[gi];
                    var mid = _branch[gi].Chain[0];
                    var (back, on) = Junction.Two(col.ArriveFar(), mid.ArriveNear(), col.Admittance, mid.Admittance, loss * col.Admittance * 0.3f);
                    if (_branch[gi].Turbine is { } t)
                    {
                        var (through, reflected) = t.Split(on);
                        col.PushBackward(back + reflected);
                        mid.PushForward(through);
                    }
                    else
                    {
                        col.PushBackward(back);
                        mid.PushForward(on);
                    }
                }
                break;

            case CrossoverKind.Merged:
            {
                int g = _collector.Length;
                var mid = _branch[0].Chain[0];
                float ySum = 0f;
                for (int gi = 0; gi < g; gi++) { a[gi] = _collector[gi].ArriveFar(); y[gi] = _collector[gi].Admittance; ySum += y[gi]; }
                a[g] = mid.ArriveNear(); y[g] = mid.Admittance;
                Junction.Scatter(a[..(g + 1)], y[..(g + 1)], b[..(g + 1)], loss * ySum * 0.3f);
                if (_branch[0].Turbine is { } tm)
                {
                    // One turbine fed by every collector: what it sends back is shared among them.
                    var (through, reflected) = tm.Split(b[g]);
                    float share = reflected / MathF.Max(1, g);
                    for (int gi = 0; gi < g; gi++) _collector[gi].PushBackward(b[gi] + share);
                    mid.PushForward(through);
                }
                else
                {
                    for (int gi = 0; gi < g; gi++) _collector[gi].PushBackward(b[gi]);
                    mid.PushForward(b[g]);
                }
                break;
            }

            case CrossoverKind.XPipe:
            {
                // Four pipes meet at one point: both collectors and both mid pipes.
                var mid0 = _branch[0].Chain[0];
                var mid1 = _branch[1].Chain[0];
                a[0] = _collector[0].ArriveFar(); y[0] = _collector[0].Admittance;
                a[1] = _collector[1].ArriveFar(); y[1] = _collector[1].Admittance;
                a[2] = mid0.ArriveNear(); y[2] = mid0.Admittance;
                a[3] = mid1.ArriveNear(); y[3] = mid1.Admittance;
                Junction.Scatter(a[..4], y[..4], b[..4], loss * (y[0] + y[1]) * 0.3f);
                float r0 = 0f, r1 = 0f, f0 = b[2], f1 = b[3];
                if (_branch[0].Turbine is { } tx0) { var s0 = tx0.Split(b[2]); f0 = s0.Through; r0 = s0.Back; }
                if (_branch[1].Turbine is { } tx1) { var s1 = tx1.Split(b[3]); f1 = s1.Through; r1 = s1.Back; }
                _collector[0].PushBackward(b[0] + r0);
                _collector[1].PushBackward(b[1] + r1);
                mid0.PushForward(f0);
                mid1.PushForward(f1);
                break;
            }

            case CrossoverKind.HPipe:
            {
                // A third branch on each side, joined by the balance tube.
                var tube = _crossTube!;
                float tubeToA = tube.ArriveNear();
                float tubeToB = tube.ArriveFar();
                for (int gi = 0; gi < 2; gi++)
                {
                    var col = _collector[gi];
                    var mid = _branch[gi].Chain[0];
                    a[0] = col.ArriveFar(); y[0] = col.Admittance;
                    a[1] = mid.ArriveNear(); y[1] = mid.Admittance;
                    a[2] = gi == 0 ? tubeToA : tubeToB; y[2] = tube.Admittance;
                    Junction.Scatter(a[..3], y[..3], b[..3], loss * y[0] * 0.3f);
                    float fwd = b[1], rev = 0f;
                    if (_branch[gi].Turbine is { } th) { var sp = th.Split(b[1]); fwd = sp.Through; rev = sp.Back; }
                    col.PushBackward(b[0] + rev);
                    mid.PushForward(fwd);
                    if (gi == 0) tube.PushForward(b[2]); else tube.PushBackward(b[2]);
                }
                break;
            }
        }

        // ── Each branch: the chain of pipes to the open end ──────────────────────────────────
        float radiated = 0f, shell = 0f, pipe = 0f;
        foreach (var br in _branch)
        {
            var chain = br.Chain;
            float chamberPressure = 0f;
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                var up = chain[i];
                var down = chain[i + 1];
                if (br.Resonators.TryGetValue(i, out var res))
                {
                    // Three-port: pipe, next pipe, and the resonator's neck. The neck's far end
                    // meets the cavity, whose far end is closed.
                    a[0] = up.ArriveFar(); y[0] = up.Admittance;
                    a[1] = down.ArriveNear(); y[1] = down.Admittance;
                    a[2] = res.Neck.ArriveNear(); y[2] = res.Neck.Admittance;
                    Junction.Scatter(a[..3], y[..3], b[..3], loss * y[0] * 0.2f);
                    up.PushBackward(b[0]);
                    down.PushForward(b[1]);
                    res.Neck.PushForward(b[2]);
                    var (nb, cf) = Junction.Two(res.Neck.ArriveFar(), res.Cavity.ArriveNear(), res.Neck.Admittance, res.Cavity.Admittance, 0f);
                    res.Neck.PushBackward(nb);
                    res.Cavity.PushForward(cf);
                    // Closed end of the cavity: rigid.
                    res.Cavity.PushBackward(res.Cavity.ArriveFar());
                }
                else
                {
                    // Read once: ArriveFar advances the line, and a second read runs the pipe at half
                    // its delay.
                    float atEnd = up.ArriveFar();
                    var (back, on) = Junction.Two(atEnd, down.ArriveNear(), up.Admittance, down.Admittance,
                                                  loss * MathF.Min(up.Admittance, down.Admittance) * 0.25f);
                    up.PushBackward(back);
                    down.PushForward(on);

                    // The junction's pressure (arriving plus reflected) at a chamber's end pushes on the can.
                    if (br.Shell != null && br.ChamberIndices.Contains(i)) chamberPressure += atEnd + back;
                }
            }

            // The open end.
            var tail = chain[^1];
            float arriving = tail.ArriveFar();
            var (reflected, u) = br.End.Process(arriving);
            tail.PushBackward(reflected);
            br.ExitVelocity = u / tail.Area;
            float direct = br.End.Radiate(u, _airDensity);
            float jet = br.Jet.Process(br.ExitVelocity + br.MeanVelocity, br.MeanVelocity, _x.JetNoiseLevel, _tailK);
            br.Radiated = direct + jet;

            // The can radiates straight into the air. ShellLevel is the whole conversion from chamber
            // pressure (thousands of pascals) to pressure at a metre (tens) as one measured ratio; see
            // MufflerSpec.ShellLevel.
            if (br.Shell != null)
            {
                br.ShellRadiated = br.Shell.Process(chamberPressure) * _x.Muffler.ShellLevel;
                br.Radiated += br.ShellRadiated;
            }

            // Each pipe from its own place once the listener is known; otherwise the sum at one point.
            float heard = br.Radiated;
            if (_listenerKnown)
            {
                br.PathSamples += Math.Clamp(br.PathTarget - br.PathSamples, -MaxPathSlew, MaxPathSlew);
                br.Spread += Math.Clamp(br.SpreadTarget - br.Spread, -_spreadSlew, _spreadSlew);
                var ring = br.Path;
                int mask = ring.Length - 1;
                ring[br.PathAt] = heard;
                float read = br.PathAt - br.PathSamples;
                if (read < 0f) read += ring.Length;
                int i0 = (int)read;
                float f = read - i0;
                float a0 = ring[i0 & mask], a1 = ring[(i0 + 1) & mask];
                heard = (a0 + (a1 - a0) * f) * br.Spread;
                br.PathAt = (br.PathAt + 1) & mask;
            }
            int solo = EngineSynth.DebugSoloTailpipe;
            if (solo >= 0) heard = Array.IndexOf(_branch, br) == solo ? heard * _branch.Length : 0f;

            radiated += heard;
            shell += br.ShellRadiated;
            pipe += direct + jet;
        }
        Radiated = radiated;
        ShellRadiated = shell;
        PipeRadiated = pipe;
    }

    /// <summary>Everything the console might want to print about the geometry.</summary>
    public IEnumerable<string> Describe()
    {
        float c = Gas.SoundSpeed(_x.GasCelsiusIdle + 273.15f, Gas.GammaExhaust);
        float cFull = Gas.SoundSpeed(_x.GasCelsiusFull + 273.15f, Gas.GammaExhaust);
        yield return $"gas {c:F0} m/s idling, {cFull:F0} m/s at full load";
        var lens = new List<string>();
        for (int i = 0; i < _n; i++) lens.Add($"{_primary[i].Length:F2}");
        yield return $"primaries {string.Join(" ", lens)} m ({_x.PrimaryDiameterMm:F0} mm) -> quarter-wave "
                   + $"{c / (4 * _primary[0].Length):F0}-{c / (4 * _primary[^1].Length):F0} Hz idling";
        for (int gi = 0; gi < _groups.Length; gi++)
            yield return $"collector {gi}: cylinders {string.Join(",", Array.ConvertAll(_groups[gi], k => (k + 1).ToString()))}"
                       + $" firing every {string.Join("/", Array.ConvertAll(_e.GroupIntervals(gi), v => $"{v:F0}"))} deg";
        yield return $"crossover {_crossover}, {_branch.Length} tailpipe(s), muffler {_x.Muffler.Kind}";
        foreach (var br in _branch)
        {
            float total = 0f;
            foreach (var p in br.Chain) total += p.Length;
            yield return $"  branch: {br.Chain.Count} sections, {total:F2} m from merge to tip";
        }
    }
}
