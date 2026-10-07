using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --wall-tl: the transmission loss of the city's constructions, one-third octave by one-third octave,
/// and the three figures the mixer applies (WallTransmission, AcousticBands). Flanking is included in
/// every figure. The published ranges to read it against are in WallTransmission's notes.
/// </summary>
public static class WallTlSpike
{
    public static int Run()
    {
        AcousticRegistry.Initialize();
        var walls = new List<(string Name, string Material, Vector3 Size, WallBuild Build)>
        {
            ("brick 35 cm outer wall", "Brick", new(16.86f, 2.73f, 0.35f), WallBuild.Solid),
            ("brick 10 cm", "Brick", new(16.86f, 2.73f, 0.10f), WallBuild.Solid),
            ("concrete slab 25 cm", "Concrete", new(21.25f, 0.25f, 90f), WallBuild.Solid),
            ("concrete wall 20 cm", "Concrete", new(10f, 3f, 0.20f), WallBuild.Solid),
            ("partition 35 cm, 12.5 mm board on studs", "Plaster", new(16.86f, 2.73f, 0.35f), new WallBuild(0.0125f, 0.6f)),
            ("partition 10 cm, 12.5 mm board on studs", "Plaster", new(16.86f, 2.73f, 0.10f), new WallBuild(0.0125f, 0.6f)),
            ("partition 35 cm as a solid slab (before)", "Plaster", new(16.86f, 2.73f, 0.35f), WallBuild.Solid),
            ("plasterboard ceiling 3 cm", "Plaster", new(20.55f, 0.03f, 89.3f), new WallBuild(0.0125f, 0.6f)),
            ("wood door 6 cm solid", "Wood", new(1.1f, 2.1f, 0.06f), WallBuild.Solid),
            ("steel door 8 cm, 1.2 mm skins", "Metal", new(1.0f, 2.1f, 0.08f), new WallBuild(0.0012f, 0f)),
            ("glass pane 10 mm", "Glass", new(2f, 2.4f, 0.01f), WallBuild.Solid),
            ("double glazing 4-16-4", "Glass", new(2f, 2.4f, 0.024f), new WallBuild(0.004f, 0f)),
            ("glass 6 cm solid (before)", "Glass", new(2f, 2.4f, 0.06f), WallBuild.Solid),
            ("steel panel 5 cm", "Metal", new(2f, 3f, 0.05f), WallBuild.Solid),
        };
        float[] thirds = { 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000 };
        Console.Write($"{"",-44}");
        foreach (var f in thirds) Console.Write($"{(f >= 1000 ? (f / 1000f).ToString("0.#") + "k" : f.ToString("0")),5}");
        Console.WriteLine("   | bands low/mid/high dB");
        foreach (var w in walls)
        {
            var p = AcousticRegistry.GetProperties(w.Material);
            Console.Write($"{w.Name,-44}");
            foreach (var f in thirds) Console.Write($"{WallTransmission.BoxLossDb(p, w.Size, w.Build, f),5:F0}");
            var (l, m, h) = WallTransmission.BandGains(p, w.Size, w.Build);
            static float Db(float g) => -20f * MathF.Log10(MathF.Max(1e-9f, g));
            Console.WriteLine($"   | {Db(l),5:F1} {Db(m),5:F1} {Db(h),5:F1}");
        }
        var c = AcousticBands.GeometricCentresHz;
        Console.WriteLine($"\nmixer bands: crossovers {AcousticBands.LowCrossoverHz:F0} / {AcousticBands.HighCrossoverHz:F0} Hz, centres {c.Low:F0} / {c.Mid:F0} / {c.High:F0} Hz");
        return 0;
    }
}
