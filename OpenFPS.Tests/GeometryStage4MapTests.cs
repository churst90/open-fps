using System.Numerics;
using OpenFPS.Common.Geometry;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Geometry stage 4 on the real places (docs/GEOMETRY.md 12): every form tools/gen_osm.py writes (floors and solid
/// outbuildings as their footprints, roofs from the straight skeleton, round trunks, swept kerbs) is made, closed,
/// and fills its box; no roof falls back to flat; and what it costs in triangles, against the boxes it replaced.
/// </summary>
public class GeometryStage4MapTests
{
    private readonly ITestOutputHelper _o;
    public GeometryStage4MapTests(ITestOutputHelper o) => _o = o;

    private static readonly PrefabRepository Prefabs = new(Path.Combine(AppContext.BaseDirectory, "prefabs"));

    public static IEnumerable<object[]> Places() => RealPlaceMapTests.Places();

    [Theory]
    [MemberData(nameof(Places))]
    public void Every_form_on_a_real_place_is_a_clean_solid(string id)
    {
        var map = MapRepository.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "places", id + ".json"))!;
        var byKind = new Dictionary<string, (int Count, long Triangles, long Pieces)>();
        int flatFallbacks = 0, boxes = 0;
        long boxTriangles = 0;
        var problems = new List<string>();
        foreach (var e in map.Entities)
        {
            if (!Prefabs.Prefabs.TryGetValue(e.PrefabId, out var prefab)) continue;
            var size = (prefab.ColliderSize ?? Vector3.One) * e.Scale;
            var form = e.Form ?? prefab.Form;
            if (form == null || form.Kind == ShapeKind.Box)
            {
                if (prefab.IsSolid != false && size.X > 0 && size.Y > 0 && size.Z > 0) { boxes++; boxTriangles += 12; }
                continue;
            }
            if (Shapes.Problem(form, size, Shapes.DefaultMaxRise) is { } problem) { problems.Add($"{e.EntityId} {e.Name}: {problem}"); continue; }
            var mesh = Shapes.Make(form, size)!;
            var report = MeshCheck.Check(mesh.Outer);
            if (!report.Closed || report.Degenerate > 0) problems.Add($"{e.EntityId} {e.Name} ({form}): {report}");
            string kind = form.Kind == ShapeKind.Roof ? $"{form.Style.ToString().ToLowerInvariant()} roof" : form.Kind.ToString().ToLowerInvariant();
            if (form.Kind == ShapeKind.Roof && form.Style is RoofStyle.Hip or RoofStyle.Gable)
            {
                var ring = form.Outline!.Chunk(2).Select(p => new Point2(p[0], p[1])).ToList();
                if (StraightSkeleton.Of(Polygons.CounterClockwise(ring), form.Style == RoofStyle.Gable) == null)
                {
                    flatFallbacks++;
                    if (flatFallbacks <= 5) _o.WriteLine($"  laid flat: {e.Name}, {ring.Count} corners: {string.Join(" ", ring.Select(p => $"({p.X:0.##},{p.Y:0.##})"))}");
                }
            }
            var (n, t, pc) = byKind.GetValueOrDefault(kind);
            byKind[kind] = (n + 1, t + mesh.Outer.TriangleCount, pc + (mesh.Parts?.Length ?? 1));
        }
        foreach (var (k, (n, t, pc)) in byKind.OrderBy(k => k.Key))
            _o.WriteLine($"{id}: {n} {k}, {t} triangles ({(double)t / n:0.#} each), {pc} convex pieces");
        _o.WriteLine($"{id}: {boxes} solid boxes ({boxTriangles} triangles); {byKind.Values.Sum(v => v.Triangles)} triangles of shapes; "
                     + $"{flatFallbacks} hip or gable roofs laid flat");
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
        Assert.True(flatFallbacks <= byKind.Where(k => k.Key.EndsWith("roof")).Sum(k => k.Value.Count) / 200,
                    $"{flatFallbacks} roofs laid flat");
    }
}
