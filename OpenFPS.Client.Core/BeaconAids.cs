using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using Serilog;

namespace OpenFPS.Client.Core;

/// <summary>
/// The beacons a listener hears: a short blip from each of the nearest doors, things to pick up and
/// cars to get into, in whichever categories are on. See <see cref="Beacons"/> for who decides that.
///
/// A blip is a sound IN THE WORLD, at the thing, like any other — so which way it is and how far
/// is heard, not described, and a door round a corner is quieter than one in front of you. Only the
/// nearest few of each kind, and each on its own staggered beat, so a corridor of doors is a few
/// doors near you rather than a wall of beeping.
///
/// Authored beacons (a prefab of Type Beacon, with its own sound) are not played here — they are
/// ordinary emitters — but the same on/off decides whether they are heard (<see cref="IsOn"/>).
/// </summary>
public sealed class BeaconAids
{
    private readonly AudioEngineFacade _audio;
    private readonly BeaconPreferences _prefs;
    private readonly OpenFPS.Client.AudioEngine.Acoustics.SpatialAcoustics? _acoustics;
    private Dictionary<string, Beacons.Policy> _policy = Beacons.ReadPolicies(null);

    private const int BaseId = -967000, Pool = 24;
    private int _idx;
    private bool _registered;
    private readonly Dictionary<int, double> _next = new();

    /// <summary>What each category the client blips for itself sounds like, how far it reaches, and
    /// how many of it are heard at once.</summary>
    private static readonly Dictionary<string, (string Sound, float Hz, float Range, int Nearest)> Kinds = new()
    {
        [Beacons.Door] = ("SYNTH/beacon_door_knock", 420f, 12f, 3),
        [Beacons.Item] = ("SYNTH/beacon_item_bell", 1318f, 10f, 3),
        [Beacons.Vehicle] = ("SYNTH/beacon_vehicle_low", 330f, 25f, 2),
    };

    /// <summary>How often one beacon blips, seconds.</summary>
    private const double Period = 1.6;

    /// <summary>A blip at one metre, dB: a doorbell's worth, well under speech and footsteps' echo.</summary>
    private const float BlipDb = 66f;

    public BeaconAids(AudioEngineFacade audio, BeaconPreferences? prefs = null,
                      OpenFPS.Client.AudioEngine.Acoustics.SpatialAcoustics? acoustics = null)
    {
        _audio = audio;
        _prefs = prefs ?? BeaconPreferences.Load();
        _acoustics = acoustics;
    }

    /// <summary>
    /// How blocked a beacon may be and still blip. A door in the room you are in, or round the corner
    /// of it, is a door you can walk to; one on the far side of a wall is a door in somebody else's
    /// flat, and blipping it through the brick made a corridor sound like one room full of doors —
    /// "I hear other beacons through walls which sound like the same room".
    /// </summary>
    private const float MaxOcclusion = 0.5f;

    /// <summary>The map's policies, from its manifest.</summary>
    public void SetMapPolicy(IEnumerable<string>? entries) => _policy = Beacons.ReadPolicies(entries);

    public Beacons.Policy PolicyFor(string category)
        => _policy.TryGetValue(category, out var p) ? p : Beacons.Unset;

    /// <summary>Whether a category is heard right now.</summary>
    public bool IsOn(string? category)
        => string.IsNullOrEmpty(category) || Beacons.IsOn(PolicyFor(category), _prefs.Choice(category));

    public void Update(WorldSnapshot world, Vector3 listener, double now)
    {
        EnsureSounds();
        if (!_registered) return;
        foreach (var (category, kind) in Kinds)
        {
            if (!IsOn(category)) continue;
            foreach (var (id, at) in Nearest(world, listener, category, kind.Range, kind.Nearest))
            {
                if (!_next.TryGetValue(id, out double due))
                {
                    // First heard: start on a beat of its own, so two doors side by side do not blip
                    // in unison for ever.
                    _next[id] = now + (Math.Abs(id) % 16) / 16.0 * Period;
                    continue;
                }
                if (now < due) continue;
                _next[id] = now + Period;
                Blip(world, id, kind.Sound, at, listener);
            }
        }
    }

    private void Blip(WorldSnapshot world, int sourceId, string sound, Vector3 at, Vector3 listener)
    {
        var (gain, reference) = Loudness.Place(BlipDb);
        // Through the same acoustic path every one-off sound takes: blocked by what is in the way,
        // bent round what it can bend round. The door's own leaf does not block its own blip.
        OpenFPS.Client.AudioEngine.Data.AcousticPathData? path = null;
        if (_acoustics != null)
        {
            try { path = _acoustics.CalculateAcousticPath(world, sourceId, listener, at); } catch { }
            if (path is { } blocked && blocked.Occlusion > MaxOcclusion) return;
        }
        _audio.Submit(new SpatialEmitter
        {
            EntityId = BaseId - (_idx++ % Pool),
            SoundId = sound,
            Mode = OpenFPS.Common.Components.PlaybackMode.Single,
            Position = at,
            ApparentPosition = path?.ApparentPosition ?? at,
            EffectiveDistance = path?.EffectiveDistance ?? Vector3.Distance(listener, at),
            Occlusion = path?.Occlusion ?? 0f,
            ApertureFactor = path?.ApertureFactor ?? 1f,
            TransmissionBleed = path?.TransmissionBleed ?? 0f,
            TargetRegionId = path?.RegionId ?? -1,
            Volume = gain,
            MinDistance = reference,
            Range = 40f,
            IsEvent = true,
            Type = EmitterType.WorldLocked,
            EnableReverb = true,
        });
    }

    /// <summary>The nearest things of a category within reach, and where to blip them from.</summary>
    private static List<(int Id, Vector3 At)> Nearest(WorldSnapshot world, Vector3 listener, string category, float range, int count)
    {
        var found = new List<(int Id, Vector3 At, float D)>();
        void Consider(EntitySnapshot e)
        {
            if (!string.Equals(e.Definition.Identity.BeaconCategory, category, StringComparison.OrdinalIgnoreCase)) return;
            // At the middle of the thing, and at ear height at most: a door's blip comes from the
            // door, not from the floor under it.
            var at = e.Transform.Position;
            float d = Vector3.Distance(listener, at);
            if (d <= range) found.Add((e.Id, at, d));
        }
        if (world.StaticGrid != null)
            foreach (int id in world.StaticGrid.GetItemsInRadius(listener, range))
                if (world.Entities.TryGetValue(id, out var e)) Consider(e);
        // A door leaf that swings, a car that has been driven: things that move are not in the grid.
        foreach (var e in world.DynamicEntities) Consider(e);
        found.Sort((a, b) => a.D.CompareTo(b.D));
        var result = new List<(int, Vector3)>();
        var seen = new HashSet<int>();
        foreach (var f in found)
        {
            if (!seen.Add(f.Id)) continue;
            result.Add((f.Id, f.At));
            if (result.Count >= count) break;
        }
        return result;
    }

    private void EnsureSounds()
    {
        if (_registered) return;
        int rate = TransientSynth.SampleRate;
        bool ok = true;
        foreach (var (category, kind) in Kinds)
        {
            // Three sounds that cannot be taken for anything in the street. The first version was a
            // clean high beep, and a clean high beep repeating by a doorway IS a pedestrian crossing's
            // chirp — which is what it was heard as. So: a door is a soft wooden knock, an item a
            // small bell, a car a low double tone. None of them is a pure beep.
            float[] pcm = category switch
            {
                Beacons.Door => Knock(rate),
                Beacons.Item => Bell(rate),
                _ => Twice(DrivingAids.Beep(rate, 330f, 0.06f, 0.2f), rate),
            };
            ok &= _audio.RegisterSynthesisedSound(kind.Sound, TransientSynth.ToPcm16(pcm), rate);
        }
        _registered = ok;
    }

    /// <summary>A knuckle on a wooden door: two damped modes of a panel, low and short.</summary>
    private static float[] Knock(int rate)
    {
        int n = rate * 90 / 1000;
        var buf = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            buf[i] = 0.6f * MathF.Sin(MathF.Tau * 420f * t) * MathF.Exp(-t / 0.018f)
                   + 0.3f * MathF.Sin(MathF.Tau * 1150f * t) * MathF.Exp(-t / 0.008f);
        }
        return buf;
    }

    /// <summary>A small bell: two inharmonic partials and a ring that dies away.</summary>
    private static float[] Bell(int rate)
    {
        int n = rate * 250 / 1000;
        var buf = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float attack = MathF.Min(1f, t / 0.002f);
            buf[i] = attack * (0.5f * MathF.Sin(MathF.Tau * 1318f * t) * MathF.Exp(-t / 0.09f)
                            + 0.25f * MathF.Sin(MathF.Tau * 3350f * t) * MathF.Exp(-t / 0.04f));
        }
        return buf;
    }

    private static float[] Twice(float[] beep, int rate)
    {
        int gap = rate * 50 / 1000;
        var both = new float[beep.Length * 2 + gap];
        beep.CopyTo(both, 0);
        beep.CopyTo(both, beep.Length + gap);
        return both;
    }

    // ── What the player says ────────────────────────────────────────────────────────────────

    /// <summary>
    /// /beacons — every category and whether it is on, and why. /beacons door — switch doors the
    /// other way. /beacons door on|off — say which.
    /// </summary>
    public string Command(string[] args)
    {
        if (args.Length == 0)
        {
            var parts = new List<string>();
            foreach (var c in Beacons.Categories)
            {
                var p = PolicyFor(c);
                string state = IsOn(c) ? "on" : "off";
                string why = p switch
                {
                    Beacons.Policy.ForcedOn => ", always on for this map",
                    Beacons.Policy.Forbidden => ", not allowed on this map",
                    _ => "",
                };
                parts.Add($"{c} {state}{why}");
            }
            return "Beacons: " + string.Join(". ", parts) + ". Say slash beacons and a name to switch one.";
        }

        string cat = args[0].ToLowerInvariant().TrimEnd('s');
        if (cat == "stair") cat = Beacons.Stairs;
        if (!Beacons.IsCategory(cat))
            return $"There is no beacon called {args[0]}. There are {string.Join(", ", Beacons.Categories)}.";
        var policy = PolicyFor(cat);
        if (!Beacons.PlayerMayChange(policy))
            return policy == Beacons.Policy.ForcedOn
                ? $"This map keeps {cat} beacons on."
                : $"This map does not allow {cat} beacons.";

        bool on = args.Length > 1 ? !args[1].Equals("off", StringComparison.OrdinalIgnoreCase) : !IsOn(cat);
        _prefs.Set(cat, on);
        return $"{char.ToUpper(cat[0])}{cat[1..]} beacons {(on ? "on" : "off")}.";
    }
}

/// <summary>
/// Which beacon categories this player has switched on or off, kept between sessions in
/// $XDG_CONFIG_HOME/openfps/beacons.json (or ~/.config/openfps). A category never touched has no
/// entry, and falls back to the map's default.
/// </summary>
public sealed class BeaconPreferences
{
    private readonly string? _path;
    private readonly Dictionary<string, bool> _choices;

    private BeaconPreferences(string? path, Dictionary<string, bool> choices)
    {
        _path = path;
        _choices = choices;
    }

    /// <summary>An in-memory store that is never written — for tests.</summary>
    public static BeaconPreferences InMemory() => new(null, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));

    public static BeaconPreferences Load()
    {
        string dir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? Path.Combine(x, "openfps")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "openfps");
        string path = Path.Combine(dir, "beacons.json");
        var choices = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path)) ?? new())
                    choices[k] = v;
        }
        catch (Exception ex) { Log.Warning("Beacon preferences at {Path} could not be read: {Error}", path, ex.Message); }
        return new BeaconPreferences(path, choices);
    }

    public bool? Choice(string category) => _choices.TryGetValue(category, out bool on) ? on : null;

    public void Set(string category, bool on)
    {
        _choices[category] = on;
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_choices));
        }
        catch (Exception ex) { Log.Warning("Beacon preferences could not be saved to {Path}: {Error}", _path, ex.Message); }
    }
}
