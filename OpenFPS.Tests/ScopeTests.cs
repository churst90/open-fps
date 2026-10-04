using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The scope: what it sees and how it says it, its keys, its turret and its sounds.
/// </summary>
public class ScopeTests
{
    private static readonly ScopeDefinition Scope = ScopeRegistry.Scope4To12;
    private static readonly Vector3 Eye = new(0f, 1.7f, 0f);

    // ── The view ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFieldOfViewNarrowsWithThePower()
    {
        Assert.Equal(new[] { 4f, 8f, 12f }, Scope.Magnifications);
        Assert.Equal(6f, Scope.FieldOfViewDegrees(4f), 1);
        Assert.Equal(3f, Scope.FieldOfViewDegrees(8f), 1);
        Assert.Equal(2f, Scope.FieldOfViewDegrees(12f), 1);
        // And what can be made out reaches further: a person at 150 m by eye, 600 at 4 power.
        Assert.Equal(600f, ScopeMath.RecognitionMetres(4f));
        Assert.Equal(1800f, ScopeMath.RecognitionMetres(12f));
        Assert.Equal(1000f, ScopeMath.RecognitionMetres(12f, cap: 1000f));
    }

    [Fact]
    public void ATapMovesTheCrosshairOneMagnificationthAsFar()
    {
        Assert.Equal(0.5f / 4f, ScopeMath.TapDegrees(4f), 5);
        Assert.Equal(ScopeMath.TapDegrees(4f) / 3f, ScopeMath.TapDegrees(12f), 5);
        Assert.Equal(ScopeMath.SweepDegreesPerSecond(8f) * 2f, ScopeMath.SweepDegreesPerSecond(4f), 5);
    }

    /// <summary>The keys themselves: a tap of numpad 6 at 4 power and at 12 power, as the session
    /// turns it into the look the physics applies. The 12 power tap is a third of the 4 power one, and
    /// it turns right.</summary>
    [Fact]
    public void ATapOfAnAimKeyTurnsByThePowersShareOfAStep()
    {
        var (session, sent, _, _) = NewClient();
        Spawn(session);
        session.Press(GameKey.NumpadMultiply);
        Assert.True(session.Scope.Raised);

        float At4 = TapDegrees(session, sent, GameKey.Numpad6);
        session.Press(GameKey.NumpadAdd);
        session.Press(GameKey.NumpadAdd);
        Assert.Equal(12f, session.Scope.Magnification);
        float At12 = TapDegrees(session, sent, GameKey.Numpad6);

        Assert.Equal(ScopeMath.TapDegrees(4f), At4, 3);      // a turn to the right
        Assert.Equal(At4 / 3f, At12, 4);
        // J, without a keypad, is the same fine step the other way.
        float j = TapDegrees(session, sent, GameKey.J);
        Assert.Equal(-At12, j, 4);
    }

    /// <summary>The look a tap produced, as the change in yaw in degrees the physics would make
    /// (Yaw -= LookDelta.X * RotationSpeed * dt): positive is a turn to the right.</summary>
    private static float TapDegrees(ClientGameSession session, List<IMessage> sent, GameKey key)
    {
        const float dt = PhysicsConstants.FixedDeltaTime;
        int before = sent.Count;
        session.Input.SetKey(key, true);
        session.SimStep(dt);
        session.Input.SetKey(key, false);
        session.SimStep(dt);
        var look = sent.Skip(before).OfType<ClientInputUpdate>().Select(i => i.LookDelta).First(l => l != Vector2.Zero);
        return -look.X * PhysicsConstants.RotationSpeed * dt * 180f / MathF.PI;
    }

    [Fact]
    public void WhatIsInViewIsWhatIsInsideTheConeAndNotBehindAWall()
    {
        var snap = new WorldSnapshot();
        var ahead = Person(snap, 1, new Vector3(0f, 0f, 200f));                // dead ahead
        var aside = Person(snap, 2, new Vector3(30f, 0f, 200f));               // 8.5 degrees off: outside 6
        var walled = Person(snap, 3, new Vector3(-5f, 0f, 200f));              // 1.4 degrees off, behind a wall
        var glazed = Person(snap, 4, new Vector3(5f, 0f, 200f));               // behind a window: seen
        var tooFar = Person(snap, 5, new Vector3(0f, 0f, 700f));               // past 600 m at 4 power
        // Half way, so across the line to the one behind it and nowhere near the line dead ahead.
        Static(snap, 10, new Vector3(-2.5f, 1.5f, 100f), new Vector3(3f, 3f, 0.3f), "Brick", "garden wall");
        Static(snap, 11, new Vector3(2.5f, 1.5f, 100f), new Vector3(3f, 3f, 0.02f), "Glass", "window");

        var seen = ScopeView.InView(snap, new SpatialService(), -1, Eye, 0f, 0f, Scope.FieldOfViewDegrees(4f), ScopeMath.RecognitionMetres(4f));
        var ids = seen.Select(s => s.Id).ToList();

        Assert.Contains(ahead, ids);
        Assert.Contains(glazed, ids);
        Assert.DoesNotContain(aside, ids);
        Assert.DoesNotContain(walled, ids);
        Assert.DoesNotContain(tooFar, ids);
        // On the crosshair: the one dead ahead, and only that one.
        Assert.Equal(ahead, ScopeView.OnCrosshair(seen)!.Value.Id);
        Assert.Single(seen, s => s.OnCrosshair);
    }

    [Fact]
    public void TargetsAreReadLeftToRightAndSaidAgainstTheCrosshair()
    {
        var snap = new WorldSnapshot();
        // Facing north: +X is right. Left, centre-ish above, right.
        int left = Person(snap, 1, new Vector3(-6f, 0f, 340f), velocity: new Vector3(1.3f, 0f, 0f));
        int middle = Person(snap, 2, new Vector3(2f, 4f, 340f));
        int right = Person(snap, 3, new Vector3(13f, 0f, 340f), velocity: new Vector3(0f, 0f, -1.2f));
        var seen = ScopeView.InView(snap, new SpatialService(), -1, Eye, 0f, 0f, Scope.FieldOfViewDegrees(4f), 600f);
        Assert.Equal(new[] { left, middle, right }, seen.Select(s => s.Id).ToArray());

        var scope = new ScopeController();
        scope.Raise("m700", "scope_4_12", numLockOn: true);
        string first = scope.NextTarget(seen, +1, Vector3.UnitZ);
        // The eye is half a metre above a level chest: everyone level with you is a little below.
        Assert.Equal("person, 340 metres, left and a little below, walking right. 1 of 3.", first);
        string second = scope.NextTarget(seen, +1, Vector3.UnitZ);
        Assert.Equal("person, 340 metres, a little right and above, standing still. 2 of 3.", second);
        string third = scope.NextTarget(seen, +1, Vector3.UnitZ);
        Assert.StartsWith("person, 340 metres, far right", third);
        Assert.Contains("walking towards you", third);
        // Round again, and back.
        Assert.StartsWith("person, 340 metres, left", scope.NextTarget(seen, +1, Vector3.UnitZ));
        Assert.EndsWith("3 of 3.", scope.NextTarget(seen, -1, Vector3.UnitZ));
        Assert.Equal("Nobody in view.", scope.NextTarget(new List<Sighting>(), +1, Vector3.UnitZ));
    }

    [Fact]
    public void TheWordsAreTheOnesASpotterWouldUse()
    {
        Assert.Equal("a little left and above", ScopeMath.RelativeWords(-0.008f, 0.006f, 6f, 0.001f));
        Assert.Equal("on the crosshair", ScopeMath.RelativeWords(0.0005f, -0.0005f, 6f, 0.001f));
        Assert.Equal("far right", ScopeMath.RelativeWords(0.045f, 0f, 6f, 0.001f));
        Assert.Equal("2 metres below", ScopeMath.HeightWords(-2.2f, onRoof: false));
        Assert.Equal("on a roof 9 metres up", ScopeMath.HeightWords(9.1f, onRoof: true));
        Assert.Equal("level with you", ScopeMath.HeightWords(0.3f, onRoof: false));
        Assert.Equal("driving away at 40 km/h", ScopeMath.MovementWords(new Vector3(0f, 0f, 11.1f), Vector3.UnitZ, isVehicle: true));
        Assert.Equal("running left", ScopeMath.MovementWords(new Vector3(-4f, 0f, 0f), Vector3.UnitZ, isVehicle: false));
        Assert.Equal("340 metres", ScopeMath.DistanceWords(343f));
        Assert.Equal("85 metres", ScopeMath.DistanceWords(86f));
        Assert.Equal("12 metres", ScopeMath.DistanceWords(12.4f));
        Assert.Equal("Miss. 40 centimetres low, 15 centimetres left.", CombatSpotter(-0.15f, -0.4f));
        Assert.Equal("Miss. 2.4 metres low.", CombatSpotter(0.02f, -2.4f));
    }

    private static string CombatSpotter(float right, float up) => OpenFPS.Server.Core.CombatService.SpotterCall(right, up);

    [Fact]
    public void FiveSaysWhatTheCrosshairIsOnAndPeriodSaysHowFar()
    {
        var snap = new WorldSnapshot();
        Person(snap, 1, new Vector3(0f, 9f, 300f), velocity: new Vector3(-1.2f, 0f, 0f));
        Static(snap, 10, new Vector3(0f, 8.5f, 300f), new Vector3(10f, 1f, 10f), "Concrete", "10 Elm Street roof");
        Static(snap, 11, new Vector3(30f, 5f, 412.3f), new Vector3(10f, 10f, 1f), "Brick", "Brandt Court");
        var spatial = new SpatialService();
        Vector3 eye = new(0f, 1.7f, 0f);

        // On the person on the roof: aim at their chest.
        Vector3 chest = new(0f, 10.2f, 300f);
        Vector3 f = Vector3.Normalize(chest - eye);
        var seen = ScopeView.InView(snap, spatial, -1, eye, MathF.Atan2(f.X, f.Z), -MathF.Asin(f.Y), 6f, 600f);
        string said = ScopeView.Describe(snap, spatial, seen, eye, f, 0f);
        Assert.Equal("person, 300 metres, on a roof 9 metres up, walking left.", said);
        Assert.Equal("300 metres.", ScopeView.Range(snap, spatial, seen, eye, f));

        // On the wall over to the right: what it is, what it is made of, and the exact range.
        Vector3 w = Vector3.Normalize(new Vector3(30f, 5f, 411.8f) - eye);
        var none = ScopeView.InView(snap, spatial, -1, eye, MathF.Atan2(w.X, w.Z), -MathF.Asin(w.Y), 6f, 600f);
        Assert.Equal("Brandt Court, brick, 410 metres.", ScopeView.Describe(snap, spatial, none, eye, w, 0f));
        Assert.Equal("413 metres.", ScopeView.Range(snap, spatial, none, eye, w));

        // At the sky.
        Assert.Equal("Nothing within 2000 metres.", ScopeView.Describe(snap, spatial, new List<Sighting>(), eye, Vector3.UnitY, 0f));
        Assert.Equal("No reading.", ScopeView.Range(snap, spatial, new List<Sighting>(), eye, Vector3.UnitY));
    }

    // ── The turret ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTurretIsSaidAsTheDistanceItIsZeroedFor()
    {
        var scope = new ScopeController();
        string up = scope.Raise("m700", "scope_4_12", numLockOn: true);
        Assert.Equal("Scope up, 4 power, zeroed for 100 metres.", up);
        Assert.Equal("zeroed for 100 metres", scope.ZeroWords());
        Assert.StartsWith("The turret is at its zero", scope.Click(-1));

        // Each click up pushes the zero out, and never back.
        float last = 100f;
        for (int i = 0; i < 30; i++)
        {
            scope.Click(+1);
            float z = scope.ZeroMetres!.Value;
            Assert.True(z > last, $"click {i + 1} zeroed for {z} after {last}");
            last = z;
        }
        Assert.Equal(30, scope.Clicks);
        Assert.Equal(3.0f, scope.ElevationMil, 3);

        // /zero 400 dials the nearest click to the come-up, and says what that is zeroed for.
        string set = scope.ZeroFor(400f);
        int clicks = (int)MathF.Round(ExternalBallistics.ComeUpMil(WeaponRegistry.M700, 400f, 100f, Scope.SightHeightMetres) / Scope.ClickMil);
        Assert.Equal(clicks, scope.Clicks);
        Assert.InRange(scope.ZeroMetres!.Value, 390f, 410f);
        Assert.Matches(@"^Zeroed for (395|400|405) metres, \d+ clicks up\.$", set);
        // To the top of the turret, and it says so.
        for (int i = 0; i < 300; i++) scope.Click(+1);
        Assert.Equal(scope.MaxClicks, scope.Clicks);
        Assert.StartsWith("The turret is all the way up", scope.Click(+1));
    }

    [Fact]
    public void ZoomStopsAtEachEnd()
    {
        var scope = new ScopeController();
        scope.Raise("m700", "scope_4_12", true);
        Assert.Equal("4 power, the least it goes.", scope.Zoom(-1));
        Assert.Equal("8 power.", scope.Zoom(+1));
        Assert.Equal("12 power.", scope.Zoom(+1));
        Assert.Equal("12 power, the most it goes.", scope.Zoom(+1));
        Assert.Equal("8 power.", scope.ZoomTo(7f));
    }

    // ── Keys, Num Lock and screen readers ─────────────────────────────────────────────────────

    [Fact]
    public void RaisingTheScopeWithNumLockOffSaysSo()
    {
        var (session, _, speech, shell) = NewClient();
        Spawn(session);
        shell.NumLock = false;
        speech.Said.Clear();
        session.Press(GameKey.NumpadMultiply);
        Assert.Contains(speech.Said, s => s.Contains(ScopeController.NumLockWarning));
        session.Press(GameKey.NumpadMultiply);   // down again

        shell.NumLock = true;
        speech.Said.Clear();
        session.Press(GameKey.NumpadMultiply);
        Assert.DoesNotContain(speech.Said, s => s.Contains("Num Lock"));
        Assert.Contains(speech.Said, s => s.StartsWith("Scope up"));
    }

    [Fact]
    public void WithoutAScopedRifleTheScopeSaysWhy()
    {
        var (session, _, speech, _) = NewClient();
        session.HandleMessage(new PlayerSpawned { EntityId = 1 });
        session.HandleMessage(new StatsUpdate { Health = 100, MaxHealth = 100, HeldWeaponId = "akm", HeldRounds = 30 });
        speech.Said.Clear();
        session.Press(GameKey.NumpadMultiply);
        Assert.Equal("The AKM has no scope.", speech.Said.Last());
        session.HandleMessage(new StatsUpdate { Health = 100, MaxHealth = 100 });
        session.Press(GameKey.Numpad5);
        Assert.Equal("You are not holding a rifle.", speech.Said.Last());
    }

    [Fact]
    public void TheScopeKeysAreBoundAndNoneIsAScreenReadersKey()
    {
        var (session, _, _, _) = NewClient();
        var keys = new[]
        {
            GameKey.NumpadMultiply, GameKey.NumpadAdd, GameKey.NumpadSubtract, GameKey.NumpadDecimal,
            GameKey.Numpad1, GameKey.Numpad3, GameKey.Numpad5, GameKey.Numpad7, GameKey.Numpad9,
        };
        foreach (var k in keys)
        {
            Assert.True(session.IsBound(InputContext.Gameplay, k), $"{k} does nothing");
            Assert.DoesNotContain(k, ClientGameSession.ScreenReaderKeys);
        }
        foreach (var k in new[] { GameKey.Numpad0, GameKey.Numpad2, GameKey.Numpad4, GameKey.Numpad6, GameKey.Numpad8, GameKey.NumpadDivide })
            Assert.DoesNotContain(k, ClientGameSession.ScreenReaderKeys);
    }

    [Fact]
    public void ThroughTheScopeTheTriggerSendsTheCrosshairAndTheTurret()
    {
        var (session, sent, _, _) = NewClient();
        Spawn(session);
        session.Press(GameKey.NumpadMultiply);
        session.Press(GameKey.Numpad3);
        session.Press(GameKey.Numpad3);
        int before = sent.Count;
        session.Press(GameKey.Enter);
        var shot = Assert.Single(sent.Skip(before).OfType<ScopedShot>());
        Assert.Equal(0.2f, shot.ElevationMil, 4);
        Assert.Equal(4f, shot.Magnification);
        Assert.DoesNotContain(sent.Skip(before), m => m is TextCommand { Command: "fire" });

        // Down again, Enter is the ordinary trigger.
        session.Press(GameKey.NumpadMultiply);
        before = sent.Count;
        session.Press(GameKey.Enter);
        Assert.Contains(sent.Skip(before), m => m is TextCommand { Command: "fire" });
    }

    [Fact]
    public void ScopeCommandsDoTheKeypadsWork()
    {
        var (session, _, speech, _) = NewClient();
        Spawn(session);
        session.HandleCommandEntered("/scope");
        Assert.True(session.Scope.Raised);
        session.HandleCommandEntered("/zoom 12");
        Assert.Equal(12f, session.Scope.Magnification);
        session.HandleCommandEntered("/zero 600");
        Assert.InRange(session.Scope.ZeroMetres!.Value, 590f, 610f);
        session.HandleCommandEntered("/scope tone off");
        Assert.False(session.Scope.ToneOn);
        Assert.Equal("Guidance tone off.", speech.Said.Last());
    }

    // ── The breath ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AHeldBreathStillsTheSwayForFiveSecondsAndThenItShakes()
    {
        var sway = new ScopeSway();
        float Peak(float seconds, bool hold, float exertion = 0f)
        {
            float peak = 0f;
            for (float t = 0f; t < seconds; t += 0.01f)
            {
                var (r, u) = sway.Update(0.01f, exertion, 0f, hold);
                peak = MathF.Max(peak, MathF.Sqrt(r * r + u * u));
            }
            return peak;
        }
        float breathing = Peak(8f, hold: false);
        Peak(0.8f, hold: true);                       // the breath caught
        float held = Peak(3.5f, hold: true);           // 0.8 to 4.3 s of the hold
        Peak(1.5f, hold: true);                        // to 5.8 s, past the limit
        float strained = Peak(2f, hold: true);         // 5.8 to 7.8 s
        Assert.True(held < 0.15f * breathing, $"held {held * 1000:F2} mil against {breathing * 1000:F2} breathing");
        Assert.True(strained > breathing, $"held too long {strained * 1000:F2} mil against {breathing * 1000:F2}");
        // Winded is worse than rested.
        var rested = new ScopeSway(); var winded = new ScopeSway();
        float a = 0f, b = 0f;
        for (int i = 0; i < 800; i++)
        {
            var (r1, u1) = rested.Update(0.01f, 0f, 0f, false); a = MathF.Max(a, MathF.Abs(u1));
            var (r2, u2) = winded.Update(0.01f, 0.9f, 0f, false); b = MathF.Max(b, MathF.Abs(u2));
        }
        Assert.True(b > 2f * a);
    }

    // ── The sounds ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheGuidanceToneRisesAsTheCrosshairNearsATargetAndHoldsOnIt()
    {
        static Sighting At(float right, bool on) => new(1, SightKind.Person, "person", Vector3.Zero, Vector3.Zero, Vector3.Zero,
                                                       300f, right, 0f, on, MathF.Atan2(0.225f, 300f));
        float fov = 6f;
        var far = Guidance.For(new[] { At(0.045f, false) }, fov);
        var near = Guidance.For(new[] { At(0.004f, false) }, fov);
        var on = Guidance.For(new[] { At(0.0002f, true) }, fov);
        Assert.True(far.Sounding && !far.OnTarget);
        Assert.True(near.Closeness > far.Closeness);
        Assert.True(ScopeSounds.PulseRate(near.Closeness) > ScopeSounds.PulseRate(far.Closeness));
        Assert.True(on.OnTarget);
        Assert.False(Guidance.For(Array.Empty<Sighting>(), fov).Sounding);
        // At its top the pulse is the held note: the pulses run together into it.
        Assert.Equal(ScopeSounds.SteadyHz, ScopeSounds.PulseHz * ScopeSounds.PulseRate(1f), 2);
    }

    [Fact]
    public void TheScopeSoundsAreQuietAndClickFree()
    {
        foreach (var (name, x, loops) in new (string, float[], bool)[]
                 {
                     ("pulse", ScopeSounds.RenderPulse(), true), ("steady", ScopeSounds.RenderSteady(), true),
                     ("breath in", ScopeSounds.RenderBreath(true), false), ("breath out", ScopeSounds.RenderBreath(false), false),
                 })
        {
            float peak = x.Max(MathF.Abs);
            Assert.True(20f * MathF.Log10(peak) < -10f, $"{name} peaks at {20f * MathF.Log10(peak):F1} dBFS");
            if (loops)
            {
                // A tone: no step bigger than a smooth waveform at this level makes, including across
                // the loop's seam, where a click would come round every pulse.
                float maxStep = MathF.Abs(x[0] - x[^1]);
                for (int i = 1; i < x.Length; i++) maxStep = MathF.Max(maxStep, MathF.Abs(x[i] - x[i - 1]));
                Assert.True(maxStep < 0.25f * peak, $"{name} steps {maxStep / peak:F2} of its peak in one sample");
            }
            else
            {
                // Air: it swells from nothing and dies to nothing, with no onset to hear as a knock.
                Assert.True(MathF.Abs(x[0]) < 1e-4f && MathF.Abs(x[^1]) < 1e-3f, $"{name} starts or ends off zero");
                int fiveMs = ScopeSounds.SampleRate / 200;
                float start = x.Take(fiveMs).Max(MathF.Abs);
                Assert.True(start < 0.05f * peak, $"{name} is at {start / peak:F2} of its peak within 5 ms");
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static int Person(WorldSnapshot snap, int id, Vector3 feet, Vector3 velocity = default)
    {
        var def = new EntityDefinition { EntityId = id, Type = EntityType.NPC, Moves = true };
        def.Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.5f, 1.8f, 0.5f), IsSolid = false };
        def.Identity = new IdentityComponent { Name = "someone walking" };
        var e = new EntitySnapshot { Id = id, Definition = def, Transform = new Transform { Position = feet, Rotation = Quaternion.Identity }, Velocity = velocity };
        snap.Entities[id] = e;
        snap.DynamicEntities.Add(e);
        return id;
    }

    private static void Static(WorldSnapshot snap, int id, Vector3 centre, Vector3 size, string material, string name)
    {
        var def = new EntityDefinition { EntityId = id, Type = EntityType.StaticObject };
        def.Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true };
        def.Identity = new IdentityComponent { Name = name };
        def.Material = new MaterialComponent { Material = material };
        snap.Entities[id] = new EntitySnapshot { Id = id, Definition = def, Transform = new Transform { Position = centre, Rotation = Quaternion.Identity } };
    }

    private static void Spawn(ClientGameSession session)
    {
        session.HandleMessage(new PlayerSpawned { EntityId = 1, SpawnTransform = new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity } });
        session.HandleMessage(new StatsUpdate { Health = 100, MaxHealth = 100, HeldWeaponId = "m700", HeldRounds = 5, HeldScopeId = "scope_4_12" });
    }

    private static (ClientGameSession, List<IMessage>, Speech, Shell) NewClient()
    {
        AcousticRegistry.Initialize();
        var network = new ClientNetworkService();
        var sent = new List<IMessage>();
        network.Sending = sent.Add;
        var speech = new Speech();
        var shell = new Shell();
        var session = new ClientGameSession(network, speech, shell, new AudioEngineFacade(),
            microphone: new NullMicrophoneCapture("none"), enableAudio: false);
        return (session, sent, speech, shell);
    }

    private sealed class Speech : ISpeechOutput
    {
        public readonly List<string> Said = new();
        public string BackendName => "test";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) => Said.Add(text);
        public void Interrupt() { }
        public void Dispose() { }
    }

    private sealed class Shell : IClientShell
    {
        public bool? NumLock = true;
        public bool IsGameInputActive { get; set; } = true;
        public bool? NumLockOn => NumLock;
        public event Action<string>? CommandEntered { add { } remove { } }
        public void ShowLoading(string status, bool speak = true) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() { }
        public void OpenCommandConsole() { }
        public void ShowGameMenu(Action<GameMenuChoice> chosen) { }
        public void ReturnToMenu() { }
        public void Quit() { }
    }
}
