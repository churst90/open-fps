using System;
using System.Globalization;
using System.Linq;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Common;
using OpenFPS.Common.Hearing;

namespace OpenFPS.Client.Core;

/// <summary>
/// Telling the game how loud your headphones are (docs/EAR_MODEL.md, "Playback calibration").
///
/// The ear model keeps a sound's tone right when it plays quieter or louder than it really is, and for
/// that it needs the level at your ears, which only you can hear. So: a person talks to you from one
/// step in front, at the digital level that is a normal speaking voice at arm's length (62.35 dB, ANSI
/// S3.5 normal effort at a metre) if your headphones play as the game assumes, and you set your volume,
/// or move the voice with Up and Down, until that is what it sounds like.
///
/// Up says the voice is too quiet: your headphones play quieter than assumed, the listening level goes
/// down a decibel and the voice comes up a decibel. Down the reverse. Shift with either: five. Space:
/// the line again. R: back to the default. Enter saves; Escape puts back what was there.
///
/// It changes no level in the mix: the system volume is the player's. It changes only how much tone the
/// compensation gives back, and the loudness figures the instruments report.
/// </summary>
public sealed class ListeningCalibration
{
    public const float StepDb = 1f, BigStepDb = 5f;
    /// <summary>The gap between one saying of the line and the next, seconds.</summary>
    public const double GapSeconds = 1.2;

    private readonly ISpeechOutput _speech;
    private readonly UiSounds? _ui;
    private readonly Action<string, float> _play;
    private readonly Action _stop;
    private readonly Action _save;
    private float _was;
    private double _nextAt;

    public ListeningCalibration(ISpeechOutput speech, UiSounds? ui, Action<string, float> play, Action stop, Action save)
    {
        _speech = speech;
        _ui = ui;
        _play = play;
        _stop = stop;
        _save = save;
    }

    public bool IsOpen { get; private set; }

    /// <summary>The line the reference voice says, as the bank's sound id, and how long it lasts.</summary>
    public static (string SoundId, double Seconds) Line()
    {
        // The longest greeting of the first voice: one sentence at normal effort, the same every time.
        var takes = Speech.Takes;
        var take = takes.Where(t => t.Category.StartsWith("greet", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(t => t.Voice, StringComparer.Ordinal).ThenByDescending(t => t.Seconds)
                        .FirstOrDefault() ?? takes.OrderByDescending(t => t.Seconds).FirstOrDefault();
        if (take == null) return ("", 0);
        Speech.TryParseKey(Speech.Key(take.Voice, take.Line), out string id);
        return (id, take.Seconds);
    }

    /// <summary>
    /// The gain, dB, a speech line (buffers sit at <see cref="Speech.BufferRmsDbfs"/>) is played at so
    /// that it reaches the ears at a normal voice's level a metre away, if the headphones play at
    /// <paramref name="listeningLevelDb"/>: the line's full scale is <see cref="Speech.LevelDb"/> of
    /// normal effort, and the playback is the law's nominal plus the listening level's offset.
    /// </summary>
    public static float ReferenceGainDb(float listeningLevelDb)
        => Speech.LevelDb(Speech.NormalDb) - (EarModel.NominalFullScaleDb + listeningLevelDb - Loudness.PivotDb);

    public void Open(double now)
    {
        _was = EarModel.ListeningLevelDb;
        IsOpen = true;
        _ui?.Play(UiCue.MenuSelect);
        _speech.Speak($"Listening level, {Say(EarModel.ListeningLevelDb)}. A person will talk to you from one step in front. "
                    + "Set your volume, or use Up and Down, until they sound like someone talking to you normally at arm's length. "
                    + "Shift with Up or Down moves five. Space repeats, R resets. Enter saves, Escape cancels.", interrupt: true);
        // After the prompt has had a moment: the line comes in under the end of it otherwise.
        _nextAt = now + 1.5;
    }

    /// <summary>Plays the line again when it is due. Game loop.</summary>
    public void Tick(double now)
    {
        if (!IsOpen || now < _nextAt) return;
        var (id, seconds) = Line();
        if (id.Length == 0) return;
        _play(id, ReferenceGainDb(EarModel.ListeningLevelDb));
        _nextAt = now + Math.Max(1.0, seconds) + GapSeconds;
    }

    /// <summary>One key while the calibration is open. True if it was the calibration's.</summary>
    public bool HandleKey(GameKey key, bool shift, double now)
    {
        if (!IsOpen) return false;
        float step = shift ? BigStepDb : StepDb;
        switch (key)
        {
            case GameKey.Up:
                Move(-step, "Louder", now);
                return true;
            case GameKey.Down:
                Move(+step, "Quieter", now);
                return true;
            case GameKey.Space:
                _nextAt = now;
                return true;
            case GameKey.R:
                EarModel.ListeningLevelDb = EarModel.DefaultListeningLevelDb;
                _ui?.Play(UiCue.MenuMove);
                _speech.Speak($"Default, {Say(EarModel.ListeningLevelDb)}.", interrupt: true);
                _nextAt = now + 0.8;
                return true;
            case GameKey.Enter:
                IsOpen = false;
                _stop();
                _save();
                _ui?.Play(UiCue.MenuSelect);
                _speech.Speak($"Saved. {Describe(EarModel.ListeningLevelDb)}", interrupt: true);
                return true;
            case GameKey.Escape:
            case GameKey.Backspace:
                IsOpen = false;
                _stop();
                EarModel.ListeningLevelDb = _was;
                _ui?.Play(UiCue.MenuBack);
                _speech.Speak($"Cancelled. Listening level {Say(_was)}.", interrupt: true);
                return true;
            default:
                return true;   // modal: nothing else fires while it is open
        }
    }

    private void Move(float byDb, string word, double now)
    {
        float before = EarModel.ListeningLevelDb;
        EarModel.ListeningLevelDb = before + byDb;
        if (EarModel.ListeningLevelDb == before) { _ui?.Play(UiCue.MenuEdge); _speech.Speak("No further.", interrupt: true); return; }
        _ui?.Play(UiCue.MenuMove);
        _speech.Speak(word, interrupt: true);
        // The line again soon, at its new level.
        _nextAt = Math.Min(_nextAt, now + 0.6);
    }

    private static string Say(float db) => $"{db.ToString("F0", CultureInfo.InvariantCulture)} decibels";

    /// <summary>What a listening level means, said plainly.</summary>
    public static string Describe(float db)
    {
        float off = db - EarModel.DefaultListeningLevelDb;
        string tail = MathF.Abs(off) < 0.5f ? "your headphones play the game as loud as life at the middle of its range."
            : off < 0 ? $"your headphones play {(-off):F0} decibels under life, and the game gives back more of the bass and treble a quiet sound loses."
            : $"your headphones play {off:F0} decibels over life, and the game takes back some of the bass a loud sound gains.";
        return $"Listening level {db.ToString("F0", CultureInfo.InvariantCulture)}: {tail}";
    }

    /// <summary>
    /// /listening with a value: /listening 65, /listening default. With none the calibration opens
    /// (the caller's business, signalled by null).
    /// </summary>
    public static string? Command(string[] args, Action save)
    {
        if (args.Length == 0) return null;
        string a = args[0].Trim();
        if (a.Equals("default", StringComparison.OrdinalIgnoreCase)) EarModel.ListeningLevelDb = EarModel.DefaultListeningLevelDb;
        else if (float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
        {
            if (v < EarModel.MinListeningLevelDb || v > EarModel.MaxListeningLevelDb)
                return $"Say a listening level from {EarModel.MinListeningLevelDb:F0} to {EarModel.MaxListeningLevelDb:F0} decibels, or default.";
            EarModel.ListeningLevelDb = v;
        }
        else return $"{args[0]} is not a level. Say slash listening and a number from {EarModel.MinListeningLevelDb:F0} to {EarModel.MaxListeningLevelDb:F0}, or default, or nothing to calibrate by ear.";
        save();
        string note = EarModel.ListeningFromEnvironment ? " For this run only: the environment sets it." : "";
        return Describe(EarModel.ListeningLevelDb) + note;
    }

    /// <summary>/ear, /ear on, /ear off: the whole ear model, for listening with and without it.</summary>
    public static string EarCommand(string[] args)
    {
        if (args.Length > 0)
        {
            string a = args[0].ToLowerInvariant();
            if (a is "on") EarModel.Enabled = true;
            else if (a is "off") EarModel.Enabled = false;
            else return "Say /ear on or /ear off.";
        }
        return EarModel.Enabled
            ? "Ear model on: sounds are placed by how loud they sound, and keep their tone at the level they play."
            : "Ear model off: sounds are placed by their level alone, as before October 2026.";
    }
}
