namespace OpenFPS.Client.Core.Platform;

/// <summary>
/// What the shared session needs from a window system: the session decides when, the head decides
/// what it looks like (WinForms on Windows, GTK over AT-SPI on Linux). Every member may be called from
/// the game-loop thread; the head marshals to its own UI thread, since the session cannot know how.
/// </summary>
public interface IClientShell
{
    /// <summary>Shows the loading screen with an initial status line, spoken unless <paramref name="speak"/> is false.</summary>
    void ShowLoading(string status, bool speak = true);

    /// <summary>Updates the loading screen's status line and progress (0-100).</summary>
    void UpdateLoadingStatus(string text, int percent);

    /// <summary>The player has spawned: the in-game window takes keyboard focus.</summary>
    void EnterGame();

    /// <summary>Opens the command console (slash); what is typed comes back through
    /// <see cref="CommandEntered"/>.</summary>
    void OpenCommandConsole();

    /// <summary>Opens the command console with text already typed — "/pm sean01 " from a player's
    /// menu — and the cursor at its end. A head that cannot pre-fill opens it empty.</summary>
    void OpenCommandConsole(string initialText) => OpenCommandConsole();

    /// <summary>
    /// A dialog with one labelled text box for a value (the world editor's typed settings), holding
    /// <see cref="EditorValuePrompt.Initial"/> all selected. Enter hands the text to
    /// <paramref name="submit"/>, which answers null when it was taken (the dialog closes) or the reason
    /// it was not (said and shown; the dialog stays open with the text kept). Escape cancels. A head
    /// without the dialog opens the command console with the start of the command typed.
    /// </summary>
    void AskForValue(EditorValuePrompt prompt, Func<string, string?> submit) => OpenCommandConsole(prompt.Command);

    /// <summary>
    /// The build dialog (Control+B, docs/WORLD_EDITOR.md section 15): a modal window made from
    /// <see cref="BuildDialog.Form"/>, focus on its first control. Place calls <see cref="BuildDialog.Place"/>
    /// and stays open; the head closes it on Escape, Cancel or <see cref="ModalDialog.Opener"/>, and when
    /// <see cref="ModalDialog.CloseRequested"/> fires. A head without it opens the command console.
    /// </summary>
    void ShowBuildDialog(BuildDialog dialog) => OpenCommandConsole("/edit build ");

    /// <summary>
    /// The F12 editor dialog (docs/WORLD_EDITOR.md section 16): a modal window with one tab page for each
    /// of <see cref="EditorDialog.Tabs"/>, drawn from their sections and controls, and Undo, Redo and
    /// Close under them. Control+Tab and Control+Shift+Tab (and Control+Page Down and Up) change tab
    /// through <see cref="EditorDialog.NextTab"/>; F12, Escape and Close shut it. A head without it opens
    /// the command console.
    /// </summary>
    void ShowEditorDialog(EditorDialog dialog) => OpenCommandConsole("/edit ");

    /// <summary>
    /// Escape in game: the game menu (Keep playing, Main menu, Quit), with Keep playing focused so a
    /// stray Enter does nothing. Escape or closing it is <see cref="GameMenuChoice.KeepPlaying"/>. The
    /// session does the logging out; the shell only asks.
    /// </summary>
    void ShowGameMenu(Action<GameMenuChoice> chosen);

    /// <summary>Closes the game window and anything over it and focuses the main menu.</summary>
    void ReturnToMenu();

    /// <summary>Closes the program. The session has already logged out.</summary>
    void Quit();

    /// <summary>
    /// The game window has focus and no text entry is open. When false the session zeroes movement, so
    /// the player does not drift while typing.
    /// </summary>
    bool IsGameInputActive { get; }
    /// <summary>
    /// Num Lock as the head last saw it, or null when it cannot tell. The scope's keys reach the game
    /// only with Num Lock on: off, NVDA and Orca take the keypad for review.
    /// </summary>
    bool? NumLockOn => null;

    /// <summary>A line committed in the command console, for the session to parse.</summary>
    event Action<string>? CommandEntered;
}

/// <summary>What the player chose from the game menu.</summary>
public enum GameMenuChoice { KeepPlaying, MainMenu, Quit }
