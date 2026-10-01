using System;
using System.Windows.Forms;
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
    private readonly Action _onQuitConfirmed;
    private MainWindow? _gameWindow;

    public event Action<string>? CommandEntered;

    public WinFormsClientShell(ClientNavigationService navigation, Action onQuitConfirmed)
    {
        _navigation = navigation;
        _onQuitConfirmed = onQuitConfirmed;
    }

    /// <summary>Gameplay keys are live only while the game window is the active window and neither the
    /// command console nor the quit prompt is open — otherwise the player would walk while typing.</summary>
    public bool IsGameInputActive => _gameWindow is { IsWindowActive: true, IsModalOpen: false };

    public void ShowLoading(string status, bool speak = true) => _navigation.ShowLoading(status);

    public void UpdateLoadingStatus(string text, int percent) => _navigation.UpdateLoadingStatus(text, percent);

    // Called for the first spawn and for every /tp after it; the navigation service keeps ONE window.
    public void EnterGame() => _navigation.EnterGame(win =>
    {
        _gameWindow = win;
        win.OnCommandEntered += text => CommandEntered?.Invoke(text);
    });

    public void OpenCommandConsole() => OpenCommandConsole("");

    public void OpenCommandConsole(string initialText) => OnGameWindow(win => win.OpenCommandWindow(initialText));

    public void RequestQuit()
    {
        if (_gameWindow == null) { _navigation.EnqueueUIAction(_onQuitConfirmed); return; }
        OnGameWindow(win => win.ConfirmQuit(_onQuitConfirmed));
    }

    private void OnGameWindow(Action<MainWindow> action)
    {
        var win = _gameWindow;
        if (win == null || win.IsDisposed || !win.IsHandleCreated) return;
        win.BeginInvoke(new Action(() => action(win)));
    }
}
