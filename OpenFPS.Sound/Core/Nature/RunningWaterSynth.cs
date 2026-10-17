using System.Numerics;

namespace OpenFPS.Sound.Core.Nature;

/// <summary>
/// Water running in a channel and falling out of it, as the events it is made of (docs/RUNNING_WATER.md).
///
/// Water sliding smoothly over a bed is nearly silent in air: a stream, a gutter or a drain is heard where
/// its surface breaks. At every stone in a riffle (and every lip or leaf dam in a gutter) the flow drops
/// into the slower water in its lee as a small plunging jet that, fast enough, folds air under; the air
/// breaks into bubbles that ring at their Minnaert notes (Minnaert 1933; Leighton 1994). Each site is a
/// fixed place that sings its own notes (Leighton and Walton 1987), a share of its bubbles about its own
/// radius and the rest from the Deane and Stokes (2002) spectrum; and it breaks in bursts as its eddies
/// shed (St ≈ 0.2), of very uneven size: the occasional big gulp of air is the glug.
///
/// Its falls (<see cref="RunningWaterSpec.Falls"/>) are the fountain's physics, written into these places
/// through FallingWaterSynth.Placer; what lands inside a cavity (a gully pot, a pipe) is heard through a
/// tube that rings at its air modes (<see cref="Tube"/>). A flow too small to leave a lip as a stream drips,
/// one drop every V / Q (Tate's law).
///
/// Everything follows the flow: depth and speed from Manning's law (Hydraulics), each site's air from its
/// jet's speed, every rate from that. A rain-fed gutter follows the rain through its catchment (Runoff).
/// A line source is heard from places along it, each site at the place nearest it; a drain or a downpipe
/// from a ring of places round where its water lands. No place is a copy of another.
///
/// Fitted here and nowhere else: <see cref="BreakingAirShare"/>, <see cref="BurstShare"/>,
/// <see cref="BurstSpread"/>, <see cref="SiteNoteShare"/>. A bubble's loudness and the splash's
/// efficiency are the fountain's (FallingWaterSynth), not refitted here.
/// </summary>
public sealed partial class RunningWaterSynth
{
    // ── The fitted constants ─────────────────────────────────────────────────────────────────────

    /// <summary>The volume of air a breaking site drives under, per volume of water through it, for
    /// each unit its lee jet's Froude number stands over <see cref="JumpOnsetFroude"/>. The water drops
    /// into the lee and meets the slower water there in a small hydraulic jump, and a jump's air grows
    /// with its Froude number (Rajaratnam 1962; Wang and Chanson 2018 measured 24 % of the flow in a
    /// developed jump's shear layer and 50-60 % in all at Fr 6.6 and over). FITTED with the burst
    /// constants to eight recorded creeks and rivers (docs/RUNNING_WATER.md section 8): 0.004 left a few
    /// loud gurgles over silence. The creek's 40 L/s drives 1.3 % of its flow under as air, 54 dB(A) on
    /// its bank.</summary>
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
    /// for many are each too loud and the top octaves turn to grit, as the fountain's did.</summary>
    private const int MaxPerBurst = 16;
    private const int MaxSteadyPerBlock = 32;

    public readonly RunningWaterSpec Spec;
    private readonly float _rate;
    private readonly EventSum[] _open;
    private readonly EventSum[]? _inside;
    private readonly Tube[]? _tubes;
    private readonly EventSum _rng;
    private Site[] _sites = Array.Empty<Site>();
    /// <summary>A long line's, or a bed of riffles and pools (<see cref="FromTheBed"/>): its stones, from which each cut
    /// takes the sites it renders. Null: the sites are spread evenly along it once, as they always were.</summary>
    private readonly StreamBed? _bed;
    private readonly int _seed;
    private Dictionary<long, Site>? _byId;
    private readonly FallingWaterSynth? _falls;
    private readonly int[] _fallHome;
    private readonly float _referenceFlow;
    private float _tunedFlow = float.NaN;
    private int _untilBlock;

    /// <summary>The flow the water is carrying now, L/s. The voice sets it from the spec and the rain;
    /// it glides there over a second or two (a channel does not change its water at once).</summary>
    public float Flow;
    private float _flow = float.NaN;
    private Hydraulics.State _state;

    /// <summary>How much of it its outer places carry, 0 to 1 (ExtendedSources.Shares).</summary>
    public float Spread;
    private float _toHome;

    /// <summary>A line cut into pieces at the listener (ExtendedSources.Cut): every event is written into the
    /// place that plays the piece it happens in, whatever <see cref="Spread"/> says. Null: the places along it,
    /// shared by the spread.</summary>
    public ExtendedSources.LineCut? Cut;
    private ExtendedSources.LineCut? _homedBy;

    /// <summary>Each part's share, for the lab to take the sound apart: the breaking sites' bubbles,
    /// their spray, the falls, the drips. One in the game.</summary>
    public float SitePart = 1f, SprayPart = 1f, FallPart = 1f, DripPart = 1f;

    /// <summary>How many places it is heard from, its middle first.</summary>
    public int Places => _open.Length;

    private sealed class Site
    {
        public EventSum Rng = null!;
        public int Home, OwnHome;
        public float Along;           // m from the middle, along a line
        public float Drop;            // m: how far the water falls into its lee
        public float NoteMm;          // its characteristic bubble radius
        public float Strip;           // m: the width of flow it gathers
        public float Weight = 1f;     // how many real obstacles it stands for
        public long Id;               // a bed's stone (StreamBed.Stone.Id), or minus one plus a stand-in's place in cm
        public float FlowShare = 1f;  // the share of the line's flow that has reached it
        public float SlopeFactor = 1f; // how steep the water is here against the reach's mean (a riffle, a pool)
        public int Stratum, Strata = 1; // a stand-in: which of its piece's stand-ins, of how many (StandIn)
        public float BreakingShare = 1f; // a stand-in: the share of its stones that break now (StandIn)
        // From the flow (UpdateSites):
        public float JetSpeed, Froude, AirRate, BurstRate, HinzeMm, LargestMm, Thickness, Depth;
        public float NextBurst;       // s
        public float MeanVolume, VolumeHinze, VolumeLargest;   // the mean bubble volume, cached for these
    }

    /// <param name="places">How many streams it renders: its spec's places unless a line cut into pieces wants
    /// more (ExtendedSources.FlowPlaces).</param>
    public RunningWaterSynth(RunningWaterSpec spec, float sampleRate, int seed, int places = 0)
    {
        Spec = spec;
        _rate = sampleRate;
        places = places > 0 ? places : Math.Max(1, spec.Places);
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
        InitBasin(sampleRate, seed);
        if (spec.Channel == FlowChannel.KerbGutter)
        {
            _rain = new RainSynth[places];
            _rainGain = new float[places];
            for (int k = 0; k < places; k++) _rain[k] = new RainSynth(sampleRate, seed * 977 + 41 + k * 7919);
        }

        // The breaking sites: as many as the bed has, up to MaxSites, each standing for its share. A long line's, or a
        // bed of riffles and pools, are taken from its stones as it is cut (Allot), the whole of it until then.
        _seed = seed;
        var sites = new List<Site>();
        if (FromTheBed(spec))
        {
            _bed = new StreamBed(spec, seed);
            _sites = Array.Empty<Site>();
        }
        else if (spec.Obstacles is { } obs && spec.Channel != FlowChannel.None)
        {
            float real = MathF.Max(0f, obs.PerMetre * spec.LengthMetres);
            int n = Math.Clamp((int)MathF.Round(real), real > 0f ? 1 : 0, MaxSites);
            for (int i = 0; i < n; i++)
            {
                var rng = EventSum.ForDraws(sampleRate, seed * 31 + 1000 + i * 7);
                // Spread evenly along the length (jittered), so every place has its share of them.
                float along = ((i + 0.5f + 0.8f * (rng.Uniform() - 0.5f)) / n - 0.5f) * spec.LengthMetres;
                var site = new Site
                {
                    Rng = rng,
                    Home = HomeOf(along),
                    Along = along,
                    Drop = obs.MedianDropMetres * MathF.Exp(obs.DropSpread * Gauss(rng)),
                    Strip = obs.WidthMetres * (0.6f + 0.8f * rng.Uniform()),
                    Weight = real / n,
                };
                site.NextBurst = rng.Uniform() * 0.5f;
                site.OwnHome = site.Home;
                sites.Add(site);
            }
        }
        if (_bed == null) _sites = sites.ToArray();

        // The falls, through the fountain's physics, written into these places.
        _fallHome = new int[spec.Falls.Length];
        _fallOwnHome = new int[spec.Falls.Length];
        _fallAlong = new float[spec.Falls.Length];
        if (spec.Falls.Length > 0)
        {
            var falls = new WaterFallSpec[spec.Falls.Length];
            var taps = new WaterTapSpec[spec.Falls.Length];
            for (int i = 0; i < falls.Length; i++)
            {
                falls[i] = FallFor(spec, spec.Falls[i], _referenceFlow) with { Tap = i, Onto = spec.Falls[i].Onto };
                taps[i] = new WaterTapSpec { Name = spec.Falls[i].Name };
                // A line's falls are spread along it; a ring's land in its middle and are scattered by
                // the placer below.
                _fallAlong[i] = ((i + 0.5f) / falls.Length - 0.5f) * spec.LengthMetres;
                _fallHome[i] = _fallOwnHome[i] = spec.Layout == FlowLayout.Line && places > 1 ? HomeOf(_fallAlong[i]) : -1;
            }
            var feature = new WaterFeatureSpec { Name = spec.Name, Falls = falls, Taps = taps, SourceLevelDb = spec.SourceLevelDb };
            // Nothing falls until the first control call says how much is running.
            _falls = new FallingWaterSynth(feature, sampleRate, seed * 13 + 5) { Wind = 0f, FlowScale = 0f, HardCushion = WetCushion };
            _falls.Placer = PlaceForFall;
        }
        // Each stone's own note is set by the water it was made for, whatever is running when the voice
        // starts; the flow itself starts where the first control call finds it.
        if (_bed != null) Allot(null);
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

    /// <summary>How one of this source's falls arrives at a flow (<see cref="Shape"/>).</summary>
    public readonly record struct FallShape(float FlowLitresPerSecond, float FallMetres, float DropShare, float MeanDropRadiusMm, float ChunkRadiusMm);

    /// <summary>
    /// How one of this source's falls arrives at this flow: the sheet leaving the lip, its thickness from
    /// the weir law, and from that how it lands. A thin sheet fingers into strands that bead into drops;
    /// a thick one falls coherent, in lumps the size of its strands (Rayleigh-Plateau: about 1.9 strand
    /// diameters apart). So the same lip at twice the flow throws a thicker sheet, more of it coherent
    /// and in bigger lumps, which plunge and drive air under where drops would not: a fall grows faster
    /// than its flow (Watts et al. 2009: a weir 6 dB(A) quieter at half its flow). Computed afresh as the
    /// flow changes: fixed at the reference flow with only the rates scaled, a fall grew 3 dB a doubling.
    /// </summary>
    public static FallShape Shape(RunningWaterSpec spec, FlowFall fall, float flowLitresPerSecond)
    {
        float q = MathF.Max(1e-6f, flowLitresPerSecond * fall.FlowShare);
        float head = Hydraulics.WeirHead(q, fall.LipWidthMetres);
        float speed = MathF.Max(0.1f, Hydraulics.LipSpeed(head));
        float thickness = q * 1e-3f / (speed * MathF.Max(0.01f, fall.LipWidthMetres));      // m
        // Under about 2 mm a falling sheet tears into strands and beads before it lands (the fountain
        // bowl's 30 µm sheet arrives as drops); over about 6 mm it holds together. A film held to a wall
        // has had nowhere to tear: it arrives whole whatever its thickness.
        float coherent = fall.Film ? 1f : Math.Clamp((thickness * 1e3f - 2f) / 4f, 0f, 1f);
        float strandMm = Math.Clamp(thickness * 1e3f, 1f, 12f);
        // A film down a pipe does not fall freely: the wall holds it to a terminal speed after a few
        // metres, 3.9 m/s at 1 L/s in a 110 mm pipe and as (Q/D)^0.4 (Wyly and Eaton 1961; Aoki, Öhler and
        // Kaltbeitzel 2025). It strikes the bend as if it had fallen v²/2g, never more than the pipe; and
        // water leaving the pipe's foot (a downpipe's shoe) is already moving that fast before it falls.
        float fallMetres = fall.DropMetres;
        if ((fall.Film || fall.FromPipe) && spec.Cavity is { } pipe)
        {
            float terminal = TerminalFilmSpeed(q, pipe.DiameterMetres);
            float asIfFallen = MathF.Min(pipe.LengthMetres, terminal * terminal / (2f * Hydraulics.Gravity));
            fallMetres = fall.Film ? MathF.Min(fall.DropMetres, asIfFallen) : fall.DropMetres + asIfFallen;
        }
        return new FallShape(q, fallMetres, Math.Clamp(1f - 0.85f * coherent, 0.1f, 1f),
                             Math.Clamp(0.6f * strandMm, 0.8f, 2.2f), Math.Clamp(0.95f * strandMm, 1.5f, 8f));
    }

    /// <summary>The speed a film reaches down a vertical pipe, m/s (see <see cref="Shape"/>).</summary>
    public static float TerminalFilmSpeed(float litresPerSecond, float diameterMetres)
        => 3.9f * MathF.Pow(MathF.Max(0f, litresPerSecond) * 0.110f / MathF.Max(0.02f, diameterMetres), 0.4f);

    /// <summary>The fountain's fall for one of this source's falls at this flow, as a spec (for the
    /// synth's construction and the lab).</summary>
    public static WaterFallSpec FallFor(RunningWaterSpec spec, FlowFall fall, float flowLitresPerSecond)
    {
        var f = fall.Feed is FallFeed.TapOntoBasin or FallFeed.TapIntoWater && spec.Tap is { } tap
            ? TapShape(tap, flowLitresPerSecond, 0f) : Shape(spec, fall, flowLitresPerSecond);
        return new WaterFallSpec
        {
            Name = fall.Name,
            FlowLitresPerSecond = f.FlowLitresPerSecond,
            FallMetres = f.FallMetres,
            DropShare = f.DropShare,
            MeanDropRadiusMm = f.MeanDropRadiusMm,
            ChunkRadiusMm = f.ChunkRadiusMm,
            Streams = Math.Max(1, fall.Streams),
            Onto = fall.Onto,
            DriftPerMetrePerSecond = 0f,
        };
    }

    private int _nextRingHome;
    private readonly int[] _fallOwnHome;
    private readonly float[] _fallAlong;

    /// <summary>Every site and fall of a line to the place playing the piece it is in (or back to its own place
    /// along the line when there is no cut).</summary>
    private void HomeBy(ExtendedSources.LineCut? cut)
    {
        _homedBy = cut;
        int places = _open.Length;
        if (_bed != null)
        {
            Allot(cut);
            // A new stone's note is its water's at the flow its bed was made for, as the first ones' were.
            UpdateFlow(MathF.Max(0f, Spec.ReferenceFlow));
            UpdateFlow(float.IsNaN(_flow) ? MathF.Max(0f, Spec.ReferenceFlow) : _flow);
        }
        else foreach (var s in _sites) s.Home = cut == null ? s.OwnHome : Math.Min(places - 1, cut.PlaceAt(s.Along));
        for (int i = 0; i < _fallHome.Length; i++)
            _fallHome[i] = cut == null || _fallOwnHome[i] < 0 ? _fallOwnHome[i] : Math.Min(places - 1, cut.PlaceAt(_fallAlong[i]));
    }

    // ── A long line's stones (2026-10-17) ───────────────────────────────────────────────────────

    /// <summary>A line longer than this is too long for one set of sites spread evenly along it: beside you a piece
    /// half a metre long would hold none of them. The creek preset (16 m), the gutter (15 m) and a drainage line's
    /// stretch (20 m) are under it.</summary>
    public const float LongLineMetres = 40f;

    /// <summary>A piece whose bed holds no more stones than this on the mean has each rendered (the near pieces); one
    /// with more is heard from a few that stand for all of them, sizes drawn evenly through the bed's spread.</summary>
    public const int StonesListedUpTo = 6;

    /// <summary>
    /// A far piece is never rendered stone by stone: it is heard from at most <see cref="StandInsAPiece"/> sites, and a
    /// line from at most <see cref="MostStandIns"/>, each standing for its share of the piece's stones. Such a site
    /// bursts at most <see cref="MostBurstsPerSecond"/> times a second with at most <see cref="StandInBubbles"/> bubbles
    /// a burst, and lets out at most one bubble of its steady trickle a block, each burst, gulp, spray and bubble as loud
    /// as the many it stands for together. Far off and among the rest of the line, the grain this leaves is under its
    /// distance; the cost is a few creeks' (--water-cost line).
    /// </summary>
    public const int StandInsAPiece = 3, MostStandIns = 16, StandInBubbles = 8;
    public const float MostBurstsPerSecond = 40f;

    /// <summary>
    /// Whether a line's sites come from its bed's stones as it is cut (<see cref="Allot"/>): a line longer than
    /// <see cref="LongLineMetres"/>, a bed of riffles and pools or steps, or a line that gathers its water along it.
    /// Everything else keeps the sites it was made with, spread evenly along it.
    /// </summary>
    public static bool FromTheBed(RunningWaterSpec spec)
        => spec.Layout == FlowLayout.Line && spec.Channel != FlowChannel.None && spec.Obstacles is { PerMetre: > 0f }
           && (spec.LengthMetres > LongLineMetres || spec.Bed != BedForm.Even || spec.HeadShare < 0.999f);

    // What Allot works with, kept so a recut as the listener walks allocates only the stones new to it.
    private readonly List<StreamBed.Stone> _stones = new();
    private readonly float[] _from = new float[ExtendedSources.MaxPlaces], _to = new float[ExtendedSources.MaxPlaces];
    private readonly double[] _count = new double[ExtendedSources.MaxPlaces];
    private readonly int[] _standIns = new int[ExtendedSources.MaxPlaces];
    private readonly bool[] _listed = new bool[ExtendedSources.MaxPlaces];
    private readonly List<Site> _made = new(MaxSites);
    private Dictionary<long, Site> _spare = new();

    /// <summary>
    /// The sites a cut is heard from: in each piece whose bed holds few stones (those beside you) every stone, each
    /// where it is, the fewest first while the sites last; in the rest a few stand-ins (by the square root of how many
    /// it holds, <see cref="StandInsAPiece"/> at most), each standing for its share, spread evenly through where the
    /// stones lie and how big they are. Each site's water is the share of the line's that has reached it, on the slope
    /// of its riffle or pool. A stone already rendered keeps its own state when the cut moves on.
    /// </summary>
    private void Allot(ExtendedSources.LineCut? cut)
    {
        var bed = _bed!;
        var obs = Spec.Obstacles!;
        float length = bed.Length, half = 0.5f * length;
        int pieces = Math.Min(cut?.Count ?? 1, ExtendedSources.MaxPlaces);
        // The cut's metres are its path's; the bed's are the spec's, within the rounding of its length.
        float scale = cut != null && cut.Length > 0f ? length / cut.Length : 1f;
        int wanting = 0;
        for (int i = 0; i < pieces; i++)
        {
            _from[i] = cut == null ? 0f : (cut.Edges[i] - cut.Edges[0]) * scale;
            _to[i] = cut == null ? length : (cut.Edges[i + 1] - cut.Edges[0]) * scale;
            _count[i] = bed.CountBetween(_from[i], _to[i]);
            _listed[i] = false;
            _standIns[i] = 0;
            if (_count[i] > 0) wanting++;
        }
        // The near pieces stone by stone, the fewest first, each piece still waiting keeping a site for itself.
        int used = 0;
        for (int pass = 0; pass < pieces; pass++)
        {
            int i = -1;
            for (int j = 0; j < pieces; j++)
                if (!_listed[j] && _count[j] > 0 && _count[j] <= StonesListedUpTo && _standIns[j] == 0 && (i < 0 || _count[j] < _count[i])) i = j;
            if (i < 0) break;
            _stones.Clear();
            bed.StonesBetween(_from[i], _to[i], _stones);
            if (used + _stones.Count > MaxSites - (wanting - 1)) { _standIns[i] = -1; continue; }
            _listed[i] = true;
            wanting--;
            used += _stones.Count;
        }
        // The rest stood in for, a few each and not too many in all.
        int standIns = 0;
        for (int i = 0; i < pieces; i++)
        {
            _standIns[i] = 0;
            if (_listed[i] || _count[i] <= 0) continue;
            _standIns[i] = (int)Math.Clamp(Math.Round(0.5 * Math.Sqrt(_count[i])), 1, Math.Min(StandInsAPiece, Math.Ceiling(_count[i])));
            standIns += _standIns[i];
        }
        int most = Math.Min(MostStandIns, Math.Max(wanting, MaxSites - used));
        while (standIns > most)
        {
            int widest = -1;
            for (int i = 0; i < pieces; i++)
                if (_standIns[i] > 1 && (widest < 0 || _standIns[i] > _standIns[widest])) widest = i;
            if (widest < 0) break;
            _standIns[widest]--;
            standIns--;
        }
        var known = _byId;
        var next = _spare;
        next.Clear();
        _made.Clear();
        for (int i = 0; i < pieces; i++)
        {
            if (_listed[i])
            {
                _stones.Clear();
                bed.StonesBetween(_from[i], _to[i], _stones);
                foreach (var st in _stones)
                    Take(st.Id, st.At, obs.MedianDropMetres * MathF.Exp(obs.DropSpread * NormalQuantile(st.SizeDraw)),
                         obs.WidthMetres * (0.6f + 0.8f * st.WidthDraw), 1f, i, 0, 1);
                continue;
            }
            int k = _standIns[i];
            for (int j = 0; j < k; j++)
            {
                // Where they lie, evenly through the stones; how big, each its share of those that break as the water now
                // runs (StandIn), in another order than where they lie so the biggest are not always the furthest.
                float at = bed.PointAfter(_from[i], (j + 0.5) / k * _count[i], _to[i]);
                float z = k == 1 ? 0.5f * obs.DropSpread : obs.DropSpread * NormalQuantile(((j + k / 2) % k + 0.5f) / k);
                Take(-1 - (long)MathF.Round(at * 100f), at, obs.MedianDropMetres * MathF.Exp(z), obs.WidthMetres, (float)(_count[i] / k), i, (j + k / 2) % k, k);
            }
        }
        _sites = _made.ToArray();
        _spare = known ?? new Dictionary<long, Site>();
        _byId = next;

        void Take(long id, float at, float drop, float strip, float weight, int piece, int stratum, int strata)
        {
            if (known == null || !known.TryGetValue(id, out var site))
            {
                var rng = EventSum.ForDraws(_rate, unchecked(_seed * 31 + 1000 + (int)(id ^ (id >> 32)) * 7));
                site = new Site { Rng = rng, Id = id, Drop = drop, Strip = strip, NextBurst = rng.Uniform() * 0.5f };
            }
            site.Along = at - half;
            site.Weight = weight;
            site.Stratum = stratum;
            site.Strata = strata;
            site.FlowShare = Spec.HeadShare + (1f - Spec.HeadShare) * Math.Clamp(at / length, 0f, 1f);
            site.SlopeFactor = bed.SlopeAt(at);
            site.OwnHome = HomeOf(site.Along);
            site.Home = cut == null ? site.OwnHome : Math.Min(_open.Length - 1, cut.PlaceOfPiece(piece));
            next[id] = site;
            _made.Add(site);
        }
    }

    /// <summary>The bed's stone sizes as quantiles of its spread, for <see cref="MeanBreaking"/>: the middles of 32 equal
    /// shares of the normal.</summary>
    private static readonly float[] SizeQuantiles = Enumerable.Range(0, 32).Select(m => NormalQuantile((m + 0.5f) / 32f)).ToArray();

    private readonly float[] _breaking = new float[32];

    /// <summary>
    /// A stand-in in this water (its speed, depth, the flow through a stone's strip and the strip): how far its stones'
    /// lee jets stand over the undular jump's Froude number on the mean over the bed's spread of sizes (the factor of
    /// a site's air in <see cref="UpdateFlow"/>), and its size set to its share of those that break, by the air each
    /// size drives under: the j-th of k in its piece the size at the middle of the j-th k-th of the air.
    /// </summary>
    private float StandIn(Site s, float v, float y, float q, float strip)
    {
        var obs = Spec.Obstacles!;
        float sum = 0f;
        for (int m = 0; m < SizeQuantiles.Length; m++)
        {
            float size = obs.MedianDropMetres * MathF.Exp(obs.DropSpread * SizeQuantiles[m]);
            float drop = size * y / (size + y);
            float jet = MathF.Sqrt(v * v + 2f * Hydraulics.Gravity * drop);
            float thickness = q / (jet * strip);
            sum += _breaking[m] = MathF.Max(0f, jet / MathF.Sqrt(Hydraulics.Gravity * MathF.Max(1e-4f, thickness)) - JumpOnsetFroude);
        }
        int breaking = 0;
        foreach (float b in _breaking) if (b > 0f) breaking++;
        s.BreakingShare = breaking / (float)SizeQuantiles.Length;
        if (sum > 0f)
        {
            float wanted = (s.Stratum + 0.5f) / MathF.Max(1, s.Strata) * sum, upTo = 0f;
            for (int m = 0; m < SizeQuantiles.Length; m++)
            {
                upTo += _breaking[m];
                if (upTo < wanted && m < SizeQuantiles.Length - 1) continue;
                s.Drop = obs.MedianDropMetres * MathF.Exp(obs.DropSpread * SizeQuantiles[m]);
                break;
            }
        }
        return sum / SizeQuantiles.Length;
    }

    /// <summary>The standard normal's quantile (Acklam's rational approximation, relative error under 1.2e-9).</summary>
    internal static float NormalQuantile(float p)
    {
        double q = Math.Clamp(p, 1e-6, 1 - 1e-6);
        ReadOnlySpan<double> a = stackalloc double[] { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        ReadOnlySpan<double> b = stackalloc double[] { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        ReadOnlySpan<double> c = stackalloc double[] { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        ReadOnlySpan<double> d = stackalloc double[] { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        if (q < 0.02425)
        {
            double r = Math.Sqrt(-2 * Math.Log(q));
            return (float)((((((c[0] * r + c[1]) * r + c[2]) * r + c[3]) * r + c[4]) * r + c[5]) / ((((d[0] * r + d[1]) * r + d[2]) * r + d[3]) * r + 1));
        }
        if (q > 1 - 0.02425)
        {
            double r = Math.Sqrt(-2 * Math.Log(1 - q));
            return -(float)((((((c[0] * r + c[1]) * r + c[2]) * r + c[3]) * r + c[4]) * r + c[5]) / ((((d[0] * r + d[1]) * r + d[2]) * r + d[3]) * r + 1));
        }
        double u = q - 0.5, t = u * u;
        return (float)((((((a[0] * t + a[1]) * t + a[2]) * t + a[3]) * t + a[4]) * t + a[5]) * u / (((((b[0] * t + b[1]) * t + b[2]) * t + b[3]) * t + b[4]) * t + 1));
    }

    /// <summary>For the lab and the tests: the sites rendered now, where each is along the line (m from its middle), how
    /// many stones it stands for, and the air it drives under now (m³/s, its stones' together).</summary>
    public IReadOnlyList<(float Along, float Weight, float Air, int Home)> Sites
        => _sites.Select(s => (s.Along, s.Weight, s.AirRate, s.Home)).ToList();

    /// <summary>The cut this line is played by now, if any: one with no more pieces than it has places.</summary>
    private ExtendedSources.LineCut? CutNow
        => Cut is { } cut && Spec.Layout == FlowLayout.Line && cut.Count <= _open.Length ? cut : null;

    /// <summary>Where an event of fall <paramref name="tap"/> is written: its home place with the spread,
    /// otherwise the middle; inside the cavity if it lands there.</summary>
    private EventSum PlaceForFall(int tap)
    {
        if (PlateDriveFor(tap) is { } plate) return plate;
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
        // A channel takes a second or two to change its water, a tap a fraction of one; in proportion, so
        // a trickle and a torrent settle as fast as each other.
        _flow += (target - _flow) * MathF.Min(1f, dt / (Spec.Tap != null ? 0.25f : 1.5f));
        UpdateFlow(_flow);
        BasinControl(dt);
        PlateControl();
        if (_falls != null)
        {
            // Too little to leave its lip as a stream, it drips instead (Drips); what drains through an
            // inlet is the basin's own business and goes on.
            bool dripping = Spec.DripLipMm > 0f && _flow < JetOnsetLitresPerSecond;
            _falls.FlowScale = 1f;
            // Each fall as it arrives at this flow, re-worked when the flow has moved a few per cent.
            float signature = (dripping ? 0f : _flow) + 50f * _level + 3f * _inletFlow;
            if (!(MathF.Abs(signature - _tunedFlow) <= 0.03f * _tunedFlow))
            {
                _tunedFlow = signature;
                for (int i = 0; i < Spec.Falls.Length; i++)
                {
                    var fall = Spec.Falls[i];
                    float q = dripping && fall.Feed != FallFeed.Drain ? 0f : FallFlow(i);
                    var f = fall.Feed is FallFeed.TapOntoBasin or FallFeed.TapIntoWater && Spec.Tap is { } tap
                        ? TapShape(tap, _flow, _level) with { FlowLitresPerSecond = q }
                        : Shape(Spec, fall, q);
                    if (q <= 0f) f = f with { FlowLitresPerSecond = 0f };
                    _falls.Retune(i, f.FlowLitresPerSecond, f.DropShare, f.MeanDropRadiusMm, f.ChunkRadiusMm, f.FallMetres);
                }
            }
            _falls.ImpactPart = _falls.DropBubblePart = _falls.LumpBubblePart = _falls.PlungePart = _falls.SplashPart = FallPart;
            _falls.Control(dt);
        }
        int n = _open.Length;
        Span<float> shares = stackalloc float[n];
        var cut = CutNow;
        if (!ReferenceEquals(cut, _homedBy)) HomeBy(cut);
        if (cut != null)
        {
            // Cut into pieces: each event in the piece it happens in, and each place its piece's share.
            for (int k = 0; k < n; k++) shares[k] = cut.ShareOfPlace(k);
            _toHome = 1f;
        }
        else
        {
            ExtendedSources.Shares(n > 1 ? Spread : 0f, shares);
            // An event whose home is an outer place goes there with probability s: that is what makes each
            // outer place carry s/n of the power and the middle the rest (ExtendedSources.Shares).
            _toHome = Math.Clamp(Spread, 0f, 1f);
        }
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
        float v0 = _state.SpeedMetresPerSecond, y0 = _state.DepthMetres;
        foreach (var s in _sites)
        {
            float v = v0, y = y0, wetted = _state.WettedWidthMetres, here = flow;
            if (s.FlowShare != 1f || s.SlopeFactor != 1f)
            {
                // Its own water: the share of the line's that has reached it, in a bed as much narrower as the water is
                // less (w as Q^0.5, Leopold and Maddock 1953), on its riffle's or pool's slope. Manning's law in a wide
                // channel then gives depth as Q^0.3 S^-0.3 and speed as Q^0.2 S^0.3.
                float share = MathF.Max(1e-3f, s.FlowShare), steep = MathF.Max(1e-3f, s.SlopeFactor);
                here = flow * share;
                y *= MathF.Pow(share, 0.3f) * MathF.Pow(steep, -0.3f);
                v *= MathF.Pow(share, 0.2f) * MathF.Pow(steep, 0.3f);
                wetted *= MathF.Sqrt(share);
            }
            if (here <= 0f || y <= 0f) { s.AirRate = 0f; s.BurstRate = 0f; continue; }
            // The flow through the site: its strip of the channel, never more than all of it.
            float strip = MathF.Min(s.Strip, MathF.Max(0.01f, wetted));
            float q = MathF.Min(here * 1e-3f, v * y * strip);          // m^3/s
            // A stand-in drives under what its stones do on the mean, their sizes taken through the bed's whole spread,
            // and is one of the stones that do it: near the onset only the bigger stones break, and a few sizes drawn
            // through the spread would miss or overstate that tail.
            float breaking = s.Id < 0 ? StandIn(s, v, y, q, strip) : float.NaN;
            // The surface drops into the lee by the obstacle's height, or by the depth when the obstacle
            // stands out of the water (the water goes round it and falls back behind).
            float drop = s.Drop * y / (s.Drop + y);
            float jet = MathF.Sqrt(v * v + 2f * Hydraulics.Gravity * drop);
            s.JetSpeed = jet;
            s.Thickness = q / (jet * strip);
            // The lee jet meets the water behind the obstacle in a small jump: it breaks, and drives air
            // under, by how far its Froude number stands over the undular jump's.
            s.Froude = jet / MathF.Sqrt(Hydraulics.Gravity * MathF.Max(1e-4f, s.Thickness));
            float air = q * BreakingAirShare * (float.IsNaN(breaking) ? MathF.Max(0f, s.Froude - JumpOnsetFroude) : breaking) * s.Weight;   // m^3/s
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

    /// <summary>The lab's levers on this water (LabLevers.WaterAsNoise): its mixer's, which gives it them as it
    /// makes the voice, or its instrument's. The game's when made alone.</summary>
    public LabLevers Lab { get; set; } = new();

    /// <summary>The next sample at each place, pascals at a metre from it. Their sum is the source.</summary>
    public void NextPlaces(Span<float> places)
    {
        if (Lab.WaterAsNoise)
        {
            var cut = _homedBy;
            for (int k = 0; k < _open.Length && k < places.Length; k++)
                places[k] = 0.1732f * (cut == null ? 1f : MathF.Sqrt(cut.ShareOfPlace(k) * _open.Length)) * _open[k].Signed();
            return;
        }
        Step();
        for (int k = 0; k < _open.Length; k++)
        {
            float y = _open[k].Next() + RainAt(k);
            if (_inside != null) y += _tubes![k].Process(_inside[k].Next());
            // The basin's bottom is one plate and rings as one: at the middle.
            if (k == 0) y += PlateSample();
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
        return y + PlateSample();
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
        Gulps(dt);
        Fizz(dt);
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
            // A stand-in (Allot) lets out one bubble a block for all its stones' trickle.
            int n = Math.Min(real, s.Id < 0 ? 1 : MaxSteadyPerBlock);
            float w = MathF.Sqrt(real / (float)n) * SitePart;
            for (int b = 0; b < n; b++)
                RingNear(PlaceForSite(s), rng, (int)(rng.Uniform() * Block), DrawBubbleMm(rng, s), w, s.Depth, FallingWaterSynth.BubbleRise);
        }
        // The bursts: one per eddy shed, each its own share of the air.
        s.NextBurst -= dt;
        while (s.NextBurst <= 0f)
        {
            // A stand-in's stones that do not break shed no bursts.
            float rate = MathF.Max(0.05f, s.BurstRate * s.Weight * (s.Id < 0 ? s.BreakingShare : 1f));
            // A site standing for many stones bursts no oftener than this, each burst standing for several.
            float stands = s.Id < 0 && rate > MostBurstsPerSecond ? rate / MostBurstsPerSecond : 1f;
            rate /= stands;
            int at = Math.Clamp((int)((dt + s.NextBurst) * _rate), 0, Block - 1);
            // Eddies are shed nearly in time, not at random: a jittered period.
            s.NextBurst += (0.6f + 0.8f * rng.Uniform()) / rate;
            Burst(s, at, rate * stands, meanVolume, stands);
        }
    }

    /// <param name="stands">How many bursts this one stands for: each is one burst's, as loud as that many together.</param>
    private void Burst(Site s, int at, float rate, float meanVolume, float stands = 1f)
    {
        var rng = s.Rng;
        // This burst's air: its mean share, log-normally uneven (mean one).
        float size = MathF.Exp(BurstSpread * Gauss(rng) - 0.5f * BurstSpread * BurstSpread);
        float air = BurstShare * s.AirRate / rate * size;
        float louder = stands > 1f ? MathF.Sqrt(stands) : 1f;
        int count = rng.Poisson(air / meanVolume);
        var place = PlaceForSite(s);
        int span = (int)(BurstSeconds * MathF.Sqrt(MathF.Max(0.2f, s.Drop / 0.05f)) * _rate);
        if (count > 0)
        {
            int n = Math.Min(count, s.Id < 0 ? StandInBubbles : MaxPerBurst);
            float w = MathF.Sqrt(count / (float)n) * SitePart * louder;
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
                             FallingWaterSynth.BubblePascalsPerMm * mm * depth * SitePart * louder, GlugRise);
        }
        // The crest spilling: spray off the jet, its energy the jet's kinetic energy over the burst.
        if (SprayPart > 0f && s.JetSpeed > SprayOnset)
        {
            // The water this burst carries, as if one lump: its splash's energy goes as its mass, and its
            // duration as its size, held to a lump's (a burst's spray is many small crests, not one).
            // A bed's stones (Allot) burst each as one stone: its water over its own shedding, whatever the site stands
            // for. The sites spread evenly along a stretch keep the share of it they always had.
            float water = s.Thickness * s.JetSpeed * MathF.Min(s.Strip, 1f) / (_bed != null ? MathF.Max(0.05f, s.BurstRate) : rate) * size;     // m^3
            float r = MathF.Cbrt(3f * MathF.Max(1e-12f, water) / (4f * MathF.PI));
            float rl = MathF.Min(r, 0.01f);
            var (sr, sd, sp) = FallingWaterSynth.Splash(rl, s.JetSpeed, SpillCrownShare);
            sp *= MathF.Pow(r / rl, 1.5f) * MathF.Min(1f, (s.JetSpeed - SprayOnset) / 0.5f);
            float fc = FallingWaterSynth.SplashCentreHz(rl, s.JetSpeed) * MathF.Exp(0.7f * Gauss(rng));
            fc = Math.Clamp(fc, 600f, 0.4f * _rate);
            place.Burst(at, sr, sd, sp * SprayPart * louder, fc / 1.6f, MathF.Min(fc * 1.6f, 0.45f * _rate), steep: true);
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
        int home = _homedBy is { } cut ? Math.Min(_open.Length - 1, cut.PlaceAt((_rng.Uniform() - 0.5f) * Spec.LengthMetres))
                 : _open.Length > 1 ? 1 + (int)(_rng.Uniform() * (_open.Length - 1)) % (_open.Length - 1) : 0;
        int k = home != 0 && _rng.Uniform() < _toHome ? home : 0;
        var place = Spec.DripsInside && _inside != null ? _inside[k] : _open[k];
        // Under a tap the drip lands on whatever the basin is now: its bare bottom (and a steel one rings
        // for it), or the water standing in it.
        var onto = Spec.DripOnto;
        if (Spec.Basin != null)
        {
            onto = _level > 0.003f ? WaterSurface.Pool : WaterSurface.Rock;
            if (onto == WaterSurface.Rock && _plateDrive != null) place = _plateDrive;
        }
        float impact = (onto == WaterSurface.Rock ? FallingWaterSynth.HardImpactPascals : FallingWaterSynth.ImpactPascals)
                       * MathF.Pow(r / 1e-3f * v / 5f, 1.5f) * DripPart;
        // Onto wet stone the blow builds through the film (as the falls' do), and into a puddle the air
        // squeezed out under the drop spreads the contact (Thoroddsen et al. 2005, as RainSynth's click): a
        // lone drip's one-sample spike measured a 10 ms kurtosis of 31, a digital tick. On wet steel the film
        // is tens of microns and the blow keeps its sharp first contact, which rings the plate's upper modes.
        float rise = MathF.Max(16e-6f, (place == _plateDrive ? PlateCushion : WetCushion) * r / v);
        place.Impact(at, rise, (onto == WaterSurface.Rock ? 0.4f : 1f) * r / v, impact * MathF.Sqrt(16e-6f / rise));
        if (onto != WaterSurface.Pool) return;
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
    /// low notes back in and the high ones out: an unflanged pipe's reflection falls as e^(−(ka)²/2)
    /// [recalled; close to Levine and Schwinger's below ka of about 1.5], to a third at ka 1.5 and to
    /// almost nothing by ka 3, so a round trip through an open end keeps e^(−(ka)²/2) and one through
    /// both e^(−(ka)²). That is the loss the loop has, as a cascade of four one-pole low-passes (their
    /// (1 + (f/f1)²)^-2 is e^(−2 (f/f1)²) where it matters); the walls and the water take a little more.
    /// Less loss than that and a downpipe hands its splashes back every round trip, which Cody heard as
    /// flanging (docs/RUNNING_WATER.md 11.1). Normalised on white noise: a cavity colours the sound and
    /// does not make it louder.
    /// </summary>
    private sealed class Tube
    {
        private readonly float[] _line;
        private int _at;
        private readonly float _sign, _loopGain, _lp, _hp;
        private float _s1, _s2, _s3, _s4, _hpIn, _hpOut;
        private readonly float _norm = 1f;

        public Tube(FlowCavity cavity, float sampleRate)
        {
            float a = 0.5f * MathF.Max(0.02f, cavity.DiameterMetres);
            float length = MathF.Max(0.05f, cavity.LengthMetres) + 0.6f * a * (cavity.FarEndOpen ? 2f : 1f);
            // Open end: −1; a water surface or a closed end: +1. Two open ends: +1 round the loop.
            _sign = cavity.FarEndOpen ? 1f : -1f;
            // ka = 1 here. A round trip keeps e^(−n (f/fk)²/2), n the open ends it meets; four one-poles at
            // f1 = fk √(4 / n) give e^(−2 (f/f1)²), the same.
            float fk = 343f / (MathF.Tau * a);
            float openEnds = cavity.FarEndOpen ? 2f : 1f;
            float f1 = Math.Min(fk * MathF.Sqrt(4f / openEnds), 0.4f * sampleRate);
            _lp = MathF.Exp(-MathF.Tau * f1 / sampleRate);
            // What leaves through the opening is high-passed below ka of a half (radiation goes as (ka)²).
            _hp = MathF.Exp(-MathF.Tau * 0.5f * fk / sampleRate);
            // The four poles hold the low notes back by 4 / (2π f1); the line is that much shorter, so
            // the modes stay where the length puts them.
            float poles = 4f / (MathF.Tau * f1) * sampleRate;
            int delay = Math.Max(2, (int)MathF.Round(2f * length / 343f * sampleRate - poles));
            _line = new float[delay];
            // What a round trip keeps at low notes: the open end lets out (ka)²/2 (Levine and Schwinger),
            // a gully pot's wide grate a sixth at its 200 Hz mode, the walls and the water more. At 0.9 a
            // glug that fell on a pot's mode stood 8 dB over the pour for a second.
            _loopGain = 0.8f;
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
            _s1 = _s2 = _s3 = _s4 = _hpIn = _hpOut = 0f;
            _at = 0;
        }

        public float Process(float x)
        {
            float back = _line[_at];
            float g = 1f - _lp;
            _s1 += g * (back - _s1);
            _s2 += g * (_s1 - _s2);
            _s3 += g * (_s2 - _s3);
            _s4 += g * (_s3 - _s4);
            float y = x + _sign * _loopGain * _s4;
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
