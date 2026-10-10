using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Tests;

/// <summary>
/// The engine render pool's speedups of 2026-10-09. The first pass must not move a sample: an engine
/// voice is chaotic enough that a one-ulp change in one power grows to full scale within seconds
/// (replacing the orifice law's two powf calls by one log and two exps moved every render by 0 to
/// +7 dBFS), so each of its changes is held against the code it replaced, bit for bit. The second pass
/// (Cody's approval, the same day) changes the sound by design and is held to what it promised: the
/// valve solver to its tolerance.
/// </summary>
public class EngineRenderCostTests
{
    // ── The valve solver ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The solver that brackets from its first guess (2026-10-09, the sound-changing pass) lands within
    /// its stopping rule of the true root, found here by bisecting the old solver's residual to the bit:
    /// a pascal of residual (the residual rises at least as 1/Z, so at most a pascal of b) or a bracket
    /// two and a half pascals wide. With a slope carried from a neighbouring solve, as a valve carries it
    /// from the last sample, and without. The old solver, ten steps of regula falsi from a bracket as wide
    /// as the flow cap, missed by tens of kilopascals where a nearly empty cylinder meets a port at its
    /// pressure floor; the cases where it did are counted, and the new one must hold them too.
    /// </summary>
    [Fact]
    public void TheValveSolverFindsTheRoot()
    {
        var rng = new Random(20261009);
        float[] gammas = { Gas.GammaExhaust, Gas.GammaAir, 1.35f };
        float worst = 0f;
        int above = 0, below = 0, oldMissed = 0;
        for (int i = 0; i < 200_000; i++)
        {
            float gamma = gammas[i % gammas.Length];
            float Z = (float)(2e5 + rng.NextDouble() * 4e6);
            float area = i % 97 == 0 ? 0f : (float)(1e-6 + rng.NextDouble() * 1.5e-3);
            float pMean = (float)(2e4 + rng.NextDouble() * 2e5);
            // Cylinders from a near vacuum to a firing peak, so the flow runs both ways and to both caps.
            float pCyl = (float)Math.Exp(Math.Log(5e3) + rng.NextDouble() * (Math.Log(8e6) - Math.Log(5e3)));
            float tCyl = (float)(280 + rng.NextDouble() * 2500);
            float pipeK = (float)(290 + rng.NextDouble() * 900);
            float uCap = (float)(1e-3 + rng.NextDouble() * 0.2);
            float a = (float)((rng.NextDouble() * 2 - 1) * Z * uCap * 1.5);
            float bStart = (float)((rng.NextDouble() * 2 - 1) * Z * uCap * 1.2);
            float mass = (float)(1e-6 + rng.NextDouble() * 5e-3);
            float dt = i % 2 == 0 ? 1f / 48000f : 1f / 44100f;

            float lo = a - Z * uCap * 1.05f, hi = a + Z * uCap * 1.05f;
            float root = Bisect(bb => Residual(bb, a, Z, area, pCyl, tCyl, pipeK, gamma, mass, dt, uCap, pMean, onePow: true), lo, hi);
            float oldRoot = Bisect(bb => Residual(bb, a, Z, area, pCyl, tCyl, pipeK, gamma, mass, dt, uCap, pMean, onePow: false), lo, hi);
            float bOld = OldSolveValve(a, bStart, Z, area, pCyl, tCyl, pipeK, gamma, mass, dt, 0f, uCap, pMean, out _);
            if (MathF.Abs(bOld - oldRoot) > 0.5f + 1e-6f * MathF.Abs(oldRoot)) oldMissed++;
            // A neighbouring solve first, a sample's worth of wave away, to leave a slope behind.
            float slope = 0f;
            EngineSynth.SolveValve(a * 0.98f, bStart, Z, area, pCyl, tCyl, pipeK, gamma, mass, dt, 0f, uCap, pMean, out _, ref slope);
            foreach (bool carried in new[] { false, true })
            {
                float s = carried ? slope : 0f;
                float b = EngineSynth.SolveValve(a, bStart, Z, area, pCyl, tCyl, pipeK, gamma, mass, dt, 0f, uCap, pMean, out float m, ref s);
                Assert.True(float.IsFinite(b) && float.IsFinite(m), $"case {i}: b {b}, mdot {m}");
                float off = MathF.Abs(b - root);
                worst = MathF.Max(worst, off);
                // Float steps at the root's size bound how close anything can come to it.
                Assert.True(off <= 2.5f * EngineSynth.ValveTolerancePa + 1e-6f * MathF.Abs(root),
                            $"case {i} (slope {(carried ? "carried" : "none")}): b {b:R} against the root {root:R}, {off} Pa off; the old solver {bOld:R}");
            }
            if (bStart > root) above++; else if (bStart < root) below++;
        }
        Assert.True(above > 10_000 && below > 10_000, $"the first guess was above the answer {above} times and below it {below}");
        Assert.True(oldMissed > 0, "the old solver never missed: these cases no longer reach the hard corner");
        Assert.True(worst > 0f, "every answer exact: the comparison is not looking at the new solver");
    }

    /// <summary>The root of an increasing function between two ends, to the last float; an end when the
    /// root is past it.</summary>
    private static float Bisect(Func<float, float> g, float lo, float hi)
    {
        if (g(lo) >= 0f) return lo;
        if (g(hi) <= 0f) return hi;
        for (int i = 0; i < 200; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (mid <= lo || mid >= hi) break;
            if (g(mid) > 0f) hi = mid; else lo = mid;
        }
        return MathF.Abs(g(lo)) < MathF.Abs(g(hi)) ? lo : hi;
    }

    /// <summary>The solver as it was before 2026-10-09, kept verbatim as the reference: its tight rule
    /// (0.2 Pa) puts its answer within half a pascal of the root.</summary>
    private static float OldSolveValve(float a, float bStart, float Z, float area, float pCyl, float tCyl,
                                       float pipeK, float gamma, float cylMass, float dt, float uMean, float uCap, float pMean, out float mdot)
    {
        float equalise = 0.5f * cylMass / dt;
        float uMax = uCap * 1.05f;
        float lo = a - Z * uMax, hi = a + Z * uMax;
        float gLo = Residual(lo, out _), gHi = Residual(hi, out _);
        if (gLo >= 0f) { Residual(lo, out mdot); return lo; }
        if (gHi <= 0f) { Residual(hi, out mdot); return hi; }
        float b = Math.Clamp(bStart, lo, hi);
        float gB = Residual(b, out mdot);
        if (gB > 0f) { hi = b; gHi = gB; } else { lo = b; gLo = gB; }
        int side = 0;
        for (int it = 0; it < 10; it++)
        {
            b = (gLo * hi - gHi * lo) / (gLo - gHi);
            if (!float.IsFinite(b)) b = 0.5f * (lo + hi);
            gB = Residual(b, out mdot);
            if (MathF.Abs(gB) * Z < 0.2f || hi - lo < 0.5f) break;
            if (gB > 0f) { hi = b; gHi = gB; if (side == 1) gLo *= 0.5f; side = 1; }
            else { lo = b; gLo = gB; if (side == -1) gHi *= 0.5f; side = -1; }
        }
        return b;

        float Residual(float bb, out float m)
        {
            float pPort = MathF.Max(0.05f * MathF.Max(pMean, 100f), pMean + a + bb);
            float rhoPort = Gas.Density(pPort, pipeK);
            if (pCyl >= pPort)
            {
                m = OldOrificeFlow(pCyl, tCyl, pPort, area, gamma);
                m = MathF.Min(m, equalise * (pCyl - pPort) / pCyl);
            }
            else
            {
                m = -OldOrificeFlow(pPort, pipeK, pCyl, area, gamma);
                m = MathF.Max(m, -equalise * (pPort - pCyl) / pCyl);
            }
            float u = Math.Clamp(m / rhoPort, -uCap, uCap);
            m = u * rhoPort;
            return (bb - a) / Z - (u - uMean);
        }
    }

    /// <summary>The residual the solvers zero: the old orifice law, or with <paramref name="onePow"/> the
    /// form the solver has used since 2026-10-09 (one power), whose root differs by rounding where the
    /// law is steep.</summary>
    private static float Residual(float bb, float a, float Z, float area, float pCyl, float tCyl, float pipeK, float gamma, float cylMass, float dt, float uCap, float pMean, bool onePow)
    {
        float equalise = 0.5f * cylMass / dt;
        float pPort = MathF.Max(0.05f * MathF.Max(pMean, 100f), pMean + a + bb);
        float rhoPort = Gas.Density(pPort, pipeK);
        float m;
        if (pCyl >= pPort)
        {
            m = OldOrificeFlow(pCyl, tCyl, pPort, area, gamma, onePow);
            m = MathF.Min(m, equalise * (pCyl - pPort) / pCyl);
        }
        else
        {
            m = -OldOrificeFlow(pPort, pipeK, pCyl, area, gamma, onePow);
            m = MathF.Max(m, -equalise * (pPort - pCyl) / pCyl);
        }
        float u = Math.Clamp(m / rhoPort, -uCap, uCap);
        return (bb - a) / Z - u;
    }

    private static float OldOrificeFlow(float pUp, float tUp, float pDown, float area, float gamma, bool onePow = false)
    {
        if (area <= 0f || pUp <= pDown) return 0f;
        float pr = pDown / pUp;
        float crit = MathF.Pow(2f / (gamma + 1f), gamma / (gamma - 1f));
        float rt = MathF.Sqrt(Gas.R * tUp);
        if (pr <= crit)
        {
            float k = MathF.Sqrt(gamma) * MathF.Pow(2f / (gamma + 1f), (gamma + 1f) / (2f * (gamma - 1f)));
            return area * pUp * k / rt;
        }
        float q = onePow ? MathF.Pow(pr, 1f / gamma) : 0f;
        float t1 = onePow ? q * (q - pr) : MathF.Pow(pr, 2f / gamma) - MathF.Pow(pr, (gamma + 1f) / gamma);
        if (t1 <= 0f) return 0f;
        return area * pUp * MathF.Sqrt(2f * gamma / (gamma - 1f) * t1) / rt;
    }

    // ── The waveguide ───────────────────────────────────────────────────────────────────────

    /// <summary>A line that keeps its read slot in step with time, rather than dividing for it, reads
    /// out what the dividing line did, through shocks, compressions, delay changes and many wraps.</summary>
    [Fact]
    public void AWaveLineReadsWhatTheDividingLineDid()
    {
        var rng = new Random(7);
        foreach (int longest in new[] { 3, 17, 64, 301 })
        {
            var line = new WaveLine(longest);
            var reference = new OldWaveLine(longest);
            float kappa = 1.165f / (1.33f * 101325f) * 4f;
            line.SetSteepening(kappa); reference.SetSteepening(kappa);
            line.SetLoss(0.97f, 0.6f); reference.SetLoss(0.97f, 0.6f);
            for (int i = 0; i < 60_000; i++)
            {
                if (i % 997 == 0)
                {
                    float d = (float)(1 + rng.NextDouble() * longest);
                    line.SetDelay(d); reference.SetDelay(d);
                }
                float x = line.Read();
                float y = reference.Read();
                Assert.True(BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y),
                            $"line of {longest}, sample {i}: {x:R} against {y:R}");
                // Pulses big enough to shock, quiet stretches, and now and then something not finite.
                float v = i % 5000 == 4999 ? float.NaN
                        : (float)(Math.Sin(i * 0.07) * 60000 * (rng.NextDouble() < 0.3 ? 1 : 0.05) + (rng.NextDouble() - 0.5) * 2000);
                line.Write(v); reference.Write(v);
            }
        }
    }

    /// <summary>The waveguide's delay line as it was before 2026-10-09, verbatim but for comments.</summary>
    private sealed class OldWaveLine
    {
        private readonly float[] _buf;
        private readonly int _len;
        private long _time;
        private double _tauPrev;
        private float _vPrev;
        private float _d0;
        private float _kappa;
        private float _gain = 1f, _alpha = 1f, _lp;
        private const double MinDelay = 1.05;
        private long _accSlot = long.MinValue;
        private double _accSum;
        private int _accN;
        private int _frontN;
        private double _frontSumTau;
        private double _preTau;
        private float _preV;
        private const double CrossingSamples = 0.25;

        public OldWaveLine(int maxDelaySamples)
        {
            _len = Math.Max(8, maxDelaySamples + 8);
            _buf = new float[_len];
            _d0 = Math.Max((float)MinDelay, maxDelaySamples - 2);
            _tauPrev = _d0 - 1;
            _preTau = _tauPrev - 1;
        }

        public void SetDelay(float samples)
        {
            _d0 = Math.Clamp(samples, (float)MinDelay, _len - 4f);
            if (_time == 0) { _tauPrev = _d0 - 1; _preTau = _tauPrev - 1; }
        }

        public void SetSteepening(float perPascal) => _kappa = MathF.Max(0f, perPascal);

        public void SetLoss(float gain, float onePoleAlpha)
        {
            _gain = Math.Clamp(gain, 0f, 1f);
            _alpha = Math.Clamp(onePoleAlpha, 1e-4f, 1f);
        }

        public void Write(float v)
        {
            if (!float.IsFinite(v)) v = 0f;
            float speedUp = 1f + Math.Clamp(_kappa * v, -0.4f, 1.5f);
            double d = _d0 / speedUp;
            if (d < MinDelay) d = MinDelay;
            double tau = (_time - 1) + d;
            long kMax = _time + _len - 2;
            if (tau < _tauPrev + CrossingSamples)
            {
                if (_frontN == 0) { _frontSumTau = _tauPrev; _frontN = 1; }
                _frontSumTau += tau;
                _frontN++;
                double tauS = _frontSumTau / _frontN;
                double floor = Math.Max(_preTau + 1.0, _time + 0.001);
                if (tauS < floor) tauS = floor;
                if (tauS > _tauPrev) tauS = _tauPrev;
                long kS = (long)Math.Floor(tauS);
                float frac = (float)(tauS - kS);
                long kEnd = Math.Min((long)Math.Floor(_tauPrev), kMax);
                if (kS <= kMax)
                {
                    _buf[Slot(kS)] = _preV + (v - _preV) * (1f - frac);
                    for (long k = kS + 1; k <= kEnd; k++) _buf[Slot(k)] = v;
                }
                _accSlot = long.MinValue;
                _tauPrev = tauS;
                _vPrev = v;
                return;
            }
            if (_frontN > 0) _frontN = 0;
            _preTau = _tauPrev;
            _preV = _vPrev;
            double span = tau - _tauPrev;
            long k0 = (long)Math.Floor(_tauPrev) + 1;
            long k1 = (long)Math.Floor(tau);
            if (k1 > kMax) k1 = kMax;
            for (long k = k0; k <= k1; k++)
            {
                float f = (float)((k - _tauPrev) / span);
                Deposit(k, _vPrev + (v - _vPrev) * f);
            }
            if (k1 < k0)
            {
                long k = (long)Math.Floor(tau);
                if (k <= kMax && k >= _time) Deposit(k, v);
            }
            _tauPrev = tau;
            _vPrev = v;
        }

        private int Slot(long k) => (int)(((k % _len) + _len) % _len);

        private void Deposit(long k, float value)
        {
            int idx = Slot(k);
            if (k == _accSlot) { _accSum += value; _accN++; _buf[idx] = (float)(_accSum / _accN); }
            else { _accSlot = k; _accSum = value; _accN = 1; _buf[idx] = value; }
        }

        public float Read()
        {
            int i = (int)(_time % _len);
            float v = _buf[i];
            _buf[i] = 0f;
            _time++;
            _lp += _alpha * (v - _lp);
            return _gain * _lp;
        }
    }

    /// <summary>A pipe's impedance and admittance are kept, not worked out per read; they must follow the
    /// gas and the throttle exactly as the formulas would.</summary>
    [Fact]
    public void APipesAdmittanceFollowsItsGasAndItsThrottle()
    {
        var p = new Pipe(0.4f, 1.2e-3f, 48000f, 1f, 1f);
        void Check()
        {
            Assert.Equal(p.Density * p.SoundSpeed / p.Area, p.Impedance);
            Assert.Equal(p.Area * p.AreaScale / (p.Density * p.SoundSpeed), p.Admittance);
        }
        Check();
        p.SetGas(950f, Gas.GammaExhaust, 0.05f);
        Check();
        p.AreaScale = 0.07f;
        Check();
        p.SetGas(310f, Gas.GammaAir, 0f);
        Check();
    }

    // ── The voice ───────────────────────────────────────────────────────────────────────────

    /// <summary>The mixer's side of a voice takes no memory: a callback that allocates can wait on the
    /// collector.</summary>
    [Fact]
    public void ConsumingAVoiceAllocatesNothing()
    {
        var v = new EngineVoiceState(MachineRegistry.VehicleFor("i4_compact"), 48000, 1) { TargetSpeed = 12f, CompensateLevel = true };
        v.PlaceAtSpeed(12f);
        v.SetListener(new Vector3(3f, 1.5f, -8f));
        var buf = new float[1024];
        for (int i = 0; i < 20; i++) { v.Produce(); v.Consume(buf); }
        v.Produce();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; i++) v.Consume(buf);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
