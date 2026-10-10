using System.Collections.Generic;
using OpenFPS.Common.Editing;

namespace OpenFPS.Common;

// Compressed air on its way out. Every sound of a vehicle's air system (brake release, dryer purge,
// kneeling, parking brake) is one event, a vessel at pressure emptying through a hole; only the
// volume and the hole change.
//
// At 120 psi the pressure ratio is about nine, far past the 1.89 that chokes the hole: the jet leaves
// underexpanded at about Mach 1.6, with shock cells that rasp, and peaks at Strouhal 0.2 on the hole,
// about 15 kHz for 8 mm. The muffler in the port brings it down. The vessel empties exponentially while
// choked (time constant volume / effective area / speed of sound: tenths of a second for a door valve,
// seconds for a trailer); below 1.89 the jet goes subsonic and Lighthill's eighth power takes the level
// away fast. That knee is the sound: a crack, a fat hiss, a thin tail.

/// <summary>Something that lets air out.</summary>
public sealed record AirPortSpec
{
    public required string Name { get; init; }
    /// <summary>The hole, metres. It decides the pitch (Strouhal 0.2 on it) and, with the volume
    /// behind it, how long the sound lasts.</summary>
    [Tunable("m", 0.001, 0.05, "Diameter of the hole the air leaves by. A bigger hole is lower in pitch and empties the volume faster.", Label = "orifice diameter", Step = 0.0005)]
    public required float OrificeMetres { get; init; }
    /// <summary>What is behind the hole, litres: two brake chambers for a tractor's quick-release
    /// valve, the whole spring brake side for a parking brake.</summary>
    [Tunable("L", 0.1, 2000, "Volume of air behind the hole. More volume makes the release last longer.", Label = "volume behind it", Step = 0.5)]
    public required float VolumeLitres { get; init; }
    /// <summary>Discharge coefficient of the port. A sharp-edged hole is 0.6, a nozzle 0.9.</summary>
    [Tunable("", 0.5, 1, "How much of the hole's area the flow actually uses. A sharp-edged hole is 0.6, a nozzle 0.9.", Step = 0.01)]
    public float DischargeCoefficient { get; init; } = 0.72f;
    /// <summary>The muffler screwed into the exhaust port: how much of the top it takes off, 0..1,
    /// and the corner it rolls off from. Without it every one of these is a shriek.</summary>
    [Tunable("", 0, 1, "How much of the top end the muffler in the exhaust port takes off. Zero is no muffler and a shriek.", Label = "muffler absorption", Step = 0.05)]
    public float MufflerAbsorption { get; init; } = 0.55f;
    [Tunable("Hz", 500, 10000, "Where the muffler starts to roll the top off.", Label = "muffler corner", Step = 100)]
    public float MufflerCornerHz { get; init; } = 2600f;
    /// <summary>Where it is on the vehicle: metres back from the front, out to the right, and up.</summary>
    [Tunable("m", 0, 40, "How far back from the front of the vehicle the port is.", Label = "distance back from the front", Step = 0.1)]
    public float AlongMetres { get; init; }
    [Tunable("m", -2, 2, "How far right of the centre line the port is. Negative is to the left.", Label = "distance to the right", Step = 0.05)]
    public float LateralMetres { get; init; }
    [Tunable("m", 0, 5, "How high above the ground the port is.", Label = "height", Step = 0.05)]
    public float HeightMetres { get; init; } = 0.6f;
    /// <summary>The valve itself opening: a mechanical crack before the air says anything.</summary>
    [Tunable("dB", 60, 120, "Level at one metre of the valve's mechanical crack as it opens, before the air.", Label = "valve clack level", Step = 1)]
    public float ValveClackDb { get; init; } = 88f;
}

/// <summary>The air system of a road vehicle: a compressor, a reservoir, a governor, and ports.</summary>
public sealed record AirSystemSpec
{
    [Tunable("", 0, 0, "The air system's name as it is said.")]
    public required string Name { get; init; }
    [Tunable("L", 10, 2000, "Volume of the main reservoir.", Label = "reservoir volume", Step = 5)]
    public float ReservoirLitres { get; init; } = 60f;
    /// <summary>The governor: the compressor loads at the low figure and unloads at the high one, 100
    /// and 120 psi in the American standard. The unloading is a parked truck's pop every few minutes.</summary>
    [Tunable("kPa", 300, 1100, "Pressure, gauge, at which the governor sets the compressor pumping again.", Label = "governor cut-in pressure", Step = 10, Source = "American standard governor settings, 100 and 120 psi")]
    public float CutInKPa { get; init; } = 690f;
    [Tunable("kPa", 300, 1200, "Pressure, gauge, at which the governor unloads the compressor: the pop from a parked truck.", Label = "governor cut-out pressure", Step = 10, Source = "American standard governor settings, 100 and 120 psi")]
    public float CutOutKPa { get; init; } = 827f;
    public required AirPortSpec[] Ports { get; init; }
    /// <summary>The compressor, a two-cylinder pump geared off the engine: a knocking at twice the
    /// order it is geared to.</summary>
    [Tunable("dB", 50, 110, "Level at one metre of the compressor's knocking while it pumps.", Label = "compressor level", Step = 1)]
    public float CompressorDb { get; init; } = 76f;
    [Tunable("", 0.5, 8, "Compressor knocks per engine revolution. A two-cylinder pump geared at engine speed is 2.", Label = "compressor order", Step = 0.5)]
    public float CompressorOrder { get; init; } = 2f;
    /// <summary>Where this jet sits against Lighthill's law with K = 1e-4, dB, as the aircraft's jets
    /// do: the law's one-metre figure is a near-field fiction, the balance is real. A brake release
    /// comes out near the 100 dB at a metre one measures.</summary>
    public float JetTrimDb { get; init; } = -15f;

    public AirPortSpec Port(string name)
    {
        foreach (var p in Ports) if (p.Name == name) return p;
        throw new ArgumentException($"No air port '{name}' on {Name}. Known: {string.Join(", ", Array.ConvertAll(Ports, x => x.Name))}");
    }

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A tractor unit and a loaded trailer: large volumes behind every valve, so its air is
    /// slow where a bus's is quick. The parking brakes are the loudest; the dryer purge is the
    /// bang-and-sigh of a standing truck.</summary>
    public static AirSystemSpec TractorTrailer => new()
    {
        Name = "tractor unit and trailer",
        ReservoirLitres = 120f, CutInKPa = 690f, CutOutKPa = 827f,
        CompressorDb = 78f, CompressorOrder = 2f,
        Ports = new[]
        {
            new AirPortSpec
            {
                Name = "service_release", OrificeMetres = 0.0105f, VolumeLitres = 26f,
                MufflerAbsorption = 0.55f, MufflerCornerHz = 2400f,
                AlongMetres = 12.0f, LateralMetres = -0.9f, HeightMetres = 0.75f, ValveClackDb = 90f,
            },
            new AirPortSpec
            {
                Name = "tractor_release", OrificeMetres = 0.0085f, VolumeLitres = 11f,
                MufflerAbsorption = 0.5f, MufflerCornerHz = 2800f,
                AlongMetres = 3.4f, LateralMetres = -0.95f, HeightMetres = 0.8f, ValveClackDb = 88f,
            },
            new AirPortSpec
            {
                Name = "parking", OrificeMetres = 0.014f, VolumeLitres = 40f,
                MufflerAbsorption = 0.35f, MufflerCornerHz = 3400f,
                AlongMetres = 2.2f, LateralMetres = -0.6f, HeightMetres = 1.4f, ValveClackDb = 96f,
            },
            new AirPortSpec
            {
                Name = "dryer_purge", OrificeMetres = 0.012f, VolumeLitres = 2.2f,
                MufflerAbsorption = 0.15f, MufflerCornerHz = 5200f,
                AlongMetres = 2.8f, LateralMetres = 0.9f, HeightMetres = 0.9f, ValveClackDb = 99f,
            },
        },
    };

    /// <summary>A city bus: smaller volumes and holes than a truck, so quicker and higher. The door
    /// and kneeling valves are at the front door, a metre from whoever is boarding.</summary>
    public static AirSystemSpec TransitBus => new()
    {
        Name = "transit bus",
        ReservoirLitres = 70f, CutInKPa = 690f, CutOutKPa = 827f,
        CompressorDb = 74f, CompressorOrder = 2f,
        Ports = new[]
        {
            new AirPortSpec
            {
                Name = "service_release", OrificeMetres = 0.0085f, VolumeLitres = 14f,
                MufflerAbsorption = 0.6f, MufflerCornerHz = 2200f,
                AlongMetres = 7.5f, LateralMetres = -0.9f, HeightMetres = 0.6f, ValveClackDb = 86f,
            },
            new AirPortSpec
            {
                Name = "parking", OrificeMetres = 0.011f, VolumeLitres = 18f,
                MufflerAbsorption = 0.4f, MufflerCornerHz = 3200f,
                AlongMetres = 1.8f, LateralMetres = -0.5f, HeightMetres = 1.3f, ValveClackDb = 94f,
            },
            new AirPortSpec
            {
                Name = "door", OrificeMetres = 0.006f, VolumeLitres = 2.0f,
                MufflerAbsorption = 0.45f, MufflerCornerHz = 3800f,
                AlongMetres = 2.6f, LateralMetres = 1.3f, HeightMetres = 0.5f, ValveClackDb = 84f,
            },
            new AirPortSpec
            {
                Name = "kneel", OrificeMetres = 0.0080f, VolumeLitres = 26f,
                MufflerAbsorption = 0.5f, MufflerCornerHz = 2600f,
                AlongMetres = 3.2f, LateralMetres = 1.2f, HeightMetres = 0.35f, ValveClackDb = 80f,
            },
            new AirPortSpec
            {
                Name = "dryer_purge", OrificeMetres = 0.010f, VolumeLitres = 1.6f,
                MufflerAbsorption = 0.2f, MufflerCornerHz = 5000f,
                AlongMetres = 9.0f, LateralMetres = 0.8f, HeightMetres = 0.8f, ValveClackDb = 96f,
            },
        },
    };

    /// <summary>A locomotive's air: the brake pipe runs the length of the train, so its sounds are long
    /// and low; an emergency application dumps a kilometre of pipe through a thumb-sized hole.</summary>
    public static AirSystemSpec Locomotive => new()
    {
        Name = "locomotive and train line",
        ReservoirLitres = 900f, CutInKPa = 900f, CutOutKPa = 1000f,
        CompressorDb = 88f, CompressorOrder = 2f,
        Ports = new[]
        {
            new AirPortSpec
            {
                Name = "service_release", OrificeMetres = 0.016f, VolumeLitres = 210f,
                MufflerAbsorption = 0.45f, MufflerCornerHz = 1800f,
                AlongMetres = 8f, LateralMetres = -1.2f, HeightMetres = 1.1f, ValveClackDb = 94f,
            },
            new AirPortSpec
            {
                Name = "emergency", OrificeMetres = 0.026f, VolumeLitres = 620f,
                MufflerAbsorption = 0.1f, MufflerCornerHz = 4000f,
                AlongMetres = 5f, LateralMetres = -1.2f, HeightMetres = 1.0f, ValveClackDb = 102f,
            },
        },
    };

    public static IReadOnlyDictionary<string, Func<AirSystemSpec>> Presets { get; } =
        new Dictionary<string, Func<AirSystemSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["tractor_trailer"] = () => TractorTrailer,
            ["transit_bus"] = () => TransitBus,
            ["locomotive"] = () => Locomotive,
        };

    public static AirSystemSpec ByName(string key)
        => Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No air system '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}
