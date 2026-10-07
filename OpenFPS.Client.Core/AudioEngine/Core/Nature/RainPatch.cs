using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// One kind of surface inside a patch of rain: what it is, how much of it there is, and how far it
/// is from the listener. The drops landing on it are rendered as the listener hears them, each at its
/// own distance, so a near drop stands out of a far wash the way it does in the street.
///
/// The area is held in distance bins rather than one figure: rain is a source spread over the whole
/// ground, and the ground a metre away is heard drop by drop while the ground twenty metres away is a
/// hiss, at about the same total level (every doubling of distance doubles the area and halves the
/// intensity). One bin per ring of the survey.
/// </summary>
public sealed class RainLayer
{
    public const int MaxBins = 8;

    public RainSurfaceKind Kind;
    /// <summary>The registry name of what the rain lands on.</summary>
    public string Material = "Generic";
    /// <summary>Soft ground: how many times longer the drop takes to stop (RainSurfaces.ContactStretch).</summary>
    public float Stretch = 1f;
    /// <summary>A plate: the sheet itself.</summary>
    public RainPlate Plate;
    /// <summary>A plate heard from underneath: the roof over your head. Only what comes through the
    /// sheet — its ringing and its thuds — and not the clicks of the water on its far side.</summary>
    public bool FromBelow;

    /// <summary>A canopy: its leaf area index, what is under it and how far the drips fall to it.</summary>
    public float LeafAreaIndex = RainSurfaces.DefaultLeafAreaIndex;
    public RainSurfaceKind UnderKind = RainSurfaceKind.Soft;
    public float UnderStretch = 1f;
    public float DripFallMetres = 3f;

    public int Bins;
    public readonly float[] Area = new float[MaxBins];
    public readonly float[] Distance = new float[MaxBins];

    /// <summary>
    /// How squarely the surface in each bin faces the listener: the cosine of the angle between its
    /// normal (up) and the line to the ear, as an rms over the bin. A drop's click is the force it
    /// puts on a rigid surface, and a force at a rigid boundary radiates as a dipole along the normal
    /// (its image doubles it): straight up it is heard whole, along the ground hardly at all. So the
    /// ground a metre or two round you is heard drop by drop and the far street is a quiet wash —
    /// summed over a whole plane, cos²θ / r² comes to π whatever the height, where an omnidirectional
    /// source's sum grows without end as the plane does. Plates and leaves are not this: a plate's
    /// ringing has its own radiation efficiency, and leaves face every way.
    /// </summary>
    public readonly float[] Aim = new float[MaxBins];

    /// <summary>The bin is near enough that its biggest drops are played one by one, each where it
    /// lands (DiscreteRainFromMm, NearDrops); the patch renders only the rest of it.</summary>
    public readonly bool[] Discrete = new bool[MaxBins];

    /// <summary>From what size (mm) each bin's drops are played one by one (NearDrops), for the rain
    /// and for hail; infinite where none are.</summary>
    public readonly float[] DiscreteRainFromMm = Infinite(), DiscreteHailFromMm = Infinite();

    private static float[] Infinite() { var a = new float[MaxBins]; Array.Fill(a, float.MaxValue); return a; }

    public float DiscreteFrom(PrecipitationKind kind, int bin)
        => kind == PrecipitationKind.Hail ? DiscreteHailFromMm[bin] : DiscreteRainFromMm[bin];

    /// <summary>The bin a ring of the survey went into, or −1.</summary>
    public int BinOfRing(int ring)
    {
        for (int i = 0; i < Bins; i++) if (_ring[i] == ring) return i;
        return -1;
    }

    /// <summary>The surface's Young's modulus, GPa: how long an ice sphere's contact with it lasts.</summary>
    public float ModulusGPa = 30f;

    /// <summary>The same surface as one square metre a metre away, square on: for rendering one drop.</summary>
    public RainLayer Single()
    {
        var l = new RainLayer
        {
            Kind = Kind, Material = Material, Stretch = Stretch, Plate = Plate, FromBelow = FromBelow,
            LeafAreaIndex = LeafAreaIndex, UnderKind = UnderKind, UnderStretch = UnderStretch,
            DripFallMetres = DripFallMetres, ModulusGPa = ModulusGPa,
        };
        l.Add(0, 1f, 1f, 1f);
        return l;
    }

    /// <summary>Adds an area at a distance, facing the ear at <paramref name="aim"/> (see
    /// <see cref="Aim"/>): to the bin of that ring, or a new one.</summary>
    public void Add(int ring, float area, float distance, float aim = 1f, bool discrete = false)
    {
        if (area <= 0f) return;
        distance = MathF.Max(0.1f, distance);
        aim = Math.Clamp(aim, MinAim, 1f);
        // Bins are kept by ring. Distances merge so the bin's Σ A / d² is kept, and the aim so its
        // Σ A cos²θ / d² is.
        int at = -1;
        for (int i = 0; i < Bins; i++) if (_ring[i] == ring) { at = i; break; }
        if (at < 0 && Bins == MaxBins)
        {
            at = 0;
            for (int i = 1; i < Bins; i++)
                if (MathF.Abs(Distance[i] - distance) < MathF.Abs(Distance[at] - distance)) at = i;
        }
        if (at >= 0)
        {
            float g0 = Area[at] / (Distance[at] * Distance[at]), g1 = area / (distance * distance);
            Aim[at] = MathF.Sqrt((g0 * Aim[at] * Aim[at] + g1 * aim * aim) / (g0 + g1));
            Area[at] += area;
            Distance[at] = MathF.Sqrt(Area[at] / (g0 + g1));
            return;
        }
        _ring[Bins] = ring;
        Area[Bins] = area;
        Distance[Bins] = distance;
        Aim[Bins] = aim;
        Discrete[Bins] = discrete;
        Bins++;
    }

    /// <summary>The same surface with <paramref name="share"/> of its area in every bin: one of several
    /// voices that together render it (a roof over the ear heard from several places, RainVoiceState).
    /// Its drops are that share of the whole's, so the voices' drops together are the whole's.</summary>
    public RainLayer Share(float share)
    {
        var l = new RainLayer
        {
            Kind = Kind, Material = Material, Stretch = Stretch, Plate = Plate, FromBelow = FromBelow,
            LeafAreaIndex = LeafAreaIndex, UnderKind = UnderKind, UnderStretch = UnderStretch,
            DripFallMetres = DripFallMetres, ModulusGPa = ModulusGPa, Bins = Bins,
        };
        for (int i = 0; i < Bins; i++)
        {
            l.Area[i] = Area[i] * share;
            l.Distance[i] = Distance[i];
            l.Aim[i] = Aim[i];
            l.Discrete[i] = Discrete[i];
            l.DiscreteRainFromMm[i] = DiscreteRainFromMm[i];
            l.DiscreteHailFromMm[i] = DiscreteHailFromMm[i];
            l._ring[i] = _ring[i];
        }
        return l;
    }

    /// <summary>The least a surface's clicks count for however edge-on it is: what the edge of a
    /// roof or a kerb diffracts toward an ear level with it or below it.</summary>
    public const float MinAim = 0.1f;

    private readonly int[] _ring = new int[MaxBins];

    public float TotalArea
    {
        get { float a = 0f; for (int i = 0; i < Bins; i++) a += Area[i]; return a; }
    }

    /// <summary>∫ dA / r² over the layer: how much of the listener's hearing it fills.</summary>
    public float ViewFactor
    {
        get { float g = 0f; for (int i = 0; i < Bins; i++) g += Area[i] / (Distance[i] * Distance[i]); return g; }
    }

    /// <summary>Two layers with the same key are the same kind of surface and merge. Worked out once,
    /// on the thread that builds the layer, so the voice that reads it does not make strings.</summary>
    public string Key => _key ??= MakeKey();
    private string? _key;

    private string MakeKey() => Kind == RainSurfaceKind.Plate
        ? $"{Kind}|{Material}|{Plate.SkinMetres:F4}|{Plate.BayA:F2}|{Plate.BayB:F2}|{Plate.MountedLossFactor:F3}|{FromBelow}"
        : Kind == RainSurfaceKind.Canopy ? $"{Kind}|{Material}|{UnderKind}|{UnderStretch:F2}|{DripFallMetres:F1}|{LeafAreaIndex:F1}"
        : $"{Kind}|{Material}|{Stretch:F2}";
}

/// <summary>
/// A patch of rain: the surfaces in one direction from the listener, heard as one voice from where
/// they are. Built by the survey on the game thread and handed to the voice whole; never changed
/// after it is handed over.
/// </summary>
public sealed class RainPatch
{
    public RainLayer[] Layers = Array.Empty<RainLayer>();

    /// <summary>The distance the voice is placed at, m. The synthesiser renders pressure at the
    /// listener times this, which is the level at a metre of a source the mixer then places at this
    /// distance with this extent — so the mixer's law hands back exactly what arrived.</summary>
    public float ReferenceDistance = 1f;

    /// <summary>The same patch with <paramref name="share"/> of every surface's area (RainLayer.Share).</summary>
    public RainPatch Share(float share)
    {
        var layers = new RainLayer[Layers.Length];
        for (int i = 0; i < layers.Length; i++) layers[i] = Layers[i].Share(share);
        return new RainPatch { Layers = layers, ReferenceDistance = ReferenceDistance };
    }
}
