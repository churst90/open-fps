namespace OpenFPS.Common;

/// <summary>
/// How a wall is built, beyond what it is made of: two leaves of the material with a cavity between, a
/// wall of several stuffs (an assembly), or (the default) one solid panel the full thickness of the box.
/// </summary>
/// <param name="LeafMetres">Thickness of each of the two leaves, metres; 0 for a solid panel. A box too
/// thin to hold two leaves and <c>WallTransmission.MinCavityMetres</c> (in the acoustics) of cavity is one solid
/// sheet of its material.</param>
/// <param name="StudSpacingMetres">Centres of the studs both leaves are fixed to, metres; 0 when the
/// leaves meet only at the edges of the panel (a door's skins at its frame, a glazed unit's spacer).</param>
/// <param name="Assembly">A wall of several stuffs, by its number in the acoustics' table (<c>Assemblies</c>):
/// its plies across the box's thickness, from the face its thickness axis points away from to the one it
/// points to; 0 for none. The box's own material is its face.</param>
public readonly record struct WallBuild(float LeafMetres, float StudSpacingMetres, int Assembly = 0)
{
    public static readonly WallBuild Solid = default;
}
