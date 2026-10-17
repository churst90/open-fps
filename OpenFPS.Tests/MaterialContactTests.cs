using OpenFPS.Materials;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Friction and restitution in the material table (docs/MATTER.md 2.4): each the material against itself, each from a
/// source named on its line in AcousticRegistry.AddContact, and none where no source was found. Nothing reads them yet.
/// </summary>
public class MaterialContactTests
{
    private readonly ITestOutputHelper _o;
    public MaterialContactTests(ITestOutputHelper o) => _o = o;

    /// <summary>Every filled cell, as docs/MATTER.md lists it: a figure added, changed or left out changes the doc too.</summary>
    private static readonly Dictionary<string, (float? Static, float? Kinetic, float? Restitution)> Filled = new()
    {
        ["Metal"] = (0.74f, 0.57f, null), ["Fence"] = (0.74f, 0.57f, null), ["Aluminium"] = (1.2f, 1.4f, null),
        ["CastIron"] = (1.1f, 0.15f, null), ["Copper"] = (1.0f, null, null), ["Lead"] = (0.90f, null, null),
        ["Titanium"] = (0.36f, 0.30f, null), ["Glass"] = (0.95f, 0.4f, 0.93f), ["LaminatedGlass"] = (0.95f, 0.4f, null),
        ["Concrete"] = (null, 0.65f, null), ["Brick"] = (null, 0.68f, null), ["Granite"] = (null, 0.6f, null),
        ["Wood"] = (0.38f, null, null), ["Oak"] = (0.62f, 0.48f, null), ["Acrylic"] = (0.8f, null, null),
        ["Nylon"] = (0.2f, null, null), ["Rubber"] = (1.16f, null, null), ["Dirt"] = (0.58f, null, null),
        ["Sand"] = (0.57f, null, null), ["Clay"] = (0.42f, null, null), ["Gravel"] = (0.70f, null, null),
        ["Ice"] = (0.3f, 0.035f, null),
    };

    [Fact]
    public void EachRowIsWhatTheDocSays()
    {
        AcousticRegistry.Initialize();
        var wrong = new List<string>();
        foreach (var name in AcousticRegistry.KnownMaterials())
        {
            var p = AcousticRegistry.GetProperties(name);
            var have = (p.StaticFriction, p.KineticFriction, p.Restitution);
            _o.WriteLine($"{name,-15} static {p.StaticFriction?.ToString() ?? "-",-5} kinetic {p.KineticFriction?.ToString() ?? "-",-6} restitution {p.Restitution?.ToString() ?? "-"}");
            var want = Filled.TryGetValue(name, out var w) ? w : (null, null, null);
            if (have != want) wrong.Add($"{name}: {have}, the doc says {want}");
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>What a coefficient can be: friction above zero and under the few that clean metal reaches, restitution in
    /// (0, 1]; and a slide never grips harder than a start, but for clean aluminium, which galls (RM).</summary>
    [Fact]
    public void TheFiguresArePhysical()
    {
        AcousticRegistry.Initialize();
        foreach (var name in AcousticRegistry.KnownMaterials())
        {
            var p = AcousticRegistry.GetProperties(name);
            if (p.StaticFriction is { } s) Assert.InRange(s, 0.01f, 2f);
            if (p.KineticFriction is { } k) Assert.InRange(k, 0.01f, 2f);
            if (p.Restitution is { } e) Assert.InRange(e, 0.01f, 1f);
            if (p is { StaticFriction: { } s2, KineticFriction: { } k2 } && name != "Aluminium")
                Assert.True(k2 <= s2, $"{name} slides at {k2}, above its start at {s2}");
        }
        Assert.Null(AcousticRegistry.GetProperties("Water").StaticFriction);   // a liquid has none
    }
}
