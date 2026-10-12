namespace OpenFPS.Common.Editing;

/// <summary>
/// Says what a property of a model is, for the world editor (docs/WORLD_EDITOR.md section 4): its
/// spoken name, its unit, the range that makes sense, how far one step moves it, one line of help, and
/// where the number comes from when it is physical. A property with this is editable; a property
/// without it is shown read only.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TunableAttribute : Attribute
{
    public TunableAttribute(string unit, double min, double max, string help)
    {
        Unit = unit;
        Min = min;
        Max = max;
        Help = help;
    }

    public string Unit { get; }
    public double Min { get; }
    public double Max { get; }
    public string Help { get; }
    /// <summary>What is said for it. Empty: made from the property's name.</summary>
    public string Label { get; init; } = "";
    /// <summary>How far one step up or down moves it. Zero: a hundredth of the range, rounded.</summary>
    public double Step { get; init; }
    /// <summary>Where a physical number comes from: a paper, a data sheet, a standard.</summary>
    public string Source { get; init; } = "";
    /// <summary>For a string field: the words it may be. "materials" means AcousticRegistry's names.</summary>
    public string Choices { get; init; } = "";
}
