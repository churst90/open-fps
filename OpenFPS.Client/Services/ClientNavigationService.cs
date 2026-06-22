using System;
using System.Collections.Concurrent;
using System.Windows.Forms;
using OpenFPS.Client.UI;

namespace OpenFPS.Client.Services;

/// <summary>
/// Centralizes Form management and provides a thread-safe way to update the UI
/// without risking Invoke deadlocks from the network/game thread.
/// </summary>
public class ClientNavigationService : ApplicationContext
{
    private readonly TolkService _tts;
    private readonly Func<MenuWindow> _menuFactory;
    
    private MenuWindow? _menu;
    private LoadingWindow? _loading;
    private MainWindow? _gameWindow;
    
    // Thread-safe UI update queue
    private readonly ConcurrentQueue<Action> _uiThreadQueue = new();

    public ClientNavigationService(TolkService tts, Func<MenuWindow> menuFactory)
    {
        _tts = tts;
        _menuFactory = menuFactory;

        // Drain the UI action queue when the application is idle
        Application.Idle += (s, e) => ProcessUIQueue();
    }

    public void EnqueueUIAction(Action action)
    {
        _uiThreadQueue.Enqueue(action);
    }

    private void ProcessUIQueue()
    {
        while (_uiThreadQueue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI Queue Error] {ex}");
            }
        }
    }

    public void ShowMenu()
    {
        if (this.MainForm != null && this.MainForm.InvokeRequired)
        {
            EnqueueUIAction(ShowMenu);
            return;
        }

        if (_menu == null || _menu.IsDisposed)
        {
            _menu = _menuFactory();
            _menu.FormClosed += (s, e) => 
            { 
                if (_gameWindow == null && _loading == null) ExitThread(); 
            };
        }
        
        var oldForm = this.MainForm;
        this.MainForm = _menu;
        _menu.Show();
        _menu.Focus();

        if (oldForm != null && oldForm != _menu) 
        {
            oldForm.Hide();
            // We don't dispose immediately to allow the message pump to settle
            EnqueueUIAction(() => { if (!oldForm.IsDisposed) oldForm.Dispose(); });
        }
    }

    public void ShowLoading(string initialStatus = "Connecting...")
    {
        if (this.MainForm != null && this.MainForm.InvokeRequired)
        {
            EnqueueUIAction(() => ShowLoading(initialStatus));
            return;
        }

        if (_loading == null || _loading.IsDisposed)
        {
            _loading = new LoadingWindow(_tts);
            _loading.FormClosed += (s, e) => 
            {
                if (_gameWindow == null && _menu == null) ExitThread();
            };
        }

        _loading.UpdateStatus(initialStatus, 0);
        
        var oldForm = this.MainForm;
        this.MainForm = _loading;
        _loading.Show();
        _loading.Focus();

        if (oldForm != null && oldForm != _loading) 
        {
            oldForm.Hide();
            EnqueueUIAction(() => { if (!oldForm.IsDisposed) oldForm.Dispose(); });
        }
    }

    public void UpdateLoadingStatus(string text, int percent)
    {
        if (_loading != null && !_loading.IsDisposed)
        {
            _loading.UpdateStatus(text, percent);
        }
    }

    public void EnterGame(Action<MainWindow> setup)
    {
        if (this.MainForm != null && this.MainForm.InvokeRequired)
        {
            EnqueueUIAction(() => EnterGame(setup));
            return;
        }

        try 
        {
            if (_gameWindow == null || _gameWindow.IsDisposed)
            {
                _gameWindow = new MainWindow(_tts);
                setup(_gameWindow);
                _gameWindow.FormClosed += (s, e) => ExitThread();
            }

            var oldForm = this.MainForm;
            this.MainForm = _gameWindow;
            _gameWindow.Show();
            _gameWindow.Focus();
            _tts.Speak("Game world entered.");

            if (oldForm != null && oldForm != _gameWindow) 
            {
                oldForm.Hide();
                // Delay disposal to ensure the switch is fully registered by the OS
                EnqueueUIAction(() => { 
                    if (!oldForm.IsDisposed) oldForm.Dispose(); 
                    if (oldForm == _loading) _loading = null;
                    if (oldForm == _menu) _menu = null;
                });
            }
        }
        catch (Exception ex)
        {
            _tts.Speak("Error switching to game window.");
            Console.WriteLine($"[UI ERROR] EnterGame: {ex}");
        }
    }
}
