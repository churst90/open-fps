using System;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// A wood fire, from what is going on in it.
///
/// THE FLAMES. Burning gas whose heat release is unsteady radiates as a monopole: the air round a
/// flame is pushed out when it burns faster and drawn in when it burns slower (Strahle 1971; the
/// acoustic power is a tiny fraction of the heat, η Q). A fire is a buoyant plume, and a buoyant plume
/// does not burn steadily: it necks near its base and sheds a puff, at a rate set by its width alone,
/// f ≈ 1.5 / √D (Cetegen and Ahmed 1993). So the roar is a low noise that swells and falls a couple
/// of times a second. Its energy is under half a kilohertz, strongest at 200-500 Hz (Viegas et al.
/// 2008), and above that a fire is nearly silent between its crackles: in a close recording of one the
/// 2-8 kHz bands have a kurtosis in the thousands. Wind feeds it and tears at it, and it roars harder
/// in a gust.
///
/// THE WOOD. Wood is cells, and the cells hold water and, in softwood, resin. As the fire heats a
/// log the water boils inside closed cells and the resin gasifies; the pressure climbs until a wall
/// gives, and the pocket bursts — a crackle. They are of every size at once, many small and few
/// large, as any population of things that break when stressed is (crackling noise; Sethna, Dahmen
/// and Myers 2001): their sizes follow a power law. They come in clusters, because a log that has
/// just caught has many pockets reaching the same temperature together. The big ones throw an ember
/// that lands a moment later. Each burst is a pressure pulse as short as the opening, then the
/// fragments of char round it rattling.
///
/// THE STEAM. Water driven to the end grain of a log comes out as a jet: a hiss, and when the jet
/// finds a crack of the right shape, an edge tone — the log that sings. Wetter wood, more of both.
///
/// THE SETTLING. A log that has burnt through gives way: a soft thump, a rattle of charcoal pieces
/// (charcoal rings like a brittle ceramic, briefly), then a flare as fresh surface meets the air.
///
/// WHAT IS FITTED: how loud the flames are for their heat (the combustion-noise efficiency), and how
/// loud the smallest crackle is — the pair set on 2026-10-04 against a close recording of a fire in a
/// steel ring (its octaves 125 Hz-8 kHz within 4 dB), and the whole brought to 57 dB(A) at a metre,
/// a campfire's ordinary loudness. The size law (N(>A) ∝ A^−1.2) and the rate (a dozen to twenty
/// distinct pops a second) were measured from three recordings of fires. Everything else is the
/// fire's own numbers.
///
/// PLACES (2026-10-06). The logs lie across a bed a metre wide and the flames stand over all of it.
/// With more than one place (ExtendedSources), each crackle, ember and settling is written to one place
/// (the middle by its share, otherwise one of the places round the bed), and the roar and the fizz have
/// their own noise at each place, their power split by the places' shares, under the one fire's puffing
/// and vigour. The steam jets stay at the middle. One place is the fire as it was.
/// </summary>
public sealed class FireSynth
{
    // ── The fitted constants ─────────────────────────────────────────────────────────────────────

    /// <summary>Acoustic power over heat release for an open wood fire's flames.</summary>
    public const float RoarEfficiency = 2.8e-11f;

    /// <summary>The smallest crackle's peak, Pa at a metre. Sizes run up from it on a power law.</summary>
    public const float SmallestCracklePascals = 0.043f;

    /// <summary>
    /// The fizz under the crackles, rms Pa at a metre for seasoned wood burning well: the volatiles and
    /// steam a burning log lets out through the checks in its char all the time, not only through the
    /// one end-grain jet that sings (the steam jets below). FITTED 2026-10-06 (texture round 1) to the
    /// three fire recordings' envelope statistics: without it the fire was silent between its pops
    /// above 2 kHz (envelope spread 0.45-0.59 at 3-12 kHz against the recordings' 0.18-0.30); the
    /// recordings are a steady fizz with very rare loud cracks over it (kurtosis 21-100). Carrying the
    /// crackles' power law ten to a hundred times further down instead did not fill it: under a law of
    /// exponent 2.2 the energy is in the big pops, and the small ones only lowered the kurtosis.
    /// </summary>
    public const float FizzPascals = 0.0035f;

    /// <summary>The middle of the fizz's band, Hz: gas through cracks a fraction of a millimetre wide.</summary>
    private const float FizzHz = 5000f;

    // ── The fire's laws ──────────────────────────────────────────────────────────────────────────

    /// <summary>The power law of crackle sizes: the chance a crackle is over a is a^−(α−1).</summary>
    private const float CrackleExponent = 2.2f;
    /// <summary>The largest crackle over the smallest.</summary>
    private const float CrackleRange = 150f;
    /// <summary>How many crackles a second a fire with seasoned wood makes when it is burning well,
    /// per 100 kW, before clustering.</summary>
    private const float CracklesPer100Kw = 28f;
    /// <summary>A crackle this many times the smallest throws an ember.</summary>
    private const float EmberSize = 60f;

    private const int Block = 128;

    public readonly FireSpec Spec;
    private readonly float _rate;
    private readonly EventSum _sum;
    /// <summary>Each place's events; place 0 (the middle) is <see cref="_sum"/>, which also draws every
    /// random number the scheduling needs, so a fire of one place renders exactly as before places.</summary>
    private readonly EventSum[] _sums;
    // The outer places' own roar and fizz (index 0 unused: the middle's are the plain fields).
    private readonly Resonator[] _roarLowAt, _roarHighAt, _fizzAt;
    private readonly float[] _placeGain, _placeGainTarget, _placeOut;
    private float _middleShare = 1f;

    /// <summary>How many places the fire is heard from: one, the middle, unless made with more.</summary>
    public int Places => _sums.Length;

    /// <summary>How much of the fire its outer places carry, 0 to 1 (ExtendedSources.Shares).</summary>
    public float Spread;

    /// <summary>Where outer place <paramref name="j"/> of <paramref name="places"/> is, m from the
    /// fire's middle (x east, y up, z north): round the bed at three-quarters of its radius, where the
    /// logs' ends and the hot side of the bed are.</summary>
    public static Vector3 PlaceOffset(FireSpec spec, int j, int places)
    {
        float r = 0.75f * 0.5f * spec.BaseDiameterMetres;
        float angle = MathF.Tau * (j + 0.25f) / Math.Max(1, places);
        return new Vector3(MathF.Cos(angle) * r, 0f, MathF.Sin(angle) * r);
    }

    public bool Lit = true;

    /// <summary>Each part's share, for the lab to take the fire apart by muting. One in the game.</summary>
    public float RoarPart = 1f, CracklePart = 1f, SteamPart = 1f, SettlePart = 1f;
    /// <summary>The wind at the flames, m/s.</summary>
    public float Wind;

    private float _burn = 1f;         // how lit: 1 burning, 0 out
    private float _vigour = 1f;       // the fire's own wander, slow
    private float _vigourTarget = 1f, _vigourClock;
    private float _flare;             // after a settle, the extra
    private float _clusterGain = 1f;  // crackle clustering state
    private float _clusterClock;
    private float _settleClock;
    private int _untilBlock;

    // Flames
    private readonly float _puffHz, _roarHz, _roarPascals;
    private Resonator _roarLow, _roarHigh;
    private float _puffPhase, _puffJitter, _puffDepth = 0.2f;
    private float _roarGain, _roarGainNow = 1f;
    private readonly float _gainGlide;
    private readonly float _roarNorm;

    // Steam jets
    private struct Jet
    {
        public float Life, Age, Strength, Whistle, WhistleHz, DriftPhase, DriftHz;
        public Resonator Hiss, Tone;
        public bool Live;
    }
    private readonly Jet[] _jets = new Jet[3];

    // The fizz: gas and steam let out through the char's checks, steadily, all over the burning wood
    private Resonator _fizz;
    private float _fizzNorm, _fizzTarget, _fizzNow;

    // Embers in the air, landing later
    private readonly float[] _emberAt = new float[16];
    private readonly float[] _emberSize = new float[16];
    private float _clock;
    private int _samples;

    public FireSynth(FireSpec spec, float sampleRate, int seed, int places = 1)
    {
        Spec = spec;
        _rate = sampleRate;
        _sum = new EventSum(sampleRate, seed);
        places = Math.Max(1, places);
        _sums = new EventSum[places];
        _sums[0] = _sum;
        for (int k = 1; k < places; k++) _sums[k] = new EventSum(sampleRate, seed * 7919 + k * 104729 + 1);
        _roarLowAt = new Resonator[places];
        _roarHighAt = new Resonator[places];
        _fizzAt = new Resonator[places];
        _placeGain = new float[places];
        _placeGainTarget = new float[places];
        _placeOut = new float[places];
        _placeGain[0] = _placeGainTarget[0] = 1f;
        _puffHz = 1.5f / MathF.Sqrt(MathF.Max(0.1f, spec.BaseDiameterMetres));
        // The roar sits a few octaves above the puffing: the turbulence inside each puff. A wider
        // fire's eddies are bigger and slower, so its roar is lower: about 300 Hz for a metre-wide
        // fire, a broad band from a few tens of hertz to a few hundred, where the close recordings of
        // fires carry their steady energy.
        _roarHz = 70f / MathF.Sqrt(MathF.Max(0.1f, spec.BaseDiameterMetres));
        // Monopole power W = η Q; at a metre, p_rms = √(W ρ c / 4π).
        float watts = RoarEfficiency * spec.HeatReleaseKw * 1000f;
        _roarPascals = MathF.Sqrt(watts * 1.2f * 343f / (4f * MathF.PI));
        _roarLow.Tune(_roarHz, 0.5f, sampleRate);
        // A second, higher pole pair: the band falls 12 dB an octave above half a kilohertz, so the
        // roar stays out of the kilohertz bands where the crackles live.
        _roarHigh.Tune(_roarHz * 3f, 0.5f, sampleRate);
        _roarGain = 1f;
        _gainGlide = 1f - MathF.Exp(-1f / (0.005f * sampleRate));
        _fizz.Tune(MathF.Min(FizzHz, 0.4f * sampleRate), 0.5f, sampleRate);
        for (int k = 1; k < places; k++)
        {
            _roarLowAt[k].Tune(_roarHz, 0.5f, sampleRate);
            _roarHighAt[k].Tune(_roarHz * 3f, 0.5f, sampleRate);
            _fizzAt[k].Tune(MathF.Min(FizzHz, 0.4f * sampleRate), 0.5f, sampleRate);
        }
        _fizzNorm = 1f / Resonator.NoiseGain(MathF.Min(FizzHz, 0.4f * sampleRate), 0.5f, sampleRate);
        // What the two in series pass of unit white noise, measured once rather than guessed.
        {
            var a = new Resonator(); a.Tune(_roarHz, 0.5f, sampleRate);
            var b = new Resonator(); b.Tune(_roarHz * 3f, 0.5f, sampleRate);
            var rng = new Random(1);
            double e = 0;
            for (int i = 0; i < 48000; i++)
            {
                float y = b.Process(a.Process(((float)rng.NextDouble() * 2f - 1f) * 1.7320508f));
                e += y * y;
            }
            _roarNorm = 1f / MathF.Sqrt((float)(e / 48000));
        }
        _settleClock = 30f + 60f * _sum.Uniform();
        for (int i = 0; i < _emberAt.Length; i++) _emberAt[i] = -1f;
    }

    /// <summary>Crackles a second at this moment, before clustering, for the lab.</summary>
    public float CrackleRate => CracklesPer100Kw * Spec.HeatReleaseKw / 100f
                                * (0.4f + 3f * Spec.Moisture) * (0.6f + 0.8f * Spec.Resin) * _vigour * _burn;

    public void Control(float dt)
    {
        _clock += dt;
        _burn += Math.Clamp((Lit ? 1f : 0f) - _burn, -dt / 20f, dt / 5f);

        // A fire breathes on its own: a log catching, another burning down. Tens of seconds.
        _vigourClock -= dt;
        if (_vigourClock <= 0f)
        {
            _vigourTarget = 0.75f + 0.5f * _sum.Uniform();
            _vigourClock = 8f + 20f * _sum.Uniform();
        }
        _vigour += (_vigourTarget - _vigour) * MathF.Min(1f, dt / 6f);
        _flare *= MathF.Exp(-dt / 8f);

        // Crackles cluster: a log reaching temperature fires off a run of them, then goes quiet.
        _clusterClock -= dt;
        if (_clusterClock <= 0f)
        {
            bool busy = _clusterGain < 1f;
            _clusterGain = busy ? 2.2f + 1.5f * _sum.Uniform() : 0.25f + 0.3f * _sum.Uniform();
            _clusterClock = busy ? 0.6f + 2f * _sum.Uniform() : 1.5f + 4f * _sum.Uniform();
        }

        // The fizz follows how hard the wood is gassing: the burn, the wetness, and the clusters of
        // pockets reaching temperature together that the crackles come in.
        _fizzTarget = FizzPascals * _burn * MathF.Sqrt(Spec.Moisture / 0.2f * _clusterGain);

        // The wind at the flames: more air, more burning, more turbulence.
        float gust = 1f + 0.35f * MathF.Max(0f, Wind - 1f);
        _roarGain = _burn * MathF.Sqrt(_vigour * (1f + _flare)) * gust;

        // Steam jets come and go, more of them for wetter wood.
        float births = 0.05f * Spec.Moisture / 0.2f * _burn;
        for (int i = 0; i < _jets.Length; i++)
        {
            ref var j = ref _jets[i];
            if (j.Live)
            {
                j.Age += dt;
                if (j.Age >= j.Life) j.Live = false;
                continue;
            }
            if (_sum.Uniform() < births * dt)
            {
                j.Live = true;
                j.Age = 0f;
                j.Life = 3f + 15f * _sum.Uniform();
                j.Strength = 0.3f + 0.7f * _sum.Uniform();
                j.Whistle = _sum.Uniform() < 0.3f ? 0.4f + 0.6f * _sum.Uniform() : 0f;
                j.WhistleHz = 1500f + 3000f * _sum.Uniform();
                j.DriftHz = 0.2f + 0.8f * _sum.Uniform();
                j.DriftPhase = MathF.Tau * _sum.Uniform();
                j.Hiss.Tune(2500f + 3000f * _sum.Uniform(), 0.8f, _rate);
                j.Tone.Tune(j.WhistleHz, 60f, _rate);
            }
        }

        // A log giving way.
        _settleClock -= dt;
        if (_settleClock <= 0f && _burn > 0.5f)
        {
            Settle();
            _settleClock = 40f + 90f * _sum.Uniform();
        }

        // The places' shares: events by probability, the roar's and the fizz's power by gain.
        if (_sums.Length > 1)
        {
            Span<float> shares = stackalloc float[_sums.Length];
            ExtendedSources.Shares(Spread, shares);
            _middleShare = shares[0];
            for (int k = 0; k < shares.Length; k++) _placeGainTarget[k] = MathF.Sqrt(shares[k]);
        }
    }

    /// <summary>The place an event is heard from: the middle by its share, otherwise any of the others.</summary>
    private EventSum AnyPlace()
    {
        if (_sums.Length == 1) return _sum;
        float u = _sum.Uniform();
        if (u < _middleShare) return _sum;
        int outer = _sums.Length - 1;
        int k = 1 + (int)((u - _middleShare) / MathF.Max(1e-6f, 1f - _middleShare) * outer);
        return _sums[Math.Clamp(k, 1, outer)];
    }

    /// <summary>The next sample at each place, pascals at a metre from it: <paramref name="places"/>
    /// holds <see cref="Places"/> of them. Their sum is the fire.</summary>
    public void NextPlaces(Span<float> places)
    {
        if (_sums.Length == 1)
        {
            float y = Next();
            if (places.Length > 0) places[0] = y;
            return;
        }
        if (--_untilBlock <= 0)
        {
            _untilBlock = Block;
            Schedule(Block / _rate);
        }
        _puffPhase += _puffHz * (1f + 0.35f * _puffJitter) / _rate;
        if (_puffPhase >= 1f)
        {
            _puffPhase -= 1f;
            _puffJitter = _sum.Signed();
            _puffDepth = 0.1f + 0.25f * _sum.Uniform();
        }
        float puff = 1f + _puffDepth * MathF.Sin(MathF.Tau * _puffPhase);
        _roarGainNow += (_roarGain - _roarGainNow) * _gainGlide;
        _fizzNow += (_fizzTarget - _fizzNow) * _gainGlide;
        float roarScale = _roarPascals * _roarGainNow * puff * RoarPart;
        float fizzScale = _fizzNorm * _fizzNow * CracklePart;
        _samples++;
        for (int k = 0; k < _sums.Length; k++)
        {
            _placeGain[k] += (_placeGainTarget[k] - _placeGain[k]) * _gainGlide;
            var sum = _sums[k];
            float n = sum.Signed() * 1.7320508f;
            float f = sum.Signed() * 1.7320508f;
            float roar, fizz;
            if (k == 0) { roar = _roarHigh.Process(_roarLow.Process(n)); fizz = _fizz.Process(f); }
            else { roar = _roarHighAt[k].Process(_roarLowAt[k].Process(n)); fizz = _fizzAt[k].Process(f); }
            float y = (roar * _roarNorm * roarScale + fizz * fizzScale) * _placeGain[k] + sum.Next();
            if (k == 0) y += Steam();
            if (k < places.Length) places[k] = y;
        }
    }

    /// <summary>The steam jets, at the middle.</summary>
    private float Steam()
    {
        float y = 0f;
        for (int i = 0; i < _jets.Length; i++)
        {
            ref var j = ref _jets[i];
            if (!j.Live) continue;
            float life = MathF.Sin(MathF.PI * j.Age / j.Life);
            float level = j.Strength * life * _burn * SmallestCracklePascals * 0.06f;
            float hn = _sum.Signed() * 1.7320508f;
            y += j.Hiss.Process(hn) * level * SteamPart;
            if (j.Whistle > 0f)
            {
                if ((_samples & 255) == 0)
                {
                    float drift = 1f + 0.06f * MathF.Sin(j.DriftPhase + MathF.Tau * j.DriftHz * _clock);
                    j.Tone.Tune(j.WhistleHz * drift, 60f, _rate);
                }
                y += j.Tone.Process(hn) / Resonator.NoiseGain(j.WhistleHz, 60f, _rate) * level * j.Whistle * 0.3f * SteamPart;
            }
        }
        return y;
    }

    public float Next()
    {
        if (_sums.Length > 1)
        {
            NextPlaces(_placeOut);
            float all = 0f;
            for (int k = 0; k < _placeOut.Length; k++) all += _placeOut[k];
            return all;
        }
        if (--_untilBlock <= 0)
        {
            _untilBlock = Block;
            Schedule(Block / _rate);
        }

        // The flames: noise through the eddy band, the whole of it swelling with each puff.
        _puffPhase += _puffHz * (1f + 0.35f * _puffJitter) / _rate;
        if (_puffPhase >= 1f)
        {
            _puffPhase -= 1f;
            _puffJitter = _sum.Signed();     // no two puffs alike, in length...
            _puffDepth = 0.1f + 0.25f * _sum.Uniform();   // ...or in strength
        }
        // A puff swells the roar rather than switching it: the flames never stop burning between them.
        float puff = 1f + _puffDepth * MathF.Sin(MathF.Tau * _puffPhase);
        float n = _sum.Signed() * 1.7320508f;
        // Two band-passes in series, normalised by what the pair passes of white noise.
        float roar = _roarHigh.Process(_roarLow.Process(n)) * _roarNorm;
        // The gain is set once a control call, with the wind read then; glide to it so it never steps.
        _roarGainNow += (_roarGain - _roarGainNow) * _gainGlide;
        float y = roar * _roarPascals * _roarGainNow * puff * RoarPart;
        _samples++;

        // Steam.
        for (int i = 0; i < _jets.Length; i++)
        {
            ref var j = ref _jets[i];
            if (!j.Live) continue;
            float life = MathF.Sin(MathF.PI * j.Age / j.Life);
            float level = j.Strength * life * _burn * SmallestCracklePascals * 0.06f;
            float hn = _sum.Signed() * 1.7320508f;
            y += j.Hiss.Process(hn) * level * SteamPart;
            if (j.Whistle > 0f)
            {
                // The tone wanders as the pressure behind it does.
                if ((_samples & 255) == 0)
                {
                    float drift = 1f + 0.06f * MathF.Sin(j.DriftPhase + MathF.Tau * j.DriftHz * _clock);
                    j.Tone.Tune(j.WhistleHz * drift, 60f, _rate);
                }
                y += j.Tone.Process(hn) / Resonator.NoiseGain(j.WhistleHz, 60f, _rate) * level * j.Whistle * 0.3f * SteamPart;
            }
        }

        _fizzNow += (_fizzTarget - _fizzNow) * _gainGlide;
        y += _fizz.Process(_sum.Signed() * 1.7320508f) * _fizzNorm * _fizzNow * CracklePart;
        return y + _sum.Next();
    }

    private void Schedule(float dt)
    {
        float rate = CrackleRate * _clusterGain * (1f + 1.5f * _flare) * (1f + 0.2f * MathF.Max(0f, Wind - 2f));
        int count = _sum.Poisson(rate * dt);
        for (int k = 0; k < count; k++) Crackle((int)(_sum.Uniform() * Block));

        // Embers coming down.
        for (int i = 0; i < _emberAt.Length; i++)
        {
            if (_emberAt[i] < 0f) continue;
            _emberAt[i] -= dt;
            if (_emberAt[i] > 0f) continue;
            _emberAt[i] = -1f;
            int at = (int)(_sum.Uniform() * Block);
            float p = SmallestCracklePascals * 0.15f * _emberSize[i] * CracklePart;
            var lands = AnyPlace();
            lands.Pulse(at, 25e-6f, p);
            Span<float> hz = stackalloc float[] { 5200f + 2000f * _sum.Signed(), 9000f + 2500f * _sum.Signed() };
            Span<float> tau = stackalloc float[] { 0.0015f, 0.0008f };
            Span<float> amp = stackalloc float[] { 0.5f * p, 0.3f * p };
            lands.Ring(at, hz, tau, amp);
        }
    }

    private void Crackle(int at)
    {
        // Size on the power law: P(size > s) = s^-(α-1), cut at the range.
        float u = MathF.Max(1f / CrackleRange, _sum.Uniform());
        float size = MathF.Min(CrackleRange, MathF.Pow(u, -1f / (CrackleExponent - 1f)));
        float p = SmallestCracklePascals * size * _burn * CracklePart;
        // A pocket bursting is a volume of gas let out at once: the pressure is the rate of change of
        // the outflow, a spike as the wall gives and a tail as the pocket empties. A bigger pocket
        // empties for longer, so a big pop has a body under its crack.
        float empty = 0.15e-3f * MathF.Pow(size, 0.4f);
        var place = AnyPlace();
        place.Impact(at, 25e-6f, empty, p);
        // The char round it: a scatter of fragments, a few milliseconds of rattle.
        place.Burst(at + 2, 0.0001f, 0.0015f + 0.002f * _sum.Uniform(), 0.2f * p, 1500f, 12000f);
        if (size >= EmberSize)
        {
            for (int i = 0; i < _emberAt.Length; i++)
            {
                if (_emberAt[i] >= 0f) continue;
                // Thrown up and out: it lands in a third of a second to a second.
                _emberAt[i] = 0.3f + 0.7f * _sum.Uniform();
                _emberSize[i] = 0.5f + _sum.Uniform();
                break;
            }
        }
    }

    private void Settle()
    {
        // The log giving way: a soft thump of a few kilos dropping a few centimetres onto the bed.
        int at = (int)(_sum.Uniform() * Block);
        var log = AnyPlace();
        log.Pulse(at, 0.004f, SmallestCracklePascals * 6f * SettlePart);
        // Charcoal pieces tumbling: a dozen or so brittle little rings over half a second.
        int pieces = 6 + (int)(14 * _sum.Uniform());
        Span<float> hz = stackalloc float[3];
        Span<float> tau = stackalloc float[3];
        Span<float> amp = stackalloc float[3];
        for (int k = 0; k < pieces; k++)
        {
            int when = at + (int)(_sum.Uniform() * _sum.Uniform() * 0.4f * _rate);
            float p = SmallestCracklePascals * (0.4f + 1.6f * _sum.Uniform()) * SettlePart;
            float f0 = 2200f + 4000f * _sum.Uniform();
            hz[0] = f0; hz[1] = f0 * 2.7f; hz[2] = f0 * 5.1f;
            tau[0] = 0.004f; tau[1] = 0.002f; tau[2] = 0.001f;
            amp[0] = 0.5f * p; amp[1] = 0.3f * p; amp[2] = 0.2f * p;
            log.Pulse(when, 40e-6f, p);
            log.Ring(when, hz, tau, amp);
        }
        _flare = MathF.Min(1.5f, _flare + 0.8f);
    }
}
