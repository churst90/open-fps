using OpenFPS.Common.Components;
using OpenFPS.Server.Core;

namespace OpenFPS.Tests;

/// <summary>The command box without menus: help lists what you may use, a typo is answered with the
/// command meant, and every command the server answers is in the catalogue.</summary>
public class CommandCatalogTests
{
    private static UserSession As(UserRole role) => new() { Username = "tester", Role = role };

    [Fact]
    public void HelpListsOnlyWhatThePlayerMayUse()
    {
        string player = CommandCatalog.Help(As(UserRole.Player));
        Assert.Contains("/take", player);
        Assert.DoesNotContain("/kick", player);
        Assert.DoesNotContain("/spawn", player);
        Assert.Contains("/kick", CommandCatalog.Help(As(UserRole.Moderator)));
        Assert.Contains("/setrole", CommandCatalog.Help(As(UserRole.Admin)));
    }

    [Theory]
    [InlineData("givee", "/give")]
    [InlineData("invetory", "/inventory")]
    [InlineData("frends", "/friends")]
    [InlineData("tak", "/take")]
    public void ATypoIsAnsweredWithTheCommandMeant(string typed, string meant)
        => Assert.Contains(meant, CommandCatalog.Unknown(As(UserRole.Admin), typed));

    [Fact]
    public void ATypoNeverSuggestsACommandThePlayerCannotUse()
        => Assert.DoesNotContain("/kick", CommandCatalog.Unknown(As(UserRole.Player), "kik"));

    [Fact]
    public void HelpForOneCommandSaysHowAndWhat()
    {
        string h = CommandCatalog.HelpFor(As(UserRole.Player), "give");
        Assert.StartsWith("/give [NAME] ITEM [COUNT]", h);
        Assert.Contains("give a player an item", h);
        Assert.Contains("You do not have permission", h);
    }

    /// <summary>Every case the server's command switch answers is in the catalogue, so /help and the
    /// typo answer know about it. A command added to the switch and not here fails.</summary>
    [Fact]
    public void EveryCommandTheServerAnswersIsCatalogued()
    {
        string source = File.ReadAllText(FindSource("OpenFPS.Server", "Core", "CommandHandler.cs"));
        int start = source.IndexOf("private void Execute(", StringComparison.Ordinal);
        int end = source.IndexOf("default:", start, StringComparison.Ordinal);
        var cases = System.Text.RegularExpressions.Regex.Matches(source[start..end], "case \"([^\"]+)\":")
                         .Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(cases);
        var missing = cases.Where(c => !CommandCatalog.TryFind(c, out _)).ToList();
        Assert.True(missing.Count == 0, "Not in CommandCatalog: " + string.Join(", ", missing));
    }

    private static string FindSource(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
        }
        string here = Path.GetDirectoryName(ThisFile())!;
        return Path.Combine(new[] { here, ".." }.Concat(parts).ToArray());
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
