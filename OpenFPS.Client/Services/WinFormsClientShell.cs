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
    // The connecting window, touched only on the UI thread. Ended by a login, it goes once the loading
    // screen or the menu has focus, so the form behind does not take focus for a moment and be read.
    private ConnectingWindow? _hold;
    private bool _holdCloseOnNextWindow;

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

    public void ShowLoading(string status, bool speak = true)
    {
        _navigation.ShowLoading(status, speak);
        _navigation.EnqueueUIAction(CloseHoldAfterNextWindow);
    }

    public void ShowConnecting(string status, Action cancel) => _navigation.EnqueueUIAction(() =>
    {
        _holdCloseOnNextWindow = false;
        if (_hold is { IsDisposed: false }) { _hold.SetStatus(status, cancel); return; }
        var owner = Form.ActiveForm ?? _navigation.Menu;
        var hold = new ConnectingWindow(status, cancel);
        _hold = hold;
        hold.FormClosed += (_, _) => { if (ReferenceEquals(_hold, hold)) _hold = null; };
        if (owner != null) hold.Show(owner); else hold.Show();
        hold.Activate();
    });

    public void EndConnecting(bool returnFocus) => _navigation.EnqueueUIAction(() =>
    {
        if (_hold is not { IsDisposed: false } hold) return;
        if (!returnFocus) { _holdCloseOnNextWindow = true; return; }
        // Windows gives focus back to the owner, to the control that had it.
        hold.End();
    });

    private void CloseHoldAfterNextWindow()
    {
        if (!_holdCloseOnNextWindow) return;
        _holdCloseOnNextWindow = false;
        _hold?.CloseNow();
    }

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

    public void ShowBuildDialog(BuildDialog dialog) => OnGameWindow(win => win.ShowBuildDialog(dialog));

    public void ShowEditorDialog(EditorDialog dialog) => OnGameWindow(win => win.ShowEditorDialog(dialog));

    public void ShowGameMenu(Action<GameMenuChoice> chosen)
    {
        if (_gameWindow == null) { chosen(GameMenuChoice.KeepPlaying); return; }
        OnGameWindow(win => win.ShowGameMenu(chosen));
    }

    // NVDA reads the menu as it takes focus; without it the game says where you are.
    public void ReturnToMenu() => _navigation.ReturnToMenu(() =>
    {
        CloseHoldAfterNextWindow();
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
