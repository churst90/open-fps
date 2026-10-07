using System.Numerics;

namespace OpenFPS.Common.Geometry;

/// <summary>Where one wheel meets the ground: the height under it, the face's normal and its surface.</summary>
public readonly record struct WheelContact(bool Found, float Height, Vector3 Normal, string Material);

/// <summary>
/// The ground under a vehicle's wheels (docs/GEOMETRY.md 3.11): a ray down at each wheel, each with its own
/// height, normal and surface, so a car pitches and rolls on a slope, one wheel drops off a kerb, and gravel
/// under one side is gravel under that side. Plain values in and out: where the wheels touch down (world,
/// metres, Y up) and how far above a wheel the ground may be and still be climbed onto.
/// </summary>
public static class WheelRays
{
    /// <summary>The ground under each of <paramref name="wheels"/>, no higher than <paramref name="climb"/>
    /// over it, into <paramref name="into"/> (as many as there are wheels). Walkable faces only, as a body's
    /// ground: a face steeper than one can walk is a wall to a wheel too.</summary>
    public static void Contacts<F>(TriangleWorld world, ref F filter, ReadOnlySpan<Vector3> wheels, float climb, Span<WheelContact> into)
        where F : IGeometryFilter
    {
        for (int i = 0; i < wheels.Length; i++)
        {
            var w = wheels[i];
            float y = world.FloorAt(w.X, w.Z, w.Y + climb, GeometryLayers.Ground, ref filter, out var hit);
            into[i] = y > -1000f ? new WheelContact(true, y, hit.Normal, world.SurfaceOf(hit).Material) : new WheelContact(false, 0f, Vector3.UnitY, "");
        }
    }

    /// <summary>
    /// How a body resting on its wheels sits: the height under its middle (the mean of the wheels'), and its
    /// pitch and roll in radians from the wheels' heights. <paramref name="along"/> and <paramref name="across"/>
    /// are each wheel's place forward of and to the right of the body's middle, metres. Pitch is positive
    /// nose up, roll positive right side up. Wheels with no ground under them are left out.
    /// </summary>
    public static (float Height, float Pitch, float Roll) Rest(ReadOnlySpan<WheelContact> contacts, ReadOnlySpan<float> along, ReadOnlySpan<float> across)
    {
        float sum = 0f; int n = 0;
        float frontSum = 0f, rearSum = 0f, frontAt = 0f, rearAt = 0f; int front = 0, rear = 0;
        float rightSum = 0f, leftSum = 0f, rightAt = 0f, leftAt = 0f; int right = 0, left = 0;
        for (int i = 0; i < contacts.Length; i++)
        {
            if (!contacts[i].Found) continue;
            float h = contacts[i].Height;
            sum += h; n++;
            if (along[i] >= 0f) { frontSum += h; frontAt += along[i]; front++; } else { rearSum += h; rearAt += along[i]; rear++; }
            if (across[i] >= 0f) { rightSum += h; rightAt += across[i]; right++; } else { leftSum += h; leftAt += across[i]; left++; }
        }
        if (n == 0) return (float.NaN, 0f, 0f);
        float pitch = 0f, roll = 0f;
        if (front > 0 && rear > 0)
        {
            float run = frontAt / front - rearAt / rear;
            if (run > 1e-3f) pitch = MathF.Atan((frontSum / front - rearSum / rear) / run);
        }
        if (right > 0 && left > 0)
        {
            float run = rightAt / right - leftAt / left;
            if (run > 1e-3f) roll = MathF.Atan((rightSum / right - leftSum / left) / run);
        }
        return (sum / n, pitch, roll);
    }
}
