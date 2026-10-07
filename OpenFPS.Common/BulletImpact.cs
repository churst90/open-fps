using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// A round striking something, as its own physical event: what the slug and the struck stuff do in the
/// fraction of a millisecond they meet, and what is thrown off and falls afterwards.
///
/// It was <see cref="ImpactAcoustics.Between"/> with a small steel object for the hitter, which is a
/// tap: the same small metal tick on brick, grass and water (Cody, 2026-10-04). A bullet is 3-15 grams
/// arriving at 250-900 m/s and stopping in tens of microseconds, and what the struck stuff does with
/// that is most of the sound:
///
///   BRITTLE (concrete, brick, stone, tile, asphalt): the slug stops over its own length and the
///   crater's depth, which is the CRACK; the struck face fractures, a crackle of micro-cracks over a few milliseconds; the
///   crater's pulverised stuff leaves as a puff of dust; and its chips fly out and fall, a patter whose
///   timing is each chip's own flight (<see cref="DebrisPrefix"/>, placed where they land).
///
///   METAL: the crack, and then the plate RINGS: its bending modes from the part's size and thickness,
///   each given its share of the energy the blow puts into the plate and radiating by its own efficiency.
///
///   SOIL (dirt, grass, gravel): no crack to speak of, the round decelerates over tens of centimetres;
///   the crater's sudden volume is a dull thud (a volume source), and the spray falls back as a patter.
///
///   WATER: a sharp slap as the nose enters, the cavity opening (a thud), the pinch-off bubble ringing at
///   its Minnaert frequency (the plop), the crown and jet tearing into spray (a rush), and the splash's
///   drops falling back, hundreds of broadband splats, one in twenty ringing a small bubble. It was a
///   few dozen drops a third of which rang, and Cody heard it as "just tinkling", not splashing.
///
///   WOOD: a short crack, the board's own modes (the thunk: wood is light and lossy, so they are low and
///   short), a crackle of splintering fibres, and a few splinters falling.
///
///   PANEL (plaster, drywall, plastic): the board is punched through: a crack, the board's low drumming
///   (the thump), and gypsum crumbling down with its dust.
///
///   SOFT (carpet, upholstery, foliage, rubber): a dull muffled thump; leaves swish.
///
/// LEVELS, each from the energy and the physics that makes it, at a metre:
///   The crack is the decelerating slug's acceleration noise (Richards, Westcott and Jeyapalan, J. Sound
///   Vib. 62, 1979): the air it carries with it (an added mass of half its volume of air) brought from v
///   to rest over the contact time τ by a raised-cosine deceleration, p = m_a Δv / (2 c τ²). τ is 2s/v,
///   a steady deceleration over the stopping distance s: the slug's length, and on masonry the crater's
///   depth as well. 119 dB for a 9 mm on concrete, 127 on 6 mm steel, 128 for an AKM on concrete.
///   A ringing plate: the energy a short blow puts into a plate through its point mobility
///   Y = 1/(8√(Bρh)) is I²·Y/τ (Cremer and Heckl, Structure-Borne Sound); each mode's share radiates at
///   ρc·η_rad·ω, η_rad = ρc·σ/(ρh·ω), its radiation efficiency σ rising to one at coincidence. The modes
///   reach the listener with their own signs (where the listener stands puts them either side of each
///   mode's nodal lines), so they add like noise: summed in step, a 9 mm on 6 mm steel came out at
///   162 dB. The same peak by another road: a point force F on a plate of surface mass ρh radiates
///   ρ0·F/(2π·ρh·r) (the infinite plate below coincidence), 120 dB at a metre off a 20 cm concrete wall
///   and 141 dB off 6 mm steel for a 9 mm's 54 kN, where this model declares 122 and 144.
///   The share of the round's energy that leaves as sound (<see cref="Radiated"/>) comes out about 1e-6
///   on masonry and 1.7e-3 on thin steel.
///   A crater or a cavity: a volume source, p = ρ V̈ / 4π, its volume V formed over a few milliseconds.
///   Anything small landing (a chip, a clod, a drop): the game's one impact constant, 74 dB at a metre
///   for a joule (<see cref="PanelAcoustics.ImpactReferenceDb"/>), as every dropped thing in the world.
///
/// THE LEVEL CONVENTION: the declared level of each key is the pressure its buffer's full scale stands
/// for at a metre, worked from the key alone (<see cref="HitFullScalePascals"/>), so the server declares
/// exactly what the client renders. The renders are in pascals at a metre divided by that.
/// </summary>
public static class BulletImpact
{
    public const string HitPrefix = "bullet:hit:";
    public const string DebrisPrefix = "bullet:debris:";

    /// <summary>What a struck material does with a round.</summary>
    public enum Kind { Brittle, Metal, Soil, Water, Wood, Panel, Soft }

    /// <summary>A material's part in it: how it behaves, what a cubic metre of crater costs, how dense
    /// what it throws off is, the mean chip, and how hard a chip's landing is (its contact time).</summary>
    public readonly record struct Stuff(Kind Kind, float CraterJoulesPerM3, float DebrisDensity, float ChipGrams, float ChipContactSeconds);

    // Crater energies: the specific energy a hard target spends per volume of crater. Estimates, set so
    // a 9 mm pistol bullet (500 J) leaves a crater a few cubic centimetres in concrete, as photographs of
    // bullet strikes on concrete show; softer stuff is cheaper.
    private static readonly Dictionary<string, Stuff> _stuff = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Concrete"] = new(Kind.Brittle, 250e6f, 2400f, 0.4f, 30e-6f),
        ["Brick"] = new(Kind.Brittle, 120e6f, 1900f, 0.6f, 40e-6f),
        ["Marble"] = new(Kind.Brittle, 400e6f, 2700f, 0.3f, 25e-6f),
        ["Tile"] = new(Kind.Brittle, 300e6f, 2300f, 0.2f, 25e-6f),
        ["Glass"] = new(Kind.Brittle, 300e6f, 2500f, 0.2f, 25e-6f),
        ["Asphalt"] = new(Kind.Brittle, 60e6f, 2300f, 0.5f, 50e-6f),
        ["Generic"] = new(Kind.Brittle, 150e6f, 1200f, 0.3f, 50e-6f),
        ["Metal"] = new(Kind.Metal, 0f, 7850f, 0f, 20e-6f),
        ["Fence"] = new(Kind.Metal, 0f, 7850f, 0f, 20e-6f),
        ["Wood"] = new(Kind.Wood, 30e6f, 650f, 0.3f, 150e-6f),
        ["Plaster"] = new(Kind.Panel, 20e6f, 800f, 0.4f, 250e-6f),
        ["AcousticTile"] = new(Kind.Panel, 5e6f, 250f, 0.2f, 400e-6f),
        ["Plastic"] = new(Kind.Panel, 40e6f, 1100f, 0.2f, 120e-6f),
        ["Dirt"] = new(Kind.Soil, 5e6f, 1600f, 0.6f, 500e-6f),
        ["Grass"] = new(Kind.Soil, 6e6f, 1500f, 0.6f, 700e-6f),
        ["Gravel"] = new(Kind.Soil, 8e6f, 1700f, 2.0f, 60e-6f),
        ["Water"] = new(Kind.Water, 0f, 1000f, 0f, 0f),
        ["Carpet"] = new(Kind.Soft, 0f, 200f, 0f, 1e-3f),
        ["Audience"] = new(Kind.Soft, 0f, 300f, 0f, 1e-3f),
        ["Foliage"] = new(Kind.Soft, 0f, 500f, 0f, 1e-3f),
        ["Rubber"] = new(Kind.Soft, 0f, 1100f, 0f, 1e-3f),
        ["Leather"] = new(Kind.Soft, 0f, 900f, 0f, 1e-3f),
        ["BootRubber"] = new(Kind.Soft, 0f, 1250f, 0f, 1e-3f),
        ["Skin"] = new(Kind.Soft, 0f, 1050f, 0f, 1e-3f),
        ["None"] = new(Kind.Soft, 0f, 1f, 0f, 1e-3f),
    };

    /// <summary>A material's part in a strike; an unknown name is Generic, as the registry has it.</summary>
    public static Stuff StuffOf(string? material)
        => material != null && _stuff.TryGetValue(material, out var s) ? s : _stuff["Generic"];

    public static Kind KindOf(string? material) => StuffOf(material).Kind;

    /// <summary>The share of the energy dumped in a brittle face that goes into cratering it: most of
    /// it goes into flattening the slug itself and heat. An estimate.</summary>
    public const float CraterShare = 0.3f;

    /// <summary>A drywall board, metres: what a plaster wall thicker than a few centimetres is built of,
    /// on studs 600 mm apart.</summary>
    public const float BoardMetres = 0.0125f;
    public const float StudSpacingMetres = 0.6f;

    // ── The key ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One strike, quantised for the key: the struck material, the speed it struck at (m/s), the slug's
    /// mass (mg), diameter and length (tenths of a mm), the grazing angle (degrees off the face), the
    /// share of its speed it kept going away (per cent: 0 for one that stopped), the struck part's width
    /// and height (cm) and thickness (mm), the floor its debris lands on and how far it falls (cm).
    /// </summary>
    public readonly record struct Hit(string Material, int Speed, int MassMg, int DiameterTenthMm, int LengthTenthMm,
                                      int GrazeDegrees, int KeptPercent, int WidthCm, int HeightCm, int ThicknessMm,
                                      string Floor = "", int DropCm = 0)
    {
        public float V => MathF.Max(1f, Speed);
        public float Mass => MathF.Max(1e-4f, MassMg * 1e-6f);
        public float Diameter => MathF.Max(1e-3f, DiameterTenthMm * 1e-4f);
        public float Length => MathF.Max(1e-3f, LengthTenthMm * 1e-4f);
        public float Kept => Math.Clamp(KeptPercent / 100f, 0f, 1f);
        public float Graze => Math.Clamp(GrazeDegrees, 0, 90) * MathF.PI / 180f;
        public float Width => MathF.Max(0.05f, WidthCm / 100f);
        public float Height => MathF.Max(0.05f, HeightCm / 100f);
        public float Thickness => MathF.Max(0.0005f, ThicknessMm / 1000f);
        public float Drop => MathF.Max(0f, DropCm / 100f);
        public float Joules => 0.5f * Mass * V * V;
        /// <summary>The slug's volume: a cylinder with its nose taken off.</summary>
        public float Volume => 0.85f * MathF.PI / 4f * Diameter * Diameter * Length;
        public float FrontalArea => MathF.PI / 4f * Diameter * Diameter;
        /// <summary>A plate's way of seeing it: struck from above (nothing falls), or on a wall.</summary>
        public bool OnWall => DropCm > 0;
        /// <summary>What it leaves in the struck stuff, J: see <see cref="DumpedJoules"/>.</summary>
        public float Dumped => DumpedJoules(this);
        /// <summary>The momentum the face takes, N s: see <see cref="ImpulseInto"/>.</summary>
        public float Impulse => ImpulseInto(this);
    }

    /// <summary>
    /// What a strike leaves in the struck stuff, J: all of the round's energy for one that stops, the
    /// loss for one that ricochets, and for a board it goes THROUGH (a door, a sheet of drywall) only
    /// what punching the hole costs, the board's crushing energy per volume times the hole's volume.
    /// </summary>
    public static float DumpedJoules(Hit h)
    {
        float all = h.Joules * (1f - h.Kept * h.Kept);
        var s = StuffOf(h.Material);
        if (s.Kind is Kind.Wood or Kind.Panel && h.Kept <= 0f)
        {
            float board = s.Kind == Kind.Panel ? MathF.Min(h.Thickness, BoardMetres) : h.Thickness;
            return MathF.Min(all, s.CraterJoulesPerM3 * h.FrontalArea * board);
        }
        return all;
    }

    /// <summary>
    /// The momentum the face takes, N s. A round that stops gives it all of m·v; a ricochet the normal
    /// part, reversed (m·v·sin θ, and back out at a share of it); one going through a board the speed it
    /// lost there; and water, in the two calibres the nose takes to go under, what its drag takes,
    /// ½ρ_w·Cd·A·v² over that time (Cd 0.3).
    /// </summary>
    public static float ImpulseInto(Hit h)
    {
        var s = StuffOf(h.Material);
        float mv = h.Mass * h.V;
        if (h.Kept > 0f) return mv * MathF.Sin(h.Graze) * (1f + 0.5f * h.Kept);
        if (s.Kind == Kind.Water)
            return mv * (1f - MathF.Exp(-1000f * 0.3f * h.FrontalArea * 2f * h.Diameter / (2f * h.Mass)));
        if (s.Kind is Kind.Wood or Kind.Panel)
        {
            float left = MathF.Max(0f, h.Joules - DumpedJoules(h));
            return h.Mass * (h.V - MathF.Sqrt(2f * left / h.Mass));
        }
        return mv;
    }

    /// <summary>A strike's key, everything from a strike that its sound depends on.</summary>
    public static Hit From(string material, float speed, WeaponDefinition w, float grazeRadians, float kept,
                           Vector3 partSize, string floor, float drop, float massKg = 0f, float diameter = 0f, float length = 0f)
    {
        float m = massKg > 0f ? massKg : w.BulletMassKg > 0f ? w.BulletMassKg : 124f * WeaponRegistry.Grain;
        float d = diameter > 0f ? diameter : w.BulletDiameterMetres > 0f ? w.BulletDiameterMetres : 0.009f;
        float l = length > 0f ? length : w.BulletLengthMetres > 0f ? w.BulletLengthMetres : 0.0156f;
        return new Hit(string.IsNullOrEmpty(material) ? "Generic" : material,
            (int)MathF.Round(Math.Clamp(speed, 1f, 3000f)), (int)MathF.Round(m * 1e6f),
            (int)MathF.Round(d * 1e4f), (int)MathF.Round(l * 1e4f),
            (int)MathF.Round(Math.Clamp(grazeRadians * 180f / MathF.PI, 0f, 90f)),
            (int)MathF.Round(Math.Clamp(kept, 0f, 1f) * 100f),
            (int)MathF.Round(Math.Clamp(partSize.X, 0.05f, 100f) * 100f), (int)MathF.Round(Math.Clamp(partSize.Y, 0.05f, 100f) * 100f),
            (int)MathF.Round(Math.Clamp(partSize.Z, 0.0005f, 2f) * 1000f),
            string.IsNullOrEmpty(floor) ? "Generic" : floor, (int)MathF.Round(Math.Clamp(drop, 0f, 100f) * 100f));
    }

    private static string Body(Hit h) => string.Create(CultureInfo.InvariantCulture,
        $"{h.Material}:{h.Speed}:{h.MassMg}:{h.DiameterTenthMm}:{h.LengthTenthMm}:{h.GrazeDegrees}:{h.KeptPercent}:{h.WidthCm}:{h.HeightCm}:{h.ThicknessMm}:{h.Floor}:{h.DropCm}");

    public static string HitKey(Hit h) => HitPrefix + Body(h);
    public static string DebrisKey(Hit h) => DebrisPrefix + Body(h);

    public static bool TryParseHit(string? key, out Hit h) => TryParse(key, HitPrefix, out h);
    public static bool TryParseDebris(string? key, out Hit h) => TryParse(key, DebrisPrefix, out h);

    private static bool TryParse(string? key, string prefix, out Hit h)
    {
        h = default;
        if (key == null || !key.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var p = key[prefix.Length..].Split(':');
        if (p.Length != 12 || p[0].Length == 0) return false;
        var v = new int[12];
        for (int i = 1; i < 12; i++)
        {
            if (i == 10) continue;
            if (!int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]) || v[i] < 0) return false;
        }
        if (v[1] < 1 || v[2] < 1 || v[6] > 100 || v[5] > 90) return false;
        h = new Hit(p[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], p[10], v[11]);
        return true;
    }

    // ── What the server sends ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The sounds of one strike: the strike itself where the round met the face, and what it throws
    /// off where that lands (<paramref name="foot"/>, under the strike). Each declared at its own
    /// buffer's full scale.
    /// </summary>
    public static List<TransientSound> Sounds(Hit h, Vector3 at, Vector3 foot)
    {
        var sounds = new List<TransientSound>(2)
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock,
                Position = at,
                LevelDb = BulletFlyby.Spl(HitFullScalePascals(h)),
                SynthKey = HitKey(h),
                DecaySeconds = 0.3f,
                Noisiness = 1f,
            },
        };
        if (HasDebris(h))
            sounds.Add(new TransientSound
            {
                Character = SoundCharacter.Knock,
                Position = foot,
                LevelDb = BulletFlyby.Spl(DebrisFullScalePascals(h)),
                SynthKey = DebrisKey(h),
                DecaySeconds = 0.5f,
                Noisiness = 1f,
            });
        return sounds;
    }

    /// <summary>Whether anything is thrown off that falls audibly: chips, clods, splinters, crumbs or
    /// drops. A plate rings and a cushion swallows it; neither throws anything.</summary>
    public static bool HasDebris(Hit h) => KindOf(h.Material) is not (Kind.Metal or Kind.Soft) && Pieces(h).Count > 0;

    // ── The physics of one strike ───────────────────────────────────────────────────────────────

    private const float RhoAir = 1.2f, C = 343f, RefPa = 20e-6f;

    /// <summary>How long the slug and the face are in contact, seconds.</summary>
    public static float ContactSeconds(Hit h)
    {
        var s = StuffOf(h.Material);
        float v = h.V;
        float slide = 1f + 2f * h.Kept;                 // a ricochet slides along a gouge as it goes
        switch (s.Kind)
        {
            case Kind.Brittle:
            case Kind.Metal:
            {
                // Brought from v to rest over its stopping distance s at a steady deceleration takes
                // 2s/v: the slug flattening over its own length, and into a brittle face as deep as the
                // crater it digs (about the cube root of its volume).
                float stop = h.Length + (s.Kind == Kind.Brittle ? MathF.Cbrt(CraterCubicMetres(h)) : 0f);
                return Math.Clamp(2f * stop / v * slide, 10e-6f, 2e-3f);
            }
            case Kind.Wood:
            case Kind.Panel:
            {
                // Through the board, or to where its crushing stops it, at about half the speed it came.
                float depth = h.Dumped / (s.CraterJoulesPerM3 * MathF.PI / 4f * h.Diameter * h.Diameter);
                float board = s.Kind == Kind.Panel ? MathF.Min(h.Thickness, BoardMetres) : h.Thickness;
                return Math.Clamp(2f * MathF.Min(depth, board) / v * slide, 20e-6f, 5e-3f);
            }
            case Kind.Water:
                return Math.Clamp(2f * h.Diameter / v, 10e-6f, 1e-3f);
            default:
            {
                float depth = MathF.Min(0.5f, h.Dumped / (MathF.Max(1e6f, s.CraterJoulesPerM3 * 3f) * MathF.PI / 4f * h.Diameter * h.Diameter));
                return Math.Clamp(2f * depth / v, 0.2e-3f, 5e-3f);
            }
        }
    }

    /// <summary>The crack's peak at a metre, Pa: the decelerating slug's dipole, m_a Δv / (2 c τ²).</summary>
    public static float CrackPascals(Hit h)
    {
        float addedMass = 0.5f * RhoAir * h.Volume;
        float dv = h.Impulse / h.Mass;
        float tau = ContactSeconds(h);
        float sigma = MathF.Max(tau / 4f, MinSigma);
        // A contact shorter than the render can carry is rounded to the shortest it can, keeping its
        // energy (amplitude² × width).
        return addedMass * dv / (2f * C * tau * tau) * MathF.Sqrt(tau / 4f / sigma);
    }

    /// <summary>The narrowest pulse a 48 kHz render carries without aliasing: a Gaussian of 15 µs.</summary>
    private const float MinSigma = 15e-6f;

    /// <summary>A crater's volume, m³, from the share of the dumped energy that cratering takes.</summary>
    public static float CraterCubicMetres(Hit h)
    {
        var s = StuffOf(h.Material);
        if (s.CraterJoulesPerM3 <= 0f) return 0f;
        if (s.Kind == Kind.Panel)
        {
            // A board does not crater, it breaks out: a cone of gypsum pushed off the far face, about as
            // deep as the board and a little wider (1.5 t³), from each board of a stud wall.
            float board = MathF.Min(h.Thickness, BoardMetres);
            int boards = h.Thickness > 0.04f ? 2 : 1;
            return boards * 1.5f * board * board * board * (h.Kept > 0f ? 0.3f : 1f);
        }
        float share = s.Kind == Kind.Soil ? 0.6f : CraterShare;
        return share * h.Dumped / s.CraterJoulesPerM3;
    }

    /// <summary>A volume source's peak at a metre, Pa: ρ V̈ / 4π, V formed over <paramref name="seconds"/>
    /// as V(1 − (1 + t/T) e^(−t/T)), whose second derivative starts at V/T².</summary>
    private static float VolumePascals(float volume, float seconds) => RhoAir * volume / (seconds * seconds) / (4f * MathF.PI);

    /// <summary>The time a crater or cavity takes to open, seconds.</summary>
    private static float OpeningSeconds(Hit h) => KindOf(h.Material) switch
    {
        Kind.Water => 4e-3f,
        Kind.Soil => 3e-3f,
        _ => 1.5e-3f,
    };

    // ── The plate ───────────────────────────────────────────────────────────────────────────────

    /// <summary>One bending mode of the struck part: its note, its peak at a metre per unit of its
    /// share of the drive, and its amplitude's decay rate (1/s).</summary>
    private readonly record struct Mode(int M, int N, float Hz, float PascalsPerShare, float Decay);

    /// <summary>The plate the round meets: the part itself, or for a thick plaster wall one board
    /// between studs. Width, height, thickness.</summary>
    private static (float A, float B, float T) Plate(Hit h)
    {
        var kind = KindOf(h.Material);
        if (kind == Kind.Panel && h.Thickness > 0.04f)
            return (MathF.Min(h.Width, StudSpacingMetres), MathF.Min(h.Height, 2.4f), BoardMetres);
        // A wall or a slab bigger than this answers a blow as if it were this big: a bending wave
        // that has crossed a few metres of masonry, with its loss, brings nothing back worth hearing.
        return (MathF.Min(h.Width, PlateReachMetres), MathF.Min(h.Height, PlateReachMetres), h.Thickness);
    }

    /// <summary>
    /// What a steel part loses at its bolts and welds, as a loss factor. Not the 0.03 of a door in its
    /// frame (<see cref="PanelAcoustics.MountedLoss"/>), which would stop a struck steel plate in a few
    /// hundredths of a second: total loss factors of built-up steel structures run 0.001-0.01 (Cremer and
    /// Heckl, Structure-Borne Sound). The top of that range: at 0.004 a 9 mm on 6 mm steel sent 0.4 % of
    /// its energy out as sound, over the 0.01-0.1 % such a strike radiates; at 0.01 it sends 0.15 %.
    /// </summary>
    public const float MetalJointLoss = 0.01f;

    /// <summary>The most modes a render carries, the loudest first.</summary>
    public const int MaxModes = 240;

    /// <summary>How far across a struck part is heard as one plate, metres.</summary>
    public const float PlateReachMetres = 4f;

    /// <summary>The extra loss of a slab lying on the ground (struck from above): it gives its bending
    /// energy to the ground under it. An estimate.</summary>
    public const float OnGroundLoss = 0.1f;

    /// <summary>Whether the struck part rings (or thumps) as a plate: everything but soil, water and
    /// soft stuff, which have no plate to ring.</summary>
    private static bool Rings(Hit h) => KindOf(h.Material) is Kind.Metal or Kind.Wood or Kind.Panel or Kind.Brittle;

    /// <summary>
    /// The plate's modes, lowest first, each with the peak it would radiate at a metre if it took all
    /// the vibrational energy the blow leaves (which <see cref="ModeShares"/> then divides), and its
    /// decay. Simply supported, all (m, n) from 30 Hz to 16 kHz.
    /// </summary>
    private static List<Mode> Modes(Hit h, out float vibrationJoules)
    {
        var modes = new List<Mode>();
        vibrationJoules = 0f;
        if (!Rings(h)) return modes;
        var props = AcousticRegistry.GetProperties(h.Material);
        float rho = MathF.Max(100f, props.DensityKgM3);
        float e = MathF.Max(1e6f, props.YoungsModulusGPa * 1e9f);
        var (a, b, t) = Plate(h);
        t = Math.Clamp(t, 0.0005f, 0.5f);
        float bending = e * t * t * t / (12f * (1f - 0.09f));
        float surface = rho * t;
        // The energy a short blow leaves in a plate, I²·Y/τ, through its point mobility; never more
        // than half of what the round dumped.
        float mobility = 1f / (8f * MathF.Sqrt(bending * surface));
        float tau = ContactSeconds(h);
        vibrationJoules = MathF.Min(0.5f * h.Dumped, h.Impulse * h.Impulse * mobility / tau);
        float coincidence = C * C / (2f * MathF.PI) * MathF.Sqrt(surface / bending);
        float scale = 0.4755f * t * MathF.Sqrt(e / rho);
        float loss = props.LossFactor + (KindOf(h.Material) == Kind.Metal ? MetalJointLoss : PanelAcoustics.MountedLoss)
                   + (KindOf(h.Material) == Kind.Brittle && !h.OnWall ? OnGroundLoss : 0f);
        for (int m = 1; m <= 60; m++)
            for (int n = 1; n <= 60; n++)
            {
                float hz = scale * (m * m / (a * a) + n * n / (b * b));
                if (hz > 16000f) break;
                if (hz < 30f) continue;
                float omega = 2f * MathF.PI * hz;
                // Radiation efficiency: one above coincidence, falling away below it.
                float sigma = 1f / MathF.Sqrt(1f + MathF.Pow(coincidence / hz, 3f));
                float etaRad = RhoAir * C * sigma / (surface * omega);
                float eta = loss + etaRad;
                // A mode holding energy E radiates η_rad·ω·E watts over a hemisphere: rms² at a metre is
                // ρc·η_rad·ω·E/2π, and the peak is √2 of that.
                float perJoule = MathF.Sqrt(2f * RhoAir * C * etaRad * omega / (2f * MathF.PI));
                modes.Add(new Mode(m, n, hz, perJoule, 0.5f * eta * omega));
            }
        modes.Sort((x, y) => x.Hz.CompareTo(y.Hz));
        return modes;
    }

    /// <summary>Each mode's share of the vibration: the force's spectrum at its note (a half-sine of
    /// the contact time, flat to about 1/τ), times the mode's shape where it was struck (its mean,
    /// 1/4, for the bound; the seed's own point for a render).</summary>
    private static float[] ModeShares(Hit h, List<Mode> modes, float x0, float y0, bool mean)
    {
        float tau = ContactSeconds(h);
        var w = new float[modes.Count];
        float total = 0f;
        for (int i = 0; i < modes.Count; i++)
        {
            float wt = 2f * MathF.PI * modes[i].Hz * tau / MathF.PI;
            float force = 1f / (1f + wt * wt * wt * wt);
            float shape = mean ? 0.25f : Sq(MathF.Sin(modes[i].M * MathF.PI * x0) * MathF.Sin(modes[i].N * MathF.PI * y0));
            w[i] = force * shape;
            total += force * 0.25f;
        }
        for (int i = 0; i < w.Length; i++) w[i] /= MathF.Max(1e-12f, total);
        return w;
    }

    private static float Sq(float x) => x * x;

    // ── Things thrown off ───────────────────────────────────────────────────────────────────────

    /// <summary>One piece thrown off a strike: its mass, kg, how it leaves (speed, m/s, its angle off
    /// the face and its direction across the face, radians), and how hard what it lands on is
    /// (contact seconds).</summary>
    private readonly record struct Piece(float Mass, float Speed, float Elevation, float Azimuth, float Contact);

    /// <summary>
    /// What a strike throws off, deterministic from the key (the seed varies only where in time each
    /// lands, never how many or how heavy, so the bound below holds for every render). Chips are a
    /// power law in mass from a fifth to three times the mean, their total the crater's mass.
    /// </summary>
    private static List<Piece> Pieces(Hit h)
    {
        var s = StuffOf(h.Material);
        var pieces = new List<Piece>();
        var rng = new Random((int)(BulletFlyby.Mix((uint)h.Speed, (uint)(h.MassMg * 31 + h.GrazeDegrees * 7 + h.KeptPercent)) & 0x7fffffff));
        float floorContact = StuffOf(h.Floor).ChipContactSeconds;
        switch (s.Kind)
        {
            case Kind.Brittle:
            case Kind.Soil:
            case Kind.Wood:
            case Kind.Panel:
            {
                float total = CraterCubicMetres(h) * s.DebrisDensity * (s.Kind == Kind.Brittle ? 0.8f : 1f);
                float mean = s.ChipGrams * 1e-3f;
                int count = Math.Clamp((int)MathF.Round(total / MathF.Max(1e-6f, mean)), 0, s.Kind == Kind.Soil ? 140 : 60);
                if (count == 0) return pieces;
                var masses = new float[count];
                float sum = 0f;
                for (int i = 0; i < count; i++) { masses[i] = mean * MathF.Pow(15f, (float)rng.NextDouble()) / 5f; sum += masses[i]; }
                for (int i = 0; i < count; i++)
                {
                    float m = masses[i] * total / sum;
                    // Chips leave a crater at a few to a dozen m/s, in a cone 20-70 degrees off the face;
                    // clods of soil and splinters slower, and crumbs of plaster just fall out of the hole.
                    float speed = s.Kind switch
                    {
                        Kind.Brittle => 3f + 9f * (float)rng.NextDouble(),
                        Kind.Soil => 1f + 4f * (float)rng.NextDouble(),
                        Kind.Wood => 2f + 6f * (float)rng.NextDouble(),
                        _ => 0.3f + 1.2f * (float)rng.NextDouble(),
                    };
                    float elevation = 0.35f + 0.85f * (float)rng.NextDouble();
                    float azimuth = (float)(rng.NextDouble() * 2 * Math.PI);
                    pieces.Add(new Piece(m, speed, elevation, azimuth, floorContact));
                }
                break;
            }
            case Kind.Water:
            {
                // The splash: its drops, as pieces (<see cref="Splash"/>).
                foreach (var d in Splash(h)) pieces.Add(new Piece(d.Mass, d.Speed, d.Elevation, 0f, 0f));
                break;
            }
        }
        return pieces;
    }

    /// <summary>A water entry's cavity, m³: about four calibres across and a quarter of a metre deep,
    /// longer for a faster round.</summary>
    private static float CavityCubicMetres(Hit h)
    {
        float width = 4f * h.Diameter;
        float depth = Math.Clamp(0.25f * h.V / 300f, 0.1f, 0.6f);
        return MathF.PI / 4f * width * width * depth * (1f - 0.7f * h.Kept);
    }

    /// <summary>A piece's flight until it lands <paramref name="drop"/> below where it left, with
    /// quadratic drag on a lump of its own mass: seconds and landing speed. Off a floor its angle off
    /// the face is its climb; off a wall, the face is upright, so it leaves the wall by that angle and
    /// climbs or falls by its direction across the face.</summary>
    private static (float Seconds, float Speed) Land(Piece p, float density, float drop, bool onWall)
    {
        float size = MathF.Cbrt(p.Mass / MathF.Max(100f, density));
        float k = 0.5f * RhoAir * 1f * size * size / p.Mass;
        float off = p.Speed * MathF.Sin(p.Elevation), across = p.Speed * MathF.Cos(p.Elevation);
        float vx, vy;
        if (onWall) { vy = across * MathF.Sin(p.Azimuth); vx = MathF.Sqrt(off * off + Sq(across * MathF.Cos(p.Azimuth))); }
        else { vy = off; vx = across; }
        float y = 0f, t = 0f;
        const float dt = 0.002f;
        while (t < 4f)
        {
            float v = MathF.Sqrt(vx * vx + vy * vy);
            vx -= k * v * vx * dt;
            vy -= (9.81f + k * v * vy) * dt;
            y += vy * dt;
            t += dt;
            if (y <= -drop && vy < 0f) break;
        }
        return (t, MathF.Sqrt(vx * vx + vy * vy));
    }

    /// <summary>A small thing landing, peak Pa at a metre: the game's impact constant.</summary>
    private static float LandingPascals(float mass, float speed)
        => RefPa * MathF.Pow(10f, PanelAcoustics.ImpactDb(0.5f * mass * speed * speed) / 20f);

    // ── Full scale ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pressure, Pa at a metre, that a strike buffer's full scale stands for, worked from the key
    /// alone: the crack's peak plus the plate's (the sum of its modes' amplitudes at their mean shares),
    /// plus a volume source's, with a little headroom. The render is divided
    /// by it and never needs clipping in practice.
    /// </summary>
    public static float HitFullScalePascals(Hit h)
    {
        // The parts that do not depend on the seed, as they render, over their first 60 ms (where the
        // crack, the plate's first swing and the crater's thud all are)...
        var fixedPart = Pascals(h, FullScaleRate, 0, random: false, maxSeconds: 0.06f);
        float peak = 0f;
        foreach (float v in fixedPart) peak = MathF.Max(peak, MathF.Abs(v));
        // ...and bounds on the seeded ones: two crackle pulses at once, three times a noise's rms.
        float crack = CrackPascals(h);
        float extra = KindOf(h.Material) switch
        {
            Kind.Brittle => 2f * 0.6f * crack / MathF.Sqrt(6f) + 3f * 0.7f * VolumePascals(3f * CraterCubicMetres(h), 0.005f),
            Kind.Soil => 3f * 0.15f * VolumePascals(CraterCubicMetres(h), OpeningSeconds(h)),
            Kind.Wood => 2f * 0.25f * crack,
            Kind.Panel => 3f * VolumePascals(3f * CraterCubicMetres(h), 0.006f),
            Kind.Soft => h.Material.Equals("Foliage", StringComparison.OrdinalIgnoreCase) ? 1.5f * SoftThumpPascals(h) : 0f,
            _ => 0f,
        };
        return MathF.Max(1e-3f, 1.1f * (peak + extra));
    }

    /// <summary>
    /// The sound energy a strike's crack and its plate's ring send out, J, against the energy the round
    /// left there: the acoustic efficiency of the strike. Each is ∫p² dt at a metre over the hemisphere
    /// in front of the face, 2π/(ρc) of it: for the crack (a Gaussian's derivative of peak A and width σ)
    /// A²·e·σ·√π/2, and for a mode of amplitude a dying at rate δ, a²/4δ.
    /// </summary>
    public static (float CrackJoules, float RingJoules, float Efficiency) Radiated(Hit h)
    {
        float toJoules = 2f * MathF.PI / (RhoAir * C);
        float a = CrackPascals(h);
        float sigma = MathF.Max(ContactSeconds(h) / 4f, MinSigma);
        float crack = a * a * MathF.E * sigma * MathF.Sqrt(MathF.PI) / 2f * toJoules;
        float ring = 0f;
        if (Rings(h))
        {
            var modes = Modes(h, out float joules);
            var shares = ModeShares(h, modes, 0f, 0f, mean: true);
            for (int i = 0; i < modes.Count; i++)
            {
                float amp2 = modes[i].PascalsPerShare * modes[i].PascalsPerShare * joules * shares[i];
                ring += amp2 / (4f * modes[i].Decay) * toJoules;
            }
        }
        return (crack, ring, (crack + ring) / MathF.Max(1e-6f, h.Dumped));
    }

    /// <summary>The rate the full scale is worked at: the client's.</summary>
    public const int FullScaleRate = 48000;

    /// <summary>The pressure a debris buffer's full scale stands for: twice the loudest single landing
    /// any piece could make (landing at the speed it left or its terminal speed, whichever is higher).</summary>
    public static float DebrisFullScalePascals(Hit h)
    {
        if (KindOf(h.Material) == Kind.Water) return SplashFullScalePascals(h);
        var s = StuffOf(h.Material);
        float best = 1e-4f;
        foreach (var p in Pieces(h))
        {
            float size = MathF.Cbrt(p.Mass / MathF.Max(100f, s.Kind == Kind.Water ? 1000f : s.DebrisDensity));
            float terminal = MathF.Sqrt(2f * p.Mass * 9.81f / (RhoAir * size * size));
            float v = MathF.Max(p.Speed, MathF.Min(terminal, MathF.Sqrt(p.Speed * p.Speed + 2f * 9.81f * h.Drop)));
            best = MathF.Max(best, LandingPascals(p.Mass, v) * (s.Kind == Kind.Water ? 1.6f : 1f));
        }
        return 2f * best;
    }

    // ── The splash ──────────────────────────────────────────────────────────────────────────────

    /// <summary>One drop of a splash, as rendered: its mass, radius, how it leaves (speed, angle above
    /// the water), and the weight its sound carries for the real drops it stands for.</summary>
    public readonly record struct Drop(float Mass, float Radius, float Speed, float Elevation, float Weight);

    /// <summary>The share of the cavity's water the crown and jet throw up as drops. An estimate.</summary>
    public const float SplashShare = 0.1f;
    /// <summary>The mean drop radius of the spray, m: an exponential spread of radii from 0.15 to 3 mm
    /// about it, as a sheet breaking up gives (Marshall-Palmer-like).</summary>
    public const float MeanDropRadius = 0.6e-3f;
    /// <summary>The most drops a render carries; more than that are stood for, each carrying the sound
    /// of √(real/rendered) of them, so the energy is kept.</summary>
    public const int MaxRenderedDrops = 2500;
    /// <summary>
    /// The share of falling-back drops that entrain a bubble that rings. A drop entrains one REGULARLY
    /// only in a narrow window of size and speed, about a millimetre across at its terminal speed (Pumphrey
    /// and Elmore, J. Fluid Mech. 220, 1990; Prosperetti and Oguz, Annu. Rev. Fluid Mech. 25, 1993); a
    /// splash's drops land slower and of every size, and entrain only irregularly, now and then. What
    /// every drop does make is its impact sound, a short broadband splat. Taken as one in twenty.
    /// </summary>
    public const float RingingShare = 0.05f;

    /// <summary>The splash's drops, deterministic from the key: their number from the splash's water
    /// over the mean drop's mass, launched at 1.5-5.5 m/s and 35-85 degrees.</summary>
    public static List<Drop> Splash(Hit h)
    {
        var drops = new List<Drop>();
        float water = SplashShare * CavityCubicMetres(h) * 1000f;
        // The mean of r³ for radii exponential about a mean r̄ is 6 r̄³.
        float meanMass = 1000f * 4f / 3f * MathF.PI * 6f * MeanDropRadius * MeanDropRadius * MeanDropRadius;
        int real = Math.Max(1, (int)(water / meanMass));
        int count = Math.Min(real, MaxRenderedDrops);
        float weight = MathF.Sqrt(real / (float)count);
        var rng = new Random((int)(BulletFlyby.Mix((uint)h.Speed, (uint)(h.MassMg * 3 + h.KeptPercent)) & 0x7fffffff));
        for (int i = 0; i < count; i++)
        {
            float r = Math.Clamp(-MeanDropRadius * MathF.Log(1f - 0.999f * (float)rng.NextDouble()), 0.15e-3f, 3e-3f);
            float m = 1000f * 4f / 3f * MathF.PI * r * r * r;
            drops.Add(new Drop(m, r, 1.5f + 4f * (float)rng.NextDouble(), 0.6f + 0.9f * (float)rng.NextDouble(), weight));
        }
        return drops;
    }

    /// <summary>A drop's splat on the water: peak Pa at a metre (the game's impact constant), and how
    /// long it lasts, the drop's own crossing time 2r/v and a little more for the surface closing.</summary>
    private static (float Peak, float Seconds) Splat(Drop d, float speed)
        => (LandingPascals(d.Mass, speed) * d.Weight, 0.3e-3f + 2f * d.Radius / MathF.Max(0.5f, speed));

    /// <summary>
    /// What a splash buffer's full scale stands for: the larger of twice its loudest single splat and
    /// four times the loudest 5 ms of all the splats together (they overlap in hundreds), with the drops
    /// landing as they would without the seed's variation.
    /// </summary>
    private static float SplashFullScalePascals(Hit h)
    {
        var bins = new Dictionary<int, float>();
        float loudest = 1e-5f, total = 0f;
        foreach (var d in Splash(h))
        {
            var (t, v) = Land(new Piece(d.Mass, d.Speed, d.Elevation, 0f, 0f), 1000f, 0f, false);
            var (peak, seconds) = Splat(d, v);
            loudest = MathF.Max(loudest, peak);
            float energy = (peak / 3f) * (peak / 3f) * seconds;
            int bin = (int)(t / 0.005f);
            bins[bin] = (bins.TryGetValue(bin, out float e) ? e : 0f) + energy;
            total += energy;
        }
        float worst = 0f;
        foreach (var e in bins.Values) worst = MathF.Max(worst, e);
        float rushRms = MathF.Sqrt(total / RushSeconds);
        return MathF.Max(2f * loudest, 4f * MathF.Max(MathF.Sqrt(worst / 0.005f), rushRms));
    }

    /// <summary>The crown and jet leaving, seconds: the sheet rises and tears into spray over this.</summary>
    private const float RushSeconds = 0.06f;

    /// <summary>
    /// The splash as heard: the crown and jet tearing into spray as the water leaves (a broadband rush,
    /// given the same sound energy as the fall-back, the same water at the same speeds going up rather
    /// than down), then the drops falling back, each a short broadband splat when its own flight brings
    /// it down, one in twenty ringing a small bubble (<see cref="RingingShare"/>) about as loud as its
    /// splat. Hundreds of splats overlapping are a rush of noise, which is what a splash sounds like.
    /// </summary>
    private static float[] RenderSplash(Hit h, int sampleRate, int seed, out float rawPeak)
    {
        float fullScale = SplashFullScalePascals(h);
        var rng = new Random((int)(BulletFlyby.Mix(BulletFlyby.Mix((uint)h.Speed, (uint)h.MassMg), (uint)(seed & 3) + 307u) & 0x7fffffff));
        var drops = Splash(h);
        var landings = new List<(float At, Drop Drop, float Speed)>(drops.Count);
        float total = 0f, end = RushSeconds * 3f;
        foreach (var d in drops)
        {
            var q = d with
            {
                Speed = d.Speed * (0.85f + 0.3f * (float)rng.NextDouble()),
                Elevation = Math.Clamp(d.Elevation + 0.3f * ((float)rng.NextDouble() - 0.5f), 0.2f, 1.5f),
            };
            var (t, v) = Land(new Piece(q.Mass, q.Speed, q.Elevation, 0f, 0f), 1000f, 0f, false);
            landings.Add((t, q, v));
            var (peak, seconds) = Splat(q, v);
            total += (peak / 3f) * (peak / 3f) * seconds;
            end = MathF.Max(end, t + 0.03f);
        }
        int n = (int)MathF.Ceiling((end + 0.01f) * sampleRate);
        var p = new float[n];
        // The crown and jet: a rush of the fall-back's sound energy, rising over 10 ms and dying over 50.
        AddNoiseBurst(p, 0f, 0.01f, 0.05f, MathF.Sqrt(total / RushSeconds), 500f, 12000f, rng, sampleRate);
        foreach (var (at, d, v) in landings)
        {
            var (peak, seconds) = Splat(d, v);
            AddNoiseBurst(p, at, 0.0001f, seconds, peak / 3f, 800f, 14000f, rng, sampleRate);
            if (rng.NextDouble() < RingingShare)
            {
                // The entrained bubble is a fraction of the drop's size: 3.26/R Hz, R in metres.
                float hz = 3.26f / (d.Radius * (0.25f + 0.25f * (float)rng.NextDouble()));
                if (hz < 14000f) AddBubble(p, at + 0.001f, hz, 0.04f, peak, 1.05f, sampleRate);
            }
        }
        rawPeak = 0f;
        for (int i = 0; i < n; i++) { rawPeak = MathF.Max(rawPeak, MathF.Abs(p[i]) / fullScale); p[i] = Math.Clamp(p[i] / fullScale, -1f, 1f); }
        return p;
    }

    /// <summary>A soft thing's thump: the slug stopped over a long contact, as a volume source of the
    /// stuff it pushes aside (its own volume times the penetration over its length, roughly).</summary>
    private static float SoftThumpPascals(Hit h)
    {
        float tau = ContactSeconds(h);
        float pushed = h.Volume * 30f;
        return VolumePascals(pushed, MathF.Max(1e-3f, tau));
    }

    // ── Rendering ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The strike, as the client renders it: pascals at a metre over <see cref="HitFullScalePascals"/>.</summary>
    public static float[] RenderHit(Hit h, int sampleRate, int seed) => RenderHit(h, sampleRate, seed, out _);

    /// <summary>The same, saying how far over full scale its loudest sample would have been (1 or less
    /// is the bound holding).</summary>
    public static float[] RenderHit(Hit h, int sampleRate, int seed, out float rawPeak)
    {
        float fullScale = HitFullScalePascals(h);
        var p = Pascals(h, sampleRate, seed, random: true, maxSeconds: 10f);
        rawPeak = 0f;
        for (int i = 0; i < p.Length; i++) { rawPeak = MathF.Max(rawPeak, MathF.Abs(p[i]) / fullScale); p[i] = Math.Clamp(p[i] / fullScale, -1f, 1f); }
        return p;
    }

    /// <summary>
    /// The strike in pascals at a metre. With <paramref name="random"/> false, only the parts that do
    /// not depend on the seed (the crack, the plate, the crater's or cavity's volume, the plop), which is
    /// what the full scale is worked from; the seeded parts (the crackle, the fibres, the dust and spray)
    /// are added to that as bounds.
    /// </summary>
    private static float[] Pascals(Hit h, int sampleRate, int seed, bool random, float maxSeconds)
    {
        var kind = KindOf(h.Material);
        var s = StuffOf(h.Material);
        uint hash = BulletFlyby.Mix(BulletFlyby.Mix((uint)h.Speed, (uint)(h.MassMg + 17 * h.GrazeDegrees)), (uint)(seed & 3) + 101u);
        var rng = new Random((int)(hash & 0x7fffffff));
        // What does not change with the seed: where on the plate it struck, and when the bubble pinches off.
        var fixedRng = new Random((int)(BulletFlyby.Mix((uint)(h.Speed * 131 + h.MassMg), (uint)(h.WidthCm * 7 + h.HeightCm + h.ThicknessMm * 13)) & 0x7fffffff));
        float seconds = kind switch
        {
            Kind.Metal or Kind.Wood or Kind.Panel => 0.05f,
            Kind.Water => 0.2f,
            Kind.Soil => 0.12f,
            _ => 0.12f,
        };
        List<Mode>? modes = null;
        float vib = 0f;
        float[]? shares = null;
        float[]? signs = null;
        if (Rings(h))
        {
            modes = Modes(h, out vib);
            float x0 = 0.1f + 0.8f * (float)fixedRng.NextDouble(), y0 = 0.1f + 0.8f * (float)fixedRng.NextDouble();
            shares = ModeShares(h, modes, x0, y0, mean: false);
            // Each mode's sound reaches the listener with its own sign: a mode's far field changes sign
            // across its nodal lines, and where the listener stands puts them on one side or the other
            // of each. The modes start together from rest, but their pressures at the ear do not add in
            // step; summed as if they did, a 9 mm on 6 mm steel peaked at 162 dB at a metre.
            signs = new float[modes.Count];
            for (int i = 0; i < signs.Length; i++) signs[i] = fixedRng.Next(2) == 0 ? 1f : -1f;
            // As long as the loudest-lasting mode takes to fall 60 dB, up to two and a half seconds.
            float longest = 0f, loudest = 0f;
            for (int i = 0; i < modes.Count; i++)
                loudest = MathF.Max(loudest, modes[i].PascalsPerShare * MathF.Sqrt(vib * shares[i]));
            for (int i = 0; i < modes.Count; i++)
            {
                float a = modes[i].PascalsPerShare * MathF.Sqrt(vib * shares[i]);
                if (a < loudest * 0.03f) continue;
                longest = MathF.Max(longest, MathF.Log(MathF.Max(1.001f, a / (loudest * 0.001f))) / modes[i].Decay);
            }
            seconds = MathF.Max(seconds, MathF.Min(2.5f, longest + 0.02f));
        }
        float lead = 0.001f;
        int n = (int)MathF.Ceiling((MathF.Min(seconds, maxSeconds) + lead) * sampleRate);
        var p = new float[n];

        // 1. The crack.
        float tau = ContactSeconds(h);
        float sigma = MathF.Max(tau / 4f, MinSigma);
        AddPulse(p, lead + 3f * sigma, sigma, CrackPascals(h), sampleRate);

        // 2. What the face does.
        switch (kind)
        {
            case Kind.Brittle when random:
            {
                // Fracture: a crackle of micro-cracks over a few milliseconds, more for a bigger crater.
                float crater = CraterCubicMetres(h);
                int cracks = Math.Clamp((int)MathF.Round(8f + 4f * MathF.Sqrt(crater * 1e6f)), 6, 60);
                float span = 0.002f + 0.004f * (float)rng.NextDouble();
                float each = 0.4f * CrackPascals(h) / MathF.Sqrt(cracks) * 1.5f;
                for (int i = 0; i < cracks; i++)
                {
                    float at = lead + 3f * sigma + span * MathF.Pow((float)rng.NextDouble(), 1.8f);
                    float a = each * (0.3f + 0.7f * (float)rng.NextDouble()) * (rng.Next(2) == 0 ? 1f : -1f);
                    AddPulse(p, at, MinSigma * (1f + 2f * (float)rng.NextDouble()), a, sampleRate);
                }
                // The pulverised crater leaving as dust: a short puff of noise.
                float dust = VolumePascals(3f * crater, 0.005f) * 0.7f;
                AddNoiseBurst(p, lead, 0.004f, 0.03f, dust, 600f, 5000f, rng, sampleRate);
                break;
            }
            case Kind.Soil:
            {
                AddVolumePulse(p, lead, OpeningSeconds(h), VolumePascals(CraterCubicMetres(h), OpeningSeconds(h)), sampleRate);
                // The spray leaving: a rush of grains.
                if (random) AddNoiseBurst(p, lead + 0.001f, 0.01f, 0.06f, 0.15f * VolumePascals(CraterCubicMetres(h), OpeningSeconds(h)),
                              h.Material.Equals("Grass", StringComparison.OrdinalIgnoreCase) ? 1500f : 400f, 6000f, rng, sampleRate);
                break;
            }
            case Kind.Water:
            {
                float cavity = CavityCubicMetres(h);
                AddVolumePulse(p, lead + 0.0003f, OpeningSeconds(h), VolumePascals(cavity, OpeningSeconds(h)), sampleRate);
                // The pinch-off: a bubble of about a calibre and a quarter in radius, ringing at its
                // Minnaert frequency 3.26/R Hz (R in metres), damped δ 0.08, rising as it nears the
                // surface; 15-40 ms after entry.
                float radius = 1.25f * h.Diameter * (1f + 0.3f * (float)fixedRng.NextDouble());
                float hz = 3.26f / radius;
                float at = lead + 0.015f + 0.025f * (float)fixedRng.NextDouble();
                AddBubble(p, at, hz, 0.08f, 0.5f * VolumePascals(cavity, OpeningSeconds(h)), 1.4f, sampleRate);
                break;
            }
            case Kind.Soft:
            {
                AddVolumePulse(p, lead, MathF.Max(1e-3f, tau), SoftThumpPascals(h), sampleRate);
                if (random && h.Material.Equals("Foliage", StringComparison.OrdinalIgnoreCase))
                    AddNoiseBurst(p, lead, 0.01f, 0.12f, 0.5f * SoftThumpPascals(h), 2000f, 9000f, rng, sampleRate);
                break;
            }
            case Kind.Wood when random:
            {
                // Splintering: fibres breaking one after another, softer and longer than stone cracking.
                int fibres = Math.Clamp((int)(10f + h.Dumped / 30f), 10, 50);
                float each = 0.25f * CrackPascals(h);
                for (int i = 0; i < fibres; i++)
                {
                    float at = lead + 3f * sigma + 0.012f * MathF.Pow((float)rng.NextDouble(), 1.5f);
                    AddPulse(p, at, MinSigma * (1.5f + 3f * (float)rng.NextDouble()), each * (0.2f + 0.8f * (float)rng.NextDouble()) * (rng.Next(2) == 0 ? 1f : -1f), sampleRate);
                }
                break;
            }
            case Kind.Panel when random:
            {
                // Gypsum dust blown out of the hole.
                float crater = CraterCubicMetres(h);
                AddNoiseBurst(p, lead, 0.006f, 0.05f, VolumePascals(3f * crater, 0.006f), 300f, 4000f, rng, sampleRate);
                break;
            }
        }

        // 3. The plate's ring.
        if (modes != null && shares != null)
        {
            float onset = lead + 3f * sigma;
            // The loudest few hundred modes carry it; the rest are tens of decibels under them.
            var amps = new float[modes.Count];
            for (int i = 0; i < modes.Count; i++) amps[i] = modes[i].PascalsPerShare * MathF.Sqrt(vib * shares[i]);
            var order = new int[modes.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (x, y) => amps[y].CompareTo(amps[x]));
            float top = order.Length > 0 ? amps[order[0]] : 0f;
            for (int r = 0; r < Math.Min(order.Length, MaxModes); r++)
            {
                int i = order[r];
                float a = amps[i];
                if (a < 1e-6f || a < top * 1e-3f) break;
                float w = 2f * MathF.PI * modes[i].Hz / sampleRate;
                float d = MathF.Exp(-modes[i].Decay / sampleRate);
                // A struck mode starts from rest and leaves as a sine: y[k] = a·d^k·sin(wk), run as the
                // recursion y[k] = 2d·cos(w)·y[k−1] − d²·y[k−2] from y[0] = 0, y[1] = a·d·sin(w).
                int start = (int)(onset * sampleRate);
                float c1 = 2f * MathF.Cos(w) * d, c2 = d * d;
                float prev2 = 0f, prev1 = signs![i] * a * d * MathF.Sin(w);
                if (start + 1 < n) p[start + 1] += prev1;
                float floor = 1e-5f * top;
                for (int k = start + 2; k < n; k++)
                {
                    float y = c1 * prev1 - c2 * prev2;
                    prev2 = prev1; prev1 = y;
                    p[k] += y;
                    if ((k & 511) == 0 && MathF.Abs(y) < floor && MathF.Abs(prev2) < floor) break;
                }
            }
        }
        return p;
    }

    /// <summary>What a strike throws off, landing: a patter of chips, clods, splinters, crumbs or drops,
    /// each when its own flight brings it down and as loud as its landing, over the full scale.</summary>
    public static float[] RenderDebris(Hit h, int sampleRate, int seed) => RenderDebris(h, sampleRate, seed, out _);

    public static float[] RenderDebris(Hit h, int sampleRate, int seed, out float rawPeak)
    {
        if (KindOf(h.Material) == Kind.Water) return RenderSplash(h, sampleRate, seed, out rawPeak);
        var s = StuffOf(h.Material);
        var pieces = Pieces(h);
        float fullScale = DebrisFullScalePascals(h);
        uint hash = BulletFlyby.Mix(BulletFlyby.Mix((uint)h.Speed, (uint)(h.MassMg + 13 * h.DropCm)), (uint)(seed & 3) + 211u);
        var rng = new Random((int)(hash & 0x7fffffff));
        var landings = new List<(float At, float Pa, float Contact, bool Bubble, float Hz)>();
        float density = s.Kind == Kind.Water ? 1000f : s.DebrisDensity;
        foreach (var piece in pieces)
        {
            // The seed moves each piece's launch a little, so four renders are four sprays.
            var q = piece with
            {
                Speed = piece.Speed * (0.85f + 0.3f * (float)rng.NextDouble()),
                Elevation = Math.Clamp(piece.Elevation + 0.3f * ((float)rng.NextDouble() - 0.5f), 0f, 1.5f),
            };
            var (t, v) = Land(q, density, h.Drop, h.OnWall);
            float pa = LandingPascals(q.Mass, v);
            float contact = MathF.Max(q.Contact > 0f ? q.Contact : s.ChipContactSeconds, s.ChipContactSeconds);
            landings.Add((t, pa, contact, false, 0f));
            // One bounce, at a third of the speed, after its own short hop.
            float vb = 0.33f * v;
            landings.Add((t + 2f * vb * 0.5f / 9.81f + 0.004f, LandingPascals(q.Mass, vb), contact, false, 0f));
        }
        float end = 0.05f;
        foreach (var l in landings) end = MathF.Max(end, l.At + (l.Bubble ? 0.02f : 0.005f));
        int n = (int)MathF.Ceiling((end + 0.01f) * sampleRate);
        var p = new float[n];
        foreach (var l in landings)
        {
            if (l.Bubble) AddBubble(p, l.At, l.Hz, 0.04f, l.Pa, 1.05f, sampleRate);
            else AddPulse(p, l.At, MathF.Max(MinSigma, l.Contact / 4f), l.Pa * (rng.Next(2) == 0 ? 1f : -1f), sampleRate);
        }
        rawPeak = 0f;
        for (int i = 0; i < n; i++) { rawPeak = MathF.Max(rawPeak, MathF.Abs(p[i]) / fullScale); p[i] = Math.Clamp(p[i] / fullScale, -1f, 1f); }
        return p;
    }

    // ── Pieces of sound ─────────────────────────────────────────────────────────────────────────

    /// <summary>A Gaussian's derivative, peak ±<paramref name="amplitude"/> at ±σ about <paramref name="at"/>:
    /// the pressure of a force rising and falling over about 4σ.</summary>
    private static void AddPulse(float[] p, float at, float sigma, float amplitude, int sr)
    {
        int from = Math.Max(0, (int)((at - 5f * sigma) * sr)), to = Math.Min(p.Length - 1, (int)((at + 5f * sigma) * sr) + 1);
        float k = amplitude * MathF.Sqrt(MathF.E);
        for (int i = from; i <= to; i++)
        {
            float z = (i / (float)sr - at) / sigma;
            p[i] += -k * z * MathF.Exp(-0.5f * z * z);
        }
    }

    /// <summary>A volume source opening over T: ρV̈/4π with V(t) = V(1 − (1 + t/T)e^(−t/T)), so the
    /// pressure is peak·(1 − t/T)e^(−t/T), its first edge rounded over a twentieth of T.</summary>
    private static void AddVolumePulse(float[] p, float at, float T, float peak, int sr)
    {
        int from = Math.Max(0, (int)(at * sr)), to = Math.Min(p.Length - 1, (int)((at + 12f * T) * sr));
        float rise = 0.05f * T;
        for (int i = from; i <= to; i++)
        {
            float t = i / (float)sr - at;
            float edge = t < rise ? 0.5f - 0.5f * MathF.Cos(MathF.PI * t / rise) : 1f;
            p[i] += peak * edge * (1f - t / T) * MathF.Exp(-t / T);
        }
    }

    /// <summary>A bubble ringing: a damped sine at <paramref name="hz"/>, its damping constant δ (the
    /// amplitude falls by e^(−πδ) a cycle), its pitch gliding by <paramref name="glide"/> over its ring.</summary>
    private static void AddBubble(float[] p, float at, float hz, float delta, float amplitude, float glide, int sr)
    {
        float decay = MathF.PI * delta * hz;
        float ring = 6.9f / decay;
        int from = Math.Max(0, (int)(at * sr)), to = Math.Min(p.Length - 1, (int)((at + ring) * sr));
        double phase = 0;
        for (int i = from; i <= to; i++)
        {
            float t = i / (float)sr - at;
            float f = hz * (1f + (glide - 1f) * MathF.Min(1f, t / ring));
            if (f > 0.45f * sr) break;
            phase += 2 * Math.PI * f / sr;
            float attack = MathF.Min(1f, t * hz);
            p[i] += amplitude * attack * MathF.Exp(-decay * t) * (float)Math.Sin(phase);
        }
    }

    /// <summary>A puff of band-limited noise: rises over <paramref name="rise"/>, decays with time
    /// constant <paramref name="decay"/>; its rms at the peak is <paramref name="rms"/>.</summary>
    private static void AddNoiseBurst(float[] p, float at, float rise, float decay, float rms, float lowHz, float highHz, Random rng, int sr)
    {
        if (rms <= 0f) return;
        float a1 = MathF.Exp(-2f * MathF.PI * highHz / sr);   // two one-pole low-passes
        float a2 = MathF.Exp(-2f * MathF.PI * lowHz / sr);    // minus a one-pole low-pass: a high-pass
        float lp0 = 0f, lp = 0f, lp2 = 0f;
        // The band's own gain on white noise, so the rms comes out as asked.
        float gain = rms / MathF.Sqrt(MathF.Max(1e-4f, (highHz - lowHz) / (0.5f * sr))) * 1.7320508f;
        int from = Math.Max(0, (int)(at * sr)), to = Math.Min(p.Length - 1, (int)((at + rise + 6f * decay) * sr));
        for (int i = from; i <= to; i++)
        {
            float t = i / (float)sr - at;
            float x = (float)(rng.NextDouble() * 2 - 1);
            lp0 = (1f - a1) * x + a1 * lp0;
            lp = (1f - a1) * lp0 + a1 * lp;
            lp2 = (1f - a2) * lp + a2 * lp2;
            float band = lp - lp2;
            float env = t < rise ? t / rise : MathF.Exp(-(t - rise) / decay);
            p[i] += gain * env * band;
        }
    }
}
