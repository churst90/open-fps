using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --layers: layers in contact against walls apart. For each construction of boxes, what Steam Audio's
/// direct simulation gives (the scene built as the game builds it, with LayeredFaces), what the hand-rolled
/// tracer gives, and the panel model's own figure; then the city floor's transmission loss one-third octave
/// by one-third octave.
/// </summary>
public static class LayersSpike
{
    private static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-9f, g));

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        if (args.FirstOrDefault(a => a.StartsWith("map=")) is { } mapArg) return Map(mapArg[4..], args);
        var q = Quaternion.Identity;
        var slab = new Vector3(20f, 0.25f, 20f);
        var stud = new WallBuild(0.0125f, 0.6f);
        SteamAudioScene.Box B(float y, Vector3 size, string m, WallBuild build = default) => new(new Vector3(0, y, 0), size, q, m, build);
        SteamAudioScene.Box W(float x, string m, float t, WallBuild build = default) => new(new Vector3(x, 1.5f, 0), new Vector3(t, 3f, 20f), q, m, build);
        var props = (string m) => AcousticRegistry.GetProperties(m);
        var floorLayers = new List<WallTransmission.Layer>
        {
            new(props("Plaster"), 0.03f, stud), new(props("Concrete"), 0.25f, default),
            new(props("Concrete"), 0.25f, default), new(props("Carpet"), 0.04f, default),
        };
        var cases = new List<(string Name, List<SteamAudioScene.Box> Boxes, Vector3 Ear, Vector3 Src, (float, float, float)? Model)>
        {
            ("one 25 cm slab", new() { B(3.125f, slab, "Concrete") }, Up.Ear, Up.Src, null),
            ("two 25 cm slabs 1 m apart", new() { B(2.625f, slab, "Concrete"), B(3.875f, slab, "Concrete") }, Up.Ear, Up.Src, null),
            ("two 25 cm slabs in contact", new() { B(3.125f, slab, "Concrete"), B(3.375f, slab, "Concrete") }, Up.Ear, Up.Src,
             WallTransmission.LayeredBandGains(new List<WallTransmission.Layer> { new(props("Concrete"), 0.25f, default), new(props("Concrete"), 0.25f, default) }, 20f, 20f)),
            ("one 50 cm slab", new() { B(3.25f, new Vector3(20f, 0.5f, 20f), "Concrete") }, Up.Ear, Up.Src, null),
            ("25 cm slab + 4 cm carpet (8 x 8 m)", new() { B(3.125f, slab, "Concrete"), B(3.27f, new Vector3(8f, 0.04f, 8f), "Carpet") }, Up.Ear, Up.Src,
             WallTransmission.LayeredBandGains(new List<WallTransmission.Layer> { new(props("Concrete"), 0.25f, default), new(props("Carpet"), 0.04f, default) }, 20f, 20f)),
            ("city floor: soffit, slab, slab, carpet", new()
            {
                B(2.985f, new Vector3(20f, 0.03f, 20f), "Plaster", stud),
                B(3.125f, slab, "Concrete"), B(3.375f, slab, "Concrete"),
                B(3.52f, new Vector3(8f, 0.04f, 8f), "Carpet"),
            }, Up.Ear, Up.Src, WallTransmission.LayeredBandGains(floorLayers, 20f, 20f)),
            ("stud partition 35 cm", new() { W(0f, "Plaster", 0.35f, stud) }, Across.Ear, Across.Src, null),
            ("two stud partitions, 2.2 m of air", new() { W(-1.275f, "Plaster", 0.35f, stud), W(1.275f, "Plaster", 0.35f, stud) }, Across.Ear, Across.Src, null),
            ("two stud partitions in contact", new() { W(-0.175f, "Plaster", 0.35f, stud), W(0.175f, "Plaster", 0.35f, stud) }, Across.Ear, Across.Src,
             WallTransmission.LayeredBandGains(new List<WallTransmission.Layer> { new(props("Plaster"), 0.35f, stud), new(props("Plaster"), 0.35f, stud) }, 3f, 20f)),
            ("brick 35 cm", new() { W(0f, "Brick", 0.35f) }, Across.Ear, Across.Src, null),
        };
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no Steam Audio context"); return 1; }
        SteamAudioScene.UseEmbree(ctx);
        Console.WriteLine("dB per band, low/mid/high (50-315 Hz, 400-3150 Hz, 4-12.5 kHz)");
        Console.WriteLine($"{"",-40} {"Steam Audio",20} {"tracer",20} {"the model",20} {"boxes in a row",20}");
        foreach (var (name, boxes, ear, src, model) in cases)
        {
            using var scene = new SteamAudioScene(ctx);
            scene.Build(boxes);
            using var sim = new SteamAudioSimulator(ctx, maxSources: 2);
            sim.SetScene(scene);
            var source = sim.AcquireSource();
            sim.SetListener(ear);
            sim.SetSourceInputs(source, src, 0.01f);
            sim.Run();
            var r = sim.GetResult(source);
            float sl = 0, sm = 0, sh = 0;
            foreach (var b in boxes)
            {
                var (gl, gm, gh) = WallTransmission.BandGains(b.Material, b.Size, b.Build);
                sl += Db(gl); sm += Db(gm); sh += Db(gh);
            }
            var (ml, mm, mh) = model ?? (boxes.Count == 1 ? WallTransmission.BandGains(boxes[0].Material, boxes[0].Size, boxes[0].Build) : (MathF.Pow(10f, sl / 20f), MathF.Pow(10f, sm / 20f), MathF.Pow(10f, sh / 20f)));
            var world = TracerWorld(boxes);
            new SpatialService().GetOcclusionData(world, ear, src, out _, out _, out float tl, out float tm, out float th);
            Console.WriteLine($"{name,-40} {Db(r.TransLow),6:F1} {Db(r.TransMid),6:F1} {Db(r.TransHigh),6:F1} "
                            + $"{Db(tl),6:F1} {Db(tm),6:F1} {Db(th),6:F1} {Db(ml),6:F1} {Db(mm),6:F1} {Db(mh),6:F1} {sl,6:F1} {sm,6:F1} {sh,6:F1}");
            sim.ReleaseSource(source);
        }

        float[] thirds = { 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000 };
        Console.WriteLine("\ntransmission loss, dB, by one-third octave (flanking included)");
        Console.Write($"{"",-40}");
        foreach (var f in thirds) Console.Write($"{(f >= 1000 ? (f / 1000f).ToString("0.#") + "k" : f.ToString("0")),5}");
        Console.WriteLine();
        void Row(string name, IReadOnlyList<WallTransmission.Layer> layers)
        {
            Console.Write($"{name,-40}");
            foreach (var f in thirds) Console.Write($"{WallTransmission.LayeredLossDb(layers, 20f, 18f, f),5:F0}");
            Console.WriteLine();
        }
        Row("one 25 cm slab", new[] { new WallTransmission.Layer(props("Concrete"), 0.25f, default) });
        Row("one 20 cm slab", new[] { new WallTransmission.Layer(props("Concrete"), 0.20f, default) });
        Row("one 15 cm slab", new[] { new WallTransmission.Layer(props("Concrete"), 0.15f, default) });
        Row("city floor (3+25+25+4 cm, one panel)", floorLayers);
        Row("20 cm slab, 3 cm plaster, carpet", new[] { new WallTransmission.Layer(props("Plaster"), 0.03f, stud), new WallTransmission.Layer(props("Concrete"), 0.20f, default), new WallTransmission.Layer(props("Carpet"), 0.01f, default) });
        return 0;
    }

    /// <summary>map=NAME: the map's constructions as the game finds them: how many, how many faces they
    /// show, the largest, and which materials are layered together.</summary>
    private static int Map(string mapId, string[] args)
    {
        string root = args.FirstOrDefault(a => a.StartsWith("root="))?[5..] ?? AppContext.BaseDirectory;
        var world = PathProbeSpike.LoadAsClient(root, mapId);
        var boxes = SteamAudioScene.BoxesFromWorld(world);
        var plan = SteamAudioScene.PlanOf(boxes);
        int sheets = boxes.Count(b => OpenFPS.Common.Constructions.IsSheet(b.Size));
        Console.WriteLine($"  {mapId}: {boxes.Count} boxes, {sheets} sheets; {plan.Constructions} constructions of {plan.Members.Count} layers, "
                        + $"{plan.Faces} faces shown ({plan.Milliseconds:F0} ms)");
        Console.WriteLine($"  triangles: {boxes.Count * 12} as boxes, {boxes.Count * 12 - plan.Members.Count * 4 + plan.Faces * 2} with the constructions' faces");
        var pairs = new Dictionary<string, int>();
        foreach (var (i, _) in plan.Members)
        {
            string m = boxes[i].Material;
            pairs[m] = pairs.GetValueOrDefault(m) + 1;
        }
        Console.WriteLine("  layers by material: " + string.Join(", ", pairs.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}")));
        foreach (var (i, m) in plan.Members.OrderByDescending(p => p.Value.Faces.Length).Take(8))
        {
            var b = boxes[i];
            string name = world.Entities.TryGetValue(b.EntityId, out var e) ? e.Definition.Identity.Name : "";
            Console.WriteLine($"    {m.Faces.Length,5} faces: {b.Material} {b.Size.X:F2} x {b.Size.Y:F2} x {b.Size.Z:F2} at ({b.Center.X:F1}, {b.Center.Y:F2}, {b.Center.Z:F1}) {name}");
        }
        foreach (var b in boxes.Where((b, i) => plan.Members.ContainsKey(i) && (b.Material is "Glass" or "Metal" or "Water")))
            Console.WriteLine($"    layered {b.Material}: {b.Size.X:F2} x {b.Size.Y:F2} x {b.Size.Z:F2} at ({b.Center.X:F1}, {b.Center.Y:F2}, {b.Center.Z:F1})");
        return 0;
    }

    /// <summary>An ear under a floor and a source over it, off the vertical; an ear and source either side
    /// of a wall.</summary>
    private static readonly (Vector3 Ear, Vector3 Src) Up = (new Vector3(0.3f, 1.6f, 0.2f), new Vector3(0.8f, 5.2f, 1.1f));
    private static readonly (Vector3 Ear, Vector3 Src) Across = (new Vector3(-4f, 1.6f, 0.3f), new Vector3(4f, 1.5f, -0.2f));

    internal static WorldSnapshot TracerWorld(List<SteamAudioScene.Box> boxes)
    {
        var world = new ClientWorldState();
        world.Clear(new Vector3(200, 40, 200));
        int id = 1;
        foreach (var b in boxes)
            world.RegisterDefinition(new OpenFPS.Common.Networking.EntityDefinition
            {
                EntityId = id++,
                Type = OpenFPS.Common.Components.EntityType.StaticObject,
                Transform = new OpenFPS.Common.Components.Transform { Position = b.Center, Rotation = b.Rotation },
                Collider = new OpenFPS.Common.Components.ColliderComponent { Shape = OpenFPS.Common.Components.ColliderShape.Box, Size = b.Size, IsSolid = true },
                Material = new OpenFPS.Common.Components.MaterialComponent { Material = b.Material },
                Acoustics = new OpenFPS.Common.Components.AcousticComponent { LeafMetres = b.Build.LeafMetres, StudSpacingMetres = b.Build.StudSpacingMetres },
            });
        return world.GetSnapshot();
    }
}
