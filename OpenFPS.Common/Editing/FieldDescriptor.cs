using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OpenFPS.Common.Editing;

/// <summary>What kind of value a field holds, which decides how it is typed in and checked.</summary>
public enum FieldType : byte
{
    Number,
    Integer,
    Bool,
    Choice,
    Text,
}

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

/// <summary>
/// One field the editor can show and change: where it is, what it is called, and what values it may
/// take. Built from <see cref="TunableAttribute"/> for a model, and by hand for a placed thing's own
/// settings (EntitySettings on the server). Everything the editor says about a field comes from here.
/// </summary>
public sealed record FieldDescriptor
{
    /// <summary>Where the value is: "Compressor.HumDb", "Falls[0].HeightMetres", "Volume".</summary>
    public required string Path { get; init; }
    public required string Label { get; init; }
    public FieldType Type { get; init; } = FieldType.Number;
    public string Unit { get; init; } = "";
    public double Min { get; init; } = double.MinValue;
    public double Max { get; init; } = double.MaxValue;
    public double Step { get; init; }
    public string Help { get; init; } = "";
    public string Source { get; init; } = "";
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();
    public bool ReadOnly { get; init; }

    /// <summary>The longest text a Text field takes.</summary>
    public const int MaxTextLength = 60;

    /// <summary>The label at the start of a sentence.</summary>
    private string Cap => Label.Length == 0 ? Label : char.ToUpperInvariant(Label[0]) + Label[1..];

    /// <summary>The step to use: the declared one, or about a hundredth of the range.</summary>
    public double EffectiveStep
    {
        get
        {
            if (Step > 0) return Step;
            if (Type == FieldType.Integer) return 1;
            if (Min == double.MinValue || Max == double.MaxValue || Max <= Min) return 0.1;
            double raw = (Max - Min) / 100.0;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            return mag;
        }
    }

    /// <summary>"30 to 90 dB": the range as said.</summary>
    public string RangeText
        => Type is FieldType.Number or FieldType.Integer && Min != double.MinValue && Max != double.MaxValue
            ? $"{Format(Min)} to {Format(Max)}{(Unit.Length > 0 ? " " + Unit : "")}"
            : Type == FieldType.Choice ? string.Join(", ", Choices)
            : Type == FieldType.Bool ? "on or off" : "";

    /// <summary>A value as said: "63 dB", "on", "Metal".</summary>
    public string Say(string value)
    {
        if (Type == FieldType.Bool) return value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "on" : "off";
        if (Type is FieldType.Number or FieldType.Integer
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            // NaN is how a model says "worked out from the rest" (an engine's revolutions before firing).
            return double.IsNaN(d) ? "worked out from the rest" : Format(d) + (Unit.Length > 0 ? " " + Unit : "");
        return value.Length == 0 ? "nothing" : value;
    }

    /// <summary>A number the way a person says it: no trailing zeros, at most four significant places.</summary>
    public static string Format(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
        double a = Math.Abs(d);
        string s = a >= 1000 ? d.ToString("0.#", CultureInfo.InvariantCulture)
                 : a >= 1 ? d.ToString("0.###", CultureInfo.InvariantCulture)
                 : d.ToString("0.####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    /// <summary>
    /// Reads what somebody typed as a value of this field, in its stored form ("66", "true", "Metal"),
    /// checked against the type and the range; a refusal says why and what the range is.
    /// </summary>
    public bool TryParse(string typed, out string value, out string error)
    {
        value = "";
        error = "";
        string text = (typed ?? "").Trim();
        if (ReadOnly) { error = $"{Cap} cannot be changed here."; return false; }
        switch (Type)
        {
            case FieldType.Number:
            case FieldType.Integer:
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d))
                { error = $"{Cap} needs a number{(RangeText.Length > 0 ? ", " + RangeText : "")}."; return false; }
                if (Type == FieldType.Integer && Math.Abs(d - Math.Round(d)) > 1e-9)
                { error = $"{Cap} needs a whole number{(RangeText.Length > 0 ? ", " + RangeText : "")}."; return false; }
                if (d < Min - 1e-9 || d > Max + 1e-9)
                { error = $"{Cap} must be {RangeText}; {Format(d)} is outside it."; return false; }
                value = Type == FieldType.Integer ? ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture)
                                                  : d.ToString("R", CultureInfo.InvariantCulture);
                return true;
            }
            case FieldType.Bool:
                switch (text.ToLowerInvariant())
                {
                    case "on": case "yes": case "true": case "1": value = "true"; return true;
                    case "off": case "no": case "false": case "0": value = "false"; return true;
                }
                error = $"{Cap} is on or off.";
                return false;
            case FieldType.Choice:
            {
                var exact = Choices.FirstOrDefault(c => c.Equals(text, StringComparison.OrdinalIgnoreCase));
                if (exact != null) { value = exact; return true; }
                var starts = Choices.Where(c => c.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();
                if (text.Length > 0 && starts.Count == 1) { value = starts[0]; return true; }
                error = $"{Cap} is one of: {string.Join(", ", Choices)}.";
                return false;
            }
            default:
                if (text.Length == 0) { error = $"{Cap} needs some words."; return false; }
                if (text.Length > MaxTextLength) { error = $"{Cap} is at most {MaxTextLength} letters."; return false; }
                if (text.Any(char.IsControl)) { error = $"{Cap} cannot hold control characters."; return false; }
                value = text;
                return true;
        }
    }

    /// <summary>One step up or down from a stored value, kept inside the range. Null if not a number field.</summary>
    public string? Stepped(string current, int direction)
    {
        if (Type is not (FieldType.Number or FieldType.Integer)) return null;
        if (!double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return null;
        double step = EffectiveStep;
        double next = Math.Round((d + direction * step) / step) * step;
        next = Math.Clamp(next, Min, Max);
        return Type == FieldType.Integer ? ((long)Math.Round(next)).ToString(CultureInfo.InvariantCulture)
                                         : Math.Round(next, 6).ToString("R", CultureInfo.InvariantCulture);
    }

    // ── Names ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Unit words a property name may end in, and the unit each is said as.</summary>
    private static readonly (string Suffix, string Unit)[] UnitSuffixes =
    {
        ("KgM2", "kg m²"), ("Mps2", "m/s²"), ("Kmh", "km/h"), ("Mps", "m/s"), ("Metres", "m"), ("Meters", "m"),
        ("Mm", "mm"), ("Db", "dB"), ("Hz", "Hz"), ("Seconds", "s"), ("Rpm", "rpm"), ("Kg", "kg"),
        ("Degrees", "degrees"), ("Milligrams", "mg"), ("Celsius", "°C"),
    };

    /// <summary>
    /// "HumDb" is "hum" in "dB"; "ShellQ" is "shell q"; "StalksPerSquareMetre" is "stalks per square
    /// metre". How a property with no declared label is said, and the unit its name carries.
    /// </summary>
    public static (string Label, string Unit) Describe(string propertyName)
    {
        string unit = "";
        string name = propertyName;
        foreach (var (suffix, u) in UnitSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                unit = u;
                name = name[..^suffix.Length];
                break;
            }
        }
        return (Words(name), unit);
    }

    /// <summary>PascalCase as lower-case words: "BladeGearRatio" is "blade gear ratio".</summary>
    public static string Words(string pascal)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pascal.Length; i++)
        {
            char c = pascal[i];
            bool boundary = i > 0 && char.IsUpper(c)
                            && (char.IsLower(pascal[i - 1]) || (i + 1 < pascal.Length && char.IsLower(pascal[i + 1])));
            if (boundary || (i > 0 && char.IsDigit(c) && !char.IsDigit(pascal[i - 1]))) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
