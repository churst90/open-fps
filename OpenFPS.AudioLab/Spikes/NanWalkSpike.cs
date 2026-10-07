using System.Globalization;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --nan-walk [map=city] [from=x,y,z] [to=x,y,z] [steps=12] [open=0|1]: the listener's trace over the real
/// map, walked from one point to another, and every response the tracer publishes (the late part, the
/// directional part, the late field) and the energies behind them checked for anything not finite.
/// Written for "a pop inside one of the buildings and the audio just cut out" (Cody, 2026-10-03):
/// the Marlow Tower corridor, floor 0, into flat 00B through its door.
/// </summary>
public static class NanWalkSpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string mapId = args.FirstOrDefault(a => a.StartsWith("map="))?[4..] ?? "city";
        Vector3 P(string s) { var f = s.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray(); return new Vector3(f[0], f[1], f[2]); }
        var from = P(args.FirstOrDefault(a => a.StartsWith("from="))?[5..] ?? "-19.5,1.7,-102.52");
        var to = P(args.FirstOrDefault(a => a.StartsWith("to="))?[3..] ?? "-24.5,1.7,-102.52");
        int steps = int.TryParse(args.FirstOrDefault(a => a.StartsWith("steps="))?[6..], out int st) ? st : 12;
        string root = OpenFPS.AudioLab.LabPaths.Server() + System.IO.Path.DirectorySeparatorChar;
        var (world, _) = SirenRouteSpike.Load(root + "maps/" + mapId + ".json", root + "prefabs", "none", 0f);
        var boxes = SteamAudioScene.BoxesFromWorld(world);
        var listenerBoxes = SteamAudioScene.WithoutOpenGround(boxes);
        Console.WriteLine($"  {boxes.Count} boxes, {listenerBoxes.Count} without open ground");
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no context"); return 1; }
        using var scene = new SteamAudioScene(ctx);
        scene.Build(listenerBoxes);
        using var tr = new TracedReverb(ctx) { ExtractLate = true };
        tr.SetScene(scene);
        int bad = 0;
        // inside: the listener in, on and just off the faces of the boxes nearest the walk's middle
        // (a door leaf swung through the head, a jamb brushed), not along the walk.
        var points = new List<Vector3>();
        if (args.Contains("inside"))
        {
            var mid = Vector3.Lerp(from, to, 0.5f);
            foreach (var b in listenerBoxes.OrderBy(b => Vector3.Distance(b.Center, mid)).Take(6))
            {
                var h = b.Size * 0.5f;
                var c = new Vector3(b.Center.X, mid.Y, b.Center.Z);
                points.Add(c);
                foreach (var off in new[] { 0f, 0.0005f, 0.003f, 0.02f })
                {
                    points.Add(c + Vector3.Transform(new Vector3(h.X + off, 0, 0), b.Rotation));
                    points.Add(c - Vector3.Transform(new Vector3(0, 0, h.Z + off), b.Rotation));
                }
            }
            steps = points.Count - 1;
        }
        for (int k = 0; k <= steps; k++)
        {
            var at = points.Count > 0 ? points[k] : Vector3.Lerp(from, to, k / (float)Math.Max(1, steps));
            int place = k <= steps / 2 ? 94 : 95;
            tr.SetListener(at, place);
            int runs = tr.Runs;
            var seen = tr.Late;
            for (int t = 0; t < 400 && (tr.Runs < runs + 2 || ReferenceEquals(tr.Late, seen)); t++) Thread.Sleep(25);
            var copies = EarlyCopies.From(scene.Solids, at, tr.SampleRate);
            string what = Check(tr, copies);
            Console.WriteLine($"  ({at.X:F2}, {at.Y:F2}, {at.Z:F2}) region {place}: start {tr.Smooth?.Start * 1000.0 / tr.SampleRate:F1} ms, "
                            + $"copies {(copies == null ? "none" : $"{copies.Copies.Count}, first {copies.FirstArrival * 1000.0 / tr.SampleRate:F1} ms, {10 * Math.Log10(copies.Total().Sum() + 1e-30):F1} dB")}: {what}");
            if (what != "finite") bad++;
        }
        Console.WriteLine(bad == 0 ? "  every response finite" : $"  FAIL: {bad} point(s) with something not finite");

        // Each source's own late energy (LateField) along the same walk, sources on a grid round the
        // listener: in rooms, in the walls, on the door, out in the street.
        using var lf = new LateField(ctx);
        lf.SetScene(scene);
        var rng = new Random(3);
        int badAnswers = 0, answers = 0;
        var answersBuf = new LateField.Answer[LateField.MaxSources];
        for (int k = 0; k <= steps; k++)
        {
            var at = Vector3.Lerp(from, to, k / (float)Math.Max(1, steps));
            for (int round = 0; round < 3; round++)
            {
                var ids = new int[LateField.MaxSources]; var pts = new Vector3[LateField.MaxSources];
                for (int i = 0; i < ids.Length; i++)
                {
                    ids[i] = 1000 + i;
                    float r = i < 8 ? 0.2f + 3f * (float)rng.NextDouble() : 5f + 40f * (float)rng.NextDouble();
                    double a = rng.NextDouble() * Math.PI * 2;
                    pts[i] = at + new Vector3((float)(r * Math.Cos(a)), (float)(rng.NextDouble() * 2.4 - 1.6), (float)(r * Math.Sin(a)));
                }
                int runs = lf.Runs;
                lf.Want(at, ids, pts);
                for (int t = 0; t < 400 && lf.Runs < runs + 2; t++) Thread.Sleep(10);
                int m = lf.CopyAnswers(answersBuf);
                for (int i = 0; i < m; i++)
                {
                    var ans = answersBuf[i]; answers++;
                    if (!float.IsFinite(ans.Ratio) || !float.IsFinite(ans.Directivity) || !float.IsFinite(ans.Direction.X + ans.Direction.Y + ans.Direction.Z))
                    {
                        badAnswers++;
                        if (badAnswers <= 10)
                            Console.WriteLine($"  LateField NOT FINITE: listener ({at.X:F2}, {at.Y:F2}, {at.Z:F2}), source ({ans.At.X:F2}, {ans.At.Y:F2}, {ans.At.Z:F2}): ratio {ans.Ratio}, directivity {ans.Directivity}, direction {ans.Direction}");
                    }
                }
            }
        }
        Console.WriteLine($"  LateField: {answers} answers, {badAnswers} not finite");
        return bad == 0 && badAnswers == 0 ? 0 : 1;
    }

    private static bool Finite(float[]? x) => x == null || x.All(float.IsFinite);
    private static bool Finite(double[]? x) => x == null || x.All(double.IsFinite);

    private static string Check(TracedReverb tr, EarlyCopies? copies)
    {
        var faults = new List<string>();
        if (!Finite(tr.LastReadBack)) faults.Add("the trace read back");
        if (copies != null && copies.Copies.Any(c => !Finite(c.Band))) faults.Add("the copies' energies");
        if (tr.Smooth is { } s)
        {
            for (int b = 0; b < SmoothTail.Bands; b++)
                for (int f = 0; f < s.Frames; f++)
                    if (!double.IsFinite(s.OmniEnergy(b, f))) { faults.Add($"the averaged energy (band {b}, frame {f})"); b = SmoothTail.Bands; break; }
            if (!Finite(s.LastTook)) faults.Add("what the copies took");
        }
        if (tr.Late is { } l && (!Finite(l.Re) || !Finite(l.Im))) faults.Add("the late part");
        if (tr.LateSdm is { } sdm)
        {
            if (sdm.PerDirection.Any(d => d != null && (!Finite(d.Re) || !Finite(d.Im)))) faults.Add("the directional part");
            if (!Finite(sdm.LateShare) || !Finite(sdm.Share)) faults.Add("the shares");
        }
        if (tr.DiffuseLate is { } dl && (!Finite(dl.C0) || !Finite(dl.C1))) faults.Add("the late field");
        return faults.Count == 0 ? "finite" : "NOT FINITE: " + string.Join(", ", faults);
    }
}
