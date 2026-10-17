using OpenFPS.Geometry;
using OpenFPS.Common;

namespace OpenFPS.Acoustics;

/// <summary>
/// Walls built of several stuffs in one box: a timber-framed house wall is cladding, sheathing, a stud cavity
/// and plasterboard, and sound crossing it crosses each in turn (<see cref="WallTransmission.LayeredBandGains"/>:
/// the plies either side of a cavity are a leaf each, Sharp's double-leaf law between them, the studs bridging).
/// A prefab names one (its "Assembly"); its box's own material is its face, and its thickness is the plies'.
///
/// <para>The plies run across the box's thickness, from the face its thickness axis points away from to the face it
/// points to: a generated wall's outside to its inside. A cavity is a ply of something softer than the air in it
/// (<see cref="WallTransmission.IsCavity"/>): glass-fibre batts between studs, or the air behind a brick veneer. A
/// box thicker or thinner than its plies keeps its boards and gives the difference to its cavities.</para>
///
/// <para>Sources. Bradley and Birta, "The attenuation of aircraft noise by wood stud walls", Canadian Acoustics 29(3)
/// (2001) 64-65 (https://jcaa.caa-aca.ca/index.php/jcaa/article/download/1385/1128/1518): North American exterior walls
/// measured to ASTM E90, vinyl siding on OSB sheathing, 89 or 140 mm wood studs at 406 mm with glass fibre filling the
/// cavity, one 13 mm gypsum board inside; the studs' structural resonance is a dip at 125 Hz, the boards' coincidence
/// one at 2.5 to 3.1 kHz, and brick cladding over a 16 mm air gap with occasional ties did better than its weight.
/// Brick Industry Association Technical Note 28, "Brick Veneer/Wood Stud Walls" (2012,
/// https://gobrick.com/media/file/28-brick-veneer-wood-stud-walls.pdf): a nominal 3 or 4 in. brick wythe, a 1 in.
/// (25 mm) air space customary and required (IRC: nominal), one anchor per 2.67 sq ft, so only ties bridge the air
/// space; measured STC up to 56. Western Wood Products Association, Standard Patterns (pattern book): 1/2 x 6 bevel
/// siding is 5 1/2 in. dressed, laid 4 1/2 in. to the weather. Canadian Wood Council, OSB sizes: 7/16 in. (11 mm)
/// sheathing about 46 lb a 4 x 8 sheet, 7 kg/m2 (https://old.cwc.ca/wp-content/uploads/2019/03/Oriented-Strand-Board-OSB-Sizes.pdf).
/// Owens Corning's guide specification for batts (https://www.owenscorning.com/dms/10024955): R-13 is 3 1/2 in. (89 mm)
/// for wood framing. A 2x4 stud is 1 1/2 by 3 1/2 in. dressed (NIST PS 20, American Softwood Lumber Standard); studs
/// at 16 in. (406 mm) centres (IRC R602.3(5) allows 16 or 24; Bradley and Birta's walls are at 406). Plasterboard
/// 1/2 in. (12.7 mm), ASTM C1396.</para>
/// </summary>
public static class Assemblies
{
    /// <summary>One ply: what it is made of, how thick, the centres of the studs it is built between (a cavity's), and
    /// whether it is boards lapped over one another (siding) rather than a sheet: nailed on in courses that are not joined
    /// across, it adds its weight to the leaf it is on and nothing to the leaf's stiffness.</summary>
    public readonly record struct Ply(string Material, float Metres, float StudSpacingMetres = 0f, bool Lapped = false);

    /// <summary>An assembly: its number (what a wall carries, <see cref="WallBuild.Assembly"/>), its name (what a prefab
    /// calls it), its plies in order and what they come to.</summary>
    public sealed class Assembly
    {
        public int Id { get; }
        public string Name { get; }
        public IReadOnlyList<Ply> Plies { get; }
        /// <summary>The plies' thickness together, metres: the box's when it is built as listed.</summary>
        public float Metres { get; }

        internal Assembly(int id, string name, Ply[] plies)
        {
            Id = id; Name = name; Plies = plies;
            float m = 0f;
            foreach (var p in plies) m += p.Metres;
            Metres = m;
        }
    }

    // A wall's lap siding: 1/2 x 6 bevel boards taper from about 12 mm to 5 mm (EST from the nominal half inch) and
    // cover 4 1/2 in. of wall each with 5 1/2 in. of board (WWPA), so 10 mm of board over the wall on average. Its
    // courses are lapped, not joined, so it is weight on the sheathing (Ply.Lapped): bonded as a plate, the siding and
    // sheathing together put a coincidence dip at 800 Hz to 1 kHz where Bradley and Birta measured the boards' at 2.5 kHz.
    private const float LapSiding = 0.010f;
    // 7/16 in. structural panel sheathing (CWC); plywood's figures stand for OSB's (7 kg/m2 either way).
    private const float Sheathing = 0.0111f;
    // A 2x4's depth, the cavity its batt fills (PS 20; Owens Corning R-13).
    private const float StudCavity = 0.0889f;
    // 16 in. centres (IRC R602.3(5); Bradley and Birta).
    private const float Studs = 0.4064f;
    // 1/2 in. gypsum board (ASTM C1396).
    private const float Board = 0.0127f;
    // A modular brick is 3 5/8 in. through, nominal 4 (BIA TN 28 gives the wythe as nominal 3 or 4 in.).
    private const float BrickWythe = 0.0921f;
    // The air space behind it, 1 in. nominal (BIA TN 28, IRC); only ties cross it, so nothing bridges it but its edges.
    private const float VeneerAir = 0.0254f;
    // A shed's walls: 5/8 in. plywood siding nailed straight to its studs (APA's T1-11), the shed's inside between
    // them and nothing bridging the two walls but the floor and roof at their edges.
    private const float ShedSiding = 0.0159f;

    private static readonly Assembly[] Table =
    {
        new(1, "WoodFrameSiding", new Ply[]
        {
            new("Wood", LapSiding, Lapped: true), new("Plywood", Sheathing), new("GlassWool", StudCavity, Studs), new("Plaster", Board),
        }),
        // The same wall where its plasterboard is a sheet of its own against it (a house built room by room).
        new(2, "WoodFrameSidingUnlined", new Ply[]
        {
            new("Wood", LapSiding, Lapped: true), new("Plywood", Sheathing), new("GlassWool", StudCavity, Studs),
        }),
        new(3, "WoodFrameBrickVeneer", new Ply[]
        {
            new("Brick", BrickWythe), new("None", VeneerAir), new("Plywood", Sheathing), new("GlassWool", StudCavity, Studs),
            new("Plaster", Board),
        }),
        new(4, "WoodFrameBrickVeneerUnlined", new Ply[]
        {
            new("Brick", BrickWythe), new("None", VeneerAir), new("Plywood", Sheathing), new("GlassWool", StudCavity, Studs),
        }),
        // A shed as one box: two walls of siding and the air of the shed between (the box's thickness less its boards).
        new(5, "WoodFrameShed", new Ply[]
        {
            new("Plywood", ShedSiding), new("None", StudCavity), new("Plywood", ShedSiding),
        }),
    };

    private static readonly Dictionary<int, Assembly> ById = Table.ToDictionary(a => a.Id);
    private static readonly Dictionary<string, Assembly> ByName = Table.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every assembly in the table.</summary>
    public static IReadOnlyList<Assembly> All => Table;

    /// <summary>The assembly with this number, or null (0 is none).</summary>
    public static Assembly? Get(int id) => id != 0 && ById.TryGetValue(id, out var a) ? a : null;

    /// <summary>The assembly a prefab calls <paramref name="name"/>, or null.</summary>
    public static Assembly? Named(string? name) => !string.IsNullOrEmpty(name) && ByName.TryGetValue(name, out var a) ? a : null;

    /// <summary>Whether an assembly has a cavity among its plies (a line run along it runs in that, Constructions.Of).</summary>
    public static bool HasCavity(Assembly a)
    {
        foreach (var p in a.Plies) if (WallTransmission.IsCavity(AcousticRegistry.GetProperties(p.Material))) return true;
        return false;
    }

    /// <summary>Its weight on a square metre of wall built <paramref name="thickness"/> thick, kg.</summary>
    public static float MassPerArea(Assembly a, float thickness)
    {
        var plies = new List<WallTransmission.Layer>(a.Plies.Count);
        Append(a, thickness, false, plies);
        float m = 0f;
        foreach (var l in plies) m += l.Props.DensityKgM3 * l.Thickness;
        return m;
    }

    /// <summary>
    /// Its plies as layers across a box <paramref name="thickness"/> thick, in the order a line crossing the box from the
    /// face its thickness axis points away from meets them (backwards when <paramref name="reversed"/>). The boards keep
    /// their thickness and the cavities share what is left; a box too thin for its boards is its boards made thinner.
    /// </summary>
    public static void Append(Assembly a, float thickness, bool reversed, List<WallTransmission.Layer> into)
    {
        float boards = 0f, cavities = 0f;
        foreach (var p in a.Plies)
        {
            if (WallTransmission.IsCavity(AcousticRegistry.GetProperties(p.Material))) cavities += p.Metres;
            else boards += p.Metres;
        }
        float boardScale = boards > 0f && thickness < boards ? thickness / boards : 1f;
        float left = MathF.Max(0f, thickness - boards * boardScale);
        // With no cavity to take it, what is left is shared by the boards.
        if (cavities <= 0f && boards > 0f) boardScale = thickness / boards;
        int n = a.Plies.Count;
        for (int k = 0; k < n; k++)
        {
            var p = a.Plies[reversed ? n - 1 - k : k];
            var props = AcousticRegistry.GetProperties(p.Material);
            bool cavity = WallTransmission.IsCavity(props);
            if (p.Lapped) props.YoungsModulusGPa = 0f;
            float t = cavity ? (cavities > 0f ? left * p.Metres / cavities : 0f) : p.Metres * boardScale;
            into.Add(new WallTransmission.Layer(props, t, cavity ? new WallBuild(0f, p.StudSpacingMetres) : WallBuild.Solid));
        }
    }
}
