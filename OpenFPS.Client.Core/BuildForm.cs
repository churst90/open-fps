using System.Globalization;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core;

/// <summary>
/// What can be built and how, as the server describes it in its "build.form" editor menu
/// (docs/WORLD_EDITOR.md section 15): the kinds, each kind's fields in the order shown, and the choices
/// of each choice field. The client knows no material, door or prefab of its own.
/// </summary>
public sealed class BuildCatalog
{
    public const string FormPath = "build.form";

    /// <summary>One field of a kind: the word it is sent as, what it is called, and what it may hold.</summary>
    public sealed record Field(string Word, FieldDescriptor Descriptor, string Default);

    /// <summary>One choice: what is said, what is sent, the category it is listed under (a prefab's), the
    /// other fields it sets when chosen (a material's own thickness, a prefab's own size), and whether a
    /// prefab can be sized.</summary>
    public sealed record Option(string Label, string Value, string Category, IReadOnlyDictionary<string, string> Sets, bool Sized);

    private readonly List<(string Word, string Label)> _kinds = new();
    private readonly Dictionary<string, List<Field>> _fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Option>> _options = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<(string Word, string Label)> Kinds => _kinds;

    public IReadOnlyList<Field> FieldsOf(string kind)
        => _fields.TryGetValue(kind, out var f) ? f : Array.Empty<Field>();

    public IReadOnlyList<Option> OptionsOf(string kind, string word)
        => _options.TryGetValue($"{kind}.{word}", out var o) ? o : Array.Empty<Option>();

    /// <summary>Reads the server's form: kinds are Action items, fields Input items (Command the kind, Label
    /// the word), choices Info items (Command "KIND.FIELD").</summary>
    public static BuildCatalog From(EditorMenu menu)
    {
        var c = new BuildCatalog();
        foreach (var item in menu.Items)
        {
            switch (item.Kind)
            {
                case EditorItemKind.Action:
                    c._kinds.Add((item.Command, item.Label));
                    break;
                case EditorItemKind.Input:
                    if (!c._fields.TryGetValue(item.Command, out var list)) c._fields[item.Command] = list = new List<Field>();
                    list.Add(new Field(item.Label, new FieldDescriptor
                    {
                        Path = item.Label, Label = item.Prompt.Length > 0 ? item.Prompt : item.Label, Type = item.ValueType,
                        Unit = item.Unit ?? "", Min = item.Min, Max = item.Max, Help = item.Help ?? "",
                    }, item.Value ?? ""));
                    break;
                case EditorItemKind.Info:
                    if (!c._options.TryGetValue(item.Command, out var options)) c._options[item.Command] = options = new List<Option>();
                    options.Add(new Option(item.Label, item.Value ?? "", item.Help ?? "", Pairs(item.Prompt), item.Count == 1));
                    break;
            }
        }
        return c;
    }

    /// <summary>"thickness=0.35 width=2" as a dictionary.</summary>
    private static Dictionary<string, string> Pairs(string? text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0) d[pair[..eq]] = pair[(eq + 1)..];
        }
        return d;
    }
}

/// <summary>The last values used for each kind, and the last kind, for the session: building a second
/// wall is Control+B, Enter.</summary>
public sealed class BuildMemory
{
    public string? LastKind { get; set; }
    public Dictionary<string, Dictionary<string, string>> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One field as the dialog shows it: its label (with its unit), its description (range and help)
/// and its type.</summary>
public sealed record BuildFieldView(string Word, string Label, string Description, FieldType Type);

/// <summary>
/// The build dialog's state and rules, shared by both heads: which fields a kind has, what each holds,
/// which are in use (the distance only in front of you; where and facing not when fitting into a wall;
/// a prefab's size only if it can be sized), the check of what was typed, and the command it makes.
/// </summary>
public sealed class BuildForm
{
    private readonly BuildCatalog _catalog;
    private readonly BuildMemory _memory;
    private readonly Dictionary<string, string> _text = new(StringComparer.OrdinalIgnoreCase);

    public BuildForm(BuildCatalog catalog, BuildMemory memory)
    {
        _catalog = catalog;
        _memory = memory;
        string first = catalog.Kinds.Count > 0 ? catalog.Kinds[0].Word : "";
        SetKind(memory.LastKind is { } last && catalog.Kinds.Any(k => k.Word == last) ? last : first);
    }

    public IReadOnlyList<(string Word, string Label)> Kinds => _catalog.Kinds;

    /// <summary>The kind being built: "wall".</summary>
    public string Kind { get; private set; } = "";

    public string KindLabel => _catalog.Kinds.FirstOrDefault(k => k.Word == Kind).Label ?? Kind;

    /// <summary>Changes the kind: its fields as last used this session, else as the server offers them.</summary>
    public void SetKind(string word)
    {
        Kind = word;
        _text.Clear();
        foreach (var f in _catalog.FieldsOf(word)) _text[f.Word] = f.Default;
        if (_memory.Values.TryGetValue(word, out var kept))
            foreach (var (w, v) in kept)
                if (_text.ContainsKey(w) && Accepts(w, v)) _text[w] = v;
        // A remembered prefab outside the remembered category, or none at all: the category's first.
        if (Has("prefab") && !Options("prefab").Any(o => o.Value == _text["prefab"]) && Options("prefab").FirstOrDefault() is { } p)
            _text["prefab"] = p.Value;
    }

    /// <summary>Whether a remembered value is still one the field can hold (a material still offered).</summary>
    private bool Accepts(string word, string value)
    {
        var f = FieldOf(word);
        return f?.Descriptor.Type != FieldType.Choice || _catalog.OptionsOf(Kind, word).Any(o => o.Value == value);
    }

    private BuildCatalog.Field? FieldOf(string word) => _catalog.FieldsOf(Kind).FirstOrDefault(f => f.Word == word);

    private bool Has(string word) => _text.ContainsKey(word);

    /// <summary>The fields of the kind, in order.</summary>
    public IReadOnlyList<BuildFieldView> Fields
        => _catalog.FieldsOf(Kind).Select(f => new BuildFieldView(f.Word, LabelOf(f.Descriptor), DescriptionOf(f.Descriptor), f.Descriptor.Type)).ToList();

    private static string LabelOf(FieldDescriptor f)
        => Capital(f.Label) + (f.Type is FieldType.Number or FieldType.Integer && f.Unit == "m" ? ", in metres"
                              : f.Type is FieldType.Number or FieldType.Integer && f.Unit.Length > 0 ? $", in {f.Unit}" : "");

    private static string DescriptionOf(FieldDescriptor f)
    {
        var parts = new List<string>();
        if (f.Type is FieldType.Number or FieldType.Integer && f.RangeText.Length > 0) parts.Add($"From {f.RangeText}.");
        if (f.Help.Length > 0) parts.Add(f.Help);
        return string.Join(" ", parts);
    }

    /// <summary>What a field holds now: a number as typed, "true" or "false", or a choice's value.</summary>
    public string Text(string word) => _text.TryGetValue(word, out var t) ? t : "";

    /// <summary>The choices a choice field offers now: the prefabs of the chosen category only.</summary>
    public IReadOnlyList<BuildCatalog.Option> Options(string word)
    {
        var all = _catalog.OptionsOf(Kind, word);
        if (word == "prefab" && Has("category")) return all.Where(o => o.Category == Text("category")).ToList();
        return all;
    }

    /// <summary>Where the current choice is in <see cref="Options"/>, or -1.</summary>
    public int Selected(string word)
    {
        var options = Options(word);
        for (int i = 0; i < options.Count; i++) if (options[i].Value == Text(word)) return i;
        return -1;
    }

    /// <summary>
    /// Puts what was typed, ticked or chosen in a field. A choice sets what it carries (a material its own
    /// thickness, a prefab its own size); a new category chooses its first prefab if the one chosen is not in it.
    /// </summary>
    public void Set(string word, string text)
    {
        if (!Has(word)) return;
        _text[word] = text ?? "";
        var option = _catalog.OptionsOf(Kind, word).FirstOrDefault(o => o.Value == text);
        if (option != null)
            foreach (var (w, v) in option.Sets)
                if (Has(w)) _text[w] = v;
        if (word == "category" && Has("prefab") && !Options("prefab").Any(o => o.Value == Text("prefab"))
            && Options("prefab").FirstOrDefault() is { } first)
            Set("prefab", first.Value);
    }

    private bool Fitting => Has("fit") && Text("fit") == "true";

    /// <summary>Whether a field is in use now; a field not in use is shown dimmed, skipped by Tab, and not sent.</summary>
    public bool IsEnabled(string word)
    {
        if (word is "where" or "facing") return !Fitting;
        if (word == "distance") return !Fitting && Text("where") == "ahead";
        if (Kind == "prefab" && word is "width" or "height" or "depth")
            return _catalog.OptionsOf(Kind, "prefab").FirstOrDefault(o => o.Value == Text("prefab"))?.Sized == true;
        return true;
    }

    /// <summary>
    /// Checks every field in use, as the server will, and makes the command: "/edit build wall length 6
    /// height 2.7 thickness 0.35 material Brick where ahead distance 2 facing me dialog". The last word asks
    /// the server to answer the dialog, so a refusal keeps it open. A refusal names the field and its range.
    /// </summary>
    public bool TryCommand(out string command, out string error)
    {
        command = "";
        error = "";
        var words = new List<string> { "/edit", "build", Kind };
        foreach (var f in _catalog.FieldsOf(Kind))
        {
            if (!IsEnabled(f.Word)) continue;
            string text = Text(f.Word).Trim();
            string value;
            switch (f.Descriptor.Type)
            {
                case FieldType.Number:
                case FieldType.Integer:
                    if (!f.Descriptor.TryParse(Decimal(text), out value, out error)) return false;
                    value = FieldDescriptor.Format(double.Parse(value, CultureInfo.InvariantCulture));
                    break;
                case FieldType.Bool:
                    value = text == "true" ? "yes" : "no";
                    break;
                case FieldType.Choice:
                    if (!_catalog.OptionsOf(Kind, f.Word).Any(o => o.Value == text))
                    {
                        error = $"Choose a {f.Descriptor.Label}.";
                        return false;
                    }
                    value = text;
                    break;
                default:
                    if (text.Length == 0 || text.Any(char.IsWhiteSpace)) { error = $"{Capital(f.Descriptor.Label)} is one word."; return false; }
                    value = text;
                    break;
            }
            words.Add(f.Word);
            words.Add(value);
        }
        words.Add("dialog");
        command = string.Join(" ", words);
        return true;
    }

    /// <summary>Keeps this kind's values, and the kind, for the next time the dialog opens.</summary>
    public void Remember()
    {
        _memory.LastKind = Kind;
        _memory.Values[Kind] = new Dictionary<string, string>(_text, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What the game says as the dialog opens.</summary>
    public string Spoken => $"Build: {KindLabel}. Tab moves through the fields, Enter places, Escape cancels.";

    /// <summary>A decimal comma is read as a point: "0,5" is a half.</summary>
    private static string Decimal(string word)
        => word.Count(c => c == ',') == 1 && !word.Contains('.') ? word.Replace(',', '.') : word;

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];
}

/// <summary>
/// A dialog over the game that stays open while it is used: the key that opened it closes it again, as do
/// Escape and Cancel; doing what it is for keeps it open. The build dialog (Control+B) is one; the F12
/// editor dialog is to be another. The head shows it and asks <see cref="IsCloseKey"/> of each key.
/// </summary>
public abstract class ModalDialog
{
    protected ModalDialog(GameKeyChord opener) => Opener = opener;

    /// <summary>The key, with its modifiers, that opened the dialog and closes it.</summary>
    public GameKeyChord Opener { get; }

    public bool IsOpen { get; private set; } = true;

    /// <summary>The dialog is to close: the head closes its window (no "Cancelled" is said for it).</summary>
    public event Action? CloseRequested;

    /// <summary>Escape, or the key that opened it.</summary>
    public bool IsCloseKey(Platform.GameKey key, Input.KeyModifiers modifiers)
        => (key == Platform.GameKey.Escape && modifiers == Input.KeyModifiers.None) || (key == Opener.Key && modifiers == Opener.Modifiers);

    /// <summary>Closes it from outside the head (the opener pressed again in the game window).</summary>
    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        CloseRequested?.Invoke();
    }

    /// <summary>The head closed it itself (Escape, Cancel, the opener, the window's close button).</summary>
    public void Closed() => IsOpen = false;
}

/// <summary>A key and the modifiers held with it.</summary>
public readonly record struct GameKeyChord(Platform.GameKey Key, Input.KeyModifiers Modifiers);

/// <summary>
/// One open build dialog, between the session and the head that shows it. Place checks the form and sends
/// its command; the server's answer comes back through <see cref="Answer"/>. Either way the dialog stays
/// open with its values, so another can be placed at once: placed puts the focus back on What, refused
/// shows and says the reason.
/// </summary>
public sealed class BuildDialog : ModalDialog
{
    /// <summary>How long a placing waits for the server before Place may be pressed again.</summary>
    public static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(5);

    /// <summary>Control+B opens it and closes it.</summary>
    public static readonly GameKeyChord Key = new(Platform.GameKey.B, Input.KeyModifiers.Control);

    private readonly Action<string> _send;
    private DateTime? _sentUtc;

    public BuildForm Form { get; }

    /// <summary>The server placed it: the head puts the focus back on What. The session says what was placed.</summary>
    public event Action? Placed;
    /// <summary>The server refused: the head shows and says the reason.</summary>
    public event Action<string>? Refused;

    public BuildDialog(BuildForm form, Action<string> send) : base(Key)
    {
        Form = form;
        _send = send;
    }

    /// <summary>Checks and sends. Null when sent (the answer follows), or the reason it was not, to show and say.</summary>
    public string? Place(DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        if (_sentUtc is { } sent && at - sent < AnswerWait) return "Still placing the last one.";
        if (!Form.TryCommand(out var command, out var error)) return error;
        _sentUtc = at;
        _send(command);
        return null;
    }

    /// <summary>The server's answer to what Place sent.</summary>
    public void Answer(bool placed, string text)
    {
        _sentUtc = null;
        if (!IsOpen) return;
        if (placed)
        {
            Form.Remember();
            Placed?.Invoke();
        }
        else Refused?.Invoke(text);
    }
}
