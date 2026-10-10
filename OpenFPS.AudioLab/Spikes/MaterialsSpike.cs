using System.Globalization;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// The material table as it stands, one row a material, by family (docs/MATTER.md section 2).
///
///   --materials            a markdown table, the one docs/MATTER.md carries
/// </summary>
public static class MaterialsSpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        var ci = CultureInfo.InvariantCulture;
        string F(float v, string fmt = "G3") => float.IsPositiveInfinity(v) ? "inf"
                                               : MathF.Abs(v) >= 1000f ? v.ToString("F0", ci)
                                               : MathF.Abs(v) > 0f && MathF.Abs(v) < 0.001f ? v.ToString("0.#####", ci)
                                               : v.ToString(fmt, ci);
        var names = AcousticRegistry.KnownMaterials()
            .Where(n => n != "None")
            .Select(n => (Name: n, P: AcousticRegistry.GetProperties(n)))
            .OrderBy(x => FamilyOrder(x.P.Family)).ThenBy(x => x.P.DensityKgM3)
            .ToList();
        Console.WriteLine("| Material | Family | rho kg/m3 | E GPa (across) | Poisson | loss at 1 kHz (exp) | c_L m/s | hardness MPa | Ra mm | strength MPa | cp J/kgK | k W/mK | melts C | water % | mu |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var (name, p) in names)
        {
            string e = p.TransverseModulusGPa > 0f ? $"{F(p.YoungsModulusGPa)} ({F(p.TransverseModulusGPa)})" : F(p.YoungsModulusGPa);
            string loss = p.LossExponent > 0f ? $"{F(p.LossFactor)} ({F(p.LossExponent)})" : F(p.LossFactor);
            float cl = MathF.Sqrt(p.YoungsModulusGPa * 1e9f / MathF.Max(1f, p.DensityKgM3));
            string melts = p.MeltingPointC is { } m ? F(m, "F0") : "no";
            Console.WriteLine($"| {name} | {p.Family} | {F(p.DensityKgM3, "F0")} | {e} | {F(p.PoissonRatio)} | {loss} | {cl:F0} | {F(p.HardnessMPa)} | " +
                              $"{F(p.RoughnessMm)} | {F(p.StrengthMPa)} | {F(p.SpecificHeatJKgK, "F0")} | {F(p.ThermalConductivityWmK)} | {melts} | " +
                              $"{F(p.WaterUptakePercent)} | {F(p.VapourResistance)} |");
        }
        Console.WriteLine();
        Console.WriteLine($"{names.Count} materials.");
        return 0;
    }

    private static int FamilyOrder(string? family) => family switch
    {
        "metal" => 0, "stone" => 1, "glass" => 2, "wood" => 3, "polymer" => 4, "building" => 5,
        "ground" => 6, "soft" => 7, "liquid" => 8, _ => 9,
    };
}
