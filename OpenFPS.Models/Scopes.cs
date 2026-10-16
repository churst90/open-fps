namespace OpenFPS.Common;

/// <summary>
/// A rifle scope: the magnifications it turns through, how wide it sees at each, and its elevation
/// turret.
/// </summary>
public sealed record ScopeDefinition
{
    public required string Id { get; init; }
    /// <summary>What it is called aloud: "4-12 power scope".</summary>
    public required string Name { get; init; }
    /// <summary>The powers the zoom ring stops at, lowest first.</summary>
    public required float[] Magnifications { get; init; }

    /// <summary>
    /// The field of view at one power, degrees: divided by the magnification it gives the scope's view
    /// at each power. 24 makes a 4-12x what a real one is, about 6 degrees at 4x (26 feet at a hundred
    /// yards, the usual published figure), 3 at 8x and 2 at 12x.
    /// </summary>
    public float FieldOfViewAtOnePowerDegrees { get; init; } = 24f;

    /// <summary>One click of the elevation turret, milliradians: a tenth, the usual for a mil scope.</summary>
    public float ClickMil { get; init; } = 0.1f;
    /// <summary>The most the turret dials up, milliradians: 25, a common figure, past 1000 m for a .308.</summary>
    public float MaxElevationMil { get; init; } = 25f;
    /// <summary>The sight's centre above the bore, metres: 38 mm, the inch and a half tables assume.</summary>
    public float SightHeightMetres { get; init; } = 0.038f;
    /// <summary>The distance the rifle is zeroed at with the turret at nothing.</summary>
    public float BaseZeroMetres { get; init; } = 100f;

    /// <summary>The full field of view at a magnification, degrees.</summary>
    public float FieldOfViewDegrees(float magnification) => FieldOfViewAtOnePowerDegrees / MathF.Max(1f, magnification);
}

/// <summary>The scopes that exist.</summary>
public static class ScopeRegistry
{
    public static readonly ScopeDefinition Scope4To12 = new()
    {
        Id = "scope_4_12",
        Name = "4-12 power scope",
        Magnifications = new[] { 4f, 8f, 12f },
    };

    private static readonly Dictionary<string, ScopeDefinition> _byId = new(StringComparer.OrdinalIgnoreCase)
    {
        [Scope4To12.Id] = Scope4To12,
    };

    public static bool TryGet(string? id, out ScopeDefinition scope) => _byId.TryGetValue(id ?? "", out scope!);
}
