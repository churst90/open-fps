using System.Numerics;
using System.Text;
using OpenFPS.Common;

namespace OpenFPS.Instruments;

/// <summary>The ballistics table: ExternalBallistics.Fly for every weapon at stated ranges and angles.</summary>
public static partial class PhysicsReplays
{
    public const string BallisticsHeader = "weapon\tcase\tangle_mrad\trange_m\theight_m\tdrift_m\tseconds\tspeed_mps";

    /// <summary>The days and angles each weapon is flown at: zeroed at 100 m on a standard day; level; raised
    /// 5 and 30 degrees; zeroed in a 5 m/s crosswind; zeroed on a hot day at altitude (35 C, 850 mb).</summary>
    private static IEnumerable<(string Case, float Angle, Air Air, Vector3 Wind)> Cases(WeaponDefinition w)
    {
        const float sight = 0.04f;
        float zero = ExternalBallistics.ZeroAngle(w, 100f, sight);
        yield return ("zero100", zero, Air.Standard, Vector3.Zero);
        yield return ("level", 0f, Air.Standard, Vector3.Zero);
        yield return ("up5deg", 5f * MathF.PI / 180f, Air.Standard, Vector3.Zero);
        yield return ("up30deg", 30f * MathF.PI / 180f, Air.Standard, Vector3.Zero);
        yield return ("crosswind5", zero, Air.Standard, new Vector3(5f, 0f, 0f));
        yield return ("hot_high", zero, new Air(35f, 850f), Vector3.Zero);
    }

    /// <summary>Ranges per weapon: every 50 m to 300 m for a pistol or shotgun round, every 100 m to 1,000 m
    /// for a rifle's.</summary>
    private static IEnumerable<float> Ranges(WeaponDefinition w)
    {
        bool rifle = w.MuzzleVelocity > 600f;
        float step = rifle ? 100f : 50f, last = rifle ? 1000f : 300f;
        for (float r = step; r <= last + 0.1f; r += step) yield return r;
    }

    public static string BallisticsTable()
    {
        var sb = new StringBuilder(BallisticsHeader).Append('\n');
        foreach (var w in WeaponRegistry.All.OrderBy(w => w.Id, StringComparer.Ordinal))
            foreach (var (name, angle, air, wind) in Cases(w))
                foreach (float range in Ranges(w))
                {
                    var p = ExternalBallistics.Fly(w, range, angle, 0.04f, air, wind);
                    sb.Append(w.Id).Append('\t').Append(name).Append('\t').Append(F(angle * 1000f, 4)).Append('\t').Append(F(p.Range, 1)).Append('\t')
                      .Append(F(p.Height, 4)).Append('\t').Append(F(p.Drift, 4)).Append('\t').Append(F(p.Seconds, 5)).Append('\t')
                      .Append(F(p.Speed, 2)).Append('\n');
                }
        return sb.ToString();
    }
}
