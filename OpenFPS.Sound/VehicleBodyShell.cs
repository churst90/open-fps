using System.Numerics;
using OpenFPS.Geometry;

namespace OpenFPS.Common;

/// <summary>
/// A vehicle's body as one closed mesh, made from its parts list (docs/GEOMETRY.md, stage 6;
/// docs/PHYSICS_BOUNDARY.md, stage 4 and decision 13): the shell (<see cref="BodyShell"/>), what each of its
/// faces is made of, and where each of the vehicle's own sources comes out on the outside of it.
///
/// The size is the body's, not the sound preset's: a vehicle that carries its own length, width and height is
/// meshed at them, and the parts the preset measured on its own car move with the body (a tailpipe at the
/// back of a short car is at the back of a long one). The cabin is <see cref="VehicleCabin"/>'s, the same one
/// the server builds to sit in and the client traces for its sound.
/// </summary>
/// <param name="Shell">The mesh and the sources' points, in the vehicle's frame (x right, y up from the road,
/// z forward).</param>
/// <param name="Sources">Each of <see cref="BodyShell.Sources"/> by the part it is and which of them.</param>
/// <param name="Materials">The acoustic material of each <see cref="BodyFace"/>, by its number.</param>
public sealed record VehicleBodyShell(BodyShell Shell, IReadOnlyList<VehicleBodyShell.Source> Sources, IReadOnlyList<string> Materials)
{
    /// <summary>One source of the vehicle's own: the part of its parts list (<see cref="MachineModels"/>), which
    /// of that part (a tyre's wheel, counted axle by axle, left first), and where it comes out.</summary>
    public readonly record struct Source(string Part, int Index, Vector3 At);

    /// <summary>An end of the body shorter than this is the cabin's own end: a van's nose, a bus's back
    /// (as <see cref="VehicleCabin.Shell"/> has it).</summary>
    public const float ShortestEnd = 0.2f;

    /// <summary>The material of a face.</summary>
    public string MaterialOf(BodyFace face) => Materials[(int)face];

    /// <summary>The shell of <paramref name="vehicle"/>, from its parts list (<paramref name="parts"/>, which the
    /// catalogue describes it as: MachineRegistry.Describe), at its own body's size or at <paramref name="size"/>
    /// (width, height, length). MachineRegistry.Shell makes one by the vehicle's id.</summary>
    public static VehicleBodyShell For(VehicleProfile vehicle, MachineDefinition parts, Vector3? size = null)
    {
        var body = size is { } given
            ? vehicle with { WidthMetres = given.X, HeightMetres = given.Y, LengthMetres = given.Z }
            : vehicle;
        // Where a part sat on the preset's body, on this one.
        var scale = new Vector3(body.WidthMetres / vehicle.WidthMetres, body.HeightMetres / vehicle.HeightMetres,
                                body.LengthMetres / vehicle.LengthMetres);

        var names = new List<(string Part, int Index)>();
        var at = new List<Vector3>();
        foreach (var part in parts.Parts)
        {
            if (part.Model is not (MachineModels.Exhaust or MachineModels.Intake)) continue;
            names.Add((part.Model, 0));
            at.Add(part.At * scale);
        }
        // A tyre is heard where it meets the road, under the body.
        int wheel = 0;
        foreach (var axle in vehicle.Running.Axles)
        {
            float half = axle.TrackMetres * 0.5f;
            for (int side = 0; side < axle.Wheels; side++)
            {
                names.Add((MachineModels.Tyres, wheel++));
                at.Add(new Vector3(axle.Wheels == 1 ? 0f : side == 0 ? -half : half, 0f, axle.Z) * scale);
            }
        }

        var shell = BodyShell.Build(SpecOf(body), at);
        var sources = new Source[names.Count];
        for (int i = 0; i < sources.Length; i++) sources[i] = new Source(names[i].Part, names[i].Index, shell.Sources[i]);

        // What the panels are made of is the body part's; the glass is glass.
        string panel = parts.Part(MachineModels.Body) is { Material.Length: > 0 } b ? b.Material : VehicleCabin.MaterialOf(VehicleCabin.Steel);
        var materials = new string[Enum.GetValues<BodyFace>().Length];
        Array.Fill(materials, panel);
        materials[(int)BodyFace.Glass] = VehicleCabin.MaterialOf(VehicleCabin.Glass);
        return new VehicleBodyShell(shell, sources, materials);
    }

    /// <summary>
    /// A body as the geometry meshes it: the bonnet and the boot to the waist, the cabin between them to the
    /// roof, all as wide as the body and standing <see cref="VehicleCabin.ChassisBottom"/> off the road. A
    /// body with no cabin to sit in (a motorcycle, a formula car) is one length, as high as it is.
    /// </summary>
    public static BodyShellSpec SpecOf(VehicleProfile body)
    {
        float half = body.LengthMetres * 0.5f;
        float underside = MathF.Min(VehicleCabin.ChassisBottom, body.HeightMetres * 0.5f);
        if (VehicleCabin.Measure(body) is not { } g)
            return new BodyShellSpec(body.WidthMetres, underside, underside,
                                     new[] { new BodySection(-half, half, body.HeightMetres, Cabin: false) });

        float back = g.RearLen > ShortestEnd ? g.Back : -half;
        float front = g.FrontLen > ShortestEnd ? g.Front : half;
        var sections = new List<BodySection>(3);
        if (back > -half) sections.Add(new BodySection(-half, back, g.Belt, Cabin: false));
        sections.Add(new BodySection(back, front, body.HeightMetres, Cabin: true));
        if (front < half) sections.Add(new BodySection(front, half, g.Belt, Cabin: false));
        return new BodyShellSpec(body.WidthMetres, underside, g.Belt, sections);
    }
}
