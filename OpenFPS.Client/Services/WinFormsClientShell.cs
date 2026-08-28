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
/// The shared session calls every one of these from the game-loop thread. The navigation service
/// already marshals its own calls; the two that reach a Form directly — the command console and the
/// quit prompt — are queued onto the UI thread here.
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

    /// <summary>Gameplay keys are live only while the game window is the active window and its modal
    /// command dialog is closed — otherwise the player would walk while typing a command.</summary>
    public bool IsGameInputActive =>
        _gameWindow != null && _gameWindow.IsWindowActive && !_gameWindow.IsCommandMode;

    public void ShowLoading(string status) => _navigation.ShowLoading(status);

    public void UpdateLoadingStatus(string text, int percent) => _navigation.UpdateLoadingStatus(text, percent);

    public void EnterGame() => _navigation.EnterGame(win =>
    {
        _gameWindow = win;
        win.OnCommandEntered += text => CommandEntered?.Invoke(text);
    });

    public void OpenCommandConsole()
    {
        var win = _gameWindow;
        if (win == null || !win.IsHandleCreated) return;
        win.BeginInvoke(new Action(win.OpenCommandWindow));
    }

    public void RequestQuit()
    {
        var win = _gameWindow;
        if (win == null || !win.IsHandleCreated)
        {
            _onQuitConfirmed();
            return;
        }

        win.BeginInvoke(new Action(() =>
        {
            var result = MessageBox.Show("Do you want to quit the game?", "Quit", MessageBoxButtons.YesNo);
            if (result == DialogResult.Yes) _onQuitConfirmed();
        }));
    }
}
