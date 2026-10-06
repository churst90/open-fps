using System;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// Water falling into water, as the events it is made of.
///
/// WHAT MAKES THE SOUND. Almost none of it is the water itself. A drop hitting a pool makes a short
/// click as it strikes — the impact — and sometimes, as the crater it opened closes, it traps a
/// little air. That bubble is a spring of air in a mass of water, and it rings at its Minnaert
/// frequency, 3.26 / R hertz for a radius R in metres, for a few dozen cycles, its note climbing as
/// it rises toward the surface. A millimetre bubble is a 3.3 kHz "plink"; a 5 mm one is a 650 Hz
/// "bloop". A coherent body of water — a jet's collapsing column, a sheet — drives a whole line of
/// air under and makes bubbles of every size at once, more small than large (Deane and Stokes 2002:
/// the count goes as R^-3/2 below about a millimetre and R^-10/3 above). The sum of those is a
/// fountain.
///
/// WHAT DECIDES HOW MANY AND HOW BIG. The flow and the fall. A litre a second as 1.4 mm drops is
/// tens of thousands of drops a second; they arrive at the speed the fall gives them, never more than
/// their terminal velocity (Atlas, Srivastava and Sekhon 1973: 9.65 − 10.3 e^−0.6D m/s, D in mm);
/// a drop of 0.8-1.1 mm diameter arriving near its terminal speed traps a bubble every time and the
/// same size of bubble every time (Pumphrey and Elmore 1990, the "regular" entrainment that makes
/// rain on a lake ring at 14 kHz). Medwin et al. (1992) sorted drops by what they do: under 0.8 mm
/// diameter almost nothing; 0.8-1.1 mm the regular bubble near 15 kHz; 1.1-2.2 mm the impact and no
/// bubble; over 2.2 mm the impact and a loud "type II" bubble at 2-10 kHz, lower for a bigger drop.
/// A fountain's drops, a millimetre or two in radius falling a metre or two, are mostly the last.
///
/// HOW LOUD EACH BUBBLE IS depends on how deep under the surface it was made, and that is close to
/// random and very skewed: most are made shallow and are faint, a few deep and loud (van den Doel
/// 2005 draws the factor as u^β with u uniform). That skew is the difference between water and a
/// hiss: a few plinks stand out of a bed of faint ones.
///
/// WHAT IS FITTED. Two constants and the plunge's air share, named here and nowhere else, fitted
/// together on 2026-10-04 to a recording of a dozen jets falling back into their pool (its octaves
/// 500 Hz-16 kHz within 4 dB) and then brought to 71 dB(A) at the kerb, from Watts et al. (2009):
/// 1.1 L/s falling 30 cm into water measured 67 dB(A) at a metre, and this fountain moves 2.6 L/s
/// over a metre or more. The constants are: how loud a bubble is for its size
/// in air at a metre, and how loud an impact is for its size and speed. Their LAWS are physical — a
/// bubble's first peak goes as its radius (ρ ω² R² ξ with ωR fixed and the wall's travel ξ a fixed
/// fraction of R), an impact's as r v² (its energy as m v³, Franz 1959, delivered over r / v) — and
/// the two numbers are set against measured fountains, not chosen. Re-fit them; never nudge them.
/// </summary>
public sealed class FallingWaterSynth
{
    // ── The fitted constants ─────────────────────────────────────────────────────────────────────

    /// <summary>A 1 mm bubble's first peak at its deepest, Pa at a metre in air. Goes as R (a wall
    /// travel that is a fixed fraction of the radius, ρ ω² R² ξ with ωR fixed), times the depth
    /// factor. van den Doel's predicted R^1.5 put so much of the energy in the largest bubbles that
    /// they rang as a steady note.</summary>
    public const float BubblePascalsPerMm = 0.015f;

    /// <summary>An impact's spike, Pa at a metre, for a 1 mm radius drop at 5 m/s. Its energy goes as
    /// m v³ (Franz 1959) and a drop's rise is fixed, so its peak goes as (r v)^1.5.</summary>
    public const float ImpactPascals = 0.0142f;

    /// <summary>How fast a drop's impact force arrives, s: the first contact, microseconds.</summary>
    private const float ImpactRise = 16e-6f;

    /// <summary>A coherent lump's force rise as a share of its r / v: it lands in the froth of the one
    /// before it, not on still water. Set on 2026-10-05 by measuring, not by ear: with the lumps
    /// striking as sharply as drops, a 6 mm lump's spike carried a hundred times a drop's energy in
    /// one click, and the few of them made the 8-16 kHz hiss peaky (its kurtosis over 10 ms windows
    /// 5.2 against recorded fountains' 3.5-3.8, even with every impact rendered) — the "static" in
    /// it. A fiftieth brings that band to 3.8 and keeps the octaves 500 Hz-16 kHz within 2 dB of the
    /// fountain the constants were fitted to; the level does not move. A thirtieth was smoother
    /// still in 2-8 kHz (kurtosis 3.4, the recordings' own) but left the top octave 2.8 dB short.
    /// Small lumps still strike in the drop's own rise.</summary>
    private const float LumpCushion = 0.02f;

    /// <summary>β in the depth factor u^β: how skewed the bubbles' loudness is.</summary>
    public const float DepthSkew = 4f;

    // ── Physical constants ───────────────────────────────────────────────────────────────────────

    /// <summary>Minnaert: f R = 3.26 m/s for an air bubble near the surface of water.</summary>
    public const float MinnaertHzMetres = 3.26f;

    /// <summary>The bubble's note climbs as it rises: ξ in f = f0 (1 + ξ d t) (van den Doel 2005).</summary>
    public const float BubbleRise = 0.1f;

    /// <summary>The smallest bubble worth ringing: under 0.16 mm it sings above 20 kHz.</summary>
    private const float SmallestBubbleMm = 0.16f;

    /// <summary>Hinze scale, mm: the bubble size spectrum under a plunge steepens above it.</summary>
    private const float HinzeMm = 1.0f;

    /// <summary>The share of the regular-window drops that trap their bubble.</summary>
    private const float RegularShare = 0.9f;

    /// <summary>The share of drops over 2.2 mm diameter that make their type II bubble. Medwin et al.
    /// call it the dominant sound of a large drop, so most of them.</summary>
    private const float IrregularShare = 0.8f;

    /// <summary>The chance a large drop's crater also sheds a weaker second bubble.</summary>
    private const float SecondaryShare = 0.3f;

    /// <summary>The share of a coherent lump's craters that trap one.</summary>
    private const float ChunkShare = 0.6f;

    /// <summary>How much air a plunge drives under, per litre of water, at 3 m/s over the 1 m/s
    /// below which a falling sheet enters without entraining: about one per cent, fitted with the
    /// two constants above against a recorded fountain of jets falling back into their pool.</summary>
    private const float PlungeAirShare = 0.012f;

    /// <summary>The most BUBBLES of one kind a fall renders per block; more are stood for, each
    /// carrying √(real / rendered) of them so the energy is kept. Bubbles ring for milliseconds and
    /// are most of the cost; their notes are spread in pitch and time, so a dozen a block of each
    /// kind already sum to a wash.</summary>
    private const int MaxPerBlock = 12;

    /// <summary>The most IMPACTS a fall renders per block: in practice every one. An impact is a few
    /// samples of spike and a short tail, and it is what the hiss of a fountain is made of, so it is
    /// not thinned the way the bubbles are. Thinning it was grain in the whoosh: twelve clicks a block
    /// each twice as loud as a drop stand out of the sum where fifty clicks of their own size merge
    /// into it — a sum of many small independent events tends to Gaussian noise, a sum of a few large
    /// ones does not.</summary>
    private const int MaxImpactsPerBlock = 256;

    /// <summary>How unevenly ONE jet sheds its drops: the standard deviation of the log of its rate
    /// from slug to slug. A column necks and bursts in slugs tens of milliseconds long.</summary>
    private const float ClumpSigma = 0.7f;

    private const int Block = 128;

    public readonly WaterFeatureSpec Spec;
    private readonly float _rate;
    private readonly EventSum _sum;
    private readonly FallState[] _falls;
    private int _untilBlock;

    /// <summary>The water is running. When it stops, what is in the air still lands.</summary>
    public bool Running = true;
    private float _flow = 1f;
    private float _wind = float.NaN;

    /// <summary>The wind at the spray, m/s.</summary>
    public float Wind;

    /// <summary>Each part's share, for the lab to take the sound apart by muting: the impacts, the
    /// drops' bubbles, the lumps' bubbles and the plunge's. One in the game.</summary>
    public float ImpactPart = 1f, DropBubblePart = 1f, LumpBubblePart = 1f, PlungePart = 1f;

    private sealed class FallState
    {
        public WaterFallSpec Spec = null!;
        public float DropRate, ChunkRate, BubbleRate;   // per second, at full flow
        public float DropMean, DropMax;                  // m
        public float ChunkSpeed;                          // m/s
        public float BubbleMeanVolume;                    // m^3
        public float Wander, WanderTarget, WanderClock;  // the column's breakup moving about
        public float Clump = 1f, ClumpFrom = 1f, ClumpTo = 1f, ClumpLength = 1f, ClumpClock; // drops arriving in bunches
        public float Spread;                              // 1/√streams: how much of one jet's wobble is left in the sum
    }

    public FallingWaterSynth(WaterFeatureSpec spec, float sampleRate, int seed)
    {
        Spec = spec;
        _rate = sampleRate;
        _sum = new EventSum(sampleRate, seed);
        _falls = new FallState[spec.Falls.Length];
        for (int i = 0; i < _falls.Length; i++)
        {
            var f = spec.Falls[i];
            var s = new FallState
            {
                Spec = f,
                DropMean = f.MeanDropRadiusMm * 1e-3f,
                DropMax = MathF.Max(f.MeanDropRadiusMm, f.MaxDropRadiusMm) * 1e-3f,
                Spread = 1f / MathF.Sqrt(Math.Max(1, f.Streams)),
            };
            float q = f.FlowLitresPerSecond * 1e-3f;                                  // m^3/s
            float meanVolume = 4f / 3f * MathF.PI * MeanCube(s.DropMean, MinDropRadius, s.DropMax);
            s.DropRate = f.DropShare * q / meanVolume;
            float chunk = f.ChunkRadiusMm * 1e-3f;
            s.ChunkRate = (1f - f.DropShare) * q / (4f / 3f * MathF.PI * chunk * chunk * chunk);
            s.ChunkSpeed = MathF.Sqrt(2f * 9.81f * f.FallMetres);
            float entrain = PlungeAirShare * MathF.Max(0f, s.ChunkSpeed - 1f) / 2f;
            s.BubbleMeanVolume = PlungeMeanVolume();
            s.BubbleRate = (1f - f.DropShare) * q * entrain / s.BubbleMeanVolume;
            _falls[i] = s;
        }
    }

    /// <summary>How many drops, lumps and plunge bubbles a second each fall makes, for the lab.</summary>
    public (string Name, float Drops, float Lumps, float Bubbles)[] Census()
    {
        var c = new (string, float, float, float)[_falls.Length];
        for (int i = 0; i < _falls.Length; i++) c[i] = (_falls[i].Spec.Name, _falls[i].DropRate, _falls[i].ChunkRate, _falls[i].BubbleRate);
        return c;
    }

    private const float MinDropRadius = 0.2e-3f;

    /// <summary>The mean of r³ for radii exponential about <paramref name="mean"/>, cut to [lo, hi].</summary>
    private static float MeanCube(float mean, float lo, float hi)
    {
        double num = 0, den = 0;
        for (int k = 0; k < 400; k++)
        {
            double r = lo + (hi - lo) * (k + 0.5) / 400.0;
            double w = Math.Exp(-(r - lo) / mean);
            num += w * r * r * r;
            den += w;
        }
        return (float)(num / den);
    }

    /// <summary>The mean volume of a plunge bubble under the Deane-Stokes spectrum, m³.</summary>
    private static float PlungeMeanVolume()
    {
        double num = 0, den = 0;
        for (int k = 0; k < 600; k++)
        {
            double rmm = SmallestBubbleMm * Math.Pow(6.0 / SmallestBubbleMm, (k + 0.5) / 600.0);
            double dr = rmm * Math.Log(6.0 / SmallestBubbleMm) / 600.0;
            double n = PlungeDensity(rmm) * dr;
            double r = rmm * 1e-3;
            num += n * 4.0 / 3.0 * Math.PI * r * r * r;
            den += n;
        }
        return (float)(num / den);
    }

    /// <summary>The Deane-Stokes size spectrum, unnormalised, radius in mm, with the large end let go
    /// gently: a plunge this size cannot hold a bubble much over the depth it drives air to, and a
    /// hard cut at the top piled every large bubble onto one note (a steady line at 550 Hz).</summary>
    private static double PlungeDensity(double rmm)
        => (rmm < HinzeMm ? Math.Pow(rmm, -1.5) : Math.Pow(HinzeMm, -1.5) * Math.Pow(rmm / HinzeMm, -10.0 / 3.0))
           * Math.Exp(-rmm / LargestPlungeMm);

    /// <summary>The scale over which large plunge bubbles thin out, mm.</summary>
    private const double LargestPlungeMm = 2.5;

    /// <summary>A plunge bubble's radius, mm, drawn from the Deane-Stokes spectrum by rejection on a
    /// log-uniform proposal.</summary>
    private float DrawPlungeBubbleMm()
    {
        double lo = Math.Log(SmallestBubbleMm), hi = Math.Log(6.0);
        double peak = PlungeDensity(SmallestBubbleMm) * SmallestBubbleMm;
        for (int tries = 0; tries < 64; tries++)
        {
            double rmm = Math.Exp(lo + (hi - lo) * _sum.Uniform());
            if (_sum.Uniform() * peak <= PlungeDensity(rmm) * rmm) return (float)rmm;
        }
        return SmallestBubbleMm;
    }

    /// <summary>The terminal speed of a raindrop of radius r (m), m/s (Atlas et al. 1973).</summary>
    public static float TerminalSpeed(float r)
        => MathF.Max(0.27f, 9.65f - 10.3f * MathF.Exp(-0.6f * 2000f * r));

    /// <summary>How fast a drop of radius r arrives after falling h from rest, m/s: gravity against
    /// a drag that holds it to its terminal speed.</summary>
    public static float ArrivalSpeed(float r, float h)
    {
        float vt = TerminalSpeed(r);
        return vt * MathF.Sqrt(1f - MathF.Exp(-2f * 9.81f * h / (vt * vt)));
    }

    /// <summary>A bubble's decay rate, 1/s, for a radius in mm: d = 0.13 / R + 0.0072 R^−3/2, R in
    /// metres (van den Doel 2005, after Devin's radiation, thermal and viscous losses). About 360/s at
    /// a millimetre, which is a damping ratio of 0.04.</summary>
    public static float BubbleDecay(float rmm)
    {
        float r = MathF.Max(0.05f, rmm) * 1e-3f;
        return 0.13f / r + 0.0072f * MathF.Pow(r, -1.5f);
    }

    /// <summary>The damping ratio that decay rate is at the bubble's own note.</summary>
    public static float BubbleDamping(float rmm) => BubbleDecay(rmm) / (MathF.PI * MinnaertHzMetres / (rmm * 1e-3f));

    /// <summary>The type II bubble's radius, mm, for a drop of radius r (mm): 10 kHz from a 2.2 mm
    /// drop down to 2 kHz from a 6 mm one, the note falling as D^−1.6 (Medwin et al. 1992).</summary>
    public static float TypeTwoBubbleMm(float rmm)
    {
        float hz = 10000f * MathF.Pow(2f * rmm / 2.2f, -1.6f);
        return MinnaertHzMetres / hz * 1e3f;
    }

    /// <summary>Things that move on the scale of seconds: the jet's top breaking up now more, now
    /// less; the water being turned on or off.</summary>
    public void Control(float dt)
    {
        // A pump coming up to pressure or running down takes a couple of seconds.
        float target = Running ? 1f : 0f;
        _flow += Math.Clamp(target - _flow, -dt / 2f, dt / 2f);
        foreach (var f in _falls)
        {
            f.WanderClock -= dt;
            if (f.WanderClock <= 0f)
            {
                // The top of a jet does not break up the same way twice: sometimes it holds together
                // and comes down as a column, sometimes it bursts into spray. Seconds at a time. Each
                // jet or strand of a fall does it on its own, so the fall as a whole wanders by the
                // square root of their count less.
                f.WanderTarget = 0.18f * f.Spread * _sum.Signed();
                f.WanderClock = 0.4f + 1.6f * _sum.Uniform();
            }
            f.Wander += (f.WanderTarget - f.Wander) * MathF.Min(1f, dt * 2f);
        }
    }

    /// <summary>The next sample, pascals at a metre.</summary>
    public float Next()
    {
        if (--_untilBlock <= 0)
        {
            _untilBlock = Block;
            Schedule(Block / _rate);
        }
        return _sum.Next();
    }

    private void Schedule(float dt)
    {
        // The wind is handed in once a control call, eleven milliseconds apart; glide to it over a
        // few blocks so the spray's share never steps.
        _wind = float.IsNaN(_wind) ? Wind : _wind + (Wind - _wind) * MathF.Min(1f, dt / 0.05f);
        if (_flow <= 0f) return;
        float wind = _wind;
        foreach (var f in _falls)
        {
            // Wind breaks more of a column into spray, and the spray is what it carries away.
            float share = Math.Clamp(f.Spec.DropShare + f.Wander + 0.03f * MathF.Max(0f, wind - 2f), 0.05f, 1f);
            float drift = Math.Clamp(f.Spec.DriftPerMetrePerSecond * (wind - 2f), 0f, 0.5f);
            float dropScale = f.Spec.DropShare > 0f ? share / f.Spec.DropShare : 0f;
            float coherentScale = f.Spec.DropShare < 1f ? (1f - share) / (1f - f.Spec.DropShare) : 0f;

            // A jet's top does not shed drops evenly: the column necks and bursts in slugs, and a
            // slug's drops arrive together, tens of milliseconds at a time. That bunching is what
            // makes the hiss of spray flicker the way a real one does. A fall of many jets or
            // strands flickers by the square root of their count less, since each bunches on its
            // own. And a slug does not land in an instant — its drops are spread along it by their
            // different speeds — so the rate glides from one slug's to the next rather than
            // stepping; the steps, tens a second, were heard as a crackle in the hiss.
            f.ClumpClock -= dt;
            if (f.ClumpClock <= 0f)
            {
                float g = MathF.Sqrt(-2f * MathF.Log(MathF.Max(1e-6f, _sum.Uniform()))) * MathF.Cos(MathF.Tau * _sum.Uniform());
                float sigma = ClumpSigma * f.Spread;
                f.ClumpFrom = f.Clump;
                f.ClumpTo = MathF.Exp(sigma * g - 0.5f * sigma * sigma);
                f.ClumpLength = 0.015f + 0.05f * _sum.Uniform();
                f.ClumpClock = f.ClumpLength;
            }
            f.Clump = f.ClumpTo + (f.ClumpFrom - f.ClumpTo) * MathF.Max(0f, f.ClumpClock / f.ClumpLength);
            Drops(f, f.DropRate * dropScale * _flow * dt * f.Clump, drift);
            Chunks(f, f.ChunkRate * coherentScale * _flow * dt * f.Clump);
            Plunge(f, f.BubbleRate * coherentScale * _flow * dt);
        }
    }

    private void Drops(FallState f, float mean, float drift)
    {
        int real = _sum.Poisson(mean);
        if (real == 0) return;
        int n = Math.Min(real, MaxImpactsPerBlock);
        float weight = MathF.Sqrt(real / (float)n);
        // Every drop's impact is rendered; one drop in `stride` also traps its bubble, for the rest.
        int stride = (n + MaxPerBlock - 1) / MaxPerBlock;
        float ringWeight = weight * MathF.Sqrt(stride);
        float tail = 1f - MathF.Exp(-(f.DropMax - MinDropRadius) / f.DropMean);
        for (int k = 0; k < n; k++)
        {
            int at = (int)(_sum.Uniform() * Block);
            float r = MinDropRadius - f.DropMean * MathF.Log(1f - _sum.Uniform() * tail);
            float v = ArrivalSpeed(r, f.Spec.FallMetres);
            // The impact: the force arrives as the drop's front meets the surface and goes over the
            // time the whole drop takes to bury itself.
            float tau = r / v;
            float impact = ImpactPascals * MathF.Pow(r / 1e-3f * v / 5f, 1.5f) * weight * ImpactPart;
            // Small drops falling far are the ones the wind takes to the paving: they click and
            // trap nothing.
            if (drift > 0f && r < 1e-3f && _sum.Uniform() < drift * (1f - r / 1e-3f))
            {
                // On stone the drop stops in its own length and splashes flat: a shorter force.
                _sum.Impact(at, ImpactRise, 0.4f * tau, impact * 1.2f);
                continue;
            }
            _sum.Impact(at, ImpactRise, tau, impact);
            if (k % stride != 0) continue;

            float rmm = r * 1e3f;
            int later = at + (int)(0.003f * _rate * (0.5f + _sum.Uniform()));
            if (rmm >= 0.4f && rmm <= 0.55f && v > 0.8f * TerminalSpeed(r))
            {
                // The regular bubble: always about the same size, rain on a lake.
                if (_sum.Uniform() < RegularShare) Ring(later, 0.18f + 0.08f * _sum.Uniform(), ringWeight * DropBubblePart);
            }
            else if (rmm >= 1.1f)
            {
                if (_sum.Uniform() > IrregularShare) continue;
                float b = TypeTwoBubbleMm(rmm) * (0.8f + 0.4f * _sum.Uniform());
                Ring(later, b, ringWeight * DropBubblePart);
                if (_sum.Uniform() < SecondaryShare)
                    Ring(later + (int)(0.004f * _rate * _sum.Uniform()), b * (0.3f + 0.6f * _sum.Uniform()), 0.4f * ringWeight * DropBubblePart);
            }
        }
    }

    private void Chunks(FallState f, float mean)
    {
        int real = _sum.Poisson(mean);
        if (real == 0) return;
        int n = Math.Min(real, MaxImpactsPerBlock);
        float weight = MathF.Sqrt(real / (float)n);
        int stride = (n + MaxPerBlock - 1) / MaxPerBlock;
        float ringWeight = weight * MathF.Sqrt(stride);
        float r0 = f.Spec.ChunkRadiusMm * 1e-3f;
        for (int k = 0; k < n; k++)
        {
            int at = (int)(_sum.Uniform() * Block);
            float r = r0 * (0.5f + _sum.Uniform());
            float v = f.ChunkSpeed * (0.9f + 0.2f * _sum.Uniform());
            // A lump lands where the column before it landed, into its own crater and the froth that
            // left, so its force builds over a share of the time it takes to bury itself rather than
            // in the microseconds a drop's round front meets still water. Same energy (m v³), spread
            // over the longer rise, so the peak comes down as the root of it.
            float rise = MathF.Max(ImpactRise, LumpCushion * r / v);
            _sum.Impact(at, rise, r / v, MathF.Sqrt(ImpactRise / rise) * ImpactPascals * MathF.Pow(r / 1e-3f * v / 5f, 1.5f) * weight * ImpactPart);
            if (k % stride != 0 || _sum.Uniform() > ChunkShare) continue;
            // A lump opens a crater too big to close in one: the bubble it traps is a large one, up to
            // the lump's own size — the low "glug" under a fountain. Smaller ones far more often.
            float u = _sum.Uniform();
            float bubbleMm = r * 1e3f * (0.15f + 0.85f * u * u);
            Ring(at + (int)(0.006f * _rate * (0.5f + _sum.Uniform())), bubbleMm, ringWeight * LumpBubblePart);
        }
    }

    private void Plunge(FallState f, float mean)
    {
        int real = _sum.Poisson(mean);
        if (real == 0) return;
        int n = Math.Min(real, MaxPerBlock);
        float weight = MathF.Sqrt(real / (float)n);
        for (int k = 0; k < n; k++)
            Ring((int)(_sum.Uniform() * Block), DrawPlungeBubbleMm(), weight * PlungePart);
    }

    private void Ring(int at, float bubbleMm, float weight)
    {
        if (bubbleMm < SmallestBubbleMm || weight <= 0f) return;
        float hz = MinnaertHzMetres / (bubbleMm * 1e-3f);
        float depth = MathF.Pow(_sum.Uniform(), DepthSkew);
        // A bubble made at the surface is 40 dB under one made deep, and there are a lot of them:
        // their share of the power is under a ten-thousandth, and rendering them was most of the cost.
        if (depth < 0.01f) return;
        _sum.Bubble(at, hz, BubbleDamping(bubbleMm), BubblePascalsPerMm * bubbleMm * depth * weight, BubbleRise);
    }
}
