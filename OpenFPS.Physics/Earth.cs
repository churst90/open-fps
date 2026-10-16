namespace OpenFPS.Common;

/// <summary>The world as it is unless a map says otherwise.</summary>
public static class Earth
{
    /// <summary>
    /// Standard gravity, m/s² (the CGPM's, 1901): the one number for how fast things fall, roll and fly
    /// (docs/PHYSICS_BOUNDARY.md, decision 5). A map's own gravity starts from it; the sounds' own
    /// mechanisms (a bolt dropping, a shard's flight, a jet of water) read it as it is. Cody, 2026-10-04:
    /// "shouldn't you fall at the speed of gravity on earth?": the eighteen metres off the Brandt Court
    /// roof take 1.92 s, against 1.55 s at the 15 once chosen for how a jump felt. It was written 44 times,
    /// 40 of them as 9.81.
    /// </summary>
    public const float StandardGravity = 9.80665f;
}
