using OpenFPS.Common.Editing;

namespace OpenFPS.Common;

/// <summary>
/// What a vehicle weighs, how it meets the air and the road, and how big it is: the numbers of
/// MachineRegistry's chassis part, as the world editor shows them.
/// </summary>
public sealed record VehicleChassisSpec
{
    [Tunable("kg", 50, 200000, "What it weighs with its fuel and a driver. More mass is slower to speed up and to stop.", Label = "mass", Step = 10)]
    public float MassKg { get; init; } = 1620f;

    [Tunable("m²", 0.05, 30, "Its drag coefficient times its frontal area. Sets the top speed and the wind load at speed.", Label = "drag area", Step = 0.01)]
    public float DragArea { get; init; } = 0.62f;

    [Tunable("", 0.001, 0.05, "The rolling resistance coefficient of its tyres on the road: about 0.01 for a car on asphalt.", Step = 0.001)]
    public float RollingResistance { get; init; } = 0.013f;

    [Tunable("m", -20, 30, "Where the front axle is, metres forward of the middle of the vehicle.", Label = "front axle", Step = 0.05)]
    public float FrontAxleZ { get; init; } = 1.25f;

    [Tunable("m", -30, 20, "Where the rear axle is, metres forward of the middle (negative is behind it).", Label = "rear axle", Step = 0.05)]
    public float RearAxleZ { get; init; } = -1.35f;

    [Tunable("m", 0.5, 60, "Its length, bumper to bumper.", Label = "length", Step = 0.05)]
    public float LengthMetres { get; init; } = 4.6f;

    [Tunable("m", 0.3, 10, "Its width.", Label = "width", Step = 0.05)]
    public float WidthMetres { get; init; } = 1.9f;

    [Tunable("m", 0.3, 10, "Its height.", Label = "height", Step = 0.05)]
    public float HeightMetres { get; init; } = 1.4f;

    [Tunable("dB", 50, 160, "The exhaust's level at one metre at full power. Measured on the rev bench; change it only after measuring.", Label = "exhaust level at one metre", Step = 0.5)]
    public float SourceLevelDb { get; init; } = 116f;

    [Tunable("", 1, 40, "How many tyres it runs on. Each one is heard on the road.", Label = "tyre count")]
    public int TyreCount { get; init; } = 4;
}

/// <summary>Where the exhaust gas leaves, in the vehicle's own frame: right, up and forward of its middle.</summary>
public sealed record VehicleExhaustSpec
{
    [Tunable("m", -6, 6, "How far right of the middle the tailpipe is (negative is left).", Label = "right of the middle", Step = 0.05)]
    public float OffsetX { get; init; }

    [Tunable("m", 0, 10, "How high the tailpipe is above the road.", Label = "height", Step = 0.05)]
    public float Height { get; init; } = 0.3f;

    [Tunable("m", -30, 30, "How far forward of the middle the tailpipe is (negative is behind it).", Label = "forward of the middle", Step = 0.05)]
    public float OffsetZ { get; init; } = -2.05f;
}

/// <summary>Where the engine breathes in and its block is heard from, in the vehicle's own frame.</summary>
public sealed record VehicleIntakeSpec
{
    [Tunable("m", 0, 10, "How high the intake is above the road.", Label = "height", Step = 0.05)]
    public float Height { get; init; } = 0.7f;

    [Tunable("m", -30, 30, "How far forward of the middle the intake is (negative is behind it).", Label = "forward of the middle", Step = 0.05)]
    public float OffsetZ { get; init; } = 1.35f;
}

/// <summary>
/// A road vehicle as the world editor edits it (docs/WORLD_EDITOR.md section 11.1): the engine by name,
/// the chassis, where the exhaust and intake are, and the tyres, body and gearbox records as they are.
/// Everything else a vehicle has (its siren, its air system, its fan, its starter, where its engine sits)
/// comes from <see cref="Base"/>, the way MachineRegistry's parts lists take it from their base machine.
///
/// It is a library kind (ModelLibrary.Kinds.Vehicle). An edited one is what MachineRegistry.VehicleFor
/// gives, on the server (the physics) and on every client (the sound).
/// </summary>
public sealed record VehicleSpec
{
    [Tunable("", 0, 0, "What it is called.")]
    public string Name { get; init; } = "";

    /// <summary>The vehicle this one is built on, whose everything else it has. Its own id for a
    /// built-in vehicle; the source's for a copy.</summary>
    public string Base { get; init; } = "";

    [Tunable("", 0, 0, "The engine it has, by name. Changing it changes its sound and its power.", Choices = "models:engine")]
    public string Engine { get; init; } = "";

    public VehicleChassisSpec Chassis { get; init; } = new();
    public VehicleExhaustSpec Exhaust { get; init; } = new();
    public VehicleIntakeSpec Intake { get; init; } = new();
    public TyreProfile? Tyres { get; init; }
    public VehicleBody? Body { get; init; }
    public Gearbox? Gearbox { get; init; }

    /// <summary>A vehicle in MachineRegistry as it is without any edit: the built-in model.</summary>
    public static VehicleSpec Of(string id) => From(MachineRegistry.Unedited(id), id);

    /// <summary>A vehicle, as the editor shows it, built on <paramref name="baseId"/>.</summary>
    public static VehicleSpec From(VehicleProfile v, string baseId) => new()
    {
        Name = v.Name,
        Base = baseId,
        Engine = MachineRegistry.EngineKeyOf(v.Engine),
        Chassis = new VehicleChassisSpec
        {
            MassKg = v.MassKg, DragArea = v.DragArea, RollingResistance = v.RollingResistance,
            FrontAxleZ = v.FrontAxleZ, RearAxleZ = v.RearAxleZ,
            LengthMetres = v.LengthMetres, WidthMetres = v.WidthMetres, HeightMetres = v.HeightMetres,
            SourceLevelDb = v.SourceLevelDb, TyreCount = v.TyreCount,
        },
        Exhaust = new VehicleExhaustSpec { OffsetX = v.ExhaustOffsetX, Height = v.ExhaustHeight, OffsetZ = v.ExhaustOffsetZ },
        Intake = new VehicleIntakeSpec { Height = v.IntakeHeight, OffsetZ = v.IntakeOffsetZ },
        Tyres = v.Tyres,
        Body = v.Body,
        Gearbox = v.Gearbox,
    };

    /// <summary>The vehicle this describes, under the id <paramref name="id"/>: its base with what it says.</summary>
    public VehicleProfile Build(string id)
    {
        string baseId = Base.Length > 0 ? Base : id;
        var b = string.Equals(baseId, id, StringComparison.OrdinalIgnoreCase)
            ? MachineRegistry.Unedited(id)
            : MachineRegistry.VehicleFor(baseId);
        return b with
        {
            Name = Name.Length > 0 ? Name : b.Name,
            EngineKey = id,
            Engine = Engine.Length > 0 ? MachineRegistry.EngineFor(Engine) : b.Engine,
            MassKg = Chassis.MassKg,
            DragArea = Chassis.DragArea,
            RollingResistance = Chassis.RollingResistance,
            FrontAxleZ = Chassis.FrontAxleZ,
            RearAxleZ = Chassis.RearAxleZ,
            LengthMetres = Chassis.LengthMetres,
            WidthMetres = Chassis.WidthMetres,
            HeightMetres = Chassis.HeightMetres,
            SourceLevelDb = Chassis.SourceLevelDb,
            TyreCount = Chassis.TyreCount,
            ExhaustOffsetX = Exhaust.OffsetX,
            ExhaustHeight = Exhaust.Height,
            ExhaustOffsetZ = Exhaust.OffsetZ,
            IntakeHeight = Intake.Height,
            IntakeOffsetZ = Intake.OffsetZ,
            Tyres = Tyres ?? b.Tyres,
            Body = Body ?? b.Body,
            Gearbox = Gearbox ?? b.Gearbox,
        };
    }
}
