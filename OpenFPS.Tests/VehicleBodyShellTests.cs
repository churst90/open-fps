using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Geometry;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Each vehicle's body as one closed mesh, made from its parts list (docs/GEOMETRY.md, stage 6;
/// docs/PHYSICS_BOUNDARY.md, stage 4 and decision 13): over every vehicle the presets, the authored machines
/// and the maps hold, the mesh is watertight with its faces outward, its own sources come out on the outside
/// of it by the shortest way, and its size is the body's it was given.
/// </summary>
public class VehicleBodyShellTests
{
    private readonly ITestOutputHelper _o;
    public VehicleBodyShellTests(ITestOutputHelper o) => _o = o;

    /// <summary>Every vehicle there is: the built-in presets, the authored machines beside the maps, and every
    /// vehicle a map names.</summary>
    private static List<string> EveryVehicle()
    {
        MachineRegistry.EnsureLoaded(Path.Combine(AppContext.BaseDirectory, "machines"));
        var ids = new SortedSet<string>(MachineRegistry.Ids, StringComparer.OrdinalIgnoreCase);
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")),
                                  new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        foreach (var map in maps.GetAllMaps())
            foreach (var v in map.Value.data.Vehicles ?? new())
                if (MachineRegistry.Knows(v.Preset)) ids.Add(v.Preset);
        return ids.ToList();
    }

    /// <summary>Sizes a map might give a vehicle: its own, longer and lower, shorter and taller, and a coach's.</summary>
    private static IEnumerable<Vector3?> Sizes(VehicleProfile v)
    {
        yield return null;
        yield return new Vector3(v.WidthMetres * 1.1f, v.HeightMetres * 0.9f, v.LengthMetres * 1.3f);
        yield return new Vector3(v.WidthMetres * 0.9f, v.HeightMetres * 1.2f, v.LengthMetres * 0.8f);
        yield return new Vector3(2.55f, 3.4f, 12.2f);
    }

    private static float Volume(MeshAsset m)
    {
        double six = 0;
        for (int t = 0; t < m.TriangleCount; t++)
        {
            var a = m.Vertices[m.Indices[3 * t]]; var b = m.Vertices[m.Indices[3 * t + 1]]; var c = m.Vertices[m.Indices[3 * t + 2]];
            six += Vector3.Dot(a, Vector3.Cross(b, c));
        }
        return (float)(six / 6.0);
    }

    /// <summary>What is wrong with a mesh as a closed solid with its faces outward, or nothing.</summary>
    private static List<string> Faults(BodyShell shell)
    {
        var m = shell.Mesh;
        var faults = new List<string>();
        if (!m.Closed) faults.Add("not marked closed");
        // Every edge is walked once each way: the surface has no hole, no fin and no face turned over.
        var edges = new Dictionary<(int, int), int>();
        for (int t = 0; t < m.TriangleCount; t++)
        {
            int a = m.Indices[3 * t], b = m.Indices[3 * t + 1], c = m.Indices[3 * t + 2];
            var pa = m.Vertices[a]; var pb = m.Vertices[b]; var pc = m.Vertices[c];
            var n = Vector3.Cross(pb - pa, pc - pa);
            if (n.Length() < 1e-9f) { faults.Add($"triangle {t} has no area"); continue; }
            foreach (var e in new[] { (a, b), (b, c), (c, a) }) edges[e] = edges.GetValueOrDefault(e) + 1;
            // Outward: just off the face along its normal is outside the body, just behind it is inside.
            n = Vector3.Normalize(n);
            var centre = (pa + pb + pc) / 3f;
            if (shell.Contains(centre + n * 0.01f)) faults.Add($"triangle {t} at {centre} faces into the body");
            if (!shell.Contains(centre - n * 0.01f)) faults.Add($"triangle {t} at {centre} has nothing behind it");
        }
        foreach (var (e, n) in edges)
        {
            if (n != 1) faults.Add($"edge {e} is walked {n} times one way");
            if (edges.GetValueOrDefault((e.Item2, e.Item1)) != 1) faults.Add($"edge {e} has no triangle on its other side");
        }
        // The volume the faces enclose is the body's: none missing, none doubled, all outward.
        float expected = 0f;
        foreach (var s in shell.Spec.Sections) expected += shell.Spec.Width * (s.Top - shell.Spec.Underside) * (s.ToZ - s.FromZ);
        float volume = Volume(m);
        if (MathF.Abs(volume - expected) > 1e-3f * expected) faults.Add($"encloses {volume:F4} m³, the body is {expected:F4}");
        return faults;
    }

    [Fact]
    public void EveryBodysMeshIsClosedWithItsFacesOutward()
    {
        var ids = EveryVehicle();
        int meshes = 0, triangles = 0;
        var wrong = new List<string>();
        foreach (string id in ids)
        {
            var v = MachineRegistry.VehicleFor(id);
            foreach (var size in Sizes(v))
            {
                var shell = VehicleBodyShell.For(v, MachineRegistry.Describe(v, id), size).Shell;
                meshes++; triangles += shell.Mesh.TriangleCount;
                foreach (string fault in Faults(shell).Take(3)) wrong.Add($"{id} at {size?.ToString() ?? "its own size"}: {fault}");
            }
        }
        _o.WriteLine($"{ids.Count} vehicles, {meshes} meshes, {triangles} triangles in all");
        Assert.True(ids.Count >= 30, $"only {ids.Count} vehicles found");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(20)));
    }

    [Fact]
    public void EveryBodysMeshIsTheSizeOfTheBodyItWasGiven()
    {
        var wrong = new List<string>();
        foreach (string id in EveryVehicle())
        {
            var v = MachineRegistry.VehicleFor(id);
            foreach (var size in Sizes(v))
            {
                var want = size ?? new Vector3(v.WidthMetres, v.HeightMetres, v.LengthMetres);
                var m = VehicleBodyShell.For(v, MachineRegistry.Describe(v, id), size).Shell.Mesh;
                var lo = m.BoundsMin; var hi = m.BoundsMax;
                bool right = MathF.Abs(hi.X - want.X * 0.5f) < 1e-4f && MathF.Abs(lo.X + want.X * 0.5f) < 1e-4f
                          && MathF.Abs(hi.Z - want.Z * 0.5f) < 1e-4f && MathF.Abs(lo.Z + want.Z * 0.5f) < 1e-4f
                          && MathF.Abs(hi.Y - want.Y) < 1e-4f && lo.Y > 0f && lo.Y <= VehicleCabin.ChassisBottom + 1e-4f;
                if (!right) wrong.Add($"{id}: asked for {want}, the mesh runs {lo} to {hi}");
            }
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(20)));
    }

    [Fact]
    public void EverySourceComesOutOnTheOutsideByTheShortestWay()
    {
        var wrong = new List<string>();
        int sources = 0, moved = 0;
        foreach (string id in EveryVehicle())
        {
            var v = MachineRegistry.VehicleFor(id);
            foreach (var size in Sizes(v))
            {
                var body = VehicleBodyShell.For(v, MachineRegistry.Describe(v, id), size);
                var shell = body.Shell;
                string at = $"{id} at {size?.ToString() ?? "its own size"}";
                // One exhaust, one intake, and a tyre for every wheel.
                int wheels = v.Running.Axles.Sum(a => a.Wheels);
                if (body.Sources.Count(s => s.Part == MachineModels.Exhaust) != 1 || body.Sources.Count(s => s.Part == MachineModels.Intake) != 1
                    || body.Sources.Count(s => s.Part == MachineModels.Tyres) != wheels)
                    wrong.Add($"{at}: its sources are {string.Join(", ", body.Sources.Select(s => s.Part))}, with {wheels} wheels");

                // Where each part sits on this body, as the parts list has it.
                var scale = size is { } g ? new Vector3(g.X / v.WidthMetres, g.Y / v.HeightMetres, g.Z / v.LengthMetres) : Vector3.One;
                var parts = MachineRegistry.Describe(v, id);
                foreach (var s in body.Sources)
                {
                    sources++;
                    if (shell.Contains(s.At)) { wrong.Add($"{at}: its {s.Part} {s.Index} at {s.At} is inside the shell"); continue; }
                    if (s.Part == MachineModels.Tyres)
                    {
                        if (s.At.Y != 0f) wrong.Add($"{at}: tyre {s.Index} is at {s.At}, not on the road");
                        continue;
                    }
                    var sat = parts.Part(s.Part)!.At * scale;
                    if (!shell.Contains(sat))
                    {
                        if (s.At != sat) wrong.Add($"{at}: its {s.Part} was outside at {sat} and was moved to {s.At}");
                        continue;
                    }
                    moved++;
                    // It came straight out, the clearance beyond the face, and no way out was shorter: nothing
                    // outside the shell is nearer to where the part sits than the way it took (less the clearance).
                    float way = MathF.Max(0f, Vector3.Distance(s.At, sat) - BodyShell.SourceClearance);
                    // (A part that sits in a face is already at the way out: it goes the clearance and no more.)
                    var back = sat + (s.At - sat) * (MathF.Max(0f, way - 0.001f) / (way + BodyShell.SourceClearance));
                    if (Vector3.Distance(s.At, sat) < BodyShell.SourceClearance - 1e-4f || !shell.Contains(back)) wrong.Add($"{at}: its {s.Part} went {way:F3} m from {sat} to {s.At}, not straight out through a face");
                    const float step = 0.05f;
                    float reach = MathF.Max(0f, way - 2f * step);
                    for (float x = -reach; x <= reach && wrong.Count < 20; x += step)
                        for (float y = -reach; y <= reach; y += step)
                            for (float z = -reach; z <= reach; z += step)
                            {
                                var p = sat + new Vector3(x, y, z);
                                if (Vector3.Distance(p, sat) < reach && !shell.Contains(p))
                                {
                                    wrong.Add($"{at}: its {s.Part} went {way:F3} m out to {s.At}, but {p} is outside and {Vector3.Distance(p, sat):F3} m away");
                                    x = y = z = float.MaxValue;
                                }
                            }
                }
            }
        }
        _o.WriteLine($"{sources} sources, {moved} of them brought out of the shell");
        Assert.True(moved > 20, $"only {moved} sources were inside a shell: the test has nothing to show");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(20)));
    }

    /// <summary>The first face a ray from <paramref name="from"/> along <paramref name="dir"/> leaves the shell
    /// through, or null.</summary>
    private static BodyFace? FaceAlong(MeshAsset m, Vector3 from, Vector3 dir)
    {
        float best = float.MaxValue; BodyFace? face = null;
        for (int t = 0; t < m.TriangleCount; t++)
        {
            var a = m.Vertices[m.Indices[3 * t]]; var b = m.Vertices[m.Indices[3 * t + 1]]; var c = m.Vertices[m.Indices[3 * t + 2]];
            var e1 = b - a; var e2 = c - a;
            var h = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, h);
            if (MathF.Abs(det) < 1e-9f) continue;
            var s = from - a;
            float u = Vector3.Dot(s, h) / det;
            var q = Vector3.Cross(s, e1);
            float w = Vector3.Dot(dir, q) / det;
            float d = Vector3.Dot(e2, q) / det;
            if (u < 0f || w < 0f || u + w > 1f || d <= 1e-6f || d >= best) continue;
            best = d; face = (BodyFace)m.TriangleSurface[t];
        }
        return face;
    }

    /// <summary>
    /// From a seat in a car: glass to either side above the waist and doors below it, the roof overhead, the
    /// floor underfoot, the windscreen ahead and the back window behind; and each face is made of what the
    /// parts list says (glass is glass, the rest the body's panel).
    /// </summary>
    [Fact]
    public void ACabinsFacesAreWhatTheyAre()
    {
        int cabins = 0;
        foreach (string id in EveryVehicle())
        {
            var v = MachineRegistry.VehicleFor(id);
            if (VehicleCabin.Measure(v) is not { } g) continue;
            cabins++;
            var body = VehicleBodyShell.For(v, MachineRegistry.Describe(v, id));
            var m = body.Shell.Mesh;
            var high = new Vector3(0f, (g.Belt + v.HeightMetres) * 0.5f, g.Cz);
            var low = new Vector3(0f, (VehicleCabin.ChassisBottom + g.Belt) * 0.5f, g.Cz);
            Assert.Equal(BodyFace.Glass, FaceAlong(m, high, Vector3.UnitX));
            Assert.Equal(BodyFace.Glass, FaceAlong(m, high, -Vector3.UnitX));
            Assert.Equal(BodyFace.Door, FaceAlong(m, low, Vector3.UnitX));
            Assert.Equal(BodyFace.Door, FaceAlong(m, low, -Vector3.UnitX));
            Assert.Equal(BodyFace.Roof, FaceAlong(m, high, Vector3.UnitY));
            Assert.Equal(BodyFace.Floor, FaceAlong(m, low, -Vector3.UnitY));
            Assert.Equal(BodyFace.Glass, FaceAlong(m, high, Vector3.UnitZ));
            Assert.Equal(BodyFace.Glass, FaceAlong(m, high, -Vector3.UnitZ));
            // Below the waist, ahead: the bonnet's end, or the cabin's own front where the body has no bonnet.
            Assert.Equal(BodyFace.Panel, FaceAlong(m, low, Vector3.UnitZ));
            Assert.Equal("Glass", body.MaterialOf(BodyFace.Glass));
            string panel = v.Body?.PanelMaterial is { Length: > 0 } p ? p : "Metal";
            foreach (var face in new[] { BodyFace.Panel, BodyFace.Door, BodyFace.Roof, BodyFace.Floor })
                Assert.Equal(panel, body.MaterialOf(face));
        }
        Assert.True(cabins >= 15, $"only {cabins} vehicles with a cabin");
    }

    /// <summary>A body with nothing to sit in is one closed box of its size: a motorcycle.</summary>
    [Fact]
    public void ABodyWithNoCabinIsOneBoxOfItsSize()
    {
        int found = 0;
        foreach (string id in EveryVehicle())
        {
            var v = MachineRegistry.VehicleFor(id);
            if (VehicleCabin.Measure(v) != null) continue;
            found++;
            var shell = VehicleBodyShell.For(v, MachineRegistry.Describe(v, id)).Shell;
            Assert.Single(shell.Spec.Sections);
            Assert.Equal(12, shell.Mesh.TriangleCount);
            Assert.True(shell.Mesh.Convex);
            Assert.Empty(Faults(shell));
        }
        Assert.True(found > 0, "no vehicle without a cabin among the presets");
    }

    /// <summary>The mesh is the same every time it is made: the same triangles in the same order, so the same
    /// hash on every machine.</summary>
    [Fact]
    public void TheSameBodyIsTheSameMesh()
    {
        foreach (string id in EveryVehicle().Take(8))
        {
            var a = MachineRegistry.Shell(id);
            var b = MachineRegistry.Shell(id);
            Assert.Equal(a.Shell.Mesh.Hash, b.Shell.Mesh.Hash);
            Assert.Equal(a.Sources, b.Sources);
        }
    }

    /// <summary>The builder by itself, on a body of three unequal lengths: closed and outward whatever the
    /// heights are, with a waist that is none of them; and it refuses a body whose lengths do not meet.</summary>
    [Fact]
    public void AShellOfAnyHeightsIsClosed()
    {
        foreach (float waist in new[] { 0.7f, 0.9f, 1.1f, 1.3f, 5f })
            foreach (var (rear, front) in new[] { (0.8f, 0.95f), (1.4f, 0.6f), (0.9f, 0.9f), (1.6f, 1.6f) })
            {
                var spec = new BodyShellSpec(1.8f, 0.2f, waist, new[]
                {
                    new BodySection(-2.2f, -1.1f, rear, false), new BodySection(-1.1f, 0.9f, 1.5f, true), new BodySection(0.9f, 2.3f, front, false),
                });
                var faults = Faults(BodyShell.Build(spec));
                Assert.True(faults.Count == 0, $"waist {waist}, rear {rear}, front {front}: {string.Join("; ", faults.Take(3))}");
            }
        Assert.Throws<ArgumentException>(() => BodyShell.Build(new BodyShellSpec(1.8f, 0.2f, 0.9f, new[]
        {
            new BodySection(-2f, -1f, 0.9f, false), new BodySection(-0.9f, 1f, 1.5f, true),
        })));
        Assert.Throws<ArgumentException>(() => BodyShell.Build(new BodyShellSpec(1.8f, 0.2f, 0.9f, Array.Empty<BodySection>())));
    }
}
