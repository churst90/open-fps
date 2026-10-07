using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenFPS.Server.Core;

/// <summary>Rules for player text the server files, passes on or says to others.</summary>
public static class SafeText
{
    /// <summary>The longest line of chat, in characters: the MUD gateway's line.</summary>
    public const int MaxChatChars = 512;
    /// <summary>The longest command, all its words together.</summary>
    public const int MaxCommandChars = 1024;
    /// <summary>The most words in one command.</summary>
    public const int MaxCommandWords = 64;
    /// <summary>The longest command name ("edit", "setmotd").</summary>
    public const int MaxCommandNameChars = 32;
    /// <summary>The longest sound id a player may give a thing.</summary>
    public const int MaxSoundIdChars = 128;

    // \z, not $: in .NET $ also matches before a final newline, so "name\n" passed.
    private static readonly Regex FileName = new("^[A-Za-z0-9_-]{1,64}\\z", RegexOptions.CultureInvariant);

    /// <summary>A name that is used as a file name: letters, digits, '_' and '-', 1 to 64 of them.</summary>
    public static bool IsFileName(string? name) => name != null && FileName.IsMatch(name);

    /// <summary>A sound id a player may give a thing: a relative name under the clients' sounds folder, or a model id.</summary>
    // Every client on the map opens it as a file: never rooted, a drive ("C:"), a share ("\\host", where Windows
    // hands over its login hash), "..", or a name with ASSETS (which the client takes as a path).
    public static bool IsSoundId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxSoundIdChars) return false;
        foreach (char c in id)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':' or ' ')) return false;
        if (id[0] is '/' or '.' or ' ' || id.Contains("..", StringComparison.Ordinal) || id.Contains("//", StringComparison.Ordinal)) return false;
        if (id.Contains("ASSETS", StringComparison.OrdinalIgnoreCase)) return false;
        int colon = id.IndexOf(':');
        if (colon >= 0)
        {
            // A model's kind before it ("machine:"), never a drive letter, and only one.
            if (colon < 2 || id.IndexOf(':', colon + 1) >= 0) return false;
            for (int i = 0; i < colon; i++) if (!(char.IsAsciiLetterOrDigit(id[i]) || id[i] == '_')) return false;
        }
        return true;
    }

    /// <summary>A volume a player may give a sound: a finite number from 0 to 4, read the same in any culture.</summary>
    public static bool TryVolume(string? text, out float volume)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out volume)
           && float.IsFinite(volume) && volume >= 0f && volume <= 4f;

    /// <summary>Control characters, and the marks that reverse or isolate reading order (U+202A-202E, U+2066-2069).</summary>
    // A terminal escape reaches a telnet player's screen as a command; a newline forges a log line.
    public static bool IsUnsafeChar(char c)
        => char.IsControl(c) || c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069';

    /// <summary>Words for other people: unsafe characters become spaces, cut at <paramref name="maxChars"/>.</summary>
    public static string Words(string? text, int maxChars = MaxChatChars)
    {
        if (string.IsNullOrEmpty(text)) return "";
        bool clean = text.Length <= maxChars;
        if (clean) foreach (char c in text) if (IsUnsafeChar(c)) { clean = false; break; }
        if (clean) return text;
        var sb = new StringBuilder(Math.Min(text.Length, maxChars));
        foreach (char c in text)
        {
            if (sb.Length >= maxChars) break;
            sb.Append(IsUnsafeChar(c) ? ' ' : c);
        }
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;
        return sb.ToString();
    }
}
