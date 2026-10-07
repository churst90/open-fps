namespace OpenFPS.Common;

/// <summary>
/// How a wall is built, beyond what it is made of: two leaves of the material with a cavity between, or
/// (the default) one solid panel the full thickness of the box.
/// </summary>
/// <param name="LeafMetres">Thickness of each of the two leaves, metres; 0 for a solid panel. A box too
/// thin to hold two leaves and <c>WallTransmission.MinCavityMetres</c> (in the acoustics) of cavity is one solid
/// sheet of its material.</param>
/// <param name="StudSpacingMetres">Centres of the studs both leaves are fixed to, metres; 0 when the
/// leaves meet only at the edges of the panel (a door's skins at its frame, a glazed unit's spacer).</param>
public readonly record struct WallBuild(float LeafMetres, float StudSpacingMetres)
{
    public static readonly WallBuild Solid = default;
}
