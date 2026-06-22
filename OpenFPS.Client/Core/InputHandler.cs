using System.Windows.Forms;
using OpenFPS.Client.Services;
using OpenFPS.Common.Networking;
using OpenFPS.Client.UI;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Client.Core;

/// <summary>
/// Responsibility: Orchestrates the transformation of raw peripheral input (keyboard) 
/// into meaningful game commands and network messages.
/// </summary>
public class InputHandler
{
    private readonly TolkService _tts;
    private readonly ChatManager _chat;
    private readonly ClientNetworkService _network;
    private readonly LocalPlayerState _state;
    private readonly ClientWorldState _world;
    private MainWindow? _activeWindow;
    private readonly InputCommandMapper _commandMapper = new();

    /// <summary>
    /// Initializes the input handler and sets up the default keyboard-to-action bindings.
    /// </summary>
    public InputHandler(TolkService tts, ClientNetworkService network, LocalPlayerState state, ClientWorldState world)
    {
        _tts = tts;
        _chat = new ChatManager(tts);
        _network = network;
        _state = state;
        _world = world;

        new OpenFPS.Client.Core.Input.SystemInputProcessor(_network, _chat, OpenCommandWindow, ConfirmQuit).RegisterBindings(_commandMapper);
        new OpenFPS.Client.Core.Input.AccessibilityProcessor(_tts, _state, _world).RegisterBindings(_commandMapper);
        new OpenFPS.Client.Core.Input.CombatProcessor(_network, _world, _state).RegisterBindings(_commandMapper);
    }

    public ChatManager Chat => _chat;


    private void OpenCommandWindow()
    {
        if (_activeWindow != null && _activeWindow.IsHandleCreated)
        {
            _activeWindow.BeginInvoke(new Action(() => _activeWindow.OpenCommandWindow()));
        }
    }


    /// <summary>
    /// Displays a confirmation dialog before exiting the application.
    /// </summary>
    private void ConfirmQuit()
    {
        if (_activeWindow != null && _activeWindow.IsHandleCreated)
        {
            _activeWindow.BeginInvoke(new Action(() => {
                var result = MessageBox.Show("Do you want to quit the game?", "Quit", MessageBoxButtons.YesNo);
                if (result == DialogResult.Yes)
                {
                    _network.Send(new LogoutRequest());
                    Application.Exit();
                }
            }));
        }
    }

    /// <summary>
    /// Sets the UI context to ensure input is only processed when the window is focused.
    /// </summary>
    public void SetActiveWindow(MainWindow win) => _activeWindow = win;

    public bool IsFocused()
    {
        if (_activeWindow == null) return false;
        return _activeWindow.IsWindowActive && !_activeWindow.IsCommandMode;
    }

    /// <summary>
    /// Main entry point for input processing. 
    /// Filters input based on window focus and command-mode state.
    /// </summary>
    public void ProcessInput(HashSet<Keys> held, HashSet<Keys> justPressed)
    {
        if (_activeWindow == null || !_activeWindow.IsWindowActive) return;
        
        InputContext context = _activeWindow.IsCommandMode ? InputContext.UI : InputContext.Gameplay;

        foreach (var key in justPressed)
        {
            _commandMapper.Execute(context, key);
        }
    }

    /// <summary>
    /// Handles text entered via the UI command console, converting it into a TextCommand message.
    /// </summary>
    public void HandleCommandEntered(string text)
    {
        string input = text.Trim();
        if (string.IsNullOrEmpty(input)) return;
        
        if (input.StartsWith("/")) 
        {
            // Handle as Command
            string cmdBody = input.Substring(1);
            var parts = cmdBody.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            string commandName = parts[0].ToLower();
            string[] args = parts.Skip(1).ToArray();

            _network.Send(new TextCommand { Command = commandName, Args = args });
        }
        else 
        {
            // Handle as Public Chat
            _network.Send(new ChatMessage { Text = input });
        }
    }
}
