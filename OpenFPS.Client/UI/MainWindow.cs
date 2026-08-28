using System.Windows.Forms;
using OpenFPS.Client.Services;
using OpenFPS.Client.Core.Platform;
using System.Drawing;
using System;

namespace OpenFPS.Client.UI;

public class MainWindow : Form
{
    private readonly ISpeechOutput _tts;
    private Panel _gameArea = null!; 

    public bool IsCommandMode { get; private set; } = false;
    
    // Thread-safe state for the Game Loop
    private bool _isWindowActive = false;
    public bool IsWindowActive => _isWindowActive;

    public event Action<string>? OnCommandEntered;

    public MainWindow(ISpeechOutput tts)
    {
        _tts = tts;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        this.Text = "OpenFPS Accessible Client";
        this.Size = new Size(800, 600);
        this.KeyPreview = true;

        _gameArea = new Panel
        {
            Dock = DockStyle.Fill,
            AccessibleName = "Game Area. W A S D to move. J L K O to look. Space to jump. C for coordinates. Slash or Question Mark for commands.",
            TabStop = false 
        };
        
        this.Controls.Add(_gameArea);

        this.Activated += (s, e) => _isWindowActive = true;
        this.Deactivate += (s, e) => _isWindowActive = false;
        this.Load += (s, e) => { this.Focus(); };
    }

    public void OpenCommandWindow()
    {
        // Guard to prevent multiple dialogs if the key is held
        if (IsCommandMode) return;

        IsCommandMode = true;
        _isWindowActive = false; // Input shouldn't process while this is open
        _tts.Speak("Command Mode. Type command and press enter.");
        
        using (Form cmdForm = new Form { Text = "Command", Size = new Size(400, 120), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
        {
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            TextBox input = new TextBox { Dock = DockStyle.Fill, Font = new Font("Arial", 12) };
            Button send = new Button { Dock = DockStyle.Fill, Text = "Send (Enter)" };
            
            layout.Controls.Add(input, 0, 0);
            layout.Controls.Add(send, 0, 1);
            cmdForm.Controls.Add(layout);

            cmdForm.AcceptButton = send; 
            
            send.Click += (s, e) => { 
                if (!string.IsNullOrWhiteSpace(input.Text)) OnCommandEntered?.Invoke(input.Text); 
                cmdForm.Close(); 
            };

            cmdForm.Load += (s, e) => input.Focus();
            cmdForm.ShowDialog();
        }
        
        IsCommandMode = false;
        _isWindowActive = true;
        this.Focus();
    }
}
