using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Each source's own late energy and direction, as the game traces them (LateField), in the three
/// places the tail was measured in: flat 01F, the city tunnel, a 20 m street.
///
///   --late-field [place=flat|tunnel|street]
///
/// What it should show, from the source-side traces of 2026-09-29: in the flat, every source raises
/// about what one at the listener does and the late field comes from everywhere (|I|/E near 0.07);
/// in the tunnel the late energy falls about 7 dB from 5 to 45 m and comes from the source's side
/// (|I|/E 0.4-0.8); the street falls faster still. The send's old stand-in, distance to the
/// enclosure, gave the tunnel 1.8 dB over the same span.
/// </summary>
public static class LateFieldSpike
{
    private static readonly Quaternion Q = Quaternion.Identity;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no Steam Audio context"); return 1; }
        var ground = new SteamAudioScene.Box(new Vector3(0, -0.25f, 0), new Vector3(800, 0.5f, 800), Q, "Asphalt");
        var flat = new List<SteamAudioScene.Box>
        {
            new(new Vector3(0, 0.04f, 0), new Vector3(8.65f, 0.04f, 17.86f), Q, "Carpet"),
            new(new Vector3(0, -0.07f, 0), new Vector3(21f, 0.17f, 90f), Q, "Concrete"),
            new(new Vector3(0, 2.735f, 0), new Vector3(20.5f, 0.03f, 89.3f), Q, "Plaster"),
            new(new Vector3(4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, 80f), Q, "Brick"),
            new(new Vector3(-4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, 17.86f), Q, "Plaster"),
            new(new Vector3(0, 1.4f, -9.0f), new Vector3(8.65f, 2.73f, 0.2f), Q, "Plaster"),
            new(new Vector3(0, 1.4f, 9.0f), new Vector3(8.65f, 2.73f, 0.2f), Q, "Plaster"),
            new(new Vector3(3.2f, 0.32f, -3.4f), new Vector3(1.8f, 0.6f, 1.8f), Q, "Audience"),
        };
        var flatEar = new Vector3(0.175f, 1.7f, 0.16f);
        var tunnel = new List<SteamAudioScene.Box>
        {
            ground,
            new(new Vector3(-6.25f, 2.75f, 0), new Vector3(0.5f, 5.5f, 100f), Q, "Concrete"),
            new(new Vector3(6.25f, 2.75f, 0), new Vector3(0.5f, 5.5f, 100f), Q, "Concrete"),
            new(new Vector3(-8f, 2.75f, 0), new Vector3(3f, 5.5f, 100f), Q, "Concrete"),
            new(new Vector3(8f, 2.75f, 0), new Vector3(3f, 5.5f, 100f), Q, "Concrete"),
            new(new Vector3(0, 5.7f, 0), new Vector3(19f, 0.4f, 100f), Q, "Concrete"),
        };
        var street = new List<SteamAudioScene.Box> { ground };
        for (int i = 0; i < 8; i++)
        {
            float z = -70f + i * 20f;
            street.Add(new(new Vector3(-15f, 9f, z), new Vector3(10f, 18f, 20f), Q, "Brick"));
            street.Add(new(new Vector3(15f, 9f, z), new Vector3(10f, 18f, 20f), Q, "Brick"));
        }
        var places = new List<(string Name, List<SteamAudioScene.Box> Boxes, Vector3 Ear, Vector3[] Srcs)>
        {
            ("flat", flat, flatEar, new[] { flatEar + new Vector3(0, -0.5f, 3f), flatEar + new Vector3(-2f, -0.5f, -7f) }),
            ("tunnel", tunnel, new Vector3(1.5f, 1.6f, 0f), new[] { new Vector3(-3f, 1f, 3f), new Vector3(-3f, 1f, 10f), new Vector3(-3f, 1f, 25f), new Vector3(-3f, 1f, 45f) }),
            ("street", street, new Vector3(2f, 1.6f, 0f), new[] { new Vector3(-3f, 1f, 5f), new Vector3(-3f, 1f, 20f), new Vector3(-3f, 1f, 50f) }),
        };
        string only = args.FirstOrDefault(a => a.StartsWith("place="))?[6..] ?? "";
        foreach (var (name, boxes, ear, srcs) in places)
        {
            if (only != "" && name != only) continue;
            var solids = boxes.Select(b => new Enclosure.Solid(b.Center, b.Size, b.Rotation, b.Material)).ToList();
            float enc = Enclosure.Look(ear, solids).Enclosure;
            using var scene = new SteamAudioScene(ctx);
            scene.Build(SteamAudioScene.WithoutOpenGround(boxes));
            using var lf = new LateField(ctx);
            lf.SetScene(scene);
            var ids = Enumerable.Range(1, srcs.Length).ToArray();
            lf.Want(ear, ids, srcs);
            for (int t = 0; t < 200 && lf.Runs < 3; t++) Thread.Sleep(100);
            Console.WriteLine($"\n===== {name}: ear {ear}, enclosure {enc:F2}, {lf.Runs} traces, last {lf.LastRunMs:F0} ms");
            Console.WriteLine("   dist | late energy re a source at the ear | tail/direct, new send  old send | |I|/E  arrives from the source's side (cos)");
            float? first = null, firstOld = null;
            for (int i = 0; i < srcs.Length; i++)
            {
                if (!lf.TryGet(ids[i], out var a)) { Console.WriteLine($"  {i}: no answer"); continue; }
                float d = Vector3.Distance(ear, srcs[i]);
                float newDb = 20f * MathF.Log10(d * MathF.Sqrt(a.Ratio));
                float oldDb = 20f * MathF.Log10(MathF.Pow(MathF.Max(d, 1f), enc));
                first ??= newDb - 20f * MathF.Log10(d); firstOld ??= oldDb - 20f * MathF.Log10(d);
                var toSrc = Vector3.Normalize(srcs[i] - ear);
                Console.WriteLine($"  {d,5:F1} | {10 * MathF.Log10(a.Ratio),8:F1} dB | tail re its direct {newDb,6:F1} {oldDb,6:F1} dB | "
                                + $"tail re the nearest {newDb - 20f * MathF.Log10(d) - first,6:F1} {oldDb - 20f * MathF.Log10(d) - firstOld,6:F1} dB | {a.Directivity:F2}  {Vector3.Dot(a.Direction, toSrc):F2}");
            }
        }
        Phonon.iplContextRelease(ref ctx);
        return 0;
    }
}
