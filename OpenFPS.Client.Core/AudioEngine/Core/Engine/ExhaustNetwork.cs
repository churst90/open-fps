using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Engine;

/// <summary>
/// The exhaust system as a network of pipes that meet each other, built from an
/// <see cref="ExhaustSpec"/>: one primary per cylinder into its collector, the collectors joined
/// (or not) by the crossover, then each branch runs mid-pipe, muffler and tailpipe to an open end.
///
/// A junction is not a mixer. When a wave arrives at one, part reflects back up the pipe it came from
/// and the rest divides among the others — including back up the other primaries toward closed
/// valves, which return it again. That cross-talk is why cylinder 3 is audible in cylinder 5's pipe,
/// why four pipes of four lengths make a forest of resonances rather than one, and why the same
/// engine on a cast manifold and on long-tubes is two different sounds.
///
/// The muffler is not a filter either. A chambered muffler is literally what it says — the pipe
/// opens into a can several times its area and closes again, and the transmission loss of that is
/// the textbook expansion chamber, 10 log(1 + (m - 1/m)^2 sin^2(kL) / 4), which this reproduces from
/// the two area steps and the delay between them. An absorptive muffler is a straight tube with
/// packing round it, so it is a pipe with heavy frequency-dependent loss. A resonator is a neck into
/// a closed volume. They are built from the same pipes and junctions as everything else.
/// </summary>
internal sealed class ExhaustNetwork
{
    private readonly EngineProfile _e;
    private readonly ExhaustSpec _x;
    private readonly float _rate;
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
    private float _meanMassFlow;
    private float _tailK = 293f;

    /// <summary>
    /// The turbine, sitting where it really sits: between the manifold and the downpipe.
    ///
    /// This is the other half of the turbo, and the model had neither half. A turbine is not a pipe
    /// with a restriction in it — it is a bladed rotor across the whole gas path, and to a pressure
    /// wave arriving from the cylinders it is three things at once:
    ///
    /// IT TAKES ENERGY OUT. That is its job. The pulse energy the exhaust would otherwise radiate is
    /// what spins the compressor, so a turbo engine is quieter at the tailpipe than the same engine
    /// with the turbo removed — measurably so, and it is why a "turbo" exhaust note is a RUSH where a
    /// naturally aspirated one is a set of beats.
    ///
    /// IT REFLECTS. What it does not pass and does not absorb goes back up the manifold, which is why
    /// a turbo manifold has its own strong resonances and why turbo engines are so sensitive to
    /// manifold volume. Blocking without reflecting would leave the manifold looking like an open
    /// end, which it is not.
    ///
    /// AND IT IS A LOW-PASS. The blade passages are short compared with a long wavelength, so the low
    /// end leaks through while everything above a few hundred hertz is scattered into the rotor. That
    /// asymmetry is most of the character: the deep part of the pulse survives and the crack does not.
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

        /// <summary>Acoustic pressure summed over the chambers this sample — the drive. Diagnostic.</summary>
        public float ChamberPressure;

        /// <summary>Where this pipe leaves the car, machine frame, relative to the exhaust part.</summary>
        public Vector3 Exit;

        /// <summary>
        /// The extra distance this pipe's sound travels to the listener, against the nearest pipe,
        /// in samples — and the ring that delays it by that much. See <see cref="SetListener"/>.
        /// </summary>
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
                // Position along the bank decides the length: the pipe from the rear cylinder
                // has further to go to a front collector, and a spread of 0 makes them equal.
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
                // A radial turbine passes the bottom of the band and scatters the rest: about a
                // third of a low wave gets to the downpipe, the corner is a few hundred hertz, and
                // half of what is stopped comes back up the manifold rather than becoming work.
                // TURBOCHARGED ONLY. A blower is belt-driven and has nothing in the exhaust at all —
                // that is the whole difference between the two kinds of forced induction, and giving
                // a supercharged V8 a turbine took 17 dB off it for no reason.
                Turbine = e.Induction == Induction.Turbocharged
                        ? new Turbine(rate, 260f, 0.34f, 0.5f) : null,
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

    /// <summary>True when the profile places its tailpipes apart; false means one point, as before.</summary>
    private readonly bool _hasExits;
    /// <summary>True once anyone has said where the listener is.</summary>
    private bool _listenerKnown;

    /// <summary>
    /// The most a pipe's path delay may move per sample. A delay that changes is a pitch shift of
    /// that pipe against the other, which is real — the two ends of a car passing you have slightly
    /// different Dopplers — and on a real pass-by it stays under half a per cent. This is the cap
    /// that keeps a game-thread jump (a car teleported, a listener respawned) from arriving as a chirp.
    /// </summary>
    private const float MaxPathSlew = 0.005f;

    /// <summary>
    /// Tells the network where the listener stands, in the machine's frame (x across, y up, z
    /// forward, origin at the exhaust part), so each tailpipe can radiate from its own place.
    ///
    /// The exhaust used to be one source: every branch's radiated pressure added at a single point.
    /// That is exactly right for a listener equidistant from every pipe — dead behind the car — and
    /// systematically wrong everywhere else, because the components that DIFFER between banks are the
    /// ones a coherent sum destroys. On an even-firing V10 the banks are anti-phase at the bank firing
    /// rate, so the sum cancelled the engine's fundamental and the ear was handed the next harmonic
    /// alone: a siren. Measured, order 2.5 read 12-19 dB under order 5 on the sum and level with it
    /// on one pipe.
    ///
    /// Each branch now gets the path difference its exit implies — the extra distance to the listener
    /// against the nearest pipe, as a delay — and the ratio of spherical spreading, which only matters
    /// up close. In the far field on the centre line this reduces to the old sum exactly; off it the
    /// two pipes interfere as two sources do, and on a pass-by the balance sweeps with the bearing.
    /// Called a few hundred times a second at most; the delays slew, never step.
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
            // Inside a metre the pipes are separate sources at separate distances; beyond it the
            // ratio is within a few per cent of one and not worth a discontinuity at the boundary.
            br.SpreadTarget = r > 1f ? Math.Clamp(r / MathF.Max(0.1f, path), 0.25f, 4f) : 1f;
        }
        _listenerKnown = true;
    }

    private static float Circle(float diameterMm)
    {
        float r = diameterMm * 0.5e-3f;
        return MathF.PI * r * r;
    }

    /// <summary>Builds the muffler's internals onto the branch chain.</summary>
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
                    // The chamber: an expansion to the can's area over its length. Baffles and
                    // deflectors inside take energy off every internal reflection, which broadens
                    // the chamber's notches — a clean expansion chamber rings, a Flowmaster does not.
                    var chamber = new Pipe(m.ChamberLengthsMetres[i], canArea, _rate, wall, 0f);
                    float baffle = Math.Clamp(m.BaffleLoss, 0f, 0.95f);
                    chamber.SetExtraLoss(1f - 0.5f * baffle, OnePole.AlphaFor(MathHelper.Lerp(12000f, 1500f, baffle), _rate));
                    br.Chain.Add(chamber);
                    // Remember where it is: this is a pipe with the can's steel wrapped round it, so
                    // its pressure is what shakes the case.
                    br.ChamberIndices.Add(br.Chain.Count - 1);
                    // Between chambers, a short passage through the partition at pipe area.
                    if (i + 1 < m.ChamberLengthsMetres.Length)
                        br.Chain.Add(new Pipe(0.04f, pipeArea, _rate, wall, 0f));
                }
                if (m.Kind == MufflerKind.Baffled)
                {
                    AddAbsorptive(br, m, pipeArea, wall);
                    if (m.ResonatorHz > 0f) AddResonator(br, m, pipeArea, wall);
                }
                return;
            }

            case MufflerKind.Absorptive:
                AddAbsorptive(br, m, pipeArea, wall);
                if (m.ResonatorHz > 0f) AddResonator(br, m, pipeArea, wall);
                return;
        }
    }

    /// <summary>A perforated tube in packing: the pipe continues at its own area, and loses its top
    /// progressively along the length. Absorption 0.6 takes the corner down to about 700 Hz.</summary>
    private void AddAbsorptive(Branch br, MufflerSpec m, float pipeArea, float wall)
    {
        var p = new Pipe(m.AbsorptiveLengthMetres, pipeArea, _rate, wall, 0.3f);
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
        // Cavity as a wide short pipe closed at the far end; keep it well under a quarter wave at
        // the frequencies that matter so it behaves as a compliance.
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
    public float PrimaryDensity(int cyl) => _primary[cyl].Density;
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

    /// <summary>How much of <see cref="Radiated"/> came off the muffler CASE rather than out of the
    /// pipe. Diagnostic: the only way to answer "how loud is the can" without guessing at it.</summary>
    public float ShellRadiated { get; private set; }

    /// <summary>...and the rest of it, out of the pipes. Kept separately because the obvious way to
    /// report the can's level — against <see cref="Radiated"/> — compares it against ITSELF plus the
    /// pipe, so the number saturates at 0 dB however loud the can gets and a six-fold change in it
    /// reads as three decibels.</summary>
    public float PipeRadiated { get; private set; }
    /// <summary>Exit velocity of branch 0 this sample, m/s, for anyone who wants to look.</summary>
    public float ExitVelocity => _branch[0].ExitVelocity;

    /// <summary>
    /// Retunes every pipe for the gas now in the system. Called a few hundred times a second, not
    /// per sample: it has square roots in it.
    /// </summary>
    /// <param name="portKelvin">Gas temperature at the port.</param>
    /// <param name="massFlowKgPerS">Mean exhaust mass flow of the whole engine.</param>
    public void UpdateGas(float portKelvin, float massFlowKgPerS)
    {
        _meanMassFlow = massFlowKgPerS;
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
            // Each primary carries its cylinder's share, but only while that valve is open; the
            // Mach number here is the mean, which is what the loss and delay corrections want.
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
                    var (back, on) = Junction.Two(up.ArriveFar(), down.ArriveNear(), up.Admittance, down.Admittance,
                                                  loss * MathF.Min(up.Admittance, down.Admittance) * 0.25f);
                    up.PushBackward(back);
                    down.PushForward(on);

                    // Pressure at this junction is the sum of the two waves meeting there — the
                    // arriving one and the one reflected back into it. Where that junction is the end
                    // of a chamber, that is the pressure pushing on the can.
                    if (br.Shell != null && br.ChamberIndices.Contains(i)) chamberPressure += up.ArriveFar() + back;
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

            // ...and the can, which radiates straight into the air rather than out of the pipe.
            //
            // CALIBRATION. The drive is the acoustic pressure inside the chambers and the output is
            // pressure at one metre, and those differ by orders of magnitude: the internal wave is
            // thousands of pascals where a metre away is tens. ShellLevel carries that whole
            // conversion — transmission through the steel, the case's radiating area, and the
            // spreading out to a metre — as one measured ratio rather than three guessed ones. It is
            // set by rendering with the shell on and reading the level it lands at against the pipe;
            // see the note on MufflerSpec.ShellLevel.
            br.ChamberPressure = chamberPressure;
            if (br.Shell != null)
            {
                br.ShellRadiated = br.Shell.Process(chamberPressure) * _x.Muffler.ShellLevel;
                br.Radiated += br.ShellRadiated;
            }

            // Each pipe from its own place, if the profile says where that is and anyone has said
            // where the listener is. Otherwise the sum at one point, bit for bit as it always was.
            float heard = br.Radiated;
            if (_listenerKnown)
            {
                br.PathSamples += Math.Clamp(br.PathTarget - br.PathSamples, -MaxPathSlew, MaxPathSlew);
                br.Spread += Math.Clamp(br.SpreadTarget - br.Spread, -0.0005f, 0.0005f);
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

    /// <summary>Mean exhaust flow the network was last told about, kg/s.</summary>
    public float MeanMassFlow => _meanMassFlow;

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
