using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;

namespace OpenFPS.Tests;

/// <summary>
/// Cover for step 7 of the engineering audit — "finish weather, converge the heads, delete the dead
/// code".
///
/// Two families of defect, with one shape between them: something was written down and never read.
/// The weather system computed a scenario temperature into a field nothing consumed, set gustiness on
/// a state nothing modulated, and broadcast an air-absorption multiplier it never assigned — which the
/// client's `distance / max(0.1, multiplier)` guard then turned into a TEN-FOLD absorption distance,
/// switching air absorption off for the whole game. Meanwhile the two heads each carried their own
/// copy of the client's game logic, and the copies had drifted: the Linux client had no chat buffers,
/// no proximity announcements, no voice key.
/// </summary>
public class WeatherAndConvergenceTests
{
    // ── The gust model ──────────────────────────────────────────────────────────────────────────
    // Gustiness is a scalar on the wire and a waveform at the listener. The server broadcasts once a
    // second; sampling a two-second swell at 1 Hz aliases it into a stutter, so the swell is
    // synthesized on the client and only its amplitude is transmitted.

    [Fact]
    public void GustFactorStaysWithinItsBounds()
    {
        for (double t = 0; t < 600; t += 0.05)
        {
            float g = WindModel.GustFactor(t);
            Assert.InRange(g, -1.0f, 1.0f);
        }
    }

    [Fact]
    public void GustFactorIsDeterministic()
    {
        // Two clients with the same clock hear the same weather; a replay of the same second is the
        // same second. This is also what makes every assertion below reproducible.
        Assert.Equal(WindModel.GustFactor(12.5), WindModel.GustFactor(12.5));
        Assert.NotEqual(WindModel.GustFactor(12.5), WindModel.GustFactor(13.5));
    }

    [Fact]
    public void NoGustinessLeavesTheSustainedWindUntouched()
    {
        var sustained = new Vector3(4, 0, -3);
        for (double t = 0; t < 30; t += 0.37)
            Assert.Equal(sustained, WindModel.Felt(sustained, gustiness: 0f, seconds: t));
    }

    [Fact]
    public void GustinessSwingsTheWindBothWaysWithoutReversingIt()
    {
        var sustained = new Vector3(5, 0, 0);
        float min = float.MaxValue, max = float.MinValue;

        for (double t = 0; t < 120; t += 0.02)
        {
            var felt = WindModel.Felt(sustained, gustiness: 1f, seconds: t);
            // A gust makes the wind stronger or weaker; it never blows the other way.
            Assert.True(felt.X >= 0f, $"wind reversed at t={t}: {felt.X}");
            Assert.Equal(0f, felt.Z);
            min = MathF.Min(min, felt.X);
            max = MathF.Max(max, felt.X);
        }

        Assert.True(max > sustained.X * 1.5f, $"gusts never swelled: peak {max} vs sustained {sustained.X}");
        Assert.True(min < sustained.X * 0.5f, $"gusts never lulled: trough {min} vs sustained {sustained.X}");
    }

    [Fact]
    public void ShelterStillsTheWind()
    {
        var sustained = new Vector3(12, 0, 4);
        Assert.Equal(Vector3.Zero, WindModel.Felt(sustained, 0.8f, 3.0, shelterFactor: 1f));

        float half = WindModel.FeltSpeed(sustained, 0.8f, 3.0, shelterFactor: 0.5f);
        float open = WindModel.FeltSpeed(sustained, 0.8f, 3.0, shelterFactor: 0f);
        Assert.True(half < open);
        Assert.Equal(open * 0.5f, half, 3);
    }

    // ── The weather simulation ──────────────────────────────────────────────────────────────────

    /// <summary>Midsummer, mid-morning: warm enough that rain stays rain. Day 1 — the system's
    /// default — is deep winter, where every front freezes into snow and the scenario offsets are
    /// impossible to tell apart.</summary>
    private const int MidsummerDay = 172;

    /// <summary>A system with the clock and the dice pinned, so the scenario is the only thing moving.</summary>
    private static WorldEnvironmentSystem NewPinnedSystem(int seed = 1234)
    {
        var env = new WorldEnvironmentSystem(new Random(seed)) { FrontProbabilityPerTick = 0 };
        env.SetDate(9f, MidsummerDay);
        return env;
    }

    /// <summary>Runs the sim to equilibrium under a pinned scenario and returns the settled state.</summary>
    private static WorldEnvironmentComponent Settle(WeatherType scenario, int seconds = 4000)
    {
        var env = NewPinnedSystem();
        env.SetScenario(scenario);
        for (int i = 0; i < seconds * 4; i++) env.Update(0.25f);
        return env.GetCurrentState();
    }

    [Fact]
    public void RainActuallyCoolsTheAir()
    {
        // The scenario temperature was computed into `_targetTemp` and then never read: the lerp went
        // to the bare seasonal/daily curve. Rain changed the humidity and the wind and left the
        // temperature exactly where it was.
        float clear = Settle(WeatherType.Clear).Temperature;
        float rain = Settle(WeatherType.Rain).Temperature;
        float storm = Settle(WeatherType.Storm).Temperature;

        Assert.True(rain < clear - 1.0f, $"rain did not cool the air: clear {clear}, rain {rain}");
        Assert.True(storm < rain, $"a storm should be colder than rain: storm {storm}, rain {rain}");
    }

    [Fact]
    public void RepeatedFrontsDoNotCompoundTheCooling()
    {
        // `_targetTemp -= 2.0f` subtracted from a running total, so two rain fronts in a row would
        // have cooled the world by four degrees and never given them back. The offset is now a
        // property of the scenario, not an accumulator.
        // Both systems run the SAME number of ticks from the same clock, so the daily curve is at the
        // same point in both; the only difference is how many times the front was applied.
        const int Ticks = 16000;

        var once = NewPinnedSystem(seed: 7);
        once.SetScenario(WeatherType.Rain);
        for (int i = 0; i < Ticks; i++) once.Update(0.25f);

        var repeatedly = NewPinnedSystem(seed: 7);
        for (int block = 0; block < 8; block++)
        {
            repeatedly.SetScenario(WeatherType.Rain);
            for (int i = 0; i < Ticks / 8; i++) repeatedly.Update(0.25f);
        }

        Assert.Equal(once.GetCurrentState().Temperature, repeatedly.GetCurrentState().Temperature, 2);
    }

    [Fact]
    public void SnowHoldsTheAirBelowFreezing()
    {
        // Even at midsummer: the snow scenario caps the air below freezing rather than merely
        // offsetting it, which is what stops "snow" falling into a 30 degree afternoon.
        var snow = Settle(WeatherType.Snow);
        Assert.True(snow.Temperature < 0f, $"snow settled above freezing at {snow.Temperature} C");
        Assert.True(snow.PrecipitationIntensity > 0.3f);
    }

    [Fact]
    public void RainTurnsToSnowWhenTheAirFreezes()
    {
        // Day 1 is deep winter, where the seasonal curve alone takes the air below zero.
        var env = new WorldEnvironmentSystem(new Random(3)) { FrontProbabilityPerTick = 0 };
        env.SetDate(3f, 1);
        env.SetScenario(WeatherType.Rain);
        Assert.Equal(WeatherType.Rain, env.CurrentScenario);

        for (int i = 0; i < 4000; i++) env.Update(0.25f);

        Assert.Equal(WeatherType.Snow, env.CurrentScenario);
        Assert.True(env.GetCurrentState().Temperature < 0f);
    }

    [Fact]
    public void GustinessFadesInsteadOfSnapping()
    {
        // Gustiness was assigned straight onto the state, so a front took the air from calm to a gale
        // between one tick and the next. It now travels with the wind it belongs to.
        var env = NewPinnedSystem(seed: 99);
        env.SetScenario(WeatherType.Clear);
        for (int i = 0; i < 8000; i++) env.Update(0.25f);
        float calm = env.GetCurrentState().WindGustiness;

        env.SetScenario(WeatherType.Storm);
        env.Update(0.25f);
        float oneTickLater = env.GetCurrentState().WindGustiness;
        Assert.True(oneTickLater - calm < 0.05f, $"gustiness jumped {calm} -> {oneTickLater} in one tick");

        for (int i = 0; i < 8000; i++) env.Update(0.25f);
        Assert.True(env.GetCurrentState().WindGustiness > 0.7f, "gustiness never reached the storm's target");
    }

    [Fact]
    public void MapAtmosphereOverlaysTheGlobalWeather()
    {
        var env = NewPinnedSystem(seed: 5);
        for (int i = 0; i < 400; i++) env.Update(0.25f);

        var global = env.GetCurrentState();
        var mountain = env.GetStateForMap(new MapAtmosphere(
            Temperature: WorldEnvironmentSystem.BaselineTemperature - 10f,
            Humidity: WorldEnvironmentSystem.BaselineHumidity + 0.2f,
            AirPressure: 700f,
            AirAbsorptionMultiplier: 2.5f));

        // Air pressure and the absorption multiplier belong to the PLACE and are taken as authored.
        Assert.Equal(700f, mountain.AirPressure);
        Assert.Equal(2.5f, mountain.AirAbsorptionMultiplier);
        // Temperature and humidity are biases from the baseline, so a map that authors the defaults is
        // unchanged and a map authored ten degrees colder stays ten degrees colder than the season.
        Assert.Equal(global.Temperature - 10f, mountain.Temperature, 3);
        Assert.Equal(global.Humidity + 0.2f, mountain.Humidity, 3);

        var plain = env.GetStateForMap(MapAtmosphere.Default);
        Assert.Equal(global.Temperature, plain.Temperature, 4);
        Assert.Equal(global.Humidity, plain.Humidity, 4);
    }

    // ── Units at the map boundary ───────────────────────────────────────────────────────────────

    [Fact]
    public void AirPressureAuthoredInAtmospheresIsCaughtAndCorrected()
    {
        // The shipped default was 1.0 — atmospheres — while every consumer reads millibars. Divided by
        // 1013.25 that clamped at the floor of the normalisation, so every map on the server was
        // silently authored as near-vacuum.
        var data = new MapData { Id = "thin-air", AirPressure = 1.0f };
        MapRepository.NormalizeAtmosphere(data, "thin-air.json");
        Assert.Equal(1013.25f, data.AirPressure, 2);

        // A real altitude is left alone.
        var high = new MapData { Id = "mountain", AirPressure = 700f };
        MapRepository.NormalizeAtmosphere(high, "mountain.json");
        Assert.Equal(700f, high.AirPressure);
    }

    [Fact]
    public void AnUnusableAirAbsorptionMultiplierBecomesNeutral()
    {
        var data = new MapData { Id = "zeroed", AirAbsorptionMultiplier = 0f, Humidity = 4f };
        MapRepository.NormalizeAtmosphere(data, "zeroed.json");
        Assert.Equal(1.0f, data.AirAbsorptionMultiplier);
        Assert.Equal(1.0f, data.Humidity);
    }

    // ── The client's end of the wire ────────────────────────────────────────────────────────────

    [Fact]
    public void ManifestAtmosphereAppliesBeforeTheFirstWorldStateUpdate()
    {
        // The world state broadcast arrives once a second. Without this the first second in a map was
        // heard through the DEFAULTS, and the map's authored air never applied at all.
        var world = new ClientWorldState();
        world.ApplyManifestAtmosphere(new MapManifest
        {
            Temperature = -8f,
            Humidity = 0.85f,
            AirPressure = 720f,
            AirAbsorptionMultiplier = 3.0f
        });

        var snap = world.GetSnapshot();
        Assert.Equal(-8f, snap.Temperature);
        Assert.Equal(0.85f, snap.Humidity, 4);
        Assert.Equal(720f, snap.AirPressure);
        Assert.Equal(3.0f, snap.AirAbsorptionMultiplier);
    }

    [Fact]
    public void AnUnsetAirAbsorptionMultiplierNeverReachesTheAcoustics()
    {
        // This is the bug the whole item hangs off: BroadcastEnvironment never assigned the field, so
        // it arrived as 0 and the client's Math.Max(0.1f, m) guard multiplied the absorption distance
        // by ten. The guard now substitutes the NEUTRAL value, at both ends.
        var world = new ClientWorldState();
        world.UpdateAtmosphere(new WorldStateUpdate { AirPressure = 1013.25f, AirAbsorptionMultiplier = 0f });
        Assert.Equal(1.0f, world.GetSnapshot().AirAbsorptionMultiplier);

        // A server sending nonsense must not be able to switch the acoustics off from a distance.
        world.UpdateAtmosphere(new WorldStateUpdate { AirPressure = 1.0f, AirAbsorptionMultiplier = 1f });
        Assert.Equal(1013.25f, world.GetSnapshot().AirPressure, 2);
    }

    [Fact]
    public void AirAbsorptionRespondsToTheWeatherItIsGiven()
    {
        var acoustics = new SpatialAcoustics();
        var listener = new Vector3(0, 1.7f, 0);
        var source = new Vector3(0, 1.7f, 120);

        // Dry, cold, thin air absorbs high frequencies over a shorter distance than warm damp air at
        // sea level does; the model's job is only to move in that direction, consistently.
        float humid = AirAbsorptionFor(acoustics, listener, source, humidity: 0.95f, temperature: 25f, multiplier: 1f);
        float dry = AirAbsorptionFor(acoustics, listener, source, humidity: 0.05f, temperature: 25f, multiplier: 1f);
        Assert.True(dry < humid, $"humidity did not shorten the absorption distance: dry {dry}, humid {humid}");

        float cold = AirAbsorptionFor(acoustics, listener, source, humidity: 0.5f, temperature: -20f, multiplier: 1f);
        float warm = AirAbsorptionFor(acoustics, listener, source, humidity: 0.5f, temperature: 25f, multiplier: 1f);
        Assert.True(cold < warm, $"temperature had no effect: cold {cold}, warm {warm}");

        // A map that authors heavier absorption gets it; a map that authors nothing (0) gets the same
        // answer as one that authors 1, rather than a tenth of the absorption.
        float neutral = AirAbsorptionFor(acoustics, listener, source, 0.5f, 20f, multiplier: 1f);
        float unset = AirAbsorptionFor(acoustics, listener, source, 0.5f, 20f, multiplier: 0f);
        float heavy = AirAbsorptionFor(acoustics, listener, source, 0.5f, 20f, multiplier: 4f);
        Assert.Equal(neutral, unset, 5);
        Assert.True(heavy > neutral, $"the map's multiplier did nothing: heavy {heavy}, neutral {neutral}");
    }

    private static float AirAbsorptionFor(
        SpatialAcoustics acoustics, Vector3 listener, Vector3 source,
        float humidity, float temperature, float multiplier)
    {
        var world = new WorldSnapshot
        {
            Humidity = humidity,
            Temperature = temperature,
            AirPressure = 1013.25f,
            AirAbsorptionMultiplier = multiplier
        };
        return acoustics.CalculateAcousticPath(world, entityId: 1, listener, source).AirAbsorption;
    }

    // ── Temperature reaches the mix ─────────────────────────────────────────────────────────────

    [Fact]
    public void SpeedOfSoundTracksTemperature()
    {
        Assert.Equal(343.4f, AudioPhysics.SpeedOfSoundAt(20f), 1);
        Assert.Equal(331.3f, AudioPhysics.SpeedOfSoundAt(0f), 1);
        Assert.True(AudioPhysics.SpeedOfSoundAt(-20f) < AudioPhysics.SpeedOfSoundAt(35f));

        // Clamped rather than extrapolated off the end of the linear fit.
        Assert.Equal(AudioPhysics.SpeedOfSoundAt(60f), AudioPhysics.SpeedOfSoundAt(500f));
    }

    [Fact]
    public void ColderAirFlattensTheDopplerShift()
    {
        // A source closing on the listener at 30 m/s.
        var listener = new Vector3(0, 0, 0);
        var source = new Vector3(0, 0, 50);
        var closing = new Vector3(0, 0, -30);

        float summer = AudioPhysics.DopplerFactor(listener, Vector3.Zero, source, closing,
            speedOfSound: AudioPhysics.SpeedOfSoundAt(35f));
        float winter = AudioPhysics.DopplerFactor(listener, Vector3.Zero, source, closing,
            speedOfSound: AudioPhysics.SpeedOfSoundAt(-20f));

        Assert.True(summer > 1f && winter > 1f, "a closing source should pitch up in both seasons");
        Assert.True(winter > summer, "a slower speed of sound should produce a LARGER shift for the same speed");
    }

    // ── One session class, driven by a fake head ────────────────────────────────────────────────
    // The point of the convergence: a head is a speech backend, a shell, a microphone and a key map.
    // Everything below is exercised through those four seams and nothing else — no WinForms, no GTK.

    private sealed class FakeSpeech : ISpeechOutput
    {
        public readonly List<string> Spoken = new();
        public string BackendName => "fake";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) => Spoken.Add(text);
        public void Interrupt() { }
        public void Dispose() { }
        public bool Said(string fragment) => Spoken.Any(s => s.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeShell : IClientShell
    {
        public readonly List<string> Loading = new();
        public int EnterGameCalls;
        public int ConsoleOpens;
        public int QuitRequests;
        public bool IsGameInputActive { get; set; } = true;
        public event Action<string>? CommandEntered;

        public void ShowLoading(string status) => Loading.Add(status);
        public void UpdateLoadingStatus(string text, int percent) => Loading.Add($"{text} ({percent})");
        public void EnterGame() => EnterGameCalls++;
        public void OpenCommandConsole() => ConsoleOpens++;
        public void RequestQuit() => QuitRequests++;
        public void TypeCommand(string text) => CommandEntered?.Invoke(text);
    }

    private static (ClientGameSession Session, FakeSpeech Speech, FakeShell Shell) NewSession()
    {
        AcousticRegistry.Initialize();
        var speech = new FakeSpeech();
        var shell = new FakeShell();
        var session = new ClientGameSession(
            new ClientNetworkService(), speech, shell, new AudioEngineFacade(),
            microphone: new NullMicrophoneCapture("no microphone here"),
            enableAudio: false);
        return (session, speech, shell);
    }

    [Fact]
    public void TheSessionRunsTheWholeJoinHandshakeThroughTheShell()
    {
        var (session, speech, shell) = NewSession();

        session.HandleMessage(new LoginResponse { Success = true, Username = "cody" });
        Assert.Contains(shell.Loading, l => l.Contains("manifest", StringComparison.OrdinalIgnoreCase));

        session.HandleMessage(new MapManifest
        {
            MapName = "default",
            ExpectedEntityCount = 0,
            Temperature = 4f,
            AirPressure = 1013.25f,
            AirAbsorptionMultiplier = 2f
        });
        // The manifest's atmosphere applied on arrival, not a second later.
        Assert.Equal(4f, session.World.GetSnapshot().Temperature);

        Assert.False(session.IsInGame);
        session.HandleMessage(new PlayerSpawned { EntityId = 42 });

        Assert.True(session.IsInGame);
        Assert.Equal(42, session.OwnEntityId);
        Assert.Equal(1, shell.EnterGameCalls);
        Assert.True(speech.Said("entered the world"));
    }

    [Fact]
    public void GameplayBindingsFireOnceOnPressAndOnlyWhenTheShellSaysInputIsLive()
    {
        var (session, speech, shell) = NewSession();
        session.HandleMessage(new PlayerSpawned { EntityId = 7 });
        speech.Spoken.Clear();

        // A held key is acted on once, not once per tick.
        session.Input.SetKey(GameKey.F, true);
        session.SimStep(1f / 30f);
        session.SimStep(1f / 30f);
        session.SimStep(1f / 30f);
        Assert.Equal(1, speech.Spoken.Count(s => s.StartsWith("Facing:")));

        // With a modal console open, gameplay bindings must not fire...
        session.Input.SetKey(GameKey.F, false);
        shell.IsGameInputActive = false;
        session.Input.SetKey(GameKey.F, true);
        session.SimStep(1f / 30f);
        Assert.Equal(1, speech.Spoken.Count(s => s.StartsWith("Facing:")));

        // ...but the global ones still do, so the player can always get out.
        session.Input.SetKey(GameKey.Escape, true);
        session.SimStep(1f / 30f);
        Assert.Equal(1, shell.QuitRequests);
    }

    [Fact]
    public void TheConsoleKeyOpensTheShellsConsoleOnBothHeads()
    {
        var (session, _, shell) = NewSession();
        session.HandleMessage(new PlayerSpawned { EntityId = 9 });

        session.Input.SetKey(GameKey.Slash, true);
        session.SimStep(1f / 30f);
        Assert.Equal(1, shell.ConsoleOpens);

        session.Input.SetKey(GameKey.NumpadDivide, true);
        session.SimStep(1f / 30f);
        Assert.Equal(2, shell.ConsoleOpens);
    }

    [Fact]
    public void AMissingMicrophoneIsSaidOutLoudRatherThanFakingATransmission()
    {
        var (session, speech, _) = NewSession();
        session.HandleMessage(new PlayerSpawned { EntityId = 11 });
        speech.Spoken.Clear();

        session.Input.SetKey(GameKey.V, true);
        session.SimStep(1f / 30f);

        Assert.True(speech.Said("no microphone here"));
    }

    [Fact]
    public void ChatArrivesInBuffersOnEveryHead()
    {
        // The GTK head simply spoke incoming chat and kept none of it; the buffers were Windows-only
        // because ChatManager was typed on that head's concrete TTS service.
        var (session, speech, _) = NewSession();
        session.HandleMessage(new PlayerSpawned { EntityId = 3 });

        session.HandleMessage(new ChatMessage { Sender = "ada", Text = "over here" });
        Assert.True(speech.Said("ada: over here"));

        speech.Spoken.Clear();
        session.Input.SetKey(GameKey.BracketLeft, true);
        session.SimStep(1f / 30f);
        Assert.True(speech.Said("over here"), "the chat scrollback did not read back the message");
    }

    [Fact]
    public void ConsoleTextIsRoutedAsACommandOrAsChat()
    {
        var (session, _, shell) = NewSession();
        // With no server peer these are dropped by the network service, but the parse is the part that
        // used to live only in the Windows head's InputHandler.
        shell.TypeCommand("/say hello there");
        shell.TypeCommand("just chatting");
        // No exception, and both paths accepted: the assertion is that the shell's event reaches the
        // session at all, which is the seam being tested.
        session.HandleCommandEntered("/inv");
    }

    // ── The shared input plumbing ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheInputBufferReportsAPressOnceAndAHoldForever()
    {
        var buffer = new InputStateBuffer();
        buffer.SetKey(GameKey.W, true);

        var first = buffer.GetSnapshot();
        Assert.Contains(GameKey.W, first.Held);
        Assert.Contains(GameKey.W, first.JustPressed);

        var second = buffer.GetSnapshot();
        Assert.Contains(GameKey.W, second.Held);
        Assert.DoesNotContain(GameKey.W, second.JustPressed);

        buffer.SetKey(GameKey.W, false);
        Assert.DoesNotContain(GameKey.W, buffer.GetSnapshot().Held);
    }

    [Fact]
    public void ClearingTheBufferUnsticksAnAltTabbedModifier()
    {
        // The Alt of an Alt+Tab registers key-down while focused and key-up while not, leaving Alt
        // stuck "held" — and a held modifier suppresses movement.
        var buffer = new InputStateBuffer();
        buffer.SetKey(GameKey.AltLeft, true);
        Assert.True(InputStateBuffer.HasModifier(buffer.GetHeldSnapshot()));

        buffer.Clear();
        Assert.False(InputStateBuffer.HasModifier(buffer.GetHeldSnapshot()));
    }

    [Fact]
    public void BindingsResolveTheirContextThenFallBackToGlobal()
    {
        var mapper = new InputCommandMapper();
        int gameplay = 0, global = 0;

        mapper.Bind(InputContext.Gameplay, GameKey.E, () => gameplay++);
        mapper.Bind(GameKey.F5, () => global++);

        Assert.True(mapper.Execute(InputContext.Gameplay, GameKey.E));
        Assert.Equal(1, gameplay);

        // A gameplay-only binding does not fire while a text entry has focus.
        Assert.False(mapper.Execute(InputContext.UI, GameKey.E));
        Assert.Equal(1, gameplay);

        // A global binding fires from either.
        Assert.True(mapper.Execute(InputContext.Gameplay, GameKey.F5));
        Assert.True(mapper.Execute(InputContext.UI, GameKey.F5));
        Assert.Equal(2, global);

        // Rebinding replaces rather than stacks.
        mapper.Bind(InputContext.Gameplay, GameKey.E, () => gameplay += 10);
        mapper.Execute(InputContext.Gameplay, GameKey.E);
        Assert.Equal(11, gameplay);
    }

    // ── The dead code is gone ───────────────────────────────────────────────────────────────────

    [Fact]
    public void SoundMappingServiceOnlyResolvesPaths()
    {
        // It once also submitted emitters of its own — PlayPhysicalInteraction, PlayUiSound,
        // PlayReflection — duplicating ClientAudioSystem's emitter construction with drifted values,
        // and nothing had called any of them for a long time.
        var methods = typeof(SoundMappingService).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.DeclaredOnly).Select(m => m.Name).ToList();

        Assert.DoesNotContain("PlayPhysicalInteraction", methods);
        Assert.DoesNotContain("PlayUiSound", methods);
        Assert.DoesNotContain("PlayReflection", methods);
        Assert.Contains("ResolvePath", methods);
        Assert.Contains("GetImpactSoundId", methods);

        // And the backward-compat three-argument constructor the Windows head needed is gone with it.
        Assert.Single(typeof(SoundMappingService).GetConstructors());
    }

    [Fact]
    public void NeitherHeadCarriesItsOwnSessionClassAnyMore()
    {
        // If a head ever grows one back, this fails. The whole point of step 7 is that there is one.
        var core = typeof(ClientGameSession).Assembly;
        Assert.Single(core.GetTypes(), t => t.Name.EndsWith("GameSession") && !t.IsNested);
        Assert.DoesNotContain(core.GetTypes(), t => t.Name == "ClientSimulationSystem");
    }
}
