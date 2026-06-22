using System.Windows.Forms;
using OpenFPS.Client.Services;
using System.Drawing;
using System;

namespace OpenFPS.Client.UI;

public class MenuWindow : Form
{
    private readonly TolkService _tts;
    private readonly PersistenceService _persistence;

    public event Action<SavedServer, string, string>? OnLoginRequested;
    public event Action<SavedServer, string, string>? OnRegisterRequested;

    public MenuWindow(TolkService tts, PersistenceService persistence)
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
        using (Form authForm = new Form { Text = isRegister ? "Register" : "Login", Size = new Size(300, 200), StartPosition = FormStartPosition.CenterParent })
        {
            TextBox userBox = new TextBox { Dock = DockStyle.Top, Text = "admin" };
            TextBox passBox = new TextBox { Dock = DockStyle.Top, Text = "admin123", UseSystemPasswordChar = true };
            Button submit = new Button { Dock = DockStyle.Bottom, Text = isRegister ? "Create Account" : "Login" };

            userBox.GotFocus += (s, e) => _tts.Speak("Username");
            passBox.GotFocus += (s, e) => _tts.Speak("Password");

            authForm.Controls.Add(submit);
            authForm.Controls.Add(passBox);
            authForm.Controls.Add(userBox);

            submit.Click += (s, e) => {
                var server = _persistence.SavedServers.Count > 0 ? _persistence.SavedServers[0] : new SavedServer();
                if (isRegister) OnRegisterRequested?.Invoke(server, userBox.Text, passBox.Text);
                else OnLoginRequested?.Invoke(server, userBox.Text, passBox.Text);
                authForm.Close();
            };

            authForm.ShowDialog();
        }
    }

    private Button CreateMenuButton(string text, EventHandler onClick)
    {
        Button btn = new Button { Text = text, Size = new Size(380, 50), FlatStyle = FlatStyle.System };
        btn.Click += onClick;
        btn.GotFocus += (s, e) => _tts.Speak(text);
        return btn;
    }
}
