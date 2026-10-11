using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Session;

namespace OpenFPS.Client.UI;

/// <summary>
/// The connecting window: while a connect is under way its status line holds focus over whatever form
/// started it, so the menu's arrows and Tab move nothing (Cody, 2026-10-11), and Escape stops the
/// connect. NVDA reads the line as it takes focus; the session has said it already.
/// </summary>
public sealed class ConnectingWindow : Form
{
    private readonly TextBox _status;
    private Action _cancel;
    // Escape is down: a held key repeats, and a repeat landing on the form behind once this has gone
    // would close a connect form with the password typed in it. So this stays until the key is up.
    private bool _escapeDown, _endPending, _closingOurselves;

    public ConnectingWindow(string status, Action cancel)
    {
        _cancel = cancel;
        Text = "Connecting";
        ClientSize = new Size(440, 100);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;

        _status = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Multiline = true,
            AccessibleName = "Connecting",
            Text = status,
        };
        Controls.Add(_status);
        Shown += (_, _) => _status.Focus();
        // Closed from the title bar: the same as Escape.
        FormClosing += (_, e) => { if (!_closingOurselves && e.CloseReason == CloseReason.UserClosing) _cancel(); };
    }

    /// <summary>A new line about the trying, shown.</summary>
    public void SetStatus(string status, Action cancel)
    {
        _cancel = cancel;
        _endPending = false;
        _status.Text = status;
    }

    /// <summary>The connect is over: closes now, or once Escape is up if it is held.</summary>
    public void End()
    {
        if (!_escapeDown) { CloseNow(); return; }
        _endPending = true;
        // A release that never arrives (focus taken by another program) must not leave it up.
        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += (_, _) => { timer.Dispose(); if (_endPending) CloseNow(); };
        timer.Start();
    }

    public void CloseNow()
    {
        _endPending = false;
        if (IsDisposed) return;
        _closingOurselves = true;
        Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        int code = (int)(keyData & Keys.KeyCode);
        // The keypad with Num Lock off is NVDA's review keys, and they reach NVDA untouched.
        if (KeypadKeys.IsNumLockOffKeypad(code, KeypadKeys.IsExtended(msg.LParam.ToInt64())))
            return base.ProcessCmdKey(ref msg, keyData);
        switch (ConnectHold.Decide(WinFormsKeyMap.Map(keyData)))
        {
            case HoldKey.Cancel:
                if (!_escapeDown) { _escapeDown = true; _cancel(); }
                return true;
            case HoldKey.Hold:
                return true;
            default:
                return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            _escapeDown = false;
            if (_endPending) CloseNow();
        }
        base.OnKeyUp(e);
    }
}
