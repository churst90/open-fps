using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// Water running in a channel and falling out of it, as the events it is made of (docs/RUNNING_WATER.md).
///
/// WHERE THE SOUND IS. Water sliding smoothly over a bed is nearly silent in air: what a stream, a
/// gutter or a drain is heard by is where its surface BREAKS. At every stone in a riffle (and every
/// lip, leaf dam and broken edge in a gutter) the flow speeds up over or round it and drops into the
/// slower water in its lee as a small plunging jet. Fast enough, the jet folds air under the surface,
/// and the air breaks into bubbles, each of which rings at its Minnaert frequency for a few dozen
/// cycles (Minnaert 1933; Leighton 1994). A brook is thousands of those a second from a few dozen
/// places, which is the babble; the hiss on top is the smallest bubbles and the spray thrown off the
/// steepest jets.
///
/// THE PLACES SING THEIR OWN NOTES. A breaking site is a fixed place with a fixed geometry, and it
/// makes bubbles of the sizes its own jet makes, over and over: the recurring notes in a recording of
/// a brook, a few pitches from each stone (a brook's sound is "a succession of Minnaert-like
/// oscillations", Leighton and Walton 1987). So each site here has its own characteristic radius, drawn
/// once, about which a share of its bubbles fall, and the rest follow the Deane and Stokes (2002) size
/// spectrum below its own Hinze scale.
///
/// IT COMES IN BURSTS. The lee of a stone does not entrain steadily: the shear layer off the obstacle
/// sheds eddies, and the jet's toe breaks with each one (a Strouhal frequency, St = f d / U ≈ 0.2), so
/// a site's bubbles arrive in bursts a few times a second, of very uneven size: the occasional big
/// gulp of air is the "glug". Bursts are drawn heavy-tailed and most of a site's air goes in them.
///
/// WHERE IT FALLS (<see cref="RunningWaterSpec.Falls"/>) is the fountain's physics (FallingWaterSynth):
/// a drain's water pouring between the bars into its gully pot, a downpipe's stream leaving its shoe, a
/// basin's overflow, written into this synth's places through FallingWaterSynth.Placer. What lands
/// INSIDE a cavity (the gully pot, the pipe) is heard through it: a tube that rings at its own air
/// modes and lets the sound out of its opening (<see cref="Tube"/>).
///
/// WHEN IT IS ALMOST NOTHING it drips: a flow too small to leave a lip as a stream leaves it as drops,
/// one every V / Q, V the drop a lip of that size holds (Tate's law). The last of a shower running off
/// a downpipe's shoe.
///
/// HOW MUCH. Everything follows the flow: depth and speed from Manning's law for the channel
/// (Hydraulics), the air each site drives under from its jet's speed, every rate from that. A gutter
/// fed by the rain follows the rain through its catchment (Runoff), filling over a minute or two and
/// running on after.
///
/// EXTENDED (ExtendedSources). A creek or a gutter is metres long and is heard from places along it,
/// each its own stream of events: every site belongs to the place nearest it, and with the spread
/// (<see cref="Spread"/>) its events are heard there, otherwise at the middle. A drain or a downpipe is
/// heard from a ring of places round where its water lands. No place is a copy of another.
///
/// WHAT IS FITTED is named here and nowhere else: <see cref="BreakingAirShare"/> (how much air a
/// breaking site drives under for its jet's speed), <see cref="BurstShare"/> and
/// <see cref="BurstSpread"/> (how bursty), <see cref="SiteNoteShare"/> (how much of a site's air goes
/// into its own notes). The bubble's loudness for its size and the splash's efficiency are the
/// fountain's (FallingWaterSynth), fitted there and not refitted here.
/// </summary>
public sealed class RunningWaterSynth
{
    // ── The fitted constants ─────────────────────────────────────────────────────────────────────

    /// <summary>The volume of air a breaking site drives under, per volume of water through it, for
    /// each unit its lee jet's Froude number stands over <see cref="JumpOnsetFroude"/>. The water drops
    /// into the lee and meets the slower water there in a small hydraulic jump, and a jump's air grows
    /// with its Froude number (Rajaratnam 1962; Wang and Chanson 2018 measured 24 % of the flow in a
    /// developed jump's shear layer and 50-60 % in all at Fr 6.6 and over). FITTED 2026-10-06 with the
    /// burst constants below to eight recorded creeks and rivers (the texture statistics, docs/
    /// RUNNING_WATER.md section 8): the sum of many sites has to be as dense as a recorded riffle, and
    /// 0.004 left it a few loud gurgles over silence. The creek's 40 L/s then drives 1.3 % of its flow
    /// under as air, and stands 54 dB(A) on its bank.</summary>
    public const float BreakingAirShare = 0.016f;

    /// <summary>The lee jet's Froude number under which its jump does not break: undular, with no roller
    /// and no air, under 1.4; a breaking roller forms from 1.5-3 (Wüthrich, Shi and Chanson 2022).</summary>
    public const float JumpOnsetFroude = 1.4f;

    /// <summary>The speed over which a jet's spilling crest throws spray, m/s: a plunging jet's own onset
    /// of entrainment, 0.8-1 m/s (Ervine, McKeogh and Elsawy 1980; reviewed in Smit 2007).</summary>
    public const float SprayOnset = 0.8f;

    /// <summary>The share of a site's air that goes in its bursts; the rest is a steady trickle of
    /// bubbles between them.</summary>
    public const float BurstShare = 0.25f;

    /// <summary>How uneven the bursts are: the standard deviation of the natural log of one burst's
    /// air. Turbulent intermittency is log-normal (Kolmogorov 1962).</summary>
    public const float BurstSpread = 0.7f;

    /// <summary>The share of a site's bubbles that are its own notes, about its characteristic radius.</summary>
    public const float SiteNoteShare = 0.3f;

    /// <summary>How tightly a site's notes keep to its radius: the standard deviation of their natural log.</summary>
    private const float SiteNoteSpread = 0.18f;

    /// <summary>A site's shedding: St = f d / U.</summary>
    private const float Strouhal = 0.2f;

    /// <summary>How long one burst's bubbles take to pinch off, s, for a 5 cm drop; goes as the drop.</summary>
    private const float BurstSeconds = 0.03f;

    /// <summary>The chance a burst also closes on a big air pocket in the lee: the glug.</summary>
    private const float GlugShare = 0.6f;

    /// <summary>The note of a glug rises faster than a small bubble's: it is let go right at the
    /// surface, where a bubble rings up to √2 higher than deep (Strasberg 1953), and the rise is audible
    /// on bubbles of about 4 mm and up (van den Doel 2005: ξ 0.1 for drop bubbles, 0.5-1 for bubbles
    /// let go from a nozzle).</summary>
    private const float GlugRise = 0.35f;

    /// <summary>The share of a site jet's kinetic energy flux that its spilling crest throws as spray.</summary>
    private const float SpillCrownShare = 0.05f;

    // ── Physical constants ───────────────────────────────────────────────────────────────────────

    /// <summary>Surface tension of water against air, N/m, and its density.</summary>
    private const float SurfaceTension = 0.072f, WaterDensity = 1000f;

    /// <summary>The smallest bubble worth ringing, mm: under it the note is over 20 kHz.</summary>
    private const float SmallestBubbleMm = 0.16f;

    /// <summary>The flow over a lip below which it drips rather than runs, L/s: a jet holds together
    /// from a Weber number of about four at the lip (Clanet and Lasheras 1999).</summary>
    private const float JetOnsetLitresPerSecond = 0.006f;

    private const int Block = 128;
    private const int MaxSites = 40;
    /// <summary>The most bubbles of one burst, and of one site's steady trickle in one block, rendered;
    /// more are stood for, each carrying √(real / rendered) of them. Thinned harder, the few that stand
    /// for many are each too loud and the top octaves turn to grit (the fountain's lesson, 2026-10-05).</summary>
    private const int MaxPerBurst = 16;
    private const int MaxSteadyPerBlock = 32;

    public readonly RunningWaterSpec Spec;
    private readonly float _rate;
    private readonly EventSum[] _open;
    private readonly EventSum[]? _inside;
    private readonly Tube[]? _tubes;
    private readonly EventSum _rng;
    private readonly Site[] _sites;
    private readonly FallingWaterSynth? _falls;
    private readonly int[] _fallHome;
    private readonly float _referenceFlow;
    private int _untilBlock;

    /// <summary>The flow the water is carrying now, L/s. The voice sets it from the spec and the rain;
    /// it glides there over a second or two (a channel does not change its water at once).</summary>
    public float Flow;
    private float _flow = float.NaN;
    private Hydraulics.State _state;

    /// <summary>How much of it its outer places carry, 0 to 1 (ExtendedSources.Shares).</summary>
    public float Spread;
    private float _toHome;

    /// <summary>Each part's share, for the lab to take the sound apart: the breaking sites' bubbles,
    /// their spray, the falls, the drips. One in the game.</summary>
    public float SitePart = 1f, SprayPart = 1f, FallPart = 1f, DripPart = 1f;

    /// <summary>How many places it is heard from, its middle first.</summary>
    public int Places => _open.Length;

    private sealed class Site
    {
        public EventSum Rng = null!;
        public int Home;
        public float Drop;            // m: how far the water falls into its lee
        public float NoteMm;          // its characteristic bubble radius
        public float Strip;           // m: the width of flow it gathers
        public float Weight = 1f;     // how many real obstacles it stands for
        // From the flow (UpdateSites):
        public float JetSpeed, Froude, AirRate, BurstRate, HinzeMm, LargestMm, Thickness, Depth;
        public float NextBurst;       // s
        public float MeanVolume, VolumeHinze, VolumeLargest;   // the mean bubble volume, cached for these
    }

    public RunningWaterSynth(RunningWaterSpec spec, float sampleRate, int seed)
    {
        Spec = spec;
        _rate = sampleRate;
        int places = Math.Max(1, spec.Places);
        _open = new EventSum[places];
        for (int k = 0; k < places; k++) _open[k] = new EventSum(sampleRate, seed * 7919 + 1 + k * 15485863);
        _rng = _open[0];
        if (spec.Cavity is { } cavity)
        {
            _inside = new EventSum[places];
            _tubes = new Tube[places];
            for (int k = 0; k < places; k++)
            {
                _inside[k] = new EventSum(sampleRate, seed * 104729 + 3 + k * 32452843);
                _tubes[k] = new Tube(cavity, sampleRate);
            }
        }
        _referenceFlow = MathF.Max(1e-6f, spec.ReferenceFlow);
        if (spec.Channel == FlowChannel.KerbGutter)
        {
            _rain = new RainSynth[places];
            _rainGain = new float[places];
            for (int k = 0; k < places; k++) _rain[k] = new RainSynth(sampleRate, seed * 977 + 41 + k * 7919);
        }

        // The breaking sites: as many as the bed has, up to MaxSites, each standing for its share.
        var sites = new List<Site>();
        if (spec.Obstacles is { } obs && spec.Channel != FlowChannel.None)
        {
            float real = MathF.Max(0f, obs.PerMetre * spec.LengthMetres);
            int n = Math.Clamp((int)MathF.Round(real), real > 0f ? 1 : 0, MaxSites);
            for (int i = 0; i < n; i++)
            {
                var rng = new EventSum(sampleRate, seed * 31 + 1000 + i * 7);
                // Spread evenly along the length (jittered), so every place has its share of them.
                float along = ((i + 0.5f + 0.8f * (rng.Uniform() - 0.5f)) / n - 0.5f) * spec.LengthMetres;
                var site = new Site
                {
                    Rng = rng,
                    Home = HomeOf(along),
                    Drop = obs.MedianDropMetres * MathF.Exp(obs.DropSpread * Gauss(rng)),
                    Strip = obs.WidthMetres * (0.6f + 0.8f * rng.Uniform()),
                    Weight = real / n,
                };
                site.NextBurst = rng.Uniform() * 0.5f;
                sites.Add(site);
            }
        }
        _sites = sites.ToArray();

        // The falls, through the fountain's physics, written into these places.
        _fallHome = new int[spec.Falls.Length];
        if (spec.Falls.Length > 0)
        {
            var falls = new WaterFallSpec[spec.Falls.Length];
            var taps = new WaterTapSpec[spec.Falls.Length];
            for (int i = 0; i < falls.Length; i++)
            {
                falls[i] = FallFor(spec, spec.Falls[i], _referenceFlow) with { Tap = i };
                taps[i] = new WaterTapSpec { Name = spec.Falls[i].Name };
                // A line's falls are spread along it; a ring's land in its middle and are scattered by
                // the placer below.
                _fallHome[i] = spec.Layout == FlowLayout.Line && places > 1
                    ? HomeOf(((i + 0.5f) / falls.Length - 0.5f) * spec.LengthMetres) : -1;
            }
            var feature = new WaterFeatureSpec { Name = spec.Name, Falls = falls, Taps = taps, SourceLevelDb = spec.SourceLevelDb };
            // Nothing falls until the first control call says how much is running.
            _falls = new FallingWaterSynth(feature, sampleRate, seed * 13 + 5) { Wind = 0f, FlowScale = 0f, HardCushion = WetCushion };
            _falls.Placer = PlaceForFall;
        }
        // Each stone's own note is set by the water it was made for, whatever is running when the voice
        // starts; the flow itself starts where the first control call finds it.
        UpdateFlow(MathF.Max(0f, spec.ReferenceFlow));
        Flow = spec.ReferenceFlow;
    }

    /// <summary>The place nearest a point <paramref name="along"/> metres from the middle, on a line.</summary>
    private int HomeOf(float along)
    {
        int places = _open.Length;
        if (places == 1 || Spec.Layout != FlowLayout.Line) return 0;
        // Places sit at LineOffset(k): the middle, then alternately either side.
        int best = 0;
        float bestD = float.MaxValue;
        for (int k = 0; k < places; k++)
        {
            float d = MathF.Abs(LineOffset(k, places, Spec.LengthMetres) - along);
            if (d < bestD) { bestD = d; best = k; }
        }
        return best;
    }

    /// <summary>Where place <paramref name="k"/> of <paramref name="places"/> sits along a line source
    /// of this length, m from its middle. The line is cut into as many equal pieces as it has places
    /// and each outer place stands in the middle of its own piece, nearest first; the middle place is
    /// the source's own point (with an odd count, the middle of the middle piece).</summary>
    public static float LineOffset(int k, int places, float lengthMetres)
    {
        if (places <= 1 || k <= 0) return 0f;
        float piece = lengthMetres / places;
        // The pieces' middles, nearest the middle first, the one nearest zero left to the middle place.
        Span<float> centres = stackalloc float[places];
        for (int i = 0; i < places; i++) centres[i] = (i + 0.5f) * piece - 0.5f * lengthMetres;
        centres.Sort((a, b) => MathF.Abs(a) != MathF.Abs(b) ? MathF.Abs(a).CompareTo(MathF.Abs(b)) : b.CompareTo(a));
        return centres[Math.Min(k, places - 1)];
    }

    /// <summary>For the lab: how many of its sites break at this moment, their lee jets, and the air.</summary>
    public string Census()
    {
        int breaking = 0;
        float air = 0f, maxFr = 0f, sumJet = 0f, bursts = 0f;
        foreach (var s in _sites)
        {
            if (s.AirRate > 0f) { breaking++; air += s.AirRate; bursts += s.BurstRate * s.Weight; }
            maxFr = MathF.Max(maxFr, s.Froude);
            sumJet += s.JetSpeed;
        }
        return $"  sites: {_sites.Length} rendered, {breaking} breaking; lee jets {(_sites.Length > 0 ? sumJet / _sites.Length : 0f):F2} m/s mean, Froude up to {maxFr:F1}; " +
               $"air {air * 1e6:F1} mL/s, {bursts:F0} bursts/s";
    }

    /// <summary>The places of a source, as offsets from its middle in its own frame (x along a line).</summary>
    public static Vector3[] Layout(RunningWaterSpec spec)
    {
        int n = Math.Max(1, spec.Places);
        var at = new Vector3[n];
        for (int k = 1; k < n; k++)
        {
            if (spec.Layout == FlowLayout.Line) at[k] = new Vector3(LineOffset(k, n, spec.LengthMetres), 0f, 0f);
            else
            {
                float r = 0.5f * spec.ExtentMetres, a = MathF.Tau * (k - 1 + 0.25f) / (n - 1);
                at[k] = new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
            }
        }
        return at;
    }

    /// <summary>The fountain's fall for one of this source's falls, at this flow: the sheet leaving the
    /// lip, its thickness from the weir law, and from that how it arrives — a thin sheet fingers into
    /// strands that bead into drops; a thick one falls coherent, in lumps the size of its strands
    /// (Rayleigh-Plateau: about 1.9 strand diameters apart).</summary>
    public static WaterFallSpec FallFor(RunningWaterSpec spec, FlowFall fall, float flowLitresPerSecond)
    {
        float q = MathF.Max(1e-6f, flowLitresPerSecond * fall.FlowShare);
        float head = Hydraulics.WeirHead(q, fall.LipWidthMetres);
        float speed = MathF.Max(0.1f, Hydraulics.LipSpeed(head));
        float thickness = q * 1e-3f / (speed * MathF.Max(0.01f, fall.LipWidthMetres));      // m
        // Under about 2 mm a falling sheet tears into strands and beads before it lands (the fountain
        // bowl's 30 µm sheet arrives as drops); over about 6 mm it holds together.
        // A film held to a wall has had nowhere to tear: it arrives whole whatever its thickness.
        float coherent = fall.Film ? 1f : Math.Clamp((thickness * 1e3f - 2f) / 4f, 0f, 1f);
        float strandMm = Math.Clamp(thickness * 1e3f, 1f, 12f);
        // A film down a pipe does not fall freely: the wall holds it to a terminal speed after a few
        // metres, 3.9 m/s at 1 L/s in a 110 mm pipe and as (Q/D)^0.4 (Wyly and Eaton 1961; Aoki, Öhler and
        // Kaltbeitzel 2025). It strikes the bend as if it had fallen v²/2g, never more than the pipe.
        float fallMetres = fall.DropMetres;
        if (fall.Film && spec.Cavity is { } pipe)
        {
            float terminal = 3.9f * MathF.Pow(q * 0.110f / MathF.Max(0.02f, pipe.DiameterMetres), 0.4f);
            fallMetres = MathF.Min(fall.DropMetres, terminal * terminal / (2f * Hydraulics.Gravity));
        }
        return new WaterFallSpec
        {
            Name = fall.Name,
            FlowLitresPerSecond = q,
            FallMetres = fallMetres,
            DropShare = Math.Clamp(1f - 0.85f * coherent, 0.1f, 1f),
            MeanDropRadiusMm = Math.Clamp(0.6f * strandMm, 0.8f, 2.2f),
            ChunkRadiusMm = Math.Clamp(0.95f * strandMm, 1.5f, 8f),
            Streams = Math.Max(1, fall.Streams),
            Onto = fall.Onto,
            DriftPerMetrePerSecond = 0f,
        };
    }

    private int _nextRingHome;

    /// <summary>Where an event of fall <paramref name="tap"/> is written: its home place with the spread,
    /// otherwise the middle; inside the cavity if it lands there.</summary>
    private EventSum PlaceForFall(int tap)
    {
        int home = tap >= 0 && tap < _fallHome.Length ? _fallHome[tap] : 0;
        if (home < 0) home = _open.Length > 1 ? 1 + (_nextRingHome++ % (_open.Length - 1)) : 0;
        int k = home != 0 && _rng.Uniform() < _toHome ? home : 0;
        bool inside = _inside != null && tap >= 0 && tap < Spec.Falls.Length && Spec.Falls[tap].Inside;
        return inside ? _inside![k] : _open[k];
    }

    /// <summary>Where one event of a site is heard.</summary>
    private EventSum PlaceForSite(Site s)
    {
        int home = s.Home;
        if (Spec.Layout == FlowLayout.Ring && _open.Length > 1) home = 1 + (int)(s.Rng.Uniform() * (_open.Length - 1)) % (_open.Length - 1);
        return home != 0 && s.Rng.Uniform() < _toHome ? _open[home] : _open[0];
    }

    private static float Gauss(EventSum s)
        => MathF.Sqrt(-2f * MathF.Log(MathF.Max(1e-7f, s.Uniform()))) * MathF.Cos(MathF.Tau * s.Uniform());

    /// <summary>Seconds-scale things: the flow gliding to where it is going, and the places' shares.</summary>
    public void Control(float dt)
    {
        float target = MathF.Max(0f, float.IsFinite(Flow) ? Flow : 0f);
        if (float.IsNaN(_flow)) _flow = target;
        // A channel takes a second or two to change its water; glide in proportion, so a trickle
        // and a torrent settle as fast as each other.
        _flow += (target - _flow) * MathF.Min(1f, dt / 1.5f);
        UpdateFlow(_flow);
        if (_falls != null)
        {
            // Too little to leave its lip as a stream, it drips instead (Drips).
            bool dripping = Spec.DripLipMm > 0f && _flow < JetOnsetLitresPerSecond;
            _falls.FlowScale = dripping ? 0f : _flow / _referenceFlow;
            _falls.ImpactPart = _falls.DropBubblePart = _falls.LumpBubblePart = _falls.PlungePart = _falls.SplashPart = FallPart;
            _falls.Control(dt);
        }
        int n = _open.Length;
        Span<float> shares = stackalloc float[n];
        ExtendedSources.Shares(n > 1 ? Spread : 0f, shares);
        // An event whose home is an outer place goes there with probability s: that is what makes each
        // outer place carry s/n of the power and the middle the rest (ExtendedSources.Shares).
        _toHome = Math.Clamp(Spread, 0f, 1f);
        if (_rain != null) RainControl(dt, shares);
    }

    // ── Rain on the running water ────────────────────────────────────────────────────────────────

    /// <summary>The rain falling now, mm/h (not what is running off: the rain itself). The voice sets it.</summary>
    public float RainOnWater;

    /// <summary>The rain's share, for the lab. One in the game.</summary>
    public float RainPart = 1f;

    private readonly RainSynth[]? _rain;
    private readonly float[]? _rainGain;
    private float _rainWidth = -1f;
    private double _rainClock;

    /// <summary>
    /// A gutter's own water in the rain: drops landing on a moving strip of water, not on the asphalt the
    /// rain survey sees there, so the clicks and the bubbles of rain on a pool (RainSynth, a Pool layer:
    /// Medwin et al. 1992's drop classes, the regular bubble near 14 kHz) over the strip's wetted width
    /// along its length. A creek's water is a surface of the map's own and its rain is the survey's.
    /// Each place has its own synth over its share of the strip, and the shares move with the spread as
    /// power (its gain), since a stream of drops cannot be handed out event by event.
    /// </summary>
    private void RainControl(float dt, ReadOnlySpan<float> shares)
    {
        int n = _rain!.Length;
        float width = _state.WettedWidthMetres;
        if (MathF.Abs(width - _rainWidth) > 0.1f * MathF.Max(0.05f, _rainWidth))
        {
            _rainWidth = width;
            float area = MathF.Max(0f, width) * Spec.LengthMetres / n;
            for (int k = 0; k < n; k++)
            {
                var layer = new RainLayer { Kind = RainSurfaceKind.Pool, Material = "Water" };
                if (area > 0f) layer.Add(0, area, 1f, 1f);
                _rain[k].Patch = new RainPatch { Layers = new[] { layer }, ReferenceDistance = 1f };
            }
        }
        _rainClock += dt;
        for (int k = 0; k < n; k++)
        {
            _rain[k].RainRate = MathF.Max(0f, RainOnWater);
            _rain[k].Clock = _rainClock;
            // Each place renders 1/n of the strip; its power share of the whole is shares[k].
            _rainGain![k] = MathF.Sqrt(shares[k] * n) * RainPart;
        }
    }

    private float RainAt(int k)
    {
        if (_rain == null) return 0f;
        float g = _rainGain![k];
        // A place nobody hears (merged) is not rendered at all; its synth picks up where it left off.
        return g > 0f && RainOnWater > 0f ? g * _rain[k].Next() : 0f;
    }

    /// <summary>Depth, speed and every site's rates for this flow.</summary>
    private void UpdateFlow(float flow)
    {
        _state = Hydraulics.Of(Spec, flow);
        float v = _state.SpeedMetresPerSecond, y = _state.DepthMetres;
        foreach (var s in _sites)
        {
            if (flow <= 0f || y <= 0f) { s.AirRate = 0f; s.BurstRate = 0f; continue; }
            // The surface drops into the lee by the obstacle's height, or by the depth when the obstacle
            // stands out of the water (the water goes round it and falls back behind).
            float drop = s.Drop * y / (s.Drop + y);
            float jet = MathF.Sqrt(v * v + 2f * Hydraulics.Gravity * drop);
            s.JetSpeed = jet;
            // The flow through the site: its strip of the channel, never more than all of it.
            float strip = MathF.Min(s.Strip, MathF.Max(0.01f, _state.WettedWidthMetres));
            float q = MathF.Min(flow * 1e-3f, v * y * strip);          // m^3/s
            s.Thickness = q / (jet * strip);
            // The lee jet meets the water behind the obstacle in a small jump: it breaks, and drives air
            // under, by how far its Froude number stands over the undular jump's.
            s.Froude = jet / MathF.Sqrt(Hydraulics.Gravity * MathF.Max(1e-4f, s.Thickness));
            float air = q * BreakingAirShare * MathF.Max(0f, s.Froude - JumpOnsetFroude) * s.Weight;   // m^3/s
            // The size of the bubbles the jet breaks the air into: the Hinze scale falls as the
            // turbulence is more violent, as ε^(-2/5) (Hinze 1955; about a millimetre at the 0.1-40 W/kg
            // of a surf zone, Garrett, Li and Farmer 2000), ε here the jet's kinetic energy spent over
            // its own fall.
            float eps = jet * jet * jet / MathF.Max(0.005f, 2f * drop + s.Thickness);
            s.HinzeMm = Math.Clamp(MathF.Pow(eps / 15f, -0.4f), 0.5f, 3f);
            // Nothing bigger than the jet that folds it under: half its thickness, and the cavity it opens.
            s.LargestMm = Math.Clamp(500f * MathF.Min(s.Thickness, drop + 0.5f * y), 0.8f, 9f);
            if (s.NoteMm <= 0f)
                s.NoteMm = Math.Clamp(s.HinzeMm * MathF.Exp(0.6f * Gauss(s.Rng)) * 1.6f, 0.4f, 0.7f * s.LargestMm);
            s.AirRate = air;
            // How deep the jet drives its air: about its own fall and thickness.
            s.Depth = Math.Clamp(drop + s.Thickness, 0.003f, 0.1f);
            // Its shedding: the eddies off an obstacle of its own size (its drop and its strip), at the
            // jet's speed.
            float size = MathF.Max(0.02f, 0.5f * (s.Drop + s.Strip));
            s.BurstRate = Strouhal * jet / size;
        }
    }

    /// <summary>The next sample at each place, pascals at a metre from it. Their sum is the source.</summary>
    public void NextPlaces(Span<float> places)
    {
        Step();
        for (int k = 0; k < _open.Length; k++)
        {
            float y = _open[k].Next() + RainAt(k);
            if (_inside != null) y += _tubes![k].Process(_inside[k].Next());
            if (k < places.Length) places[k] = y;
        }
    }

    /// <summary>The next sample of the whole source at one point, pascals at a metre.</summary>
    public float Next()
    {
        Step();
        float y = 0f;
        for (int k = 0; k < _open.Length; k++)
        {
            y += _open[k].Next() + RainAt(k);
            if (_inside != null) y += _tubes![k].Process(_inside[k].Next());
        }
        return y;
    }

    private void Step()
    {
        if (--_untilBlock <= 0)
        {
            _untilBlock = Block;
            Schedule(Block / _rate);
        }
        _falls?.Advance();
    }

    private void Schedule(float dt)
    {
        float flow = _flow;
        if (!(flow > 0f)) return;
        foreach (var s in _sites)
            if (s.AirRate > 0f) BreakSite(s, dt);
        if (Spec.DripLipMm > 0f && flow < JetOnsetLitresPerSecond) Drips(flow, dt);
    }

    // ── A breaking site ──────────────────────────────────────────────────────────────────────────

    private void BreakSite(Site s, float dt)
    {
        var rng = s.Rng;
        if (MathF.Abs(s.VolumeHinze - s.HinzeMm) > 0.02f * s.HinzeMm || MathF.Abs(s.VolumeLargest - s.LargestMm) > 0.02f * s.LargestMm)
        {
            s.MeanVolume = MeanBubbleVolume(s.HinzeMm, s.LargestMm, s.NoteMm);
            s.VolumeHinze = s.HinzeMm;
            s.VolumeLargest = s.LargestMm;
        }
        float meanVolume = s.MeanVolume;
        // The steady trickle of bubbles between bursts.
        float steady = (1f - BurstShare) * s.AirRate / meanVolume * dt;
        int real = rng.Poisson(steady);
        if (real > 0)
        {
            int n = Math.Min(real, MaxSteadyPerBlock);
            float w = MathF.Sqrt(real / (float)n) * SitePart;
            for (int b = 0; b < n; b++)
                RingNear(PlaceForSite(s), rng, (int)(rng.Uniform() * Block), DrawBubbleMm(rng, s), w, s.Depth, FallingWaterSynth.BubbleRise);
        }
        // The bursts: one per eddy shed, each its own share of the air.
        s.NextBurst -= dt;
        while (s.NextBurst <= 0f)
        {
            float rate = MathF.Max(0.05f, s.BurstRate * s.Weight);
            int at = Math.Clamp((int)((dt + s.NextBurst) * _rate), 0, Block - 1);
            // Eddies are shed nearly in time, not at random: a jittered period.
            s.NextBurst += (0.6f + 0.8f * rng.Uniform()) / rate;
            Burst(s, at, rate, meanVolume);
        }
    }

    private void Burst(Site s, int at, float rate, float meanVolume)
    {
        var rng = s.Rng;
        // This burst's air: its mean share, log-normally uneven (mean one).
        float size = MathF.Exp(BurstSpread * Gauss(rng) - 0.5f * BurstSpread * BurstSpread);
        float air = BurstShare * s.AirRate / rate * size;
        int count = rng.Poisson(air / meanVolume);
        var place = PlaceForSite(s);
        int span = (int)(BurstSeconds * MathF.Sqrt(MathF.Max(0.2f, s.Drop / 0.05f)) * _rate);
        if (count > 0)
        {
            int n = Math.Min(count, MaxPerBurst);
            float w = MathF.Sqrt(count / (float)n) * SitePart;
            for (int b = 0; b < n; b++)
                RingNear(place, rng, at + (int)(span * rng.Uniform() * rng.Uniform()), DrawBubbleMm(rng, s), w, s.Depth, FallingWaterSynth.BubbleRise);
        }
        // A big burst closes on a pocket of air in the lee: one large bubble, its note climbing.
        if (rng.Uniform() < GlugShare * MathF.Min(1f, size))
        {
            float u = rng.Uniform();
            float mm = MathF.Min(s.LargestMm, s.NoteMm * (1.5f + 2.5f * u * u));
            // Not a bubble sheared off deep and heard through its image: a cavity closing at the surface,
            // which drives the surface above it like a piston, as a drip's cavity does (Phillips, Agarwal
            // and Jordan 2018). So the fountain's law for a crater's bubble, not the dipole's.
            float depth = MathF.Pow(rng.Uniform(), FallingWaterSynth.DepthSkew);
            if (depth >= 0.01f)
                place.Bubble(at + (int)(span * rng.Uniform()), FallingWaterSynth.MinnaertHzMetres / (mm * 1e-3f), FallingWaterSynth.BubbleDamping(mm),
                             FallingWaterSynth.BubblePascalsPerMm * mm * depth * SitePart, GlugRise);
        }
        // The crest spilling: spray off the jet, its energy the jet's kinetic energy over the burst.
        if (SprayPart > 0f && s.JetSpeed > SprayOnset)
        {
            // The water this burst carries, as if one lump: its splash's energy goes as its mass, and its
            // duration as its size, held to a lump's (a burst's spray is many small crests, not one).
            float water = s.Thickness * s.JetSpeed * MathF.Min(s.Strip, 1f) / rate * size;     // m^3
            float r = MathF.Cbrt(3f * MathF.Max(1e-12f, water) / (4f * MathF.PI));
            float rl = MathF.Min(r, 0.01f);
            var (sr, sd, sp) = FallingWaterSynth.Splash(rl, s.JetSpeed, SpillCrownShare);
            sp *= MathF.Pow(r / rl, 1.5f) * MathF.Min(1f, (s.JetSpeed - SprayOnset) / 0.5f);
            float fc = FallingWaterSynth.SplashCentreHz(rl, s.JetSpeed) * MathF.Exp(0.7f * Gauss(rng));
            fc = Math.Clamp(fc, 600f, 0.4f * _rate);
            place.Burst(at, sr, sd, sp * SprayPart, fc / 1.6f, MathF.Min(fc * 1.6f, 0.45f * _rate), steep: true);
        }
    }

    /// <summary>
    /// A bubble made at a depth under the free surface, ringing. A bubble just under a pressure-release
    /// surface radiates with its own image as a dipole (measured: Pumphrey and Crum 1990; under spilling
    /// breakers, Medwin and Beaky 1989), and a dipole's far field goes as 2 k z: for a bubble a
    /// centimetre down, 0.09 of its strength at 1 kHz and 0.85 at 10 kHz, so the shallow small bubbles of a
    /// riffle carry further than their size says (in air, small water features are bright: Watts et al.
    /// 2009). Its strength is the fountain's law for its size (FallingWaterSynth.BubblePascalsPerMm, ε R at
    /// a fixed wall amplitude); its depth is anywhere down to how far the site's jet drives the air,
    /// uniformly. The fountain's plunge drives bubbles deep, where k z nears one and the factor flattens.
    /// </summary>
    private void RingNear(EventSum place, EventSum rng, int at, float mm, float weight, float maxDepth, float rise)
    {
        if (mm < SmallestBubbleMm || weight <= 0f) return;
        float hz = FallingWaterSynth.MinnaertHzMetres / (mm * 1e-3f);
        if (hz > 0.45f * _rate) return;
        float z = maxDepth * rng.Uniform();
        float dipole = MathF.Min(1f, 2f * MathF.Tau * hz / SoundInWater * z);
        // Under a hundredth of its strength it is under everything round it.
        if (dipole < 0.01f) return;
        place.Bubble(at, hz, FallingWaterSynth.BubbleDamping(mm), FallingWaterSynth.BubblePascalsPerMm * mm * dipole * weight, rise);
    }

    /// <summary>A drop's blow on wet stone builds over this share of its r / v (FallingWaterSynth.HardCushion).</summary>
    private const float WetCushion = 0.2f;

    /// <summary>The speed of sound in water, m/s.</summary>
    private const float SoundInWater = 1480f;

    /// <summary>A site's bubble radius, mm: one of its own notes, or from the Deane-Stokes spectrum
    /// (R^-3/2 under its Hinze scale, R^-10/3 over it) up to the largest its cavity can hold.</summary>
    private static float DrawBubbleMm(EventSum rng, Site s)
    {
        if (rng.Uniform() < SiteNoteShare)
            return MathF.Max(SmallestBubbleMm, s.NoteMm * MathF.Exp(SiteNoteSpread * Gauss(rng)));
        double lo = Math.Log(SmallestBubbleMm), hi = Math.Log(s.LargestMm);
        double peak = Density(SmallestBubbleMm, s.HinzeMm) * SmallestBubbleMm;
        for (int tries = 0; tries < 48; tries++)
        {
            double rmm = Math.Exp(lo + (hi - lo) * rng.Uniform());
            if (rng.Uniform() * peak <= Density(rmm, s.HinzeMm) * rmm) return (float)rmm;
        }
        return SmallestBubbleMm;
    }

    private static double Density(double rmm, double hinzeMm)
        => rmm < hinzeMm ? Math.Pow(rmm, -1.5) : Math.Pow(hinzeMm, -1.5) * Math.Pow(rmm / hinzeMm, -10.0 / 3.0);

    /// <summary>A site's mean bubble volume, m³, for the mix of its notes and its spectrum.</summary>
    private static float MeanBubbleVolume(float hinzeMm, float largestMm, float noteMm)
    {
        double num = 0, den = 0;
        double lo = Math.Log(SmallestBubbleMm), hi = Math.Log(Math.Max(SmallestBubbleMm * 1.01, largestMm));
        for (int k = 0; k < 64; k++)
        {
            double rmm = Math.Exp(lo + (hi - lo) * (k + 0.5) / 64.0);
            double n = Density(rmm, hinzeMm) * rmm;      // per log step
            double r = rmm * 1e-3;
            num += n * r * r * r;
            den += n;
        }
        double spectrum = 4.0 / 3.0 * Math.PI * num / den;
        double note = 4.0 / 3.0 * Math.PI * Math.Pow(noteMm * 1e-3, 3) * Math.Exp(4.5 * SiteNoteSpread * SiteNoteSpread);
        return (float)(SiteNoteShare * note + (1 - SiteNoteShare) * spectrum);
    }

    // ── Drips ────────────────────────────────────────────────────────────────────────────────────

    private float _dripClock = -1f;

    /// <summary>A lip too slow to run: one drop every V / Q. A pendant drop lets go when its weight
    /// beats the surface tension round the lip, m g = 2π r γ f (Tate 1864, Harkins and Brown's f ≈
    /// 0.6), so a lip of a few millimetres lets go drops of a few tenths of a millilitre. Dripping is
    /// nearly periodic (Shaw 1984): the period jitters by a few per cent.</summary>
    private void Drips(float flow, float dt)
    {
        float lip = Spec.DripLipMm * 1e-3f;
        float volume = 2f * MathF.PI * lip * SurfaceTension * 0.6f / (WaterDensity * Hydraulics.Gravity);   // m^3
        float rate = flow * 1e-3f / volume;
        if (rate < 0.02f) return;
        if (_dripClock < 0f) _dripClock = _rng.Uniform() / rate;
        _dripClock -= dt;
        while (_dripClock <= 0f)
        {
            int at = Math.Clamp((int)((dt + _dripClock) * _rate), 0, Block - 1);
            _dripClock += (1f + 0.06f * Gauss(_rng)) / rate;
            Drop(at, volume);
        }
    }

    private void Drop(int at, float volume)
    {
        float r = MathF.Cbrt(3f * volume / (4f * MathF.PI));
        float v = FallingWaterSynth.ArrivalSpeed(r, Spec.DripFallMetres);
        int home = _open.Length > 1 ? 1 + (int)(_rng.Uniform() * (_open.Length - 1)) % (_open.Length - 1) : 0;
        int k = home != 0 && _rng.Uniform() < _toHome ? home : 0;
        var place = Spec.DripsInside && _inside != null ? _inside[k] : _open[k];
        float impact = (Spec.DripOnto == WaterSurface.Rock ? FallingWaterSynth.HardImpactPascals : FallingWaterSynth.ImpactPascals)
                       * MathF.Pow(r / 1e-3f * v / 5f, 1.5f) * DripPart;
        // Onto wet stone the blow builds through the film (as the falls' do). Into a puddle too it is not a
        // point: the air under the drop is squeezed out and a thin disc of it trapped, and the contact
        // spreads over the drop's tip (Thoroddsen et al. 2005, as RainSynth's click). A lone drip's
        // first-contact spike, one sample wide, measured a 10 ms kurtosis of 31: a digital tick on its
        // own, where in rain a thousand of them merge.
        float rise = MathF.Max(16e-6f, WetCushion * r / v);
        place.Impact(at, rise, (Spec.DripOnto == WaterSurface.Rock ? 0.4f : 1f) * r / v, impact * MathF.Sqrt(16e-6f / rise));
        if (Spec.DripOnto != WaterSurface.Pool) return;
        // Into a puddle: the crater closes on a bubble often enough to be the sound of it (Phillips,
        // Agarwal and Jordan 2018: the "plink" of a dripping tap is that bubble driving the surface).
        if (_rng.Uniform() < 0.6f)
            FallingWaterSynth.Ring(place, at + (int)(0.004f * _rate), r * 1e3f * (0.25f + 0.3f * _rng.Uniform()), DripPart);
    }

    // ── The cavity ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A tube's air column, as a delay line with its ends' reflections: a gully pot is open at its grate
    /// and closed by the water (a quarter-wave tube, its modes at the odd multiples of c / 4L'), a
    /// downpipe open at both ends (a half-wave tube, every multiple of c / 2L'). L' is the length with
    /// the open end's correction, 0.6 of its radius (Levine and Schwinger 1948). The open end lets the
    /// low notes back in more than the high ones (its reflection falls as (ka)² grows), which is the
    /// loss the loop has; the walls take a little more. Normalised so white noise in comes out at the
    /// same power, so a cavity colours the sound and does not make it louder.
    /// </summary>
    private sealed class Tube
    {
        private readonly float[] _line;
        private int _at;
        private readonly float _sign, _loopGain, _lp, _hp;
        private float _state, _hpIn, _hpOut;
        private readonly float _norm = 1f;

        public Tube(FlowCavity cavity, float sampleRate)
        {
            float a = 0.5f * MathF.Max(0.02f, cavity.DiameterMetres);
            float length = MathF.Max(0.05f, cavity.LengthMetres) + 0.6f * a * (cavity.FarEndOpen ? 2f : 1f);
            int delay = Math.Max(2, (int)MathF.Round(2f * length / 343f * sampleRate));
            _line = new float[delay];
            // Open end: −1; a water surface or a closed end: +1. Two open ends: +1 round the loop.
            _sign = cavity.FarEndOpen ? 1f : -1f;
            // The open end's reflection falls off above ka ≈ 1: a one-pole low-pass there in the loop,
            // and what leaves through it is high-passed below the same (radiation goes as (ka)²).
            float fc = Math.Min(343f / (MathF.Tau * a), 0.4f * sampleRate);
            _lp = MathF.Exp(-MathF.Tau * fc / sampleRate);
            _hp = MathF.Exp(-MathF.Tau * 0.5f * fc / sampleRate);
            // What a round trip keeps at low notes: the open end lets out (ka)²/2 of it (Levine and
            // Schwinger), a gully pot's wide grate a sixth at its 200 Hz mode, and the walls and the
            // water's ruffled surface take more. At 0.9 a glug that fell on a pot's mode stood 8 dB
            // over the pour for a second.
            _loopGain = 0.8f;
            // Normalised on white noise: a cavity colours what falls in it, it does not make it louder.
            var noise = new EventSum(sampleRate, 99);
            double inPower = 0, outPower = 0;
            for (int i = 0; i < 4 * delay + 16384; i++)
            {
                float x = noise.Signed();
                float y = Process(x);
                if (i >= 2 * delay) { inPower += x * x; outPower += y * y; }
            }
            _norm = outPower > 0 ? (float)Math.Sqrt(inPower / outPower) : 1f;
            Array.Clear(_line);
            _state = _hpIn = _hpOut = 0f;
            _at = 0;
        }

        public float Process(float x)
        {
            float back = _line[_at];
            _state = (1f - _lp) * back + _lp * _state;
            float y = x + _sign * _loopGain * _state;
            _line[_at] = y;
            if (++_at >= _line.Length) _at = 0;
            // One-pole high-pass: what the opening radiates.
            float h = _hp * (_hpOut + y - _hpIn);
            _hpIn = y;
            _hpOut = h;
            return h * _norm;
        }
    }
}
