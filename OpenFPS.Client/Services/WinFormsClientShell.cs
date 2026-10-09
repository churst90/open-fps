using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.UI;

namespace OpenFPS.Client.Services;

/// <summary>
/// The Windows half of <see cref="IClientShell"/>: a thin adapter over
/// <see cref="ClientNavigationService"/> (which owns the Forms and the UI-thread queue) and the
/// in-game <see cref="MainWindow"/>.
///
/// The shared session calls every one of these from the game-loop thread; each is queued onto the
/// UI thread before it touches a Form.
/// </summary>
public sealed class WinFormsClientShell : IClientShell
{
    private readonly ClientNavigationService _navigation;
    private readonly NvdaSpeechOutput _speech;
    private readonly Action _quit;
    private MainWindow? _gameWindow;

    public event Action<string>? CommandEntered;

    public WinFormsClientShell(ClientNavigationService navigation, NvdaSpeechOutput speech, Action quit)
    {
        _navigation = navigation;
        _speech = speech;
        _quit = quit;
    }

    /// <summary>Gameplay keys are live only while the game window is the active window and neither the
    /// command console nor the game menu is open — otherwise the player would walk while typing.</summary>
    public bool IsGameInputActive => _gameWindow is { IsWindowActive: true, IsModalOpen: false };

    public bool? NumLockOn => _gameWindow?.NumLockOn;

    public void ShowLoading(string status, bool speak = true) => _navigation.ShowLoading(status, speak);

    public void UpdateLoadingStatus(string text, int percent) => _navigation.UpdateLoadingStatus(text, percent);

    // Called for the first spawn and for every /tp after it; the navigation service keeps ONE window.
    public void EnterGame() => _navigation.EnterGame(win =>
    {
        _gameWindow = win;
        win.OnCommandEntered += text => CommandEntered?.Invoke(text);
    });

    public void OpenCommandConsole() => OpenCommandConsole("");

    public void OpenCommandConsole(string initialText) => OnGameWindow(win => win.OpenCommandWindow(initialText));

    public void AskForValue(EditorValuePrompt prompt, Func<string, string?> submit) => OnGameWindow(win => win.AskForValue(prompt, submit));

    public void ShowGameMenu(Action<GameMenuChoice> chosen)
    {
        if (_gameWindow == null) { chosen(GameMenuChoice.KeepPlaying); return; }
        OnGameWindow(win => win.ShowGameMenu(chosen));
    }

    // NVDA reads the menu as it takes focus; without it the game says where you are.
    public void ReturnToMenu() => _navigation.ReturnToMenu(() =>
    {
        if (!_speech.ScreenReaderRunning) _speech.Speak("Main menu.", interrupt: false);
    });

    public void Quit() => _quit();

    private void OnGameWindow(Action<MainWindow> action)
    {
        var win = _gameWindow;
        if (win == null || win.IsDisposed || !win.IsHandleCreated) return;
        win.BeginInvoke(new Action(() => action(win)));
    }
}
