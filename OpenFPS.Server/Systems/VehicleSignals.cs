using System.Collections.Concurrent;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Systems;

/// <summary>
/// A driven vehicle's horn and siren switches. The horn sounds while every input packet says the key
/// is down (<see cref="OpenFPS.Common.Networking.ClientInputUpdate.Horn"/>); the siren stays on until
/// switched off, driver or not. Clients hear both from EntityState.Signals. Not on a component: a save
/// does not carry them.
/// </summary>
public static class VehicleSignals
{
    private sealed class Switches
    {
        public bool Horn;
        /// <summary>Seconds since the horn key was last reported down; the horn lets go past HoldSeconds.</summary>
        public float HornAge;
        /// <summary>A tap from a text session (/horn): blowing until this runs out, seconds.</summary>
        public float TapLeft;
        public bool SirenOn;
        public SirenMode Tone = SirenMode.Wail;
        /// <summary>The vehicle's entity version: an id taken again by a new vehicle starts with its switches off.</summary>
        public int Version;
    }

    /// <summary>Each world's switches, by vehicle entity id. By world, because ids start again in every
    /// map's world, and a world that goes takes its switches with it.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<World, ConcurrentDictionary<int, Switches>> _byVehicle = new();

    /// <summary>How long a held horn survives without a packet saying so: a few ticks of loss, not a
    /// driver who has gone.</summary>
    public const float HoldSeconds = 0.25f;

    /// <summary>How long /horn blows, seconds: a firm tap.</summary>
    public const float TapSeconds = 0.5f;

    /// <summary>The tones a driver steps through, in order: the three a North American head's switch
    /// has. Hi-lo, the European two-tone, is chosen by name (/siren hilo).</summary>
    private static readonly SirenMode[] Tones = { SirenMode.Wail, SirenMode.Yelp, SirenMode.Phaser };

    private static Switches Of(World world, Entity vehicle)
    {
        var all = _byVehicle.GetValue(world, static _ => new());
        if (all.TryGetValue(vehicle.Id, out var s) && s.Version == vehicle.Version) return s;
        return all[vehicle.Id] = new Switches { Version = vehicle.Version };
    }

    private static bool TryFind(World world, Entity vehicle, out Switches s)
    {
        s = null!;
        return _byVehicle.TryGetValue(world, out var all) && all.TryGetValue(vehicle.Id, out s!) && s.Version == vehicle.Version;
    }

    /// <summary>A driver's input packet: the horn key down or up.</summary>
    public static void Horn(World world, Entity vehicle, bool held)
    {
        if (!held && !TryFind(world, vehicle, out _)) return;
        var s = Of(world, vehicle);
        s.Horn = held;
        if (held) s.HornAge = 0f;
    }

    /// <summary>A half-second blast, for a session that cannot hold a key.</summary>
    public static void Tap(World world, Entity vehicle) => Of(world, vehicle).TapLeft = TapSeconds;

    public static bool HornSounding(World world, Entity vehicle)
        => TryFind(world, vehicle, out var s) && (s.Horn || s.TapLeft > 0f);

    public static (bool On, SirenMode Tone) Siren(World world, Entity vehicle)
        => TryFind(world, vehicle, out var s) ? (s.SirenOn, s.Tone) : (false, SirenMode.Wail);

    /// <summary>Lets go of horns nobody is holding any more. Once a tick.</summary>
    public static void Update(float dt)
    {
        foreach (var (_, all) in _byVehicle)
        foreach (var s in all.Values)
        {
            if (s.Horn)
            {
                s.HornAge += dt;
                if (s.HornAge > HoldSeconds) s.Horn = false;
            }
            if (s.TapLeft > 0f) s.TapLeft = MathF.Max(0f, s.TapLeft - dt);
        }
    }

    /// <summary>
    /// /siren [on|off|wail|yelp|phaser|hilo|next] from the driver's seat of <paramref name="root"/>: the
    /// switch, and what the driver is told. No argument switches it the other way.
    /// </summary>
    public static string SirenCommand(World world, Entity root, string[] args)
    {
        string? head = SirenOf(world, root);
        if (head == null) return "This vehicle has no siren.";
        var s = Of(world, root);
        string word = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "";
        switch (word)
        {
            case "":
            case "toggle":
                s.SirenOn = !s.SirenOn;
                break;
            case "on": s.SirenOn = true; break;
            case "off": s.SirenOn = false; break;
            case "next":
            case "tone":
            case "mode":
            {
                int at = Array.IndexOf(Tones, s.Tone);
                s.Tone = Tones[(at + 1) % Tones.Length];
                if (!s.SirenOn) return $"Siren tone {Name(s.Tone)}; the siren is off.";
                break;
            }
            default:
                if (!Enum.TryParse<SirenMode>(word, ignoreCase: true, out var tone) || tone == SirenMode.Off)
                    return "Say /siren on, off, wail, yelp, phaser, hilo or next.";
                s.Tone = tone;
                s.SirenOn = true;
                break;
        }
        return s.SirenOn ? $"Siren on, {Name(s.Tone)}." : "Siren off.";
    }

    private static string Name(SirenMode m) => m switch
    {
        SirenMode.HiLo => "hi-lo",
        _ => m.ToString().ToLowerInvariant(),
    };

    /// <summary>The siren head a vehicle carries, from its preset; null for none.</summary>
    public static string? SirenOf(World world, Entity root)
    {
        if (!world.Has<DriveComponent>(root)) return null;
        string preset = world.Get<DriveComponent>(root).Preset;
        return !string.IsNullOrEmpty(preset) && MachineRegistry.Knows(preset) ? MachineRegistry.VehicleFor(preset).Siren : null;
    }

    /// <summary>
    /// The byte the wire carries for this entity (EntityState.Signals): zero for anything nobody can
    /// drive, and the driver's switches, marked as a driver's, for anything they can.
    /// </summary>
    public static byte WireByte(World world, Entity e)
    {
        if (!world.Has<DriveComponent>(e)) return 0;
        bool horn = false, siren = false;
        var tone = SirenMode.Wail;
        if (TryFind(world, e, out var s))
        {
            horn = s.Horn || s.TapLeft > 0f;
            siren = s.SirenOn;
            tone = s.Tone;
        }
        return VehicleSignalBits.Encode(manual: true, horn, siren, tone);
    }
}
