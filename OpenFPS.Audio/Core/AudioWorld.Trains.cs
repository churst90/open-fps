using System.Numerics;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Rail;
using OpenFPS.Audio.Data;
using OpenFPS.Audio.Fmod;
using OpenFPS.Acoustics;

namespace OpenFPS.Audio.Core;

/// <summary>
/// Trains: a handful of voices however long the train (TrainVoicing, RailVoice.cs). The server places
/// one entity per source of a train; none of them is given a voice of its own. The machine budget admits
/// a train as a whole (ChooseLiveMachines); here its sources are grouped by what the ear can tell apart
/// from where the listener stands, each group is given one of the train's voices, placed where its
/// sound comes from, and told which sources it carries at what weight.
/// </summary>
public partial class AudioWorld
{
    /// <summary>How often a train's sources are regrouped, seconds. The weights follow every frame.</summary>
    private const double TrainRegroupSeconds = 0.5;

    /// <summary>How long a train's voice is kept with nothing in it before it is let go, seconds.</summary>
    private const double TrainVoiceHoldSeconds = 4.0;

    private readonly TrainSlotPlan EmptyTrainPlan = new(Array.Empty<int>(), Array.Empty<float>());

    /// <summary>Trains the machine budget admitted this frame, by "preset/train", and their entities.</summary>
    private readonly HashSet<string> _liveTrains = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _wantedTrains = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int[]> _trainMembers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _trainStarted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrainVoicer> _trainVoicers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _trainsDone = new();
    private int _nextTrainOrdinal;

    private sealed class TrainVoicer
    {
        public required string Preset, Name, Key;
        public required int Ordinal;
        public required IReadOnlyList<TrainLayout.Entry> Layout;
        public required float SlotLevelDb;
        public double RegroupedAt = double.NegativeInfinity;
        public int Groups;
        /// <summary>Each source's field voice, as last grouped.</summary>
        public readonly Dictionary<int, int> SlotOf = new();
        /// <summary>Per voice, how far each source has faded in (0..1), mirroring the voice's own fade
        /// (TrainSlotState.FadeSeconds): where the voice is placed follows what it is playing.</summary>
        public readonly Dictionary<int, float>[] Shown = Enumerable.Range(0, TrainVoicing.Slots).Select(_ => new Dictionary<int, float>()).ToArray();
        public readonly bool[] Playing = new bool[TrainVoicing.Slots];
        /// <summary>Since when a playing voice has carried nothing, or -1.</summary>
        public readonly double[] IdleSince = Enumerable.Repeat(-1.0, TrainVoicing.Slots).ToArray();
        public double LastSeen;
        public readonly Dictionary<float, float> Correction = new();
    }

    /// <summary>What a train costs the machine budget in voices: its field voices, as many as it had or
    /// could have, and its horn and bell while they sound.</summary>
    private int TrainVoiceCost(MachineGroup g)
    {
        int field = Math.Min(TrainVoicing.MaxFieldVoices, g.Members.Count);
        if (_trainVoicers.TryGetValue(g.Train!, out var v) && v.Groups > 0) field = Math.Min(field, Math.Max(1, v.Groups));
        int signals = _trainSignals.TryGetValue(g.Train!, out double until) && _now() <= until ? 2 : 0;
        return field + signals;
    }

    /// <summary>A train's voice, by the train's number and the voice's slot (its field voices, then its horn and bell).</summary>
    private int TrainVoiceId(TrainVoicer v, int slot) => _voices.IdFor(VoiceKind.TrainField, v.Ordinal, slot);

    /// <summary>
    /// Places every admitted train's voices and tells each what it carries; lets go of the voices of
    /// trains no longer admitted, a fade first. Once a frame, after ChooseLiveMachines.
    /// </summary>
    private void UpdateTrains(IAcousticWorld world, Vector3 eyePos, float dt)
    {
        double now = _now();
        foreach (string train in _liveTrains)
        {
            if (!_trainMembers.TryGetValue(train, out var members) || members.Length == 0) continue;
            if (!_trainVoicers.TryGetValue(train, out var voicer))
            {
                int slash = train.IndexOf('/');
                var layout = TrainLayoutOf(train[..slash]);
                voicer = new TrainVoicer
                {
                    Preset = train[..slash], Name = train[(slash + 1)..], Key = train,
                    Ordinal = _nextTrainOrdinal++, Layout = layout, SlotLevelDb = TrainVoicing.SlotLevelDb(layout),
                };
                _trainVoicers[train] = voicer;
            }
            voicer.LastSeen = now;
            VoiceTrain(world, eyePos, voicer, members, now, dt);
        }

        // Trains let go: every voice faded, then stopped (the registry's); the voicer forgotten once silent.
        _trainsDone.Clear();
        foreach (var (key, voicer) in _trainVoicers)
        {
            if (_liveTrains.Contains(key)) continue;
            bool any = false;
            for (int slot = 0; slot < TrainVoicing.Slots; slot++)
            {
                if (!StillVoiced(voicer, slot)) continue;
                _voices.Retire(TrainVoiceId(voicer, slot));
                any = true;
            }
            if (!any) _trainsDone.Add(key);
        }
        foreach (var key in _trainsDone)
        {
            _trainVoicers.Remove(key);
            _trainStarted.Remove(key);
        }
    }

    private readonly List<TrainVoicing.Source> _trainSources = new();
    private readonly Dictionary<int, (AudioSource Src, Vector3 At, float Gain)> _trainSourceAt = new();
    private readonly Dictionary<int, float>[] _trainWeights = Enumerable.Range(0, TrainVoicing.Slots).Select(_ => new Dictionary<int, float>()).ToArray();

    /// <summary>Whether a train's voice still sounds or fades: an entry of the registry's, which keeps one
    /// until its fade has ended. Its slot is free once not.</summary>
    private bool StillVoiced(TrainVoicer v, int slot)
    {
        if (v.Playing[slot] && !_voices.IsRegistered(TrainVoiceId(v, slot))) v.Playing[slot] = false;
        return v.Playing[slot];
    }

    private void VoiceTrain(IAcousticWorld world, Vector3 eyePos, TrainVoicer v, int[] members, double now, float dt)
    {
        for (int slot = 0; slot < TrainVoicing.Slots; slot++) StillVoiced(v, slot);
        _trainSources.Clear();
        _trainSourceAt.Clear();
        int warning = TrainSignal.WarningSource(v.Layout), bell = TrainSignal.BellSource(v.Layout);
        int warningEntity = -1, bellEntity = -1, lead = -1, leadIndex = int.MaxValue;
        foreach (int id in members)
        {
            if (!_sources.TryGet(id, out var src)) continue;
            string? soundId = src.Spec.SoundId;
            if (soundId == null || !TrainVoiceState.ParseKey(soundId, out _, out _, out int index)) continue;
            if (index < 0 || index >= v.Layout.Count) continue;
            var entry = v.Layout[index];
            var at = src.Emission;
            float d = Vector3.Distance(at, eyePos);
            // As loud as its own voice would play it, the ear model's correction included.
            float gain = TrainVoicing.LawGain(_audio.Hearing, entry.LevelDb, entry.ExtentMetres, d) * Correction(v, soundId, entry.LevelDb);
            _trainSourceAt[index] = (src, at, gain);
            if (index < leadIndex) { leadIndex = index; lead = id; }
            if (index == warning) { warningEntity = id; continue; }
            if (index == bell) { bellEntity = id; continue; }
            if (entry.IsSignal) continue;
            _trainSources.Add(new TrainVoicing.Source(index, entry.AlongMetres, at));
        }
        if (_trainSources.Count == 0 && warningEntity < 0 && bellEntity < 0) return;

        // The train's speed and effort, from its leading source: the notch its driver has it in, as the server
        // sends it on every source of the train (MachinePower.TrainNotch).
        float speed = 0f, lever = 0f;
        if (lead >= 0 && _sources.TryGet(lead, out var leadSrc))
        {
            speed = leadSrc.State.Velocity.Length();
            lever = leadSrc.State.Lever;
        }

        // Regrouped twice a second: where the groups fall changes as the train moves, not every frame.
        if (now - v.RegroupedAt >= TrainRegroupSeconds || v.Groups == 0)
        {
            v.RegroupedAt = now;
            var groups = TrainVoicing.Group(_trainSources, eyePos, TrainVoicing.MaxFieldVoices, v.Groups);
            var slots = TrainVoicing.AssignSlots(groups, v.SlotOf, v.Playing);
            v.SlotOf.Clear();
            for (int g = 0; g < groups.Count; g++)
                if (slots[g] >= 0) foreach (int i in groups[g]) v.SlotOf[i] = slots[g];
            v.Groups = groups.Count;
        }

        // The fades the voices are making, mirrored: each source heads for one in its voice, nothing in the rest.
        float step = dt / TrainSlotState.FadeSeconds;
        for (int slot = 0; slot < TrainVoicing.MaxFieldVoices; slot++)
        {
            var shown = v.Shown[slot];
            foreach (var (i, s) in v.SlotOf) if (s == slot && !shown.ContainsKey(i)) shown[i] = 0f;
            foreach (int i in shown.Keys.ToList())
            {
                bool here = v.SlotOf.TryGetValue(i, out int s) && s == slot && _trainSourceAt.ContainsKey(i);
                float f = Math.Clamp(shown[i] + (here ? step : -step), 0f, 1f);
                if (f <= 0f && !here) shown.Remove(i); else shown[i] = f;
            }
        }

        for (int slot = 0; slot < TrainVoicing.MaxFieldVoices; slot++)
        {
            var shown = v.Shown[slot];
            // Placed where what it plays comes from: the middle of its sources by the power each brings.
            Vector3 sum = Vector3.Zero;
            float power = 0f, nearest = float.MaxValue;
            AudioSource? rep = null;
            foreach (var (i, f) in shown)
            {
                if (!_trainSourceAt.TryGetValue(i, out var src)) continue;
                float p = f * src.Gain * src.Gain;
                sum += src.At * p;
                power += p;
            }
            bool wanted = v.SlotOf.ContainsValue(slot);
            if (power <= 0f || !wanted && shown.Count == 0)
            {
                // Kept a few seconds with nothing in it before it is let go: as a train rounds a curve the
                // groups merge and split again, and a voice stopped and started for that is a voice
                // coming and going. A held voice is the first a new group takes (AssignSlots).
                if (v.Playing[slot])
                {
                    if (v.IdleSince[slot] < 0) v.IdleSince[slot] = now;
                    _audio.PlanTrainSlot(v.Preset, v.Name, slot, EmptyTrainPlan);
                    if (now - v.IdleSince[slot] > TrainVoiceHoldSeconds) StopTrainVoice(v, slot);
                    // Held empty, it still sounds: an entry as it was declared.
                    else _voices.Keep(TrainVoiceId(v, slot));
                }
                continue;
            }
            v.IdleSince[slot] = -1;
            Vector3 centre = sum / power;
            foreach (var (i, _) in shown)
                if (_trainSourceAt.TryGetValue(i, out var src))
                {
                    float d = Vector3.DistanceSquared(src.At, centre);
                    if (d < nearest) { nearest = d; rep = src.Src; }
                }
            if (rep is null) continue;   // no car of the group has a source this frame
            float slotGain = TrainVoicing.LawGain(_audio.Hearing, v.SlotLevelDb, 0f, Vector3.Distance(centre, eyePos))
                           * Correction(v, rep.Spec.SoundId!, v.SlotLevelDb);
            var weights = _trainWeights[slot];
            weights.Clear();
            foreach (var (i, s) in v.SlotOf)
            {
                if (s != slot || !_trainSourceAt.TryGetValue(i, out var src)) continue;
                weights[i] = TrainVoicing.Weight(v.Layout[i].LevelDb, src.Gain, v.SlotLevelDb, slotGain);
            }
            _audio.PlanTrainSlot(v.Preset, v.Name, slot, new TrainSlotPlan(weights.Keys.ToArray(), weights.Values.ToArray()));
            PlaceTrainVoice(world, eyePos, v, slot, VoiceKind.TrainField, rep, centre, v.SlotLevelDb, 0f, speed, lever);
        }

        // The horn and the bell, each its own voice while it sounds: at full weight, where it is.
        TrainSignalVoice(world, eyePos, v, TrainVoicing.WarningSlot, warning, warningEntity, speed, lever);
        TrainSignalVoice(world, eyePos, v, TrainVoicing.BellSlot, bell, bellEntity, speed, lever);
    }

    private void TrainSignalVoice(IAcousticWorld world, Vector3 eyePos, TrainVoicer v, int slot, int index, int entity,
                                  float speed, float lever)
    {
        bool sounding = index >= 0 && entity >= 0 && _trainSignals.TryGetValue(v.Key, out double until) && _now() <= until;
        if (!sounding || !_trainSourceAt.TryGetValue(index, out var src))
        {
            StopTrainVoice(v, slot);
            return;
        }
        var entry = v.Layout[index];
        _audio.PlanTrainSlot(v.Preset, v.Name, slot, new TrainSlotPlan(new[] { index }, new[] { 1f }));
        PlaceTrainVoice(world, eyePos, v, slot, VoiceKind.TrainSignal, src.Src, src.At, entry.LevelDb, entry.ExtentMetres, speed, lever);
    }

    private void StopTrainVoice(TrainVoicer v, int slot)
    {
        if (!v.Playing[slot]) return;
        _voices.Retire(TrainVoiceId(v, slot));
    }

    private float Correction(TrainVoicer v, string soundId, float levelDb)
    {
        if (v.Correction.TryGetValue(levelDb, out float c)) return c;
        c = MathF.Pow(10f, OpenFPS.Sound.Core.EarTimbres.CorrectionDb(_audio.Hearing, soundId, levelDb) / 20f);
        v.Correction[levelDb] = c;
        return c;
    }

    /// <summary>One of a train's voices, placed at <paramref name="at"/> and heard through the path of the
    /// source nearest there.</summary>
    private void PlaceTrainVoice(IAcousticWorld world, Vector3 eyePos, TrainVoicer v, int slot, VoiceKind kind, AudioSource rep,
                                 Vector3 at, float levelDb, float extent, float speed, float lever)
    {
        int voiceId = TrainVoiceId(v, slot);
        var (gain, reference) = _audio.Hearing.Place(levelDb, extent);
        var e = new SpatialEmitter
        {
            EntityId = voiceId,
            Seed = VoiceRegistry.SeedOf(VoiceKind.TrainField, v.Ordinal, slot),
            SoundId = "rail",
            IsSynth = true,
            PhysicalKey = TrainVoiceState.SlotKey(v.Preset, v.Name, slot),
            EngineKey = "",
            Mode = PlaybackMode.LoopOne,
            Type = EmitterType.EntityAttached,
            Velocity = rep.State.Velocity,
            PositionSampledAt = _sources.SampledAt,
            Direction = Vector3.Transform(Vector3.UnitZ, rep.State.Rotation),
            // A voice carrying many sources renders with more headroom; the difference comes back here.
            Volume = gain * PhysicalVoiceState.HeadroomGain(TrainSlotState.HeadroomDb),
            HeadroomGain = PhysicalVoiceState.HeadroomGain(TrainSlotState.HeadroomDb),
            EarLevelDb = levelDb,
            MinDistance = reference,
            ExtentMetres = extent,
            Range = _audio.Hearing.AudibleRange(levelDb),
            Pitch = 1f,
            EngineRunning = true,
            // The speed in the wake slot and the notch over eight in the lever's, as the server's taps carried them.
            RotorWake = speed,
            PowerLever = lever,
        };
        // Its path every frame, from the registry's pass like every voice of a moving thing. Its ids are not
        // entities', and the path pass once skipped them: a horn begun on a stale outdoor answer played flat
        // through Marlow Tower's brick (2026-10-10, 74 dB in a corridor).
        if (!HandPath(ref e, kind, world, rep, at, levelDb, eyePos)) return;
        ApplyGround(ref e, _groundWorld);
        // A voice let go and still fading is brought back (Sound), not started again.
        _voices.Sound(e);
        v.Playing[slot] = true;
    }

    /// <summary>Every train voice silenced at once (leaving a map).</summary>
    private void ForgetTrains()
    {
        foreach (var voicer in _trainVoicers.Values)
            for (int slot = 0; slot < TrainVoicing.Slots; slot++)
                if (voicer.Playing[slot]) _voices.Stop(TrainVoiceId(voicer, slot));
        _trainVoicers.Clear();
        _liveTrains.Clear();
        _trainStarted.Clear();
        _trainMembers.Clear();
        _trainsSeen.Clear();
    }

    /// <summary>Trains whose model has been asked for ahead (PrepareTrain), by "preset/train".</summary>
    private readonly HashSet<string> _trainsSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A train seen for the first time, wherever it is on the map: its model is made now, off the
    /// threads that place sounds, so it is there when the train is first heard or first blows for a
    /// crossing. Made on its first voice, a freight's held every sound still for 440 ms.</summary>
    private void SeenTrain(string preset, string train)
    {
        if (_trainsSeen.Add(preset + "/" + train)) _audio.PrepareTrain(preset, train);
    }
}
