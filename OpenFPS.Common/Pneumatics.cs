using System;
using System.Collections.Generic;

namespace OpenFPS.Common;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  Compressed air, and everything it does on its way out.
//
//  A lorry, a bus, a tram and a train all run their brakes and their doors on air, and every sound
//  the system makes is the same event: A VESSEL AT PRESSURE EMPTYING THROUGH A HOLE. Get that one
//  thing right and the hiss when the driver lifts off the brake, the bang and sigh of a truck's air
//  dryer purging in a car park, the long sigh of a bus kneeling and the crack of a parking brake
//  popping out are all the same model with two numbers changed: how big the vessel is and how big
//  the hole is.
//
//  WHY IT IS SO LOUD AND SO BRIGHT. At a hundred and twenty pounds to the square inch the pressure
//  ratio across the hole is about nine, which is far past the 1.89 it takes to choke. So the flow at
//  the throat is sonic, the jet leaves underexpanded and accelerates to about Mach 1.6, and a
//  supersonic jet is not just a loud jet — it also has SHOCK CELLS in it, a train of diamonds that
//  radiate a rasp of their own. And because the noise of a jet peaks at a Strouhal number of about
//  0.2 on the size of the hole, an eight-millimetre orifice peaks somewhere around fifteen kilohertz:
//  it is pure top end. What brings it back down to something you would recognise is the MUFFLER
//  screwed into the exhaust port, which is doing exactly what a muffler on an engine does.
//
//  WHY IT DIES AWAY THE WAY IT DOES. The vessel empties exponentially while the flow is choked, with
//  a time constant of the volume over the effective area over the speed of sound — a few tenths of a
//  second for a door valve, several seconds for a trailer's reservoir. Then the pressure ratio falls
//  below 1.89, the jet goes subsonic, and Lighthill's eighth power takes the level away very fast
//  indeed. That knee is the shape of the sound: a hard crack, a fat hiss, and a thin tail.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Something that lets air out.</summary>
public sealed record AirPortSpec
{
    public required string Name { get; init; }
    /// <summary>The hole, metres. It decides the pitch (Strouhal 0.2 on it) and, with the volume
    /// behind it, how long the sound lasts.</summary>
    public required float OrificeMetres { get; init; }
    /// <summary>What is behind the hole, litres. A quick-release valve on a tractor is emptying two
    /// brake chambers; a parking brake is emptying the spring brake side of the whole unit.</summary>
    public required float VolumeLitres { get; init; }
    /// <summary>Discharge coefficient of the port. A sharp-edged hole is 0.6, a nozzle 0.9.</summary>
    public float DischargeCoefficient { get; init; } = 0.72f;
    /// <summary>The muffler screwed into the exhaust port: how much of the top it takes off, 0..1,
    /// and the corner it rolls off from. Without it every one of these is a shriek.</summary>
    public float MufflerAbsorption { get; init; } = 0.55f;
    public float MufflerCornerHz { get; init; } = 2600f;
    /// <summary>Where it is on the vehicle: metres back from the front, out to the right, and up.
    /// A trailer's brake valves are at the back and at the axles; a bus's door valve is at the
    /// front step, which is why it goes off next to your head.</summary>
    public float AlongMetres { get; init; }
    public float LateralMetres { get; init; }
    public float HeightMetres { get; init; } = 0.6f;
    /// <summary>The valve itself opening: a mechanical crack before the air says anything.</summary>
    public float ValveClackDb { get; init; } = 88f;
}

/// <summary>The air system of a road vehicle: a compressor, a reservoir, a governor, and ports.</summary>
public sealed record AirSystemSpec
{
    public required string Name { get; init; }
    public float ReservoirLitres { get; init; } = 60f;
    /// <summary>The governor: the compressor loads at the low figure and unloads at the high one.
    /// 100 and 120 psi is the American standard, and the unloading is the "pop" you hear from a
    /// parked truck every couple of minutes.</summary>
    public float CutInKPa { get; init; } = 690f;
    public float CutOutKPa { get; init; } = 827f;
    public required AirPortSpec[] Ports { get; init; }
    /// <summary>The compressor itself: a little two-cylinder pump geared off the engine. It is a
    /// knocking, not a hiss, and it is at twice the crank order it is geared to.</summary>
    public float CompressorDb { get; init; } = 76f;
    public float CompressorOrder { get; init; } = 2f;
    /// <summary>Where a jet of this kind sits against Lighthill's law with K = 1e-4, dB. The same
    /// anchor, and the same sign, as the aircraft's jets: the law's one-metre figure is a near-field
    /// fiction for a source whose mixing region is many diameters long, and what is real is the
    /// balance, which is what this pins. A brake release comes out near 100 dB at a metre with it,
    /// which is what one measures.</summary>
    public float JetTrimDb { get; init; } = -15f;

    public AirPortSpec Port(string name)
    {
        foreach (var p in Ports) if (p.Name == name) return p;
        throw new ArgumentException($"No air port '{name}' on {Name}. Known: {string.Join(", ", Array.ConvertAll(Ports, x => x.Name))}");
    }

    // ── Presets ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A tractor unit and a loaded trailer. Big reservoirs, long lines, and a lot of volume behind
    /// every valve — which is why a truck's air sounds SLOW where a bus's sounds quick. The service
    /// release comes out of the trailer's relay valve at the back; the parking brakes dump the whole
    /// spring brake side at once and are the loudest thing on it; and the air dryer purge is the
    /// bang-and-sigh from a truck that has been standing for two minutes with nobody in it.
    /// </summary>
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

    /// <summary>
    /// A city bus. Smaller volumes and smaller holes than a truck, so everything is quicker and
    /// higher; but the door valve and the kneeling valve are both at the front door, a metre from
    /// whoever is waiting to get on, which is why a bus is the vehicle most people have actually
    /// stood next to while it let its air go.
    /// </summary>
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

    /// <summary>
    /// A locomotive's air. Enormous volumes — the brake pipe runs the length of the train — so the
    /// sounds are long and low, and an emergency application dumps a kilometre of pipe through a
    /// hole the size of a thumb and can be heard from the next street.
    /// </summary>
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
