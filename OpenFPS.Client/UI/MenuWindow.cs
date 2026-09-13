using System.Windows.Forms;
using OpenFPS.Client.Services;
using OpenFPS.Client.Core.Platform;
using System.Drawing;
using System;

namespace OpenFPS.Client.UI;

public class MenuWindow : Form
{
    private readonly ISpeechOutput _tts;
    private readonly PersistenceService _persistence;

    public event Action<SavedServer, string, string>? OnLoginRequested;
    public event Action<SavedServer, string, string>? OnRegisterRequested;

    // The auth form stays up until the server has answered. Closing it on submit dropped focus back onto
    // the menu, and that focus announcement — spoken with interrupt — cut off the rejection, so a wrong
    // password was indistinguishable from silence. Same defect the GTK head had; same fix.
    private Form? _authForm;
    private TextBox? _authUser;
    private Label? _authStatus;

    // Set immediately before a programmatic focus move whose reason has ALREADY been spoken.
    private bool _suppressFocusSpeech;

    public MenuWindow(ISpeechOutput tts, PersistenceService persistence)
    {
        _tts = tts;
        _persistence = persistence;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        this.Text = "OpenFPS - Main Menu";
        this.Size = new Size(400, 500);
        this.StartPosition = FormStartPosition.CenterScreen;

        FlowLayoutPanel layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };

        Button loginBtn = CreateMenuButton("Login to Default Server", (s, e) => OpenAuthDialog(false));
        Button registerBtn = CreateMenuButton("Register on Default Server", (s, e) => OpenAuthDialog(true));
        Button quitBtn = CreateMenuButton("Quit Game", (s, e) => Application.Exit());

        layout.Controls.Add(loginBtn);
        layout.Controls.Add(registerBtn);
        layout.Controls.Add(quitBtn);

        this.Controls.Add(layout);
        this.Load += (s, e) => _tts.Speak("OpenFPS Main Menu. Use Tab to navigate.");
    }

    private void OpenAuthDialog(bool isRegister)
    {
        _tts.Speak(isRegister ? "Registration Dialog. Enter username, then tab to enter password." : "Login Dialog. Enter username, then password.");
        using (Form authForm = new Form { Text = isRegister ? "Register" : "Login", Size = new Size(320, 260), StartPosition = FormStartPosition.CenterParent })
        {
            // A readable status line so the last outcome can be re-read, rather than existing only as
            // speech that has already gone by.
            Label statusLabel = new Label { Dock = DockStyle.Bottom, Height = 48, Text = "" };
            TextBox userBox = new TextBox { Dock = DockStyle.Top, Text = "admin" };
            TextBox passBox = new TextBox { Dock = DockStyle.Top, Text = "admin123", UseSystemPasswordChar = true };
            Button submit = new Button { Dock = DockStyle.Bottom, Text = isRegister ? "Create Account" : "Login" };

            userBox.GotFocus += (s, e) => SpeakOnFocus("Username");
            passBox.GotFocus += (s, e) => SpeakOnFocus("Password");

            authForm.Controls.Add(statusLabel);
            authForm.Controls.Add(submit);
            authForm.Controls.Add(passBox);
            authForm.Controls.Add(userBox);

            submit.Click += (s, e) => {
                var server = _persistence.SavedServers.Count > 0 ? _persistence.SavedServers[0] : new SavedServer();
                if (isRegister) OnRegisterRequested?.Invoke(server, userBox.Text, passBox.Text);
                else OnLoginRequested?.Invoke(server, userBox.Text, passBox.Text);
                // The form stays open: it closes only once the server has accepted.
            };

            _authForm = authForm; _authUser = userBox; _authStatus = statusLabel;
            try { authForm.ShowDialog(); }
            finally { _authForm = null; _authUser = null; _authStatus = null; }
        }
    }

    /// <summary>Records a connect/login outcome on the still-open auth form and puts focus where the
    /// player can act on it. Called on the UI thread (see ClientNavigationService). The message has
    /// already been spoken by whoever raised it, so the focus move is silenced.</summary>
    public void ReportLoginOutcome(string message, bool success)
    {
        if (_authForm == null || _authForm.IsDisposed) return;
        if (_authStatus != null) _authStatus.Text = message;
        if (success) { _authForm.Close(); return; }
        _suppressFocusSpeech = true;
        _authUser?.Focus();
    }

    private void SpeakOnFocus(string text)
    {
        if (_suppressFocusSpeech) { _suppressFocusSpeech = false; return; }
        _tts.Speak(text);
    }

    private Button CreateMenuButton(string text, EventHandler onClick)
    {
        Button btn = new Button { Text = text, Size = new Size(380, 50), FlatStyle = FlatStyle.System };
        btn.Click += onClick;
        btn.GotFocus += (s, e) => SpeakOnFocus(text);
        return btn;
    }
}
