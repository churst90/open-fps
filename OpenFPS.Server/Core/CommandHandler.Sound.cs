using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
using Arch.Core;

namespace OpenFPS.Server.Core;

/// <summary>The sound tools (/set_sound, /set_audio_mode, /play_folder, /start_state) and the clap.</summary>
public partial class CommandHandler
{
    private void HandleSetAudioMode(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 1) { Say(reply, $"Usage: /set_audio_mode [{string.Join("|", Enum.GetNames<PlaybackMode>())}]"); return; }
        if (!Enum.TryParse<PlaybackMode>(args[0], true, out var mode)) { Say(reply, $"Unknown playback mode '{args[0]}'."); return; }
        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        var nearest = FindNearestObject(world, session.Entity, playerPos);
        if (nearest == null) { Say(reply, "No object nearby."); return; }

        var emitter = world.Has<SoundEmitterComponent>(nearest.Value) ? world.Get<SoundEmitterComponent>(nearest.Value) : new SoundEmitterComponent();
        emitter.Mode = mode;
        SetOrAdd(world, nearest.Value, emitter);
        _server.SyncAudioComponent(nearest.Value.Id);

        Say(reply, $"Set audio mode of nearest object to {mode}.");
    }

    private void HandlePlayFolder(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 1) { Say(reply, "Usage: /play_folder [FolderId]"); return; }
        if (!SafeText.IsSoundId(args[0])) { Say(reply, NotASoundId(args[0])); return; }
        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        var nearest = FindNearestObject(world, session.Entity, playerPos);
        if (nearest == null) { Say(reply, "No object nearby."); return; }

        var emitter = world.Has<SoundEmitterComponent>(nearest.Value) ? world.Get<SoundEmitterComponent>(nearest.Value) : new SoundEmitterComponent();
        emitter.SoundId = args[0];
        emitter.Mode = PlaybackMode.LoopFolder;
        SetOrAdd(world, nearest.Value, emitter);
        _server.SyncAudioComponent(nearest.Value.Id);

        Say(reply, $"Nearest object is now playing folder: {args[0]}.");
    }

    private void HandleStartState(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 2) { Say(reply, "Usage: /start_state [StartSoundId] [LoopSoundId]"); return; }
        if (!SafeText.IsSoundId(args[0]) || !SafeText.IsSoundId(args[1])) { Say(reply, NotASoundId(SafeText.IsSoundId(args[0]) ? args[1] : args[0])); return; }
        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        var nearest = FindNearestObject(world, session.Entity, playerPos);
        if (nearest == null) { Say(reply, "No object nearby."); return; }

        var emitter = world.Has<SoundEmitterComponent>(nearest.Value) ? world.Get<SoundEmitterComponent>(nearest.Value) : new SoundEmitterComponent();
        emitter.StartSoundId = args[0];
        emitter.SoundId = args[1];
        emitter.Mode = PlaybackMode.StateMachine;
        SetOrAdd(world, nearest.Value, emitter);
        _server.SyncAudioComponent(nearest.Value.Id);

        Say(reply, $"Started state machine on nearest object: {args[0]} -> {args[1]}.");
    }

    private static void SetOrAdd<T>(World world, Entity e, T component) where T : struct
    {
        if (world.Has<T>(e)) world.Set(e, component);
        else world.Add(e, component);
    }

    private static Entity? FindNearestObject(World world, Entity self, Vector3 playerPos)
    {
        Entity? nearest = null;
        float minDist = PhysicsConstants.InteractionRange;
        world.Query(new QueryDescription().WithAll<Transform, IdentityComponent>(), (Entity e, ref Transform t) => {
            if (e == self) return;
            float d = Vector3.Distance(playerPos, t.Position);
            if (d < minDist) { minDist = d; nearest = e; }
        });
        return nearest;
    }

    /// <summary>Every client on the map opens a sound id as a file, so only a relative name under its sounds folder.</summary>
    private static string NotASoundId(string id)
        => $"'{AuthService.ForLog(id)}' is not a sound id: a name under the sounds folder, like AMBIENCE/woods_mid_day.";

    private void HandleSetSound(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 2)
        {
            Say(reply, "Usage: /set_sound [SoundId] [Volume]");
            return;
        }

        if (!SafeText.IsSoundId(args[0])) { Say(reply, NotASoundId(args[0])); return; }
        if (!SafeText.TryVolume(args[1], out float vol)) { Say(reply, $"'{args[1]}' is not a volume: a number from 0 to 4."); return; }
        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        var nearest = FindNearestObject(world, session.Entity, playerPos);
        if (nearest == null)
        {
            Say(reply, "No object nearby to attach sound.");
            return;
        }

        // SetOrAdd, not Add: Add throws on an entity that already carries the component, which is what
        // every sibling handler here already knew.
        SetOrAdd(world, nearest.Value, new SoundEmitterComponent { SoundId = args[0], Volume = vol, Range = 50.0f });
        SetOrAdd(world, nearest.Value, EntityType.Beacon);
        _server.SyncAudioComponent(nearest.Value.Id);

        Say(reply, $"Attached sound {args[0]} to nearest object.");
    }

    /// <summary>Clapping your hands: one clap, in front of your chest, heard by everyone near and
    /// answered by the walls like any other short sound. Nothing is said back.</summary>
    private void HandleClap(UserSession session, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;
        var forward = Vector3.Transform(new Vector3(0, 0, 1), world.Get<Transform>(session.Entity).Rotation);
        _server.EmitWorldAudio(session.CurrentMapId, session.Entity.Id, "clap", new[]
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock,
                Position = position + new Vector3(0, 1.25f, 0) + forward * 0.3f,
                OnBody = true, BodyOffset = new Vector3(0f, 1.25f, 0.3f),
                LevelDb = Applause.SingleClapDb,
                SynthKey = Applause.ClapKey,
                DecaySeconds = 0.15f,
                Noisiness = 1f,
            },
        });
    }
}
