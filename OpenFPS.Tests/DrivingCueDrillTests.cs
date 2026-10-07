using System.Numerics;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A scripted drive that measures overshoot with and without the brake cue.
///
/// A driver who hears only the cues approaches a right turn from Main Street onto Central Street,
/// indicator on. They steer for the guide beep (pure pursuit to the planned line, no harder than the
/// tyres allow after the braking), and they brake:
///
///   * without the brake cue, on the spoken "junction in N metres", after a second to react, at an
///     ordinary 0.2 g down to 25 km/h: somebody who knows a turn is coming but not how tight it is;
///   * with it, half a second after the cue says "brake" (band 2 or above), as hard as the cue's
///     braking asks, and lifting off while it says "lift".
///
/// Measured: the speed into the turn, the most of the tyres' grip the turn took, and how far the car
/// ran wide over Central Street's centre line into the oncoming lanes, or cut inside its line round the corner. This is a
/// model of a driver, not a driver: it says whether the cue carries what is needed to make the turn,
/// not how a person will use it.
/// </summary>
public class DrivingCueDrillTests
{
    private readonly ITestOutputHelper _o;
    public DrivingCueDrillTests(ITestOutputHelper o) => _o = o;

    /// <param name="RanWide">Metres over Central Street's centre line, into the oncoming lanes.</param>
    /// <param name="OverKerb">Metres inside the line it should have taken, round the corner: cutting it.</param>
    internal sealed record Result(float EntryKmh, float PeakGrip, float RanWide, float OverKerb, bool Stopped);

    internal static Result Drive(bool brakeCue, float startKmh, float wetFactor)
    {
        var city = DrivingCueTests.City();
        var planner = new DrivingCuePlanner(city) { Indicator = +1 };
        float grip = 0.95f * WheelDynamics.G * wetFactor;
        const float half = 2.1f, dt = 0.02f;
        // The line it should take: from the lane through the turn and on into Central Street.
        var ideal = new DrivingCuePlanner(city) { Indicator = +1 }
            .Update(new Vector3(4.5f, 0.05f, -27f), Vector3.UnitZ, 30f, grip, half).Path.ToList();
        // Main Street's northbound kerb lane, 100 m short of Central Street's line (past Dock Street).
        var p = new Vector3(4.5f, 0.05f, -7f - 100f);
        float heading = 0f, v = startKmh / 3.6f, yaw = 0f;
        float t = 0f, said = -1f, cueSince = -1f, entry = -1f, peak = 0f, wide = -99f, kerb = -99f;
        while (t < 60f)
        {
            var fwd = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading));
            var plan = planner.Update(p, fwd, v, grip, half, yawRate: yaw);
            // ── Braking ──
            float decel = 0f;
            if (!brakeCue)
            {
                // "Junction in N metres" is said as the junction comes within four seconds (DrivingAids).
                if (said < 0f && plan.Junction != null && plan.JunctionDistance <= MathF.Max(35f, v * 4f)) said = t;
                if (said >= 0f && t - said > 1f && v > 25f / 3.6f) decel = 0.2f * WheelDynamics.G;
            }
            else
            {
                int band = DrivingCueBands.Of(plan.BrakeRatio);
                if (band >= 2) { if (cueSince < 0f) cueSince = t; } else cueSince = -1f;
                if (band >= 2 && t - cueSince > 0.5f) decel = MathF.Min(grip * 0.9f, MathF.Max(1.5f, plan.NeededDecel * 1.15f));
                else if (band == 1) decel = 0.6f;
            }
            // ── Steering: for the guide, no harder than the tyres allow after the braking ──
            var aim = plan.Located && plan.Path.Count > 1 ? plan.GuidePoint - p : fwd * 10f;
            aim.Y = 0f;
            var to = Vector3.Normalize(aim);
            float alpha = MathF.Atan2(Vector3.Cross(fwd, to).Y, Vector3.Dot(fwd, to));
            float curvature = 2f * MathF.Sin(alpha) / MathF.Max(1f, aim.Length());
            float lateralLeft = MathF.Sqrt(MathF.Max(0f, grip * grip - decel * decel));
            float maxK = lateralLeft / MathF.Max(0.5f, v * v);
            curvature = Math.Clamp(curvature, -maxK, maxK);
            peak = MathF.Max(peak, MathF.Sqrt(MathF.Pow(v * v * curvature, 2) + decel * decel) / grip);
            // ── Moving ──
            v = MathF.Max(0f, v - decel * dt);
            // Positive alpha is to the right (Cross(f, to).Y > 0 for a target on the right), and the
            // heading grows turning right: (sin h, cos h) swings from north toward east.
            yaw = v * curvature;
            heading += yaw * dt;
            p += new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading)) * v * dt;
            t += dt;
            if (entry < 0f && p.Z >= -10.5f) entry = v * 3.6f;
            // Once round the corner onto Central Street (eastbound lanes z -6..0): over its centre line
            // is running wide into the oncoming traffic, over its kerb is clipping the corner.
            // The car's sides are 0.9 m either side of its middle.
            if (entry >= 0f && p.X > 7f) wide = MathF.Max(wide, p.Z + 0.9f);
            // Inside the line it should take, round the corner (until it is well into Central Street).
            if (entry >= 0f && p.X < 15f) kerb = MathF.Max(kerb, Inside(ideal, p));
            if (p.X > 40f) break;
            if (v < 0.3f) return new Result(entry, peak, MathF.Max(0f, wide), MathF.Max(0f, kerb), true);
        }
        return new Result(entry, peak, MathF.Max(0f, wide), MathF.Max(0f, kerb), false);
    }

    /// <summary>How far right of the line a point is (inside a right turn), metres; nought on or outside it.</summary>
    private static float Inside(List<Vector3> line, Vector3 p)
    {
        float best = float.MaxValue, signed = 0f;
        for (int i = 1; i < line.Count; i++)
        {
            Vector3 a = line[i - 1], b = line[i];
            var ab = new Vector3(b.X - a.X, 0f, b.Z - a.Z);
            float len = ab.Length();
            if (len < 1e-4f) continue;
            float f = Math.Clamp(Vector3.Dot(new Vector3(p.X - a.X, 0f, p.Z - a.Z), ab) / (len * len), 0f, 1f);
            var d = new Vector3(p.X, 0f, p.Z) - (new Vector3(a.X, 0f, a.Z) + ab * f);
            if (d.Length() < best) { best = d.Length(); signed = Vector3.Cross(ab / len, d).Y; }
        }
        return MathF.Max(0f, signed);
    }

    [Fact]
    public void TheBrakeCueBringsTheCarIntoTheTurnAtASpeedItCanHold()
    {
        _o.WriteLine("approach   grip   | speech only: entry  grip  over centre line, cut | brake cue: entry  grip  over centre line, cut");
        var rows = new List<(float Kmh, float Wet, Result Speech, Result Cue)>();
        foreach (float kmh in new[] { 40f, 50f, 65f })
            foreach (float wet in new[] { 1f, 0.64f })
            {
                var speech = Drive(brakeCue: false, kmh, wet);
                var cue = Drive(brakeCue: true, kmh, wet);
                rows.Add((kmh, wet, speech, cue));
                _o.WriteLine($"{kmh,4:F0} km/h  x{wet:F2} | {speech.EntryKmh,8:F0} km/h {speech.PeakGrip,4:P0} {speech.RanWide,5:F1} m {speech.OverKerb,4:F1} m | "
                           + $"{cue.EntryKmh,6:F0} km/h {cue.PeakGrip,4:P0} {cue.RanWide,5:F1} m {cue.OverKerb,4:F1} m{(cue.Stopped ? " (stopped short)" : "")}");
            }
        foreach (var r in rows)
        {
            Assert.False(r.Cue.Stopped, $"at {r.Kmh} km/h the cue stopped the car short of the turn");
            Assert.True(r.Cue.EntryKmh > 0f);
            // With the cue: into the turn with grip to spare, and not run wide of the lane.
            Assert.True(r.Cue.PeakGrip < 0.9f, $"at {r.Kmh} km/h, grip x{r.Wet}: the cue's driver used {r.Cue.PeakGrip:P0} of the grip");
            // A wheel touching the line is not running wide: half a metre either way.
            Assert.True(r.Cue.RanWide < 0.5f, $"at {r.Kmh} km/h, grip x{r.Wet}: the cue's driver ran {r.Cue.RanWide:F1} m over the centre line");
            Assert.True(r.Cue.OverKerb < 0.6f, $"at {r.Kmh} km/h, grip x{r.Wet}: the cue's driver cut {r.Cue.OverKerb:F1} m inside its line");
        }
    }
}
