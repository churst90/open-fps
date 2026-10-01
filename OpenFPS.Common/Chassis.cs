using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenFPS.Common;

// ── The running gear, as data ───────────────────────────────────────────────────────────────────
//
// What a vehicle stands on: its axles, the tyres on them, which ones drive, steer and brake, and
// where its weight is. Every number here is a fact about a real vehicle, declared on its preset
// with the source beside it, so the wheel model (WheelDynamics) has nothing of its own to invent.
//
// See docs/NEXT_BODIES_WHEELS_ROADS.md, stage 3.

/// <summary>How a wheel is braked. Discs on cars; drums behind air on most heavy vehicles.</summary>
public enum BrakeKind { Disc, Drum }

/// <summary>
/// A tyre's size, as moulded on its sidewall: section width in millimetres, the sidewall height as
/// a percentage of that width, and the rim diameter in inches. "205/55R16" is 205 mm wide, a
/// sidewall 55 % of that (112.75 mm), on a 16 inch rim.
/// </summary>
public sealed record TyreSize
{
    public required float WidthMm { get; init; }
    public required float AspectPercent { get; init; }
    public required float RimInches { get; init; }

    /// <summary>
    /// The effective rolling radius over the unloaded radius: distance travelled per radian of wheel
    /// rotation, against the radius of the tyre standing free.
    ///
    /// A loaded tyre is flattened at the contact patch, but its tread belt hardly stretches, so it
    /// rolls further per turn than its loaded height suggests: the effective rolling radius of a
    /// radial tyre is close to the free radius less a third of its vertical deflection (r_e = R0 -
    /// d/3; Jazar, Vehicle Dynamics: Theory and Application, 2008 — to confirm). With a static
    /// deflection of 3 to 6 % of the radius that is 0.98 to 0.99 of R0. At 0.98 the 255/40R19 the
    /// tread tone always assumed rolls at the 0.337 m it used.
    /// </summary>
    public float RollingRadiusFactor { get; init; } = 0.98f;

    /// <summary>Half the rim diameter plus the sidewall, metres.</summary>
    public float UnloadedRadiusMetres => RimInches * 0.0254f * 0.5f + WidthMm * AspectPercent / 100f / 1000f;

    /// <summary>Metres travelled per radian of rotation.</summary>
    public float RollingRadiusMetres => UnloadedRadiusMetres * RollingRadiusFactor;

    /// <summary>The construction letter between the aspect and the rim: "R" radial, "ZR" a radial
    /// rated above 240 km/h, "B" bias-belted, "-" or "D" bias. It does not change the size.</summary>
    public string Construction { get; init; } = "R";

    public string Code => string.Create(CultureInfo.InvariantCulture, $"{WidthMm:0.#}/{AspectPercent:0.#}{Construction}{RimInches:0.#}");

    private static readonly Regex Pattern = new(
        @"^\s*(?:P|LT)?(\d+(?:\.\d+)?)\s*/\s*(\d+(?:\.\d+)?)\s*(Z?R|B|D|-)\s*(\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A size from its sidewall code: "205/55R16", "P225/60R16", "LT245/75R16", "235/35ZR20",
    /// "120/70ZR17", "130/90B16" (a bias-belted motorcycle tyre) or "80/100-21" (a bias dirt tyre).
    /// The load index and speed rating after the size are ignored.
    /// </summary>
    public static TyreSize Parse(string code)
    {
        var m = Pattern.Match(code ?? "");
        if (!m.Success) throw new FormatException($"Not a tyre size: '{code}'. Expected width/aspect R rim, e.g. 205/55R16.");
        return new TyreSize
        {
            WidthMm = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            AspectPercent = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            Construction = m.Groups[3].Value.ToUpperInvariant(),
            RimInches = float.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
        };
    }

    public override string ToString() => Code;
}

/// <summary>One axle: where it is, how wide, what it carries, and what it does.</summary>
public sealed record AxleSpec
{
    /// <summary>
    /// Metres forward of the body's centre (the frame of <see cref="VehicleProfile.FrontAxleZ"/>).
    /// Unset, it is placed from the profile: the first axle at <see cref="VehicleProfile.FrontAxleZ"/>,
    /// any other at <see cref="VehicleProfile.RearAxleZ"/> plus <see cref="TandemOffset"/> — so the
    /// wheels stand where the sound rig already puts the axles.
    /// </summary>
    public float Z { get; init; } = float.NaN;

    /// <summary>For an axle of a tandem placed from the profile: metres forward (+) or back (-) of the
    /// profile's rear axle position, which is then the middle of the pair.</summary>
    public float TandemOffset { get; init; }

    /// <summary>
    /// Centre of the left tyre's contact patch to centre of the right's, metres. For dual wheels, to
    /// the middle of each pair. Zero is one wheel on the centreline: a motorcycle.
    /// </summary>
    public float TrackMetres { get; init; }

    public required TyreSize Tyre { get; init; }

    /// <summary>Tyres at each wheel position: 1, or 2 for duals.</summary>
    public int TyresPerWheel { get; init; } = 1;

    public bool Driven { get; init; }
    public bool Steered { get; init; }
    public BrakeKind Brake { get; init; } = BrakeKind.Disc;

    /// <summary>Wheel positions on this axle: two, or one on the centreline.</summary>
    public int Wheels => TrackMetres > 0f ? 2 : 1;
}

/// <summary>
/// The running gear of a vehicle and where its mass sits. See <see cref="VehicleProfile.Chassis"/>.
/// </summary>
public sealed record ChassisSpec
{
    /// <summary>Front to back.</summary>
    public required AxleSpec[] Axles { get; init; }

    /// <summary>
    /// Height of the centre of gravity above the ground, metres. It decides how much load moves
    /// between the wheels when the vehicle brakes, accelerates or corners: m a h / wheelbase
    /// fore and aft, m a h / track side to side.
    /// </summary>
    public float CentreOfGravityHeightMetres { get; init; } = 0.55f;

    /// <summary>
    /// The share of the weight on the front axle group standing still, 0..1: the published front/rear
    /// split. Null puts the centre of gravity at the middle of the body, as it always was.
    /// </summary>
    public float? FrontWeightShare { get; init; }

    /// <summary>
    /// Metres forward of the body's centre that the centre of gravity sits: from
    /// <see cref="FrontWeightShare"/>, between the middles of the front and rear axle groups
    /// (z = rear + share x (front - rear)); zero without one.
    /// </summary>
    public float CentreOfGravityZ
    {
        get
        {
            if (FrontWeightShare is not float share || !Groups(out float front, out float rear)) return 0f;
            return rear + Math.Clamp(share, 0f, 1f) * (front - rear);
        }
    }

    /// <summary>
    /// The middles of the front and rear axle groups: the axles in the front half of the span
    /// between the first and last axle, and the rest. A tandem is one group.
    /// </summary>
    public bool Groups(out float front, out float rear)
    {
        front = rear = 0f;
        if (Axles.Length < 2) return false;
        float hi = Axles.Max(a => a.Z), lo = Axles.Min(a => a.Z), mid = 0.5f * (hi + lo);
        if (hi - lo < 1e-3f) return false;
        front = Axles.Where(a => a.Z > mid).Average(a => a.Z);
        rear = Axles.Where(a => a.Z <= mid).Average(a => a.Z);
        return true;
    }

    /// <summary>
    /// This chassis with every axle placed: those declared without a position go where the profile's
    /// axle positions say (see <see cref="AxleSpec.Z"/>).
    /// </summary>
    public ChassisSpec PlacedOn(VehicleProfile p)
    {
        if (Axles.All(a => !float.IsNaN(a.Z))) return this;
        var placed = new AxleSpec[Axles.Length];
        for (int i = 0; i < Axles.Length; i++)
        {
            var a = Axles[i];
            placed[i] = float.IsNaN(a.Z) ? a with { Z = i == 0 ? p.FrontAxleZ : p.RearAxleZ + a.TandemOffset } : a;
        }
        return this with { Axles = placed };
    }

    /// <summary>
    /// Full lock from a published turning circle: the outer front wheel runs on the circle, so with
    /// R its radius, L the wheelbase and t the front track, the rear axle's middle turns about a
    /// radius sqrt(R^2 - L^2) - t/2, and the steering angle of a single wheel at the front axle's
    /// middle that gives it is atan(L / that) (Gillespie 1992, chapter 6). Kerb-to-kerb circles,
    /// measured at the tyres.
    /// </summary>
    public static float LockFromTurningCircle(float diameterMetres, float wheelbaseMetres, float frontTrackMetres)
    {
        float r = 0.5f * diameterMetres;
        float rear = MathF.Sqrt(MathF.Max(1e-3f, r * r - wheelbaseMetres * wheelbaseMetres)) - 0.5f * frontTrackMetres;
        return MathF.Atan(wheelbaseMetres / MathF.Max(0.1f, rear));
    }

    /// <summary>Road-wheel angle at full lock, radians.</summary>
    public float MaxSteerAngleRad { get; init; } = 0.61f;

    /// <summary>
    /// The front axle's share of the body's roll stiffness, 0..1, which is how the lateral load
    /// transfer divides between the axles. Null when nothing is published for the vehicle: the
    /// transfer then divides in proportion to the static axle loads.
    /// </summary>
    public float? FrontRollStiffnessShare { get; init; }

    /// <summary>
    /// Yaw moment of inertia over m a b, with a and b the distances from the centre of gravity to the
    /// front and rear axles: the "dynamic index" of Milliken and Milliken (Race Car Vehicle Dynamics,
    /// 1995, chapter 5), close to 1 for most passenger cars.
    /// </summary>
    public float YawInertiaIndex { get; init; } = 1f;

    /// <summary>
    /// How much of true Ackermann geometry the steering has, 0 (parallel) to 1 (each front wheel
    /// turned about the rear axle's turning centre). Road cars are built close to Ackermann so the
    /// front tyres do not scrub at parking speed (Gillespie, Fundamentals of Vehicle Dynamics, 1992,
    /// chapter 6).
    /// </summary>
    public float Ackermann { get; init; } = 1f;

    /// <summary>
    /// A chassis for a preset that declares none, built from what it does declare so that nothing
    /// about it changes: two axles at its axle positions, rear-wheel drive, its width less the tyres
    /// as the track (the mean track-to-width ratio of the declared presets, 0.85), the centre of
    /// gravity in the middle of the body, and the 255/40R19 whose 0.337 m rolling radius the tyre
    /// tone always assumed. Drums above five tonnes, discs below — the rule the brake squeal used.
    /// </summary>
    public static ChassisSpec Default(VehicleProfile p)
    {
        bool single = p.TyreCount <= 2;
        float track = single ? 0f : p.WidthMetres * DefaultTrackOverWidth;
        var tyre = TyreSize.Parse("255/40R19");
        var brake = p.MassKg > 5000f ? BrakeKind.Drum : BrakeKind.Disc;
        return new ChassisSpec
        {
            Axles = new[]
            {
                new AxleSpec { TrackMetres = track, Tyre = tyre, Steered = true, Brake = brake },
                new AxleSpec { TrackMetres = track, Tyre = tyre, Driven = true, Brake = brake,
                               TyresPerWheel = single ? 1 : Math.Max(1, (p.TyreCount - 2) / 2) },
            },
        };
    }

    /// <summary>Track over body width where nothing better is known: the mean of the cars whose tracks
    /// and widths are both published among the presets (0.85).</summary>
    public const float DefaultTrackOverWidth = 0.85f;

    /// <summary>
    /// The effective wheelbase, metres: from the middle of the front axle group to the middle of the
    /// rear one. Two axles, it is the distance between them; a tandem counts as the point between its
    /// pair. Of a placed chassis (<see cref="PlacedOn"/>).
    /// </summary>
    public float Wheelbase => Groups(out float front, out float rear) ? front - rear : 0f;

    /// <summary>Tyres on steered axles: the front tyre voice's share of the rolling noise.</summary>
    public int SteeredTyres => Axles.Where(a => a.Steered).Sum(a => a.Wheels * a.TyresPerWheel);

    /// <summary>The axle whose brakes do most of the stopping: the front one.</summary>
    public BrakeKind MainBrake => Axles.Length > 0 ? Axles[0].Brake : BrakeKind.Disc;
}

/// <summary>
/// The road surfaces a wheel can stand on, as an index the wire can carry in a byte, and how much
/// grip each gives against dry asphalt.
///
/// The ratios are the average peak coefficients of road adhesion in Wong, Theory of Ground Vehicles
/// (2nd ed. 1993, p. 26, as reproduced at hpwizard.com/tire-friction-coefficient.html; the table's
/// number in later editions to confirm): asphalt and concrete (dry) 0.8-0.9, gravel 0.6, earth road (dry) 0.68,
/// snow (hard-packed) 0.2, ice 0.1, each divided by the 0.85 middle of the dry asphalt range. A
/// preset's tyre grip is its grip on dry asphalt; anything the table does not cover rolls as
/// asphalt does.
/// </summary>
public static class RoadSurfaces
{
    /// <summary>Not on the ground, or a material the table does not know.</summary>
    public const byte Unknown = 0;

    private static readonly (string Material, float Grip)[] Table =
    {
        ("", 1f),                 // 0: unknown
        ("Asphalt", 1f),          // 1
        ("Concrete", 1f),         // 2
        ("Gravel", 0.6f / 0.85f), // 3
        ("Dirt", 0.68f / 0.85f),  // 4: an earth road, dry
        ("Snow", 0.2f / 0.85f),   // 5: hard-packed
        ("Ice", 0.1f / 0.85f),    // 6
    };

    private static readonly Dictionary<string, byte> ByName = Build();

    private static Dictionary<string, byte> Build()
    {
        var d = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < Table.Length; i++) d[Table[i].Material] = (byte)i;
        return d;
    }

    /// <summary>The index of a material name; <see cref="Unknown"/> for anything else.</summary>
    public static byte IndexOf(string? material)
        => material != null && ByName.TryGetValue(material, out byte i) ? i : Unknown;

    /// <summary>The material at an index, "" for unknown.</summary>
    public static string NameOf(byte index) => index < Table.Length ? Table[index].Material : "";

    /// <summary>Grip relative to dry asphalt.</summary>
    public static float GripOf(byte index) => index < Table.Length ? Table[index].Grip : 1f;
}
