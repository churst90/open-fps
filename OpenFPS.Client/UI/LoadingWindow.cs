using OpenFPS.Client.Services;

namespace OpenFPS.Client.UI;

/// <summary>
/// The loading screen. Its status line is a read-only text field that holds focus, so NVDA reads it
/// when the window appears and it can be re-read with the screen reader's own keys. Progress is shown,
/// not spoken: a player needs to hear that they are in and where (the session says that on arrival),
/// not every step of the load.
/// </summary>
public sealed class LoadingWindow : Form
{
    private readonly NvdaSpeechOutput _speech;
    private readonly TextBox _status;
    private readonly ProgressBar _progress;

    public LoadingWindow(NvdaSpeechOutput speech)
    {
        _speech = speech;
        Text = "OpenFPS — Loading";
        ClientSize = new Size(460, 140);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        _status = new TextBox
        {
            Dock = DockStyle.Top,
            ReadOnly = true,
            Multiline = true,
            Height = 60,
            AccessibleName = "Loading status",
            TabIndex = 0,
        };
        _progress = new ProgressBar { Dock = DockStyle.Top, Minimum = 0, Maximum = 100, AccessibleName = "Loading progress" };
        Controls.Add(_progress);
        Controls.Add(_status);
        Shown += (_, _) => _status.Focus();
    }

    /// <summary>A new stage of the load: shown, and spoken once.</summary>
    public void ShowStatus(string text)
    {
        _status.Text = text;
        _progress.Value = 0;
        // NVDA reads the field as it takes focus; SAPI has nobody reading it, so it is spoken.
        if (!_speech.ScreenReaderRunning) _speech.Speak(text, interrupt: true);
    }

    public void UpdateStatus(string text, int percent)
    {
        _status.Text = percent > 0 ? $"{text} {percent} percent." : text;
        _progress.Value = Math.Clamp(percent, 0, 100);
    }
}
