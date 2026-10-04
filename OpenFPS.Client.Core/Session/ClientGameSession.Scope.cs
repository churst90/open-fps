using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Core.Session;

/// <summary>
/// The rifle scope, on the numeric keypad, for one hand.
///
///   *            raise or lower it (a scoped rifle in your hands)
///   8 2 4 6      aim up, down, left, right: a tap is a fine step, divided by the power; held, it sweeps
///   5            what is under the crosshair
///   7 9          the previous or next thing in view, said against the crosshair; the aim does not move
///   + -          zoom in and out through the powers
///   1 3          the elevation turret down and up a click, said as the distance it is zeroed for
///   .            the rangefinder
///   0 held       hold your breath: the sway stills for five seconds, then worsens until you breathe
///   / or Enter   fire
///
/// Num Lock must be on: with it off NVDA and Orca take the keypad for their own review keys and the
/// game never sees the digits. Raising the scope says so when it is off. Without a keypad, J L K O aim
/// finely while the scope is up, and /scope, /zoom, /range and /zero do the rest.
/// </summary>
public sealed partial class ClientGameSession
{
    private readonly ScopeController _scope = new();
    private double _guidanceAt;
    private bool _breathHeld;

    /// <summary>The slot the guidance tone plays in.</summary>
    private const string GuidanceSlot = "scope-guidance";
    /// <summary>How often the guidance tone looks at what is in view, seconds.</summary>
    private const double GuidanceEverySeconds = 0.1;
    /// <summary>The guidance tone's and the breath's level against the interface volume.</summary>
    private const float ScopeVolume = 0.7f;

    /// <summary>The scope, for tests.</summary>
    internal ScopeController Scope => _scope;

    private void RegisterScopeBindings()
    {
        _bindings.Bind(InputContext.Gameplay, GameKey.NumpadMultiply, ToggleScope);
        _bindings.Bind(InputContext.Gameplay, GameKey.NumpadAdd, () => ScopeKey(() => _scope.Zoom(+1)));
        _bindings.Bind(InputContext.Gameplay, GameKey.NumpadSubtract, () => ScopeKey(() => _scope.Zoom(-1)));
        _bindings.Bind(InputContext.Gameplay, GameKey.Numpad5, () => ScopeKey(DescribeCrosshair));
        _bindings.Bind(InputContext.Gameplay, GameKey.Numpad7, () => ScopeKey(() => CycleTarget(-1)));
        _bindings.Bind(InputContext.Gameplay, GameKey.Numpad9, () => ScopeKey(() => CycleTarget(+1)));
        _bindings.Bind(InputContext.Gameplay, GameKey.Numpad1, () => ScopeKey(() => _scope.Click(-1)));
        _bindings.Bind(InputContext.Gameplay, GameKey.Numpad3, () => ScopeKey(() => _scope.Click(+1)));
        _bindings.Bind(InputContext.Gameplay, GameKey.NumpadDecimal, () => ScopeKey(RangeCrosshair));
        // Numpad 8 2 4 6 and 0 are read as held keys (GatherLook, UpdateScope), like J K L O.
    }

    /// <summary>A scope key: its answer when the scope is up, and what to do about it when not.</summary>
    private void ScopeKey(Func<string> action)
    {
        if (!_scope.Raised)
        {
            Say(ScopeController.CannotRaise(_state.HeldWeaponId, _state.HeldScopeId, _state.IsRiding)
                ?? "The scope is down. Numpad star raises it.");
            return;
        }
        Say(action());
    }

    /// <summary>Numpad star, and /scope.</summary>
    internal void ToggleScope()
    {
        if (_scope.Raised) { Say(_scope.Lower()); StopGuidance(); return; }
        if (ScopeController.CannotRaise(_state.HeldWeaponId, _state.HeldScopeId, _state.IsRiding) is { } why) { Say(why); return; }
        Say(_scope.Raise(_state.HeldWeaponId, _state.HeldScopeId, _shell.NumLockOn));
        _guidanceAt = 0;
    }

    /// <summary>Where the crosshair points: the head's yaw and pitch with the sway on them. Increasing
    /// yaw turns right and increasing pitch looks DOWN, so sway up takes pitch away.</summary>
    private (float Yaw, float Pitch) Crosshair()
        => _scope.Raised
            ? (_state.Yaw + _scope.SwayNow.Right, Math.Clamp(_state.Pitch - _scope.SwayNow.Up, -1.5f, 1.5f))
            : (_state.Yaw, _state.Pitch);

    private Vector3 EyePosition => _state.Position + new Vector3(0f, _state.EyeHeight, 0f);

    /// <summary>How far anything is looked for: what can be made out at this power, never past the map.</summary>
    private float ScopeReach()
    {
        Vector3 size = _state.MapMax - _state.MapMin;
        float diagonal = MathF.Max(100f, new Vector2(size.X, size.Z).Length());
        return ScopeMath.RecognitionMetres(_scope.Magnification, diagonal);
    }

    /// <summary>What is in the scope's view now.</summary>
    private List<Sighting> LookThrough()
    {
        var (yaw, pitch) = Crosshair();
        return ScopeView.InView(_world.GetSnapshot(), _physics.Spatial, _ownEntityId, EyePosition, yaw, pitch,
                                _scope.FieldOfViewDegrees, ScopeReach());
    }

    private string DescribeCrosshair()
    {
        var (yaw, pitch) = Crosshair();
        return ScopeView.Describe(_world.GetSnapshot(), _physics.Spatial, LookThrough(), EyePosition,
                                  ScopeMath.Forward(yaw, pitch), _state.Position.Y);
    }

    private string RangeCrosshair()
    {
        var (yaw, pitch) = Crosshair();
        return ScopeView.Range(_world.GetSnapshot(), _physics.Spatial, LookThrough(), EyePosition, ScopeMath.Forward(yaw, pitch));
    }

    private string CycleTarget(int direction)
    {
        var (yaw, pitch) = Crosshair();
        return _scope.NextTarget(LookThrough(), direction, ScopeMath.Forward(yaw, pitch));
    }

    /// <summary>The shot through the scope: the crosshair, sway and all, and the turret, to the server.</summary>
    private void FireScoped()
    {
        var (yaw, pitch) = Crosshair();
        _network.Send(new ScopedShot { Yaw = yaw, Pitch = pitch, ElevationMil = _scope.ElevationMil, Magnification = _scope.Magnification });
    }

    /// <summary>
    /// Per fixed step: lowers the scope if the rifle has left the hands, moves the sway on, and plays
    /// the breath when it is caught and let go.
    /// </summary>
    private void UpdateScope(HashSet<GameKey> held, float dt, bool gameplayActive)
    {
        if (!_scope.Raised) { _breathHeld = false; return; }
        if (ScopeController.CannotRaise(_state.HeldWeaponId, _state.HeldScopeId, _state.IsRiding) != null
            || !string.Equals(_scope.Weapon?.Id, _state.HeldWeaponId, StringComparison.OrdinalIgnoreCase))
        {
            _scope.Drop();
            StopGuidance();
            _speech.Speak("Scope down.", interrupt: false);
            return;
        }
        bool hold = gameplayActive && held.Contains(GameKey.Numpad0);
        if (hold != _breathHeld)
        {
            _breathHeld = hold;
            bool inhale = hold;
            if (Ui.Enabled)
                _audioEngine.PlayUiSound(inhale ? "scope:breath-in" : "scope:breath-out",
                    () => ScopeSounds.RenderBreath(inhale), ScopeSounds.SampleRate, Ui.Volume * ScopeVolume);
        }
        float speed = new Vector2(_state.Velocity.X, _state.Velocity.Z).Length();
        _scope.SwayNow = _scope.Sway.Update(dt, _controller.Exertion, speed, hold);
    }

    /// <summary>Render rate: the guidance tone, a few times a second.</summary>
    private void UpdateGuidance()
    {
        if (!_scope.Raised || !_scope.ToneOn || !Ui.Enabled) { StopGuidance(); return; }
        double now = OpenFPS.Common.AudioClock.Now;
        if (now - _guidanceAt < GuidanceEverySeconds) return;
        _guidanceAt = now;
        var g = Guidance.For(LookThrough(), _scope.FieldOfViewDegrees);
        PlayGuidance(g);
    }

    private Guidance _lastGuidance = Guidance.Silent;

    private void PlayGuidance(Guidance g)
    {
        _lastGuidance = g;
        float volume = Ui.Volume * ScopeVolume;
        if (!g.Sounding) { _audioEngine.StopUiLoop(GuidanceSlot); return; }
        if (g.OnTarget)
            _audioEngine.SetUiLoop(GuidanceSlot, "scope:steady", ScopeSounds.RenderSteady, ScopeSounds.SampleRate, volume, 1f);
        else
            _audioEngine.SetUiLoop(GuidanceSlot, "scope:pulse", ScopeSounds.RenderPulse, ScopeSounds.SampleRate, volume,
                                   ScopeSounds.PulseRate(g.Closeness));
    }

    private void StopGuidance()
    {
        if (!_lastGuidance.Sounding) return;
        _lastGuidance = Guidance.Silent;
        _audioEngine.StopUiLoop(GuidanceSlot);
    }

    /// <summary>/scope, /zoom, /range and /zero: the keypad's work for a keyboard without one. Null
    /// when the command is not the scope's.</summary>
    internal string? ScopeCommand(string name, string[] args)
    {
        string a0 = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        switch (name)
        {
            case "scope":
                if (a0 == "tone")
                {
                    string a1 = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                    _scope.ToneOn = a1 switch { "on" => true, "off" => false, _ => !_scope.ToneOn };
                    if (!_scope.ToneOn) StopGuidance();
                    return _scope.ToneOn ? "Guidance tone on." : "Guidance tone off.";
                }
                if (a0 is "up" && _scope.Raised) return "The scope is up.";
                if (a0 is "down" && !_scope.Raised) return "The scope is down.";
                ToggleScope();
                return null;   // ToggleScope has spoken
            case "zoom":
                if (!_scope.Raised) return ScopeController.CannotRaise(_state.HeldWeaponId, _state.HeldScopeId, _state.IsRiding) ?? "The scope is down. /scope raises it.";
                if (a0 is "in" or "+") return _scope.Zoom(+1);
                if (a0 is "out" or "-") return _scope.Zoom(-1);
                if (float.TryParse(a0.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out float power)) return _scope.ZoomTo(power);
                return $"{_scope.Magnification:0.#} power. Say /zoom in, /zoom out, or a power.";
            case "range":
                if (!_scope.Raised) return "The rangefinder is in the scope. /scope raises it.";
                return RangeCrosshair();
            case "zero":
                if (!_scope.Raised) return ScopeController.CannotRaise(_state.HeldWeaponId, _state.HeldScopeId, _state.IsRiding) ?? "The scope is down. /scope raises it.";
                if (a0.Length == 0) return $"{_scope.ZeroWords()}, {_scope.Clicks} clicks up.";
                if (float.TryParse(a0.TrimEnd('m'), NumberStyles.Float, CultureInfo.InvariantCulture, out float metres)) return _scope.ZeroFor(metres);
                return "Say /zero and a distance in metres, such as /zero 400.";
        }
        return null;
    }

    /// <summary>The aim keys while the scope is up: J L K O and the keypad's 4 6 2 8. Same signs as
    /// TurnKeys: a positive x turns left, a positive y looks DOWN.</summary>
    private static readonly (GameKey Key, float X, float Y)[] ScopeAimKeys =
    {
        (GameKey.J, +1f, 0f), (GameKey.Numpad4, +1f, 0f),   // left
        (GameKey.L, -1f, 0f), (GameKey.Numpad6, -1f, 0f),   // right
        (GameKey.K, 0f, +1f), (GameKey.Numpad2, 0f, +1f),   // down
        (GameKey.O, 0f, -1f), (GameKey.Numpad8, 0f, -1f),   // up
    };

    /// <summary>
    /// The look while the scope is up: a tap is <see cref="ScopeMath.TapDegrees"/> at this power and a
    /// held key sweeps at <see cref="ScopeMath.SweepDegreesPerSecond"/>, both divided by the power, so a
    /// tap moves the crosshair the same distance across what you see at any power. No snapping to the
    /// compass grid here: through a scope the target is the grid.
    /// </summary>
    private Vector2 GatherScopeLook(HashSet<GameKey> held, IReadOnlyCollection<GameKey> justPressed, float dt)
    {
        Vector2 look = Vector2.Zero;
        float perTick = PhysicsConstants.RotationSpeed * MathF.Max(dt, 1e-4f);
        float mag = _scope.Magnification;
        foreach (var (key, ax, ay) in ScopeAimKeys)
        {
            if (!held.Contains(key) && !justPressed.Contains(key)) { _turnDownAt.Remove(key); continue; }
            float degrees;
            if (justPressed.Contains(key))
            {
                _turnDownAt[key] = _simTime;
                degrees = ScopeMath.TapDegrees(mag);
            }
            else if (_simTime - _turnDownAt.GetValueOrDefault(key, _simTime) >= TurnHoldBeforeSweep)
                degrees = ScopeMath.SweepDegreesPerSecond(mag) * dt;
            else continue;
            float m = degrees * (MathF.PI / 180f) / perTick;
            look.X += ax * m;
            look.Y += ay * m;
        }
        return look;
    }
}
