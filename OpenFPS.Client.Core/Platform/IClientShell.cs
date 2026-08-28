using System;

namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// Everything the shared game session needs from a window system, and nothing more.
///
/// This is the seam that lets one session class drive both heads: the session decides *when* a
/// loading screen should appear, when the player has entered the world, and when the command console
/// was asked for; the head decides what any of that looks like. On Windows those are WinForms
/// windows marshaled through <c>ClientNavigationService</c>; on Linux they are GTK windows that
/// expose themselves to Orca over AT-SPI.
///
/// Every method may be called from the game-loop thread. Implementations are responsible for
/// marshaling to their own UI thread — the session never does, because it cannot know how.
/// </summary>
public interface IClientShell
{
    /// <summary>Shows the loading screen with an initial status line.</summary>
    void ShowLoading(string status);

    /// <summary>Updates the loading screen's status line and progress (0-100).</summary>
    void UpdateLoadingStatus(string text, int percent);

    /// <summary>The local player has spawned: hand the player the in-game window and keyboard focus.</summary>
    void EnterGame();

    /// <summary>The player asked for the command console (slash). Opens a text entry that reports what
    /// was typed through <see cref="CommandEntered"/>.</summary>
    void OpenCommandConsole();

    /// <summary>The player asked to quit (escape). The shell confirms and, if confirmed, shuts down.</summary>
    void RequestQuit();

    /// <summary>
    /// True when gameplay keys should be acted on: the game window has focus and no modal text entry
    /// is open. When false the session zeroes movement rather than letting the player drift while
    /// typing into the console.
    /// </summary>
    bool IsGameInputActive { get; }

    /// <summary>Raised when the player commits a line in the command console. The session parses it
    /// into a command or a chat message.</summary>
    event Action<string>? CommandEntered;
}
