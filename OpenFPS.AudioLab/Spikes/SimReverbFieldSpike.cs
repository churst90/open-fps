using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --sim-reverbfield: the reflection simulation's RT60 beside the geometry's enclosure, for places from
/// an empty field to a sealed box. Outdoors the parametric fit is made on noise and reports some time
/// anyway (200 to 1579 ms on the speedway straight); enclosure knows the difference. Headless.
/// docs/AUDIOLAB_NOTES.md, "The simulator's reverb outdoors".
/// </summary>
public static class SimReverbFieldSpike
{
    private static IntPtr _ctx;

    public static int Run()
    {
        AcousticRegistry.Initialize();
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out _ctx) != Phonon.IPL_STATUS_SUCCESS)
        {
            Console.WriteLine("Steam Audio context create failed.");
            return 1;
        }

        var q = Quaternion.Identity;

        // A speedway straight as the generator builds it: a 3.5 m outer wall, a 0.9 m pit wall about
        // twenty metres inside it, and a grandstand deck behind the outer wall.
        var floor     = new SteamAudioScene.Box(new Vector3(0, -0.25f, 0), new Vector3(400, 0.5f, 400), q, "Grass");
        var pitWall   = new SteamAudioScene.Box(new Vector3(0, 0.45f, 0), new Vector3(90, 0.9f, 0.4f), q, "Concrete");
        var outerWall = new SteamAudioScene.Box(new Vector3(0, 1.75f, -22), new Vector3(90, 3.5f, 0.6f), q, "Concrete");
        var stand     = new SteamAudioScene.Box(new Vector3(0, 6f, -60), new Vector3(200, 12f, 16f), q, "Audience");

        var room = new List<SteamAudioScene.Box>
        {
            new(new Vector3(5, -0.25f, 5), new Vector3(11, 0.5f, 11), q, "Concrete"),  // floor
            new(new Vector3(5, 4.25f, 5),  new Vector3(11, 0.5f, 11), q, "Concrete"),  // ceiling
            new(new Vector3(5, 2, 10.25f), new Vector3(11, 4, 0.5f), q, "Concrete"),   // north
            new(new Vector3(5, 2, -0.25f), new Vector3(11, 4, 0.5f), q, "Concrete"),   // south
            new(new Vector3(10.25f, 2, 5), new Vector3(0.5f, 4, 11), q, "Concrete"),   // east
            new(new Vector3(-0.25f, 2, 5), new Vector3(0.5f, 4, 11), q, "Concrete"),   // west
        };
        var roofless = new List<SteamAudioScene.Box>(room); roofless.RemoveAt(1);

        Console.WriteLine("place                                RT60 low/mid/high (s)   enclosure    decay today");
        Console.WriteLine("--------------------------------------------------------------------------------------");

        var open      = Row("open field, floor only",        new[] { floor }, new Vector3(0, 1.7f, 10f));
        Row("beside a 0.9 m pit wall",       new[] { floor, pitWall }, new Vector3(0, 1.7f, 1.5f));
        var between   = Row("between pit and outer wall",    new[] { floor, pitWall, outerWall }, new Vector3(0, 1.7f, -11f));
        var atWall    = Row("hard against the outer wall",   new[] { floor, pitWall, outerWall }, new Vector3(0, 1.7f, -21f));
        Row("straight, with a grandstand",   new[] { floor, pitWall, outerWall, stand }, new Vector3(0, 1.7f, -30f));
        Row("walled yard, no ceiling",       roofless.ToArray(), new Vector3(5, 1.7f, 5f));
        var sealedBox = Row("SEALED concrete room 10x10x4",  room.ToArray(), new Vector3(5, 1.7f, 5f));

        Console.WriteLine();
        Console.WriteLine("The decay column is what reaches the mixer today. If the outdoor rows are not");
        Console.WriteLine("clearly shorter than the sealed room, the decay time alone is not enough to tell");
        Console.WriteLine("a room from a wall, and the energy column is what has to decide it.");
        Console.WriteLine();

        bool timeAlone = sealedBox.Max > 2f * MathF.Max(open.Max, MathF.Max(between.Max, atWall.Max));
        float encOpen = Enc(new[] { floor }, new Vector3(0, 1.7f, 10f));
        float encWall = Enc(new[] { floor, pitWall, outerWall }, new Vector3(0, 1.7f, -21f));
        float encStand = Enc(new[] { floor, pitWall, outerWall, stand }, new Vector3(0, 1.7f, -30f));
        float encYard = Enc(roofless.ToArray(), new Vector3(5, 1.7f, 5f));
        float encRoom = Enc(room.ToArray(), new Vector3(5, 1.7f, 5f));
        bool energySeparates = encRoom > 3f * encOpen && encRoom > encYard && encYard > encStand;

        Console.WriteLine(timeAlone
            ? "RT60 alone DOES separate indoors from out."
            : "RT60 alone does NOT separate indoors from out — an open straight fits as long a tail as a room.");
        Console.WriteLine(energySeparates
            ? "ENCLOSURE separates them cleanly and in the right order, and it is the honest measure of 'is there a field here'."
            : "Enclosure does not separate them either; something else is wrong.");

        Console.WriteLine();
        Console.WriteLine($"enclosure open field   {encOpen,6:P1}");
        Console.WriteLine($"enclosure by the wall  {encWall,6:P1}");
        Console.WriteLine($"enclosure w/ grandstand{encStand,6:P1}");
        Console.WriteLine($"enclosure roofless yard{encYard,6:P1}   (walls, no roof)");
        Console.WriteLine($"enclosure sealed room  {encRoom,6:P1}");
        Console.WriteLine($"ratio sealed:open      = {Ratio(encRoom, encOpen):F1}x");

        Phonon.iplContextRelease(ref _ctx);
        return energySeparates ? 0 : 2;
    }

    private static float Enc(IReadOnlyList<SteamAudioScene.Box> boxes, Vector3 listener)
    {
        var solids = new List<Enclosure.Solid>(boxes.Count);
        foreach (var b in boxes) solids.Add(new Enclosure.Solid(b.Center, b.Size, b.Rotation, b.Material));
        return Enclosure.Measure(listener, solids);
    }

    private static float Ratio(float a, float b) => b > 1e-9f ? a / b : float.PositiveInfinity;

    private static SteamAudioSimulator.ReverbResult Row(string name, SteamAudioScene.Box[] boxes, Vector3 listener)
    {
        var r = Measure(boxes, listener);
        var solids = new List<Enclosure.Solid>(boxes.Length);
        foreach (var b in boxes) solids.Add(new Enclosure.Solid(b.Center, b.Size, b.Rotation, b.Material));
        float enc = Enclosure.Measure(listener, solids);
        Console.WriteLine($"{name,-34} ({r.Rt60Low,5:F2},{r.Rt60Mid,5:F2},{r.Rt60High,5:F2})      " +
                          $"{enc,6:P0}     {SteamAudioSimulator.ReverbDecayMs(r),6:F0} ms");
        return r;
    }

    private static SteamAudioSimulator.ReverbResult Measure(IReadOnlyList<SteamAudioScene.Box> boxes, Vector3 listener)
    {
        var scene = new SteamAudioScene(_ctx);
        scene.Build(new List<SteamAudioScene.Box>(boxes));
        var sim = new SteamAudioSimulator(_ctx, maxSources: 4, enableReflections: true, enableDirect: false);
        sim.SetScene(scene);
        IntPtr s = sim.AcquireSource();
        sim.SetSourceInputs(s, listener);
        sim.SetListener(listener);
        // The estimate accumulates across runs, as in the worker over time; one run is the first rays' luck.
        for (int i = 0; i < 12; i++) sim.Run();
        var r = sim.GetReverb(s);
        sim.Dispose();
        scene.Dispose();
        return r;
    }
}
