namespace OpenFPS.Materials;

/// <summary>
/// The road surfaces a wheel can stand on, as a byte for the wire, and each one's grip against dry
/// asphalt: Wong's average peak adhesion (Theory of Ground Vehicles, 2nd ed. 1993, p. 26, as at
/// hpwizard.com/tire-friction-coefficient.html), dry asphalt and concrete 0.8-0.9, gravel 0.6, dry
/// earth 0.68, packed snow 0.2, ice 0.1, each over 0.85. Anything else rolls as asphalt.
/// </summary>
// TODO: confirm the table's number in later editions of Wong.
public static class RoadSurfaces
{
    /// <summary>Not on the ground, or a material the table does not know.</summary>
    public const byte Unknown = 0;

    /// <summary>What a road is made of where nothing says otherwise.</summary>
    public const string Default = "Asphalt";

    // StickSlip: whether a fully sliding tyre keeps sticking and slipping. On a dry coherent surface it
    // screeches on at a fundamental near 800 Hz with its harmonic, rising with braking (Tan Li, "Tire
    // Braking/Cornering Noise Analysis: Stick/Slip Mechanism", NOISE-CON 2019); loose or icy ground
    // shears instead, so the slide there is noise.
    private static readonly (string Material, float Grip, float StickSlip)[] Table =
    {
        ("", 1f, 1f),                 // 0: unknown, rolls as asphalt
        ("Asphalt", 1f, 1f),          // 1
        ("Concrete", 1f, 1f),         // 2
        ("Gravel", 0.6f / 0.85f, 0f), // 3
        ("Dirt", 0.68f / 0.85f, 0f),  // 4: an earth road, dry
        ("Snow", 0.2f / 0.85f, 0f),   // 5: hard-packed
        ("Ice", 0.1f / 0.85f, 0f),    // 6
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

    /// <summary>1 where a fully sliding tyre keeps its stick-slip note (dry, coherent surfaces), 0 where
    /// the slide is broadband (loose or icy ones).</summary>
    public static float StickSlipOf(byte index) => index < Table.Length ? Table[index].StickSlip : 1f;
}
