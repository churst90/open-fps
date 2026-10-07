using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Concrete Row: a straight street 24 m wide with six-storey concrete blocks down both sides and two
/// cross streets cutting through. Its boxes, faces, map and near-field probes, for the lab scenes that
/// stand in it (--engine-street).
/// </summary>
public static class ConcreteRow
{
    private const float StreetHalfWidth = 12f;    // 24 m kerb to kerb
    private const float BlockDepth = 34f;         // how far along the street one building runs
    private const float BlockWidth = 30f;         // how far back from the street it goes
    private const float BlockHeight = 26f;        // six storeys or so
    private const float SideStreetGap = 14f;

    private static SteamAudioScene.Box Ground() => new(
        new Vector3(0f, -0.25f, 80f), new Vector3(400f, 0.5f, 400f), Quaternion.Identity, "Concrete");

    public static List<SteamAudioScene.Box> Boxes()
    {
        var boxes = new List<SteamAudioScene.Box> { Ground() };
        var q = Quaternion.Identity;

        // The gaps matter as much as the blocks: a reflection needs a face where it bounces, and a
        // missing answer is the sound of a side street. One-sided on purpose: a cross street through
        // both rows leaves a source mid-road with no face at all.
        float z = -50f;
        int i = 0;
        while (z < 240f)
        {
            // Which row, if either, is open here: +1 opens the right, -1 the left, 0 neither.
            int gapSide = i switch { 2 => +1, 5 => -1, _ => 0 };
            float cz = z + BlockDepth * 0.5f;
            float cx = StreetHalfWidth + BlockWidth * 0.5f;

            if (gapSide != +1)
                boxes.Add(new SteamAudioScene.Box(
                    new Vector3(cx, BlockHeight * 0.5f, cz),
                    new Vector3(BlockWidth, BlockHeight, BlockDepth), q, "Concrete"));
            if (gapSide != -1)
                boxes.Add(new SteamAudioScene.Box(
                    new Vector3(-cx, BlockHeight * 0.5f, cz),
                    new Vector3(BlockWidth, BlockHeight, BlockDepth), q, "Concrete"));

            z += gapSide == 0 ? BlockDepth : SideStreetGap;
            i++;
        }
        return boxes;
    }

    public static ReflectingSurface[] Surfaces(List<SteamAudioScene.Box> boxes)
    {
        var all = new List<ReflectingSurface>(boxes.Count * 6);
        Span<ReflectingSurface> six = stackalloc ReflectingSurface[6];
        int id = 1;
        foreach (var b in boxes)
        {
            // 0.25, not a smooth slab's 0.02: windows, reveals, sills and signage scatter. At 98 % every
            // facade answered and the street was a hall of mirrors.
            int n = ImageSource.FacesOfBox(b.Center, b.Size, 0.25f, id, six);
            for (int i = 0; i < n; i++) all.Add(six[i]);
            id += 8;
        }
        return all.ToArray();
    }

    /// <summary>
    /// What is immediately round the listener's head, as six head-relative probes for
    /// <see cref="BoundaryModel"/> (a delayed, damped, lateralised tap each at 2d/c). A surface a metre
    /// or two away returns inside the ear's fusion window: not an arrival but a change of timbre, the
    /// sense of a wall before touching it.
    /// </summary>
    public static int Probes(Vector3 listener, List<SteamAudioScene.Box> boxes,
                                      BoundaryProbe[] into)
    {
        var dirs = BoundaryModel.ProbeDirections;
        int n = Math.Min(dirs.Length, into.Length);
        for (int i = 0; i < n; i++)
        {
            float nearest = float.MaxValue;
            foreach (var b in boxes)
            {
                // The ray/AABB slab test along the probe.
                Vector3 half = b.Size * 0.5f;
                Vector3 lo = b.Center - half, hi = b.Center + half;
                Vector3 d = dirs[i];
                float tmin = 0f, tmax = float.MaxValue;
                bool hit = true;
                for (int a = 0; a < 3 && hit; a++)
                {
                    float o = a == 0 ? listener.X : a == 1 ? listener.Y : listener.Z;
                    float dd = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
                    float l = a == 0 ? lo.X : a == 1 ? lo.Y : lo.Z;
                    float h = a == 0 ? hi.X : a == 1 ? hi.Y : hi.Z;
                    if (MathF.Abs(dd) < 1e-6f) { if (o < l || o > h) hit = false; }
                    else
                    {
                        float t1 = (l - o) / dd, t2 = (h - o) / dd;
                        if (t1 > t2) (t1, t2) = (t2, t1);
                        tmin = MathF.Max(tmin, t1);
                        tmax = MathF.Min(tmax, t2);
                        if (tmin > tmax) hit = false;
                    }
                }
                if (hit && tmin >= 0f && tmin < nearest) nearest = tmin;
            }
            into[i] = new BoundaryProbe(dirs[i],
                                        nearest == float.MaxValue ? BoundaryModel.MaxDistance : nearest,
                                        "Concrete");
        }
        return n;
    }

    /// <summary>A map with nothing in it but the outdoors, so the provider builds the global bus that
    /// the simulated RT60 then drives.</summary>
    public static AcousticMap Map()
    {
        var map = new AcousticMap(new Vector3(400f, 60f, 400f), new Vector3(-200f, 0f, -100f),
                                  AcousticConstants.DefaultVoxelResolution);
        map.GlobalEnvironmentId = AcousticConstants.GlobalRegionId;
        map.Regions[AcousticConstants.GlobalRegionId] = new RegionComponent
        {
            FriendlyName = "Concrete Row",
            IsIndoor = false,
            RoomSize = new Vector3(StreetHalfWidth * 2f, BlockHeight, 280f),
            ReverbTimeScale = 1.0f,
            Materials = new[] { 18, 18, 18, 18, 18, 18 },   // concrete on every face
        };
        map.RegionPositions[AcousticConstants.GlobalRegionId] = Vector3.Zero;
        return map;
    }
}
