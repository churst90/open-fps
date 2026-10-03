using System;
using System.Windows.Forms;
using OpenFPS.Client.UI;

namespace OpenFPS.Client.Services;

/// <summary>
/// Owns the Forms — main menu, loading screen, game window — and moves between them. Safe to call
/// from any thread: every change is marshalled onto the UI thread first.
///
/// The marshal is a hidden control's BeginInvoke, which posts a window message and so wakes the UI
/// thread. The queue it replaces was drained on Application.Idle, which only fires after some OTHER
/// message has been handled — so a loading line or a login outcome posted from the game thread could
/// sit unseen until the player happened to press a key.
/// </summary>
public sealed class ClientNavigationService : ApplicationContext
{
    private readonly Control _marshal;
    private readonly Func<MenuWindow> _menuFactory;
    private readonly Func<MainWindow> _gameFactory;
    private readonly Func<LoadingWindow> _loadingFactory;

    private MenuWindow? _menu;
    private LoadingWindow? _loading;
    private MainWindow? _gameWindow;

    /// <summary>Must be constructed on the UI thread, before Application.Run.</summary>
    public ClientNavigationService(Func<MenuWindow> menuFactory, Func<LoadingWindow> loadingFactory, Func<MainWindow> gameFactory)
    {
        _menuFactory = menuFactory;
        _loadingFactory = loadingFactory;
        _gameFactory = gameFactory;
        _marshal = new Control();
        _marshal.CreateControl();
        _ = _marshal.Handle;
    }

    /// <summary>The menu window, once shown. Null in game.</summary>
    public MenuWindow? Menu => _menu is { IsDisposed: false } m ? m : null;

    /// <summary>Runs an action on the UI thread, now if already there.</summary>
    public void EnqueueUIAction(Action action)
    {
        void Safe()
        {
            try { action(); }
            catch (Exception ex) { Serilog.Log.Error(ex, "UI action failed."); }
        }
        if (!_marshal.InvokeRequired) { Safe(); return; }
        try { _marshal.BeginInvoke(new Action(Safe)); }
        catch (InvalidOperationException) { /* shutting down: the handle is gone */ }
    }

    /// <summary>Passes a connect/login outcome to the menu's connect form, if one is open.</summary>
    public void ReportLoginOutcome(string message, bool success) =>
        EnqueueUIAction(() => Menu?.ReportLoginOutcome(message, success));

    public void ShowMenu() => EnqueueUIAction(() =>
    {
        if (_menu == null || _menu.IsDisposed)
        {
            _menu = _menuFactory();
            // Closing the menu quits unless the game is what is showing (a hidden game window after
            // Main menu does not count).
            _menu.FormClosed += (_, _) => { if (_gameWindow is not { Visible: true }) ExitThread(); };
        }
        SwitchTo(_menu);
    });

    public void ShowLoading(string status) => EnqueueUIAction(() =>
    {
        if (_loading == null || _loading.IsDisposed) _loading = _loadingFactory();
        _loading.ShowStatus(status);
        // Over the menu, not instead of it: a login that fails after this goes back to the connect form.
        if (!_loading.Visible) _loading.Show(Menu);
        _loading.Activate();
    });

    public void UpdateLoadingStatus(string text, int percent) => EnqueueUIAction(() =>
    {
        if (_loading is { IsDisposed: false }) _loading.UpdateStatus(text, percent);
    });

    /// <summary>
    /// Brings the game window up. ONE window for the life of the session: the server answers every
    /// spawn with a PlayerSpawned, the first and every /tp after it, and a window per call leaves a
    /// stack of them behind the live one.
    /// </summary>
    public void EnterGame(Action<MainWindow> setup) => EnqueueUIAction(() =>
    {
        if (_gameWindow == null || _gameWindow.IsDisposed)
        {
            _gameWindow = _gameFactory();
            setup(_gameWindow);
            _gameWindow.FormClosed += (_, _) =>
            {
                Serilog.Log.Information("Game window closed.");
                ExitThread();
            };
        }
        SwitchTo(_gameWindow);
        if (_loading is { IsDisposed: false }) { _loading.Dispose(); _loading = null; }
        _menu?.Hide();
    });

    /// <summary>
    /// Leaves the game for the main menu: the game window is hidden (kept, for the next login) with
    /// anything open over it closed, and the menu takes focus. NVDA reads the menu as it does; without
    /// a screen reader the game says where you are.
    /// </summary>
    public void ReturnToMenu(Action? speak = null) => EnqueueUIAction(() =>
    {
        if (_loading is { IsDisposed: false }) { _loading.Dispose(); _loading = null; }
        if (_gameWindow is { IsDisposed: false } game)
        {
            game.CloseModals();
            game.Hide();
        }
        if (_menu == null || _menu.IsDisposed)
        {
            _menu = _menuFactory();
            _menu.FormClosed += (_, _) => { if (_gameWindow is not { Visible: true }) ExitThread(); };
        }
        SwitchTo(_menu);
        speak?.Invoke();
    });

    private void SwitchTo(Form form)
    {
        MainForm = form;
        if (!form.Visible) form.Show();
        form.Activate();
    }
}
