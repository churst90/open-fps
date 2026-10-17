using System.Collections.Concurrent;
using OpenFPS.Audio.Data;
using OpenFPS.Common;
using OpenFPS.Sound;

namespace OpenFPS.Audio.Fmod;

/// <summary>
/// Makes the models that take longer than a frame to make, away from the threads that place sounds
/// (PlacementThread). A siren head, a bell or a horn renders itself to set its own level when it is made
/// (70-100 ms for a patrol siren, 13-30 for a crossing's gong, 10-25 for a car horn), and a train's model
/// calibrates every bogie (440 ms for a fifty-wagon freight): made where the voice started, every source
/// on the map held still that long, and a train's first horn for a crossing froze the street for 0.8 s.
///
/// What the map holds that waits for a signal is made ahead (Prepare, and the provider's PrepareTrain): a
/// train as soon as it is seen, a crossing's bell and a siren while they are switched off and within
/// earshot, so they start when the signal comes. Anything else is made when its voice is first asked for
/// and starts when next asked for once made (every voice is asked for again each frame): late by its
/// making, as it was when made in place, with nothing else held up meanwhile. Made the same way on another
/// thread, every model renders exactly as it did.
/// </summary>
public sealed class VoiceBuilds
{
    /// <summary>The kinds made here: each made from its key, its seed and the rate alone, so it can be
    /// made anywhere and ahead. The rest are made where they start: they are cheap (a gate, a hob) or
    /// made from other voices' state (a tree's outer places, the rain's feeds, a train's voices).</summary>
    public static bool Makes(ModelSynth? kind) => kind is ModelSynth.Siren or ModelSynth.Bell or ModelSynth.Horn
        or ModelSynth.Machine or ModelSynth.Aircraft;

    /// <summary>What a model is made from.</summary>
    private readonly record struct Recipe(ModelSynth Kind, string Key, int Seed, float Lever, int Rate);

    /// <summary>The model a voice of this kind starts with, made now on the calling thread.</summary>
    internal static PhysicalVoiceState? Make(ModelSynth kind, string physicalKey, int seed, float lever, float rate, IAudioClock clock,
                                             OpenFPS.Sound.Core.Atmosphere? atmosphere = null,
                                             OpenFPS.Sound.Core.MixSettings? mix = null)
    {
        var made = MakeAlone(kind, physicalKey, seed, lever, rate, clock, atmosphere);
        if (mix != null)
        {
            if (made is MachineVoiceState machine) machine.Machine.UseMix(mix);
            else if (made is AircraftVoiceState aircraft) aircraft.Aircraft.UseMix(mix);
        }
        return made;
    }

    private static PhysicalVoiceState? MakeAlone(ModelSynth kind, string physicalKey, int seed, float lever, float rate, IAudioClock clock,
                                                 OpenFPS.Sound.Core.Atmosphere? atmosphere)
    {
        string preset = ModelKindTable.KindOf(physicalKey) is { } k ? k.Rest(physicalKey) : physicalKey;
        return kind switch
        {
            ModelSynth.Machine => new MachineVoiceState(ModelLibrary.Presets.SmallMachine(preset), rate, seed, seed * 31 + 7)
            {
                Clock = clock, Atmosphere = atmosphere ?? new OpenFPS.Sound.Core.Atmosphere(),
            },
            ModelSynth.Aircraft => new AircraftVoiceState(AircraftProfile.ByName(preset), rate, seed * 17 + 3, lever: lever),
            // Its own voice: 35 dB over the car's exhaust, the two cannot share one full-scale
            // reference (SirenVoiceState).
            ModelSynth.Siren => new SirenVoiceState(SirenSpec.ByName(preset), rate),
            // Running is the server's word, not worked out locally (BellVoiceState).
            ModelSynth.Bell => new BellVoiceState(ModelLibrary.Bell(preset), rate, seed * 13 + 5),
            // The rhythm of the hand on the horn is in the key (Honk).
            ModelSynth.Horn => Honk.TryParse(physicalKey, out var hornKey, out var rhythm)
                ? new HornVoiceState(hornKey, rhythm, rate, seed * 29 + 1)
                : null,
            _ => null,
        };
    }

    private sealed class Build
    {
        public required Recipe Recipe;
        public required Task<PhysicalVoiceState?> Task;
        /// <summary>When it was last asked for (Clock).</summary>
        public double AskedAt;
    }

    private readonly IAudioClock _clock;
    private readonly OpenFPS.Sound.Core.Atmosphere? _atmosphere;
    private readonly OpenFPS.Sound.Core.MixSettings? _mix;
    private readonly OpenFPS.Audio.Core.AudioWorldOptions? _options;
    /// <summary>Models being made for a voice, by voice.</summary>
    private readonly ConcurrentDictionary<int, Build> _forVoice = new();
    /// <summary>Models made ahead, by what they are made from.</summary>
    private readonly ConcurrentDictionary<Recipe, Build> _ahead = new();
    private readonly ConcurrentDictionary<string, Task<TrainVoiceState>> _trains = new();
    private readonly ConcurrentDictionary<Recipe, byte> _failed = new();

    /// <summary>How long a model made ahead is kept unasked for, and one made for a voice that has not
    /// asked again, seconds.</summary>
    private const double KeepSeconds = 30.0, WaitSeconds = 2.0;

    /// <param name="clock">The world's clock, which the models it makes tell time by.</param>
    /// <param name="atmosphere">The world's air, which a machine's thermostat reads; null gives each its own.</param>
    /// <param name="mix">The mixer's live levers, which the engines it makes read; null gives each its own.</param>
    /// <param name="options">The world's options (whether an engine's cabin is split into paths); null: the shipped.</param>
    public VoiceBuilds(IAudioClock clock, OpenFPS.Sound.Core.Atmosphere? atmosphere = null,
                       OpenFPS.Sound.Core.MixSettings? mix = null,
                       OpenFPS.Audio.Core.AudioWorldOptions? options = null)
    {
        _clock = clock;
        _atmosphere = atmosphere;
        _mix = mix;
        _options = options;
    }

    /// <summary>What <see cref="Take"/> found.</summary>
    public enum Taken { Made, Making, Failed }

    /// <summary>
    /// The model for the voice <paramref name="emitter"/> starts: made ahead, or made for it and finished.
    /// Making: not yet; it is being made, and is there when the voice is next asked for.
    /// </summary>
    public Taken Take(in SpatialEmitter emitter, ModelSynth kind, int rate, out PhysicalVoiceState? state, out string? failure)
    {
        state = null; failure = null;
        double now = _clock.Now;
        if (_forVoice.TryGetValue(emitter.EntityId, out var b))
        {
            b.AskedAt = now;
            if (!b.Task.IsCompleted) return Taken.Making;
            _forVoice.TryRemove(emitter.EntityId, out _);
            return Result(b, out state, out failure);
        }
        var recipe = new Recipe(kind, emitter.PhysicalKey, emitter.Seed, kind == ModelSynth.Aircraft ? emitter.PowerLever : 0f, rate);
        if (_failed.ContainsKey(recipe)) { failure = "its model failed to make"; return Taken.Failed; }
        if (_ahead.TryRemove(recipe, out var ahead))
        {
            if (ahead.Task.IsCompleted) return Result(ahead, out state, out failure);
            // Still being made: this voice waits for it.
            ahead.AskedAt = now;
            _forVoice[emitter.EntityId] = ahead;
            return Taken.Making;
        }
        _forVoice[emitter.EntityId] = new Build { Recipe = recipe, Task = Start(recipe), AskedAt = now };
        return Taken.Making;
    }

    private Taken Result(Build b, out PhysicalVoiceState? state, out string? failure)
    {
        state = null; failure = null;
        if (b.Task.IsFaulted)
        {
            _failed.TryAdd(b.Recipe, 0);
            failure = b.Task.Exception?.InnerException?.Message ?? "unknown";
            return Taken.Failed;
        }
        state = b.Task.Result;
        return Taken.Made;
    }

    /// <summary>
    /// A vehicle's engine voice for <paramref name="emitter"/>, made off this thread (15-70 ms each: every
    /// car coming into the budget held every source still that long). Making: not yet. What the voice is
    /// told after it is made (its speed, its doors, the cabin) is the caller's, as when made in place.
    /// </summary>
    public Taken TakeEngine(in SpatialEmitter emitter, int rate, out EngineVoiceState? state, out string? failure)
    {
        state = null; failure = null;
        double now = _clock.Now;
        if (_engines.TryGetValue(emitter.EntityId, out var b))
        {
            b.AskedAt = now;
            if (!b.Task.IsCompleted) return Taken.Making;
            _engines.TryRemove(emitter.EntityId, out _);
            if (b.Task.IsFaulted) { failure = b.Task.Exception?.InnerException?.Message ?? "unknown"; return Taken.Failed; }
            state = b.Task.Result;
            return Taken.Made;
        }
        string key = emitter.EngineKey ?? "";
        int seed = emitter.Seed;
        bool cabinPaths = _options?.CabinPathsOn ?? true;
        _engines[emitter.EntityId] = new EngineBuild
        {
            Task = OpenFPS.Native.Platform.FlushToZero.Ahead("a voice's build", () => new EngineVoiceState(MachineRegistry.VehicleFor(key), rate, seed, cabinPaths)),
            AskedAt = now,
        };
        return Taken.Making;
    }

    private sealed class EngineBuild
    {
        public required Task<EngineVoiceState> Task;
        public double AskedAt;
    }
    private readonly ConcurrentDictionary<int, EngineBuild> _engines = new();

    /// <summary>Makes ahead the model a voice with this key and seed would start with, if it is not made
    /// or being made already. Cheap enough to ask every frame.</summary>
    public void Prepare(ModelSynth kind, string physicalKey, int seed, int rate)
    {
        // An aeroplane is made at its lever of the moment, which nothing knows ahead.
        if (!Makes(kind) || kind == ModelSynth.Aircraft) return;
        var recipe = new Recipe(kind, physicalKey, seed, 0f, rate);
        if (_ahead.TryGetValue(recipe, out var b)) { b.AskedAt = _clock.Now; return; }
        if (_failed.ContainsKey(recipe)) return;
        _ahead.TryAdd(recipe, new Build { Recipe = recipe, Task = Start(recipe), AskedAt = _clock.Now });
    }

    /// <summary>Lets go of what nobody has asked for in a while: a model made ahead for a source that went
    /// out of earshot or was switched on and given its voice, one made for a voice that was dropped.</summary>
    public void Sweep()
    {
        if (_forVoice.IsEmpty && _ahead.IsEmpty && _engines.IsEmpty) return;
        double now = _clock.Now;
        foreach (var (id, b) in _forVoice)
            if (b.Task.IsCompleted && now - b.AskedAt > WaitSeconds) _forVoice.TryRemove(id, out _);
        foreach (var (id, b) in _engines)
            if (b.Task.IsCompleted && now - b.AskedAt > WaitSeconds) _engines.TryRemove(id, out _);
        foreach (var (recipe, b) in _ahead)
            if (b.Task.IsCompleted && now - b.AskedAt > KeepSeconds) _ahead.TryRemove(recipe, out _);
    }

    /// <summary>The voice is stopped: whatever is being made for it is let go.</summary>
    public void Cancel(int voiceId)
    {
        _forVoice.TryRemove(voiceId, out _);
        _engines.TryRemove(voiceId, out _);
    }

    /// <summary>Models being made or made and waiting: for voices (engines among them), ahead, and trains.</summary>
    public (int ForVoices, int Ahead, int Trains) Counts => (_forVoice.Count + _engines.Count, _ahead.Count, _trains.Count);

    private Task<PhysicalVoiceState?> Start(Recipe r)
    {
        var clock = _clock;
        return OpenFPS.Native.Platform.FlushToZero.Ahead("a voice's build", () => Make(r.Kind, r.Key, r.Seed, r.Lever, r.Rate, clock, _atmosphere, _mix));
    }

    /// <summary>
    /// A train's model ("preset/train"): null while it is being made, which this starts if nobody has.
    /// One that failed to make throws its error, as making it in place did.
    /// </summary>
    public TrainVoiceState? Train(string preset, string train, int rate)
    {
        var clock = _clock;
        var task = _trains.GetOrAdd(preset + "/" + train, shared => OpenFPS.Native.Platform.FlushToZero.Ahead("a voice's build", () =>
        {
            // The seed from the name's characters, not string.GetHashCode (randomised per process): every
            // client and every run hears the same train.
            int seed = 17;
            foreach (char c in train) seed = unchecked(seed * 31 + c);
            var made = new TrainVoiceState(shared, TrainProfile.ByName(preset), rate, seed & 0x7fff, clock);
            if (_mix != null) made.Train.UseMix(_mix);
            return made;
        }));
        if (!task.IsCompleted) return null;
        if (task.IsFaulted) throw task.Exception!.InnerException ?? task.Exception;
        return task.Result;
    }

    /// <summary>Waits until everything asked for is made (the lab and the tests).</summary>
    public void WaitAll(int timeoutMs = 30_000)
    {
        var tasks = _forVoice.Values.Select(b => (Task)b.Task).Concat(_ahead.Values.Select(b => (Task)b.Task))
            .Concat(_engines.Values.Select(b => (Task)b.Task)).Concat(_trains.Values).ToArray();
        try { System.Threading.Tasks.Task.WaitAll(tasks, timeoutMs); } catch (AggregateException) { }
    }
}
