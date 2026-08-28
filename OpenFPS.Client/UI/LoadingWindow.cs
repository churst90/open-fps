using System.Windows.Forms;
using System.Drawing;
using System;
using OpenFPS.Client.Services;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.UI;

public class LoadingWindow : Form
{
    private readonly ISpeechOutput _tts;
    private ProgressBar _progressBar = null!;
    private Label _statusLabel = null!;

    public LoadingWindow(ISpeechOutput tts)
    {
        _tts = tts;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        this.Text = "OpenFPS - Loading World";
        this.Size = new Size(500, 300);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33F));

        _statusLabel = new Label 
        { 
            Text = "Preparing to connect...", 
            Dock = DockStyle.Fill, 
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Arial", 14),
            AccessibleName = "Loading Status: Preparing to connect..."
        };

        _progressBar = new ProgressBar 
        { 
            Dock = DockStyle.Fill, 
            Style = ProgressBarStyle.Continuous,
            Minimum = 0,
            Maximum = 100,
            Value = 0 
        };

        layout.Controls.Add(_statusLabel, 0, 0);
        layout.Controls.Add(_progressBar, 0, 1);

        this.Controls.Add(layout);

        this.Load += (s, e) => _tts.Speak("Loading screen. Please wait.");
    }

    public void UpdateStatus(string text, int percent)
    {
        if (this.InvokeRequired)
        {
            this.Invoke(new Action(() => UpdateStatus(text, percent)));
            return;
        }

        _statusLabel.Text = text;
        _statusLabel.AccessibleName = "Loading Status: " + text;
        _progressBar.Value = Math.Clamp(percent, 0, 100);
        
        // Immediate announcement for major status changes
        // Speak only at quarter marks: a thousand-entity map produces a thousand of these, and a
        // screen reader asked to read all of them ends up reading none.
        if (percent % 25 == 0 || percent == 100) _tts.Speak(text, interrupt: false);
    }
}