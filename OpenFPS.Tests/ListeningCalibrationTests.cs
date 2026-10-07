using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Common;
using OpenFPS.Common.Hearing;

namespace OpenFPS.Tests;

/// <summary>
/// The listening-level calibration (docs/EAR_MODEL.md): the keys, the reference voice's gain, and the
/// setting kept in client.json. Every file here is in a temporary folder, never the player's own.
/// </summary>
[Collection(nameof(LevelCompressionSetting))]
public class ListeningCalibrationTests
{
    private sealed class FakeSpeech : ISpeechOutput
    {
        public readonly List<string> Spoken = new();
        public string BackendName => "fake";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) => Spoken.Add(text);
        public void Interrupt() { }
        public void Dispose() { }
    }

    private static void WithLevel(Action body)
    {
        float was = EarModel.ListeningLevelDb;
        try { EarModel.ListeningLevelDb = EarModel.DefaultListeningLevelDb; body(); }
        finally { EarModel.ListeningLevelDb = was; }
    }

    /// <summary>At the default the reference voice plays at exactly the gain the law gives a normal
    /// voice a metre away: the game's own voice, as loud as life.</summary>
    [Fact]
    public void TheReferenceIsTheGamesOwnVoiceAtArmsLength()
    {
        float law = 20f * MathF.Log10(Loudness.Place(Speech.LevelDb(Speech.NormalDb)).Gain);
        float was = Loudness.DynamicRangeCompression;
        try
        {
            Loudness.DynamicRangeCompression = Loudness.DefaultCompression;
            law = 20f * MathF.Log10(Loudness.Place(Speech.LevelDb(Speech.NormalDb)).Gain);
            Assert.Equal(law, ListeningCalibration.ReferenceGainDb(EarModel.DefaultListeningLevelDb), 1);
            // Five decibels quieter headphones: the voice five decibels up.
            Assert.Equal(5f, ListeningCalibration.ReferenceGainDb(EarModel.DefaultListeningLevelDb - 5f)
                             - ListeningCalibration.ReferenceGainDb(EarModel.DefaultListeningLevelDb), 3);
        }
        finally { Loudness.DynamicRangeCompression = was; }
    }

    [Fact]
    public void UpMakesTheVoiceLouderAndEnterSaves()
    {
        WithLevel(() =>
        {
            var speech = new FakeSpeech();
            var played = new List<float>();
            int saves = 0, stops = 0;
            var cal = new ListeningCalibration(speech, null, (_, g) => played.Add(g), () => stops++, () => saves++,
                                               () => ("VOICES/test/line", 2.0));
            cal.Open(0);
            Assert.True(cal.IsOpen);
            cal.Tick(1.0);
            Assert.Empty(played);                         // the prompt first
            cal.Tick(2.0);
            Assert.Single(played);
            Assert.True(cal.HandleKey(GameKey.Up, shift: false, 2.5));
            Assert.Equal(EarModel.DefaultListeningLevelDb - 1f, EarModel.ListeningLevelDb, 3);
            cal.Tick(3.2);                                // said again soon, a decibel louder
            Assert.Equal(2, played.Count);
            Assert.Equal(1f, played[1] - played[0], 3);
            cal.HandleKey(GameKey.Up, shift: true, 4.0);  // Shift: five
            Assert.Equal(EarModel.DefaultListeningLevelDb - 6f, EarModel.ListeningLevelDb, 3);
            Assert.True(cal.HandleKey(GameKey.W, false, 4.1));   // modal: nothing else fires
            cal.HandleKey(GameKey.Enter, false, 5.0);
            Assert.False(cal.IsOpen);
            Assert.Equal(1, saves);
            Assert.Equal(1, stops);
            Assert.Contains(speech.Spoken, s => s.StartsWith("Saved.") && s.Contains("under life"));
        });
    }

    [Fact]
    public void EscapePutsBackWhatWasThere()
    {
        WithLevel(() =>
        {
            EarModel.ListeningLevelDb = 58f;
            int saves = 0;
            var cal = new ListeningCalibration(new FakeSpeech(), null, (_, _) => { }, () => { }, () => saves++, () => ("x", 1.0));
            cal.Open(0);
            cal.HandleKey(GameKey.Down, true, 0.1);
            cal.HandleKey(GameKey.Down, true, 0.2);
            Assert.Equal(68f, EarModel.ListeningLevelDb, 3);
            cal.HandleKey(GameKey.Escape, false, 0.3);
            Assert.Equal(58f, EarModel.ListeningLevelDb, 3);
            Assert.Equal(0, saves);
        });
    }

    [Fact]
    public void TheCommandSetsAndRefuses()
    {
        WithLevel(() =>
        {
            int saves = 0;
            Assert.Null(ListeningCalibration.Command(Array.Empty<string>(), () => saves++));   // opens the flow
            Assert.Contains("Listening level 58", ListeningCalibration.Command(new[] { "58" }, () => saves++));
            Assert.Equal(58f, EarModel.ListeningLevelDb, 3);
            Assert.Contains("from 40 to 90", ListeningCalibration.Command(new[] { "120" }, () => saves++));
            Assert.Contains("not a level", ListeningCalibration.Command(new[] { "loud" }, () => saves++));
            ListeningCalibration.Command(new[] { "default" }, () => saves++);
            Assert.Equal(EarModel.DefaultListeningLevelDb, EarModel.ListeningLevelDb, 3);
            Assert.Equal(2, saves);
        });
    }

    /// <summary>The level is kept in client.json and comes back on the next run.</summary>
    [Fact]
    public void TheListeningLevelIsSaved()
    {
        WithLevel(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "openfps-listening-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "client.json");
            try
            {
                EarModel.ListeningLevelDb = 57f;
                new ClientSettings().Save(path);
                EarModel.ListeningLevelDb = EarModel.DefaultListeningLevelDb;
                var loaded = ClientSettings.Load(path);
                Assert.Equal(57f, loaded.ListeningLevelDb, 3);
                loaded.ApplyHearing();
                Assert.Equal(57f, EarModel.ListeningLevelDb, 3);
                // A file from before the setting existed gets the default.
                File.WriteAllText(path, "{ \"UiSounds\": true }");
                Assert.Equal(EarModel.DefaultListeningLevelDb, ClientSettings.Load(path).ListeningLevelDb, 3);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        });
    }
}
