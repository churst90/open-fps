using System.Numerics;
using System.Text;
using OpenFPS.Common;

namespace OpenFPS.Instruments;

/// <summary>The ricochet table: every weapon's round off every face that skips one, and the slug's flight after.</summary>
public static partial class PhysicsReplays
{
    public const string RicochetHeader = "weapon\tface\tof_critical\tseed\tbounced\tvx\tvy\tvz\tmass_mg\tlength_mm\twidth_mm\tthick_mm\t"
        + "tumble_rad_s\tdumped_j\tgraze_mrad\tkept\tx_m\ty_m\tz_m\tspeed_mps\ttravelled_m";

    /// <summary>The grazing angles each round strikes at, as shares of the face's critical angle: well under it,
    /// in the band the dice decide, and at it.</summary>
    private static readonly float[] OfCritical = { 0.3f, 0.9f, 1.0f };

    /// <summary>How long the slug is flown after the bounce, seconds: long enough to slow from the face's speed
    /// to tens of metres a second and to fall.</summary>
    public const float RicochetFlightSeconds = 0.5f;

    /// <summary>
    /// Each weapon's round at its muzzle speed onto each face the material table gives a ricochet (one material
    /// for each, sorted by name), at three shares of its critical angle and with three dice: whether it skips, how it leaves, what
    /// it is afterwards, and where its tumbling flight has taken it half a second later through a standard day
    /// with a 2 m/s wind. The face is tilted and the round comes in obliquely, so every axis is used.
    /// </summary>
    public static string RicochetTable()
    {
        var sb = new StringBuilder(RicochetHeader).Append('\n');
        // One material for each face the table holds (the metals share steel's, the stones concrete's): the first by name.
        var faces = AcousticRegistry.KnownMaterials().OrderBy(m => m, StringComparer.Ordinal)
            .Select(m => (Name: m, Has: Ricochet.TryFace(m, out var f), Face: f)).Where(m => m.Has)
            .GroupBy(m => m.Face).Select(g => g.First().Name).OrderBy(m => m, StringComparer.Ordinal).ToList();
        var tilt = Quaternion.CreateFromYawPitchRoll(0.3f, 0.12f, -0.07f);
        Vector3 normal = Vector3.Transform(Vector3.UnitY, tilt);
        var wind = new Vector3(2f, 0f, -1f);
        foreach (var w in WeaponRegistry.All.OrderBy(w => w.Id, StringComparer.Ordinal))
        {
            var slug = Slug.Of(w);
            foreach (string face in faces)
            {
                float critical = Ricochet.CriticalRadians(face, slug);
                foreach (float share in OfCritical)
                    for (int seed = 1; seed <= 3; seed++)
                    {
                        float graze = share * critical;
                        // In along the face at 30 degrees off its x axis, down into it by the grazing angle.
                        var local = new Vector3(MathF.Cos(graze) * MathF.Cos(0.52f), -MathF.Sin(graze), MathF.Cos(graze) * MathF.Sin(0.52f));
                        Vector3 velocity = Vector3.Transform(local, tilt) * w.MuzzleVelocity;
                        sb.Append(w.Id).Append('\t').Append(face).Append('\t').Append(F(share, 2)).Append('\t').Append(seed).Append('\t');
                        if (!Ricochet.TryBounce(face, velocity, normal, slug, new Random(seed), out var o))
                        {
                            sb.Append("0\n");
                            continue;
                        }
                        var s = new BulletState { Velocity = o.Velocity };
                        Ricochet.Advance(ref s, RicochetFlightSeconds, o.Slug, Air.Standard, wind);
                        sb.Append("1\t").Append(F(o.Velocity.X, 4)).Append('\t').Append(F(o.Velocity.Y, 4)).Append('\t').Append(F(o.Velocity.Z, 4)).Append('\t')
                          .Append(F(o.Slug.MassKg * 1e6f, 3)).Append('\t').Append(F(o.Slug.Length * 1e3f, 5)).Append('\t')
                          .Append(F(o.Slug.Width * 1e3f, 5)).Append('\t').Append(F(o.Slug.Thickness * 1e3f, 5)).Append('\t')
                          .Append(F(o.Slug.TumbleRadPerSec, 2)).Append('\t').Append(F(o.DumpedJoules, 3)).Append('\t')
                          .Append(F(o.GrazeRadians * 1000f, 4)).Append('\t').Append(F(o.Kept, 6)).Append('\t')
                          .Append(F(s.Position.X, 5)).Append('\t').Append(F(s.Position.Y, 5)).Append('\t').Append(F(s.Position.Z, 5)).Append('\t')
                          .Append(F(s.Velocity.Length(), 4)).Append('\t').Append(F(s.Travelled, 5)).Append('\n');
                    }
            }
        }
        return sb.ToString();
    }
}
