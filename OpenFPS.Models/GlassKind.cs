using System.Numerics;
using OpenFPS.Sound;

namespace OpenFPS.Common;

/// <summary>
/// A glazed part as the map has it: the prefab it was made from and the glazing that prefab declares, and, when it
/// is part of something bigger, that thing's template ("vehicle:i4_economy") with where the part sits in it and how
/// big it is in the thing's own frame (x across, y up, z forward).
/// </summary>
public readonly record struct GlazedPart(string PrefabId, GlassType? Glazing, string OwnerTemplate, Vector3 LocalPosition, Vector3 Size);

/// <summary>
/// What kind of glass a glazed part is. A car's is chosen by where it is, as cars choose it: across the car and
/// forward of the cabin's middle is the windscreen, laminated, and its side and rear windows are toughened (UN ECE
/// R43; FMVSS 205). Anything else is the glazing its prefab declares (PrefabTemplate.Glazing): safety glazing in
/// doors, beside doors, shop fronts and other glass people can walk into (US CPSC 16 CFR 1201; England, Approved
/// Document K), toughened in practice, and a home's ordinary window annealed float glass. A part that declares none
/// is an error, said once by prefab, and breaks as tempered.
/// </summary>
public static class GlassKind
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _undeclared = new(StringComparer.OrdinalIgnoreCase);

    public static GlassType Of(GlazedPart p)
    {
        if (!string.IsNullOrEmpty(p.OwnerTemplate) && p.OwnerTemplate.StartsWith("vehicle:", StringComparison.OrdinalIgnoreCase))
        {
            bool across = p.Size.Z < p.Size.X && p.Size.Z < p.Size.Y;
            return across && p.LocalPosition.Z > CabinMiddle(p.OwnerTemplate) ? GlassType.Laminated : GlassType.Tempered;
        }
        if (p.Glazing is { } declared) return declared;
        if (_undeclared.TryAdd(p.PrefabId ?? "", true))
            Serilog.Log.Error("[GLASS] '{Prefab}' is glass and declares no Glazing; it breaks as tempered", p.PrefabId);
        return GlassType.Tempered;
    }

    /// <summary>Every prefab a pane was asked about that declared no glazing.</summary>
    public static IReadOnlyCollection<string> Undeclared => (IReadOnlyCollection<string>)_undeclared.Keys;

    /// <summary>The middle of a vehicle's cabin, front to back, in its own frame: forward of it is the
    /// windscreen, behind it the back window. Zero when the template is not a known vehicle.</summary>
    private static float CabinMiddle(string template)
    {
        string preset = template.Substring("vehicle:".Length);
        if (!MachineRegistry.Knows(preset)) return 0f;
        return VehicleCabin.Measure(MachineRegistry.VehicleFor(preset)) is { } g ? g.Cz : 0f;
    }
}
