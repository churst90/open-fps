using System.Globalization;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core;

/// <summary>
/// One typed value the world editor asks for: what the text box is called, what it holds when it opens,
/// what is said about it, and the check that turns what was typed into the /edit command to send. Both
/// heads show it as a labelled text box (docs/WORLD_EDITOR.md section 14); this is the part they share.
/// </summary>
public sealed class EditorValuePrompt
{
    /// <summary>The most numbers one box takes (east, north and up).</summary>
    private const int MaxCount = 8;

    private readonly FieldDescriptor _field;
    private readonly string _command;
    private readonly int _count;

    /// <summary>The dialog's title: "Hum level".</summary>
    public string Title { get; }
    /// <summary>The text box's label: "Hum level, in dB".</summary>
    public string Label { get; }
    /// <summary>The value now, its range and the help: the text box's description.</summary>
    public string Description { get; }
    /// <summary>In the box when it opens, all selected, so typing replaces it.</summary>
    public string Initial { get; }
    /// <summary>The start of the command the value finishes: "/edit set Volume ".</summary>
    public string Command => _command;

    public EditorValuePrompt(EditorMenuItem item)
    {
        string prompt = item.Prompt.Length > 0 ? item.Prompt : item.Label;
        bool number = item.ValueType is FieldType.Number or FieldType.Integer;
        _field = new FieldDescriptor
        {
            Path = "",
            Label = prompt,
            Type = number ? item.ValueType : FieldType.Text,
            Unit = item.Unit ?? "",
            Min = item.Min,
            Max = item.Max,
        };
        _command = item.Command ?? "";
        _count = Math.Clamp((int)item.Count, 1, MaxCount);
        Title = Capital(prompt);
        Label = Capital(prompt) + (_field.Unit.Length > 0 ? $", in {_field.Unit}" : "");
        Initial = item.Value ?? "";

        var parts = new List<string>();
        if (Initial.Length > 0) parts.Add($"Now {(_count == 1 ? _field.Say(Initial) : Initial)}.");
        if (number && _field.RangeText.Length > 0) parts.Add(_count == 1 ? $"From {_field.RangeText}." : $"Each from {_field.RangeText}.");
        if (!string.IsNullOrWhiteSpace(item.Help)) parts.Add(item.Help.Trim());
        Description = string.Join(" ", parts);
    }

    /// <summary>What the game says as the box opens: its label, what it holds, the range, and the keys.</summary>
    public string Spoken
        => $"{Label}: {(Initial.Length > 0 ? Initial : "empty")}. "
         + (_field.RangeText.Length > 0 && _field.Type != FieldType.Text ? $"From {_field.RangeText}. " : "")
         + "Type, then Enter. Escape cancels.";

    /// <summary>
    /// Reads what was typed: the command to send, or why not, in words to say. The same check the server
    /// makes for a field (FieldDescriptor.TryParse), so a value out of range is refused while the box is
    /// still open rather than after it has gone.
    /// </summary>
    public bool TryCommand(string typed, out string command, out string error)
    {
        command = "";
        error = "";
        string text = (typed ?? "").Trim();
        if (text.Length == 0) { error = $"{Title} is empty. Type a value, or Escape to cancel."; return false; }
        if (text.Any(char.IsControl)) { error = $"{Title} cannot hold control characters."; return false; }

        string value;
        if (_field.Type == FieldType.Text) value = text;
        else
        {
            var words = Words(text, _count);
            if (words.Length != _count)
            {
                error = _count == 1 ? $"{Title} is one number." : $"{Title} is {_count} numbers, apart by spaces, such as {string.Join(" ", Enumerable.Repeat("0", _count - 1).Prepend("1"))}.";
                return false;
            }
            var read = new string[words.Length];
            for (int i = 0; i < words.Length; i++)
                if (!_field.TryParse(Decimal(words[i]), out read[i], out error)) return false;
            value = string.Join(" ", read);
        }
        command = _command + value;
        return true;
    }

    /// <summary>True when what is typed is the value the box opened with, so there is nothing to send.</summary>
    public bool IsUnchanged(string typed) => Initial.Length > 0 && (typed ?? "").Trim() == Initial.Trim();

    /// <summary>The numbers typed: apart by spaces ("1 0 0", "1, 0, 0"), or by commas alone when several
    /// are asked for ("1,0,0"); one number may have a decimal comma.</summary>
    private static string[] Words(string text, int count)
    {
        if (text.Any(char.IsWhiteSpace))
            return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => w.TrimEnd(',')).Where(w => w.Length > 0).ToArray();
        return count > 1 ? text.Split(',', StringSplitOptions.RemoveEmptyEntries) : new[] { text };
    }

    /// <summary>A decimal comma is read as a point: "0,5" is a half, as it is typed in much of Europe.</summary>
    private static string Decimal(string word)
        => word.Count(c => c == ',') == 1 && !word.Contains('.') ? word.Replace(',', '.') : word;

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];
}
