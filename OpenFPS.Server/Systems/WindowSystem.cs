using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Systems;

/// <summary>
/// On a vehicle's root: how far down its side windows are, and where they are going. Server-side only;
/// what the clients need of it is <see cref="SoundEmitterComponent.WindowsOpen"/>, the target, from which
/// each of them moves the glass at the same pace.
/// </summary>
public struct WindowsComponent
{
    /// <summary>0 shut, 1 fully down.</summary>
    public float Open;
    public float Target;
}

/// <summary>
/// A vehicle's power windows, rolled from any seat. The glass moves at its motor model's pace
/// (<see cref="CarWindow.Glide"/>), so the sound and the opening arrive together. Every side window moves
/// at once: a player who cannot see which switch is which needs one command for all of them.
/// </summary>
public static class WindowSystem
{
    /// <summary>How many rows of door motors are heard; past the front two, a long cabin's add nothing a
    /// listener could tell apart.</summary>
    private const int HeardRows = 2;

    /// <summary>
    /// /window [down|up|half] — what a seated player asked for, and what they are told. With no word it
    /// rolls them the other way from wherever they are going.
    /// </summary>
    public static string Command(World world, Entity body, IReadOnlyDictionary<int, Entity> lookup, string[] args,
                                 Action<int>? resendDefinition,
                                 Action<int, string, IReadOnlyList<TransientSound>>? heard)
    {
        if (!world.Has<OccupantComponent>(body)) return "You are not sitting in anything.";
        var occupant = world.Get<OccupantComponent>(body);
        if (!lookup.TryGetValue(occupant.RootEntityId, out var root) || !world.IsAlive(root))
            return "There is nothing here with windows.";
        if (Profile(world, root) is not { } profile || CarWindow.Measure(profile) is null)
            return "There are no windows to open on this.";
        float going = world.Has<WindowsComponent>(root) ? world.Get<WindowsComponent>(root).Target : 0f;
        string word = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        float to = word switch
        {
            "" => going > 0f ? 0f : 1f,
            "down" or "open" => 1f,
            "up" or "close" or "shut" => 0f,
            "half" => 0.5f,
            _ => float.NaN,
        };
        if (float.IsNaN(to)) return "Say /window to roll the windows the other way, or /window down, up or half.";
        return Set(world, root, to, resendDefinition, heard);
    }

    /// <summary>The vehicle profile behind a drivable root, or null.</summary>
    private static VehicleProfile? Profile(World world, Entity root)
    {
        if (!world.Has<DriveComponent>(root)) return null;
        string preset = world.Get<DriveComponent>(root).Preset;
        return MachineRegistry.Knows(preset) ? MachineRegistry.VehicleFor(preset) : null;
    }

    /// <summary>
    /// Sends the windows of <paramref name="root"/> toward <paramref name="to"/> open, tells the clients and
    /// plays the motors. Returns what the person at the switch is told.
    /// </summary>
    public static string Set(World world, Entity root, float to, Action<int>? resendDefinition,
                             Action<int, string, IReadOnlyList<TransientSound>>? heard)
    {
        to = Math.Clamp(to, 0f, 1f);
        if (!world.Has<WindowsComponent>(root)) world.Add(root, new WindowsComponent());
        ref var windows = ref world.Get<WindowsComponent>(root);
        if (MathF.Abs(windows.Target - to) < 1e-3f && MathF.Abs(windows.Open - to) < 1e-3f)
            return to >= 1f ? "The windows are already down." : to <= 0f ? "The windows are already up." : "The windows are already half way.";
        float from = windows.Open;
        windows.Target = to;
        if (world.Has<SoundEmitterComponent>(root))
        {
            world.Get<SoundEmitterComponent>(root).WindowsOpen = to;
            // The target lives in the DEFINITION, which is sent once unless something asks again.
            resendDefinition?.Invoke(root.Id);
        }
        if (heard != null && Profile(world, root) is { } profile)
        {
            var sounds = Sounds(world, root, profile, from, to);
            if (sounds.Count > 0) heard(root.Id, "car window", sounds);
        }
        if (to >= 1f) return "You roll the windows down.";
        if (to <= 0f) return "You roll the windows up.";
        return to > from ? "You roll the windows half way down." : "You roll the windows half way up.";
    }

    /// <summary>Every vehicle's windows, moved one tick toward where they are going.</summary>
    public static void Update(World world, float dt)
    {
        var query = new QueryDescription().WithAll<WindowsComponent>();
        world.Query(in query, (ref WindowsComponent w) =>
        {
            if (w.Open != w.Target) w.Open = CarWindow.Glide(w.Open, w.Target, dt);
        });
    }

    /// <summary>
    /// One motor in each door of the front rows, each its own variant (no two the same sound from two
    /// places) and each a few hundredths of a second after the last. Placed inside the door at the waist,
    /// so a listener outside hears it through the body.
    /// </summary>
    internal static List<TransientSound> Sounds(World world, Entity root, VehicleProfile profile, float from, float to)
    {
        var list = new List<TransientSound>();
        if (CarWindow.Quarter(from) == CarWindow.Quarter(to)) return list;
        if (VehicleCabin.Measure(profile) is not { } g || !world.Has<Transform>(root)) return list;
        var t = world.Get<Transform>(root);
        int rows = Math.Min(HeardRows, VehicleCabin.Rows(g));
        float firstRow = g.Front - 0.75f;
        int index = 0;
        for (int r = 0; r < rows; r++)
            foreach (float side in new[] { -1f, 1f })
            {
                var local = new Vector3(side * (g.Wc * 0.5f - 0.1f), g.Belt + 0.05f, firstRow - r * VehicleCabin.RowPitch);
                int variant = (root.Id + index) % CarWindow.Variants;
                list.Add(new TransientSound
                {
                    Character = SoundCharacter.Knock,
                    Position = t.Position + Vector3.Transform(local, t.Rotation),
                    // On the moving vehicle: at speed it has gone a metre by the time the last motor starts.
                    OnBody = true,
                    BodyOffset = local,
                    DelaySeconds = 0.04f * index,
                    LevelDb = CarWindow.LevelDb(variant, CarWindow.Quarter(to)),
                    Hz = 1000f,
                    DecaySeconds = CarWindow.Seconds(CarWindow.Quarter(from), CarWindow.Quarter(to)),
                    Noisiness = 0.3f,
                    SynthKey = CarWindow.Key(variant, from, to),
                });
                index++;
            }
        return list;
    }
}
