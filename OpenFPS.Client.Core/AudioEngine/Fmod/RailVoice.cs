using System.Numerics;
using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Rail;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// A train, heard as a handful of voices however long it is (docs/TRAINS.md, "Voicing a train").
///
/// The server places one entity per source of the train (TrainLayout): 12 for a light rail set, 160 for
/// a fifty-wagon freight. None is a voice of its own: a voice per source empties the binaural pool, and
/// one synth stepped under one lock stalls every render worker that reaches it.
///
/// Now the train has at most <see cref="TrainVoicing.MaxFieldVoices"/> voices for its rolling stock and
/// engines, and one each for its horn and its bell while they sound (<see cref="TrainSlotState"/>).
/// Which sources each voice carries, and at what weight, is decided on the game thread from where they
/// are (<see cref="TrainVoicing"/>): sources the ear cannot tell apart, because they lie within a few
/// degrees of each other as seen from the listener, share a voice.
///
/// What a voice carries is rendered two ways:
/// <list type="bullet">
/// <item>The point sources with state of their own (a locomotive's engine and fans, an electric
/// train's drives, a chimney, the horn, the whistle, the bell) are each rendered once, by a
/// <see cref="TrainLane"/> on the render pool, into a ring any voice may read.</item>
/// <item>The rolling stock (bogies and body drums) is rendered by the voice carrying it, through one
/// chain per kind of bogie: the wheel, rail and sleepers are linear, so forty bogies through one chain
/// are exactly forty through their own, the roughness of each being independent noise and each bogie's
/// blows arriving at its own moment and weight (BogieVoice.StepShared). A fifty-wagon freight costs
/// what a handful of bogies does.</item>
/// </list>
/// Everything is timed by one clock (<see cref="Block"/>), so a source handed from one voice to another
/// is the same source at the same moment in both.
/// </summary>
public sealed class TrainVoiceState
{
    public readonly TrainProfile Profile;
    /// <summary>The model. Its point sources are rendered by the lanes; its rolling stock's own voices are
    /// not used (the slots build chains from their recipes).</summary>
    public readonly TrainSynth Train;
    public readonly IReadOnlyList<TrainLayout.Entry> Layout;
    public readonly string Key;
    public readonly float Rate;
    internal readonly int Seed;

    /// <summary>Samples per step of the shared clock: speed, notch and the signals change no faster.</summary>
    public const int BlockSamples = 256;
    private const int ClockBits = 12;                      // 4096 blocks, 21 s
    private readonly ClockBlock[] _clock = new ClockBlock[1 << ClockBits];
    private long _clockBlocks;                             // how many blocks exist
    private readonly object _clockGate = new();
    private float _speed, _notch = 3f;
    private double _head;
    private readonly double _t0;

    internal struct ClockBlock
    {
        public double Head;
        public float Speed, Notch;
        public bool Warn, Bell;
    }

    public volatile float TargetSpeed;
    public volatile float TargetNotch = 3f;
    public volatile bool Running = true;

    /// <summary>A lab or a test: a voice that finds a lane behind renders it itself rather than waiting
    /// for the render pool.</summary>
    public bool Offline;

    private const int RingBits = 17;                       // about 2.7 s at 48 kHz
    internal const int RingMask = (1 << RingBits) - 1;
    private readonly float[]?[] _rings;
    private readonly int[] _laneOf;
    public readonly TrainLane[] Lanes;

    /// <summary>What the train is sounding (TrainSignal): the horn's rhythm and the bell's length, from a
    /// sample of the train's own timeline. Swapped whole by the game thread; read by the clock.</summary>
    private sealed record Signalling(float[] Warning, float BellSeconds, long StartSample);
    private Signalling? _signal;

    /// <summary>The listener as the horn sees it, for its directivity (set by the voice carrying it).</summary>
    private Vector3 _hornFrame;
    private volatile bool _hornFrameKnown;

    /// <summary>The voices reading this train, for how far the lanes render.</summary>
    private TrainSlotState[] _slots = Array.Empty<TrainSlotState>();
    private readonly object _slotGate = new();

    /// <summary>What each voice carries (TrainVoicing), published by the game thread.</summary>
    private readonly TrainSlotPlan?[] _plans = new TrainSlotPlan?[TrainVoicing.Slots];

    public TrainVoiceState(string key, TrainProfile p, float sampleRate, int seed)
    {
        Key = key;
        Profile = p;
        Rate = sampleRate;
        Seed = seed;
        Train = new TrainSynth(p, sampleRate, seed);
        Layout = TrainLayout.Sources(p);
        _t0 = AudioClock.Now;
        _speed = TargetSpeed = p.TypicalSpeedMps;

        // The point sources, grouped into lanes: each prime mover with its own fans on a lane of its own
        // (a two-stroke V16 is half a core), everything else together.
        var src = Train.Sources;
        _rings = new float[src.Count][];
        _laneOf = new int[src.Count];
        var lanes = new List<List<int>>();
        var light = new List<int>();
        for (int i = 0; i < src.Count; i++)
        {
            _laneOf[i] = -1;
            if (IsRollingStock(src[i].Kind)) continue;
            _rings[i] = new float[1 << RingBits];
            if (src[i].Kind == TrainLayout.Kind.ExhaustStack) lanes.Add(new List<int> { i });
            else if (src[i].Kind == TrainLayout.Kind.RadiatorFans && lanes.Count > 0) lanes[^1].Add(i);
            else light.Add(i);
        }
        if (light.Count > 0) lanes.Add(light);
        Lanes = new TrainLane[lanes.Count];
        for (int l = 0; l < lanes.Count; l++)
        {
            Lanes[l] = new TrainLane(this, lanes[l].ToArray());
            foreach (int i in lanes[l]) _laneOf[i] = l;
        }
    }

    /// <summary>Bogies and bodies, rendered by the voice that carries them; everything else is a lane's.</summary>
    public static bool IsRollingStock(TrainLayout.Kind kind) => kind is TrainLayout.Kind.Bogie or TrainLayout.Kind.Body;

    /// <summary>The sample of the train's timeline that is playing now.</summary>
    public long NowSample => (long)((AudioClock.Now - _t0) * Rate);

    /// <summary>The ring of a point source, or null for rolling stock.</summary>
    internal float[]? Ring(int source) => _rings[source];

    /// <summary>The lane rendering a point source, or null for rolling stock.</summary>
    internal TrainLane? LaneOf(int source) => _laneOf[source] >= 0 ? Lanes[_laneOf[source]] : null;

    /// <summary>
    /// Sounds the horn (or whistle) in this rhythm and the bell for this long, begun
    /// <paramref name="secondsAgo"/> before now, from the train's own outlets. On the train's own
    /// timeline, so every voice hears the same blast at the same moment.
    /// </summary>
    public void Signal(float[] warning, float bellSeconds, double secondsAgo)
        => SignalAt(warning, bellSeconds, NowSample - (long)(Math.Max(0.0, secondsAgo) * Rate));

    /// <summary>The same, from a given sample of the train's timeline (the lab and the tests).</summary>
    public void SignalAt(float[] warning, float bellSeconds, long startSample)
        => Volatile.Write(ref _signal, new Signalling(warning, MathF.Max(0f, bellSeconds), startSample));

    /// <summary>Whether the horn, whistle or bell is being sounded.</summary>
    public bool IsSignalling => Volatile.Read(ref _signal) != null;

    /// <summary>Publishes what voice <paramref name="slot"/> carries. Game thread.</summary>
    public void SetPlan(int slot, TrainSlotPlan plan)
    {
        if (slot >= 0 && slot < _plans.Length) Volatile.Write(ref _plans[slot], plan);
    }

    internal TrainSlotPlan? PlanFor(int slot) => slot >= 0 && slot < _plans.Length ? Volatile.Read(ref _plans[slot]) : null;

    internal void SetHornFrame(Vector3 frame)
    {
        _hornFrame = frame;
        _hornFrameKnown = true;
    }

    internal bool HornFrame(out Vector3 frame)
    {
        frame = _hornFrame;
        return _hornFrameKnown;
    }

    internal void Attach(TrainSlotState slot)
    {
        lock (_slotGate) _slots = _slots.Append(slot).ToArray();
    }

    /// <summary>The voices reading the train now: the ones that rendered within the last two seconds.
    /// A released voice simply stops asking and is dropped.</summary>
    internal TrainSlotState[] LiveSlots()
    {
        var slots = Volatile.Read(ref _slots);
        double now = AudioClock.Now;
        bool stale = false;
        foreach (var s in slots) if (now - s.LastRendered > 2.0) { stale = true; break; }
        if (!stale) return slots;
        lock (_slotGate)
        {
            _slots = _slots.Where(s => now - s.LastRendered <= 2.0).ToArray();
            return _slots;
        }
    }

    /// <summary>Whether any voice still reads the train (the provider lists its lanes for rendering only then).</summary>
    public bool Heard => LiveSlots().Length > 0;

    /// <summary>How far a lane should have rendered: a little past the voice furthest ahead, never so far
    /// past the one furthest behind that it would write over what that one has still to read.</summary>
    internal long LaneTarget(out long floor)
    {
        long max = long.MinValue, min = long.MaxValue;
        foreach (var s in LiveSlots())
        {
            long c = s.Cursor;
            if (c > max) max = c;
            if (c < min) min = c;
        }
        if (max == long.MinValue) { floor = 0; return long.MinValue; }
        floor = min;
        long target = max + LaneLeadSamples;
        return Math.Min(target, min + (1 << RingBits) - 8 * BlockSamples);
    }

    /// <summary>How far past the voice furthest ahead the lanes render.</summary>
    internal int LaneLeadSamples => (int)(0.15f * Rate);

    /// <summary>The clock at sample <paramref name="t"/>'s block: speed, notch, where the head is, and
    /// whether the horn and bell sound. Worked out forward as far as anybody asks, under a lock that is
    /// only ever held for the arithmetic of a few blocks.</summary>
    internal ClockBlock Block(long t)
    {
        long b = Math.Max(0, t / BlockSamples);
        if (b >= Volatile.Read(ref _clockBlocks))
        {
            lock (_clockGate)
            {
                while (_clockBlocks <= b) Advance();
            }
        }
        // Nobody reads more than a few seconds behind the newest block; clamp anyway.
        long oldest = Math.Max(0, Volatile.Read(ref _clockBlocks) - (1 << ClockBits) + 1);
        if (b < oldest) b = oldest;
        return _clock[(int)(b & ((1 << ClockBits) - 1))];
    }

    private void Advance()
    {
        long b = _clockBlocks;
        float dt = BlockSamples / Rate;
        // A train's speed cannot step, and neither can its effort: the first-order slews a sample at a
        // time used to make, a block at a time.
        _speed += (TargetSpeed - _speed) * (1f - MathF.Exp(-1.5f * dt));
        _notch += (TargetNotch - _notch) * (1f - MathF.Exp(-0.8f * dt));
        float speed = Running ? MathF.Max(0f, _speed) : 0f;
        var block = new ClockBlock { Head = _head, Speed = speed, Notch = _notch };
        if (Volatile.Read(ref _signal) is { } sig)
        {
            float t = (b * BlockSamples - sig.StartSample) / Rate;
            block.Warn = Honk.BlowingAt(sig.Warning, t);
            block.Bell = t >= 0f && t < sig.BellSeconds;
            // Over: cleared, unless the game thread has already put a new one in its place.
            if (t > TrainSignal.Duration(sig.Warning, sig.BellSeconds)) Interlocked.CompareExchange(ref _signal, null, sig);
        }
        _clock[(int)(b & ((1 << ClockBits) - 1))] = block;
        _head += speed * dt;
        Volatile.Write(ref _clockBlocks, b + 1);
    }

    /// <summary>A point source's sample at <paramref name="at"/>, rendering its lane forward to it. For the
    /// lab and the tests (it renders inline).</summary>
    public float PointSample(int source, long at)
    {
        var lane = LaneOf(source) ?? throw new ArgumentException($"source {source} is rolling stock, not a point source");
        if (lane.Rendered <= at) lane.RenderTo(at + BlockSamples);
        return _rings[source]![(int)(at & RingMask)];
    }

    /// <summary>"rail:&lt;preset&gt;/&lt;train&gt;/&lt;source index&gt;" — what the server writes as the
    /// SoundId of each entity of a consist.</summary>
    public static bool ParseKey(string soundId, out string preset, out string train, out int index)
    {
        preset = ""; train = ""; index = -1;
        if (soundId == null || !soundId.StartsWith("rail:", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = soundId[5..].Split('/');
        if (parts.Length != 3) return false;
        preset = parts[0]; train = parts[1];
        return int.TryParse(parts[2], out index);
    }

    /// <summary>"rail:&lt;preset&gt;/&lt;train&gt;/@&lt;slot&gt;" — one of a train's voices (the client's own
    /// key; nothing on the wire carries it).</summary>
    public static string SlotKey(string preset, string train, int slot) => $"rail:{preset}/{train}/@{slot}";

    public static bool ParseSlotKey(string key, out string preset, out string train, out int slot)
    {
        preset = ""; train = ""; slot = -1;
        if (key == null || !key.StartsWith("rail:", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = key[5..].Split('/');
        if (parts.Length != 3 || !parts[2].StartsWith('@')) return false;
        preset = parts[0]; train = parts[1];
        return int.TryParse(parts[2].AsSpan(1), out slot) && slot >= 0 && slot < TrainVoicing.Slots;
    }
}

/// <summary>What one of a train's voices carries: these sources at these weights. The weight of a
/// source is what makes it, played through this voice, as loud as it would have been through its own
/// (TrainVoicing.Weight). Immutable: the game thread publishes a new one.</summary>
public sealed record TrainSlotPlan(int[] Sources, float[] Weights);

/// <summary>
/// Renders some of a train's point sources (TrainVoiceState) into their rings, ahead of the voices that
/// read them. On the render pool like any voice; a voice that finds its lane behind waits for it (renders
/// nothing that pass) rather than taking a lock, so a slow lane delays only its own train.
/// </summary>
public sealed class TrainLane : IRenderedVoice
{
    private readonly TrainVoiceState _train;
    private readonly int[] _sources;
    private readonly int _horn = -1, _whistle = -1, _bell = -1;
    private long _rendered;
    private int _busy;
    private TrainVoiceState.ClockBlock _block;
    private long _blockIndex = -1;

    internal TrainLane(TrainVoiceState train, int[] sources)
    {
        _train = train;
        _sources = sources;
        foreach (int i in sources)
            switch (train.Train.Sources[i].Kind)
            {
                case TrainLayout.Kind.Horn: _horn = i; break;
                case TrainLayout.Kind.Whistle: _whistle = i; break;
                case TrainLayout.Kind.Bell: _bell = i; break;
            }
    }

    /// <summary>Samples rendered so far, on the train's timeline: everything before this is in the rings.</summary>
    public long Rendered => Volatile.Read(ref _rendered);

    public IReadOnlyList<int> Sources => _sources;

    public void Produce()
    {
        long target = _train.LaneTarget(out long floor);
        if (target == long.MinValue) return;
        RenderTo(target, floor);
    }

    /// <summary>Renders up to <paramref name="target"/>. Whoever gets here first does it; a second caller
    /// returns at once.</summary>
    internal void RenderTo(long target, long floor = long.MinValue)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            // Idle while nothing read the train: start where the voices are. The sources' own state just
            // carries on; nothing before a voice's cursor is ever read.
            if (floor != long.MinValue && _rendered < floor - TrainVoiceState.BlockSamples)
                Volatile.Write(ref _rendered, floor - floor % TrainVoiceState.BlockSamples);
            var src = _train.Train.Sources;
            float dt = 1f / _train.Rate;
            while (_rendered < target)
            {
                long t = _rendered;
                long b = t / TrainVoiceState.BlockSamples;
                if (b != _blockIndex)
                {
                    _block = _train.Block(t);
                    _blockIndex = b;
                    if (_horn >= 0) _train.Train.HornBlowing = _block.Warn;
                    if (_whistle >= 0) _train.Train.WhistleBlowing = _block.Warn;
                    if (_bell >= 0) _train.Train.BellRinging = _block.Bell;
                    if (_horn >= 0 && _train.HornFrame(out var f))
                        _train.Train.SetListener(new Vector3(f.Z, f.Y, f.X));
                }
                int n = (int)Math.Min(TrainVoiceState.BlockSamples - t % TrainVoiceState.BlockSamples, target - t);
                float speed = _block.Speed, notch = _block.Notch;
                for (int k = 0; k < n; k++)
                {
                    long at = t + k;
                    // The governors and drives every 64 samples, as the whole synth does.
                    if ((at & 63) == 0) foreach (int i in _sources) src[i].Slow?.Invoke(notch);
                    int idx = (int)(at & TrainVoiceState.RingMask);
                    foreach (int i in _sources) _train.Ring(i)![idx] = src[i].Render(speed);
                }
                Volatile.Write(ref _rendered, t + n);
            }
        }
        finally { Volatile.Write(ref _busy, 0); }
    }
}

/// <summary>
/// One of a train's voices: the sources its plan names (TrainSlotPlan), each at its weight. Point
/// sources are read from their lanes' rings; rolling stock is rendered here through one chain per kind
/// of bogie and one per kind of body. A source joining or leaving the voice is faded over
/// <see cref="FadeSeconds"/>, as the one it moved from fades it out, so the train never steps.
/// </summary>
public sealed class TrainSlotState : PhysicalVoiceState
{
    public readonly TrainVoiceState Shared;
    public readonly int Slot;
    private readonly float _dt;
    private long _cursor;

    /// <summary>How long a source takes to move between voices, seconds.</summary>
    public const float FadeSeconds = 0.4f;

    /// <summary>The headroom a voice carrying many sources renders with: twenty decibels over a single
    /// source's, so forty bogies at the weight of the nearest still sit under full scale. The mixer gives
    /// the difference back (HeadroomGain).</summary>
    public const float HeadroomDb = VehicleProfile.PeakHeadroomDb + 20f;

    private sealed class Member
    {
        public int Source;
        public float Weight, Target, Power;
        public AxleSchedule? Axles;
        public int Chain = -1;
        public float[]? Ring;
        public TrainLane? Lane;
        /// <summary>A point source fades out over 64 samples from its last sample when its lane is late,
        /// and back in when it catches up.</summary>
        public float Gate = 1f, Last;
    }
    private readonly Dictionary<int, Member> _members = new();
    private readonly List<Member> _order = new();
    private readonly List<int> _gone = new();
    private TrainSlotPlan? _plan;

    private sealed class Chain
    {
        public BogieVoice? Bogie;
        public BodyDrum? Drum;
        public float Creep;
        public float Weight;   // the root of the sum of its members' squared weights, this block
    }
    private readonly List<Chain> _chains = new();
    private readonly Dictionary<object, int> _chainOf = new();

    /// <summary>This block's blows, in the order they fall: the sample within the block, the chain, the
    /// impact speed times the bogie's weight. Worked out once a block (AxleSchedule.AdvanceBlock).</summary>
    private readonly List<(int At, int Chain, float Impact, float Weight)> _blows = new();
    private readonly int[] _blowAt = new int[16];
    private readonly float[] _blowImpact = new float[16];
    private int _nextBlow;

    private TrainVoiceState.ClockBlock _block;
    private long _blockIndex = -1;
    private bool _carriesHorn;

    /// <summary>When this voice last rendered (AudioClock), so a released one stops holding its lanes back.</summary>
    internal double LastRendered = AudioClock.Now;

    /// <summary>The sample of the train's timeline this voice renders next.</summary>
    internal long Cursor => Volatile.Read(ref _cursor);

    /// <param name="levelDb">The level the voice is declared at (TrainVoicing.SlotLevelDb): what its
    /// weights are worked out against.</param>
    /// <param name="startSample">Where on the train's timeline it starts; by default the sample playing
    /// now. A test or the lab gives one, so two voices made apart are in step.</param>
    public TrainSlotState(TrainVoiceState shared, int slot, float levelDb, float sampleRate, long? startSample = null)
        : base(levelDb, sampleRate, HeadroomDb)
    {
        Shared = shared;
        Slot = slot;
        _dt = 1f / sampleRate;
        // On the train's timeline at the sample now playing: the warm-up it renders first is thrown away
        // (PhysicalVoiceState), so it starts that much earlier and its first kept sample is now.
        _cursor = Math.Max(0, (startSample ?? shared.NowSample) - (long)(WarmupSeconds * sampleRate));
        shared.Attach(this);
    }

    protected override void PushListener(Vector3 frame)
    {
        // Only the voice carrying the horn aims it: its frame is the listener seen from the horn (x right,
        // y up, z along the track). Every voice pushing its own frame left it aimed from the last one.
        if (_carriesHorn) Shared.SetHornFrame(frame);
    }

    protected override void Control(float seconds, float dt)
    {
        Shared.Running = Running;
        LastRendered = AudioClock.Now;
    }

    /// <summary>As many samples as every lane this voice reads has rendered, from the cursor on.</summary>
    protected override int Ready(int want)
    {
        LastRendered = AudioClock.Now;
        TakePlan();
        long at = Volatile.Read(ref _cursor);
        long ready = want;
        foreach (var m in _order)
        {
            if (m.Lane == null) continue;
            long have = m.Lane.Rendered - at;
            if (have < want && Shared.Offline)
            {
                m.Lane.RenderTo(at + want);
                have = m.Lane.Rendered - at;
            }
            if (have < ready) ready = have;
        }
        // A lane behind (a diesel's worker held up) waits while the voice has something in hand; once the
        // voice is about to run dry it renders anyway, and a source whose lane has not got there fades out
        // of it for those samples (StepSynth). One engine drops out for a moment, softly, instead of the
        // whole voice: wagons, bell and all.
        if (ready < want && Buffered < UrgentSamples) ready = want;
        return (int)Math.Max(0, ready);
    }

    /// <summary>What the voice keeps in hand however late a lane is: half its lead and a block, so it
    /// primes and never runs dry.</summary>
    private int UrgentSamples => LeadSamples / 2 + 512;

    /// <summary>The ring was resynced past what was rendered (the voice starved): the cursor moves with it,
    /// so the voice stays on the train's timeline.</summary>
    protected override void Skipped(long samples) => Volatile.Write(ref _cursor, _cursor + samples);

    /// <summary>Takes up a new plan: sources new to the voice come in at nothing, sources it no longer
    /// carries head for nothing, the rest head for their new weights.</summary>
    private void TakePlan()
    {
        var plan = Shared.PlanFor(Slot);
        if (ReferenceEquals(plan, _plan)) return;
        _plan = plan;
        foreach (var m in _order) m.Target = 0f;
        if (plan == null) return;
        var src = Shared.Train.Sources;
        for (int k = 0; k < plan.Sources.Length; k++)
        {
            int i = plan.Sources[k];
            if (i < 0 || i >= src.Count) continue;
            if (!_members.TryGetValue(i, out var m))
            {
                m = new Member { Source = i };
                var s = src[i];
                if (s.Bogie is { } recipe)
                {
                    m.Chain = ChainFor(recipe);
                    // Its own axles, placed where it is on the track now: the rhythm is the bogie's.
                    m.Axles = new AxleSchedule(recipe.Wheels, recipe.Track, recipe.Axles, recipe.Wheelbase,
                                               new Random(unchecked(Shared.Seed * 7919 + i * 104729)));
                    m.Axles.Place(Shared.Block(_cursor).Head - s.AlongMetres);
                }
                else if (s.Drum is { } drum) m.Chain = ChainFor(drum);
                else
                {
                    m.Ring = Shared.Ring(i);
                    m.Lane = Shared.LaneOf(i);
                }
                if (s.Kind == TrainLayout.Kind.Horn) _carriesHorn = true;
                _members[i] = m;
                _order.Add(m);
            }
            m.Target = plan.Weights[k];
        }
    }

    private int ChainFor(object recipe)
    {
        if (_chainOf.TryGetValue(recipe, out int c)) return c;
        var chain = new Chain();
        int seed = unchecked(Shared.Seed * 31 + Slot * 1009 + _chains.Count * 7);
        switch (recipe)
        {
            case TrainSynth.BogieRecipe b:
                // A chain carrying many bogies has many blows in flight.
                chain.Bogie = new BogieVoice(b.Wheels, b.Track, new TrackResponse(b.Track, SampleRate), b.ReferenceDb,
                                             b.Axles, b.Wheelbase, SampleRate, seed, blows: 24);
                chain.Creep = b.Creep;
                break;
            case TrainSynth.DrumRecipe d:
                chain.Drum = new BodyDrum(d.Hz, d.Db, SampleRate, seed);
                break;
        }
        _chains.Add(chain);
        _chainOf[recipe] = _chains.Count - 1;
        return _chains.Count - 1;
    }

    /// <summary>Once a block: the weights a step nearer their targets, and each chain's weight from its members'.</summary>
    private void NewBlock()
    {
        _block = Shared.Block(_cursor);
        _blockIndex = _cursor / TrainVoiceState.BlockSamples;
        float step = TrainVoiceState.BlockSamples / (FadeSeconds * SampleRate);
        _gone.Clear();
        foreach (var c in _chains) c.Weight = 0f;
        foreach (var m in _order)
        {
            // In power, not amplitude: a source fading out of one voice as it fades into another keeps its
            // power through the hand-over (a straight amplitude crossfade of two noises dips 3 dB).
            float want = m.Target * m.Target;
            float span = MathF.Max(want, m.Power) * step;
            m.Power += Math.Clamp(want - m.Power, -span, span);
            m.Weight = MathF.Sqrt(MathF.Max(0f, m.Power));
            if (m.Target == 0f && m.Power < 1e-12f) { m.Weight = m.Power = 0f; _gone.Add(m.Source); continue; }
            if (m.Chain >= 0) _chains[m.Chain].Weight += m.Weight * m.Weight;
        }
        foreach (var c in _chains) c.Weight = MathF.Sqrt(c.Weight);
        foreach (int i in _gone)
        {
            var m = _members[i];
            _members.Remove(i);
            _order.Remove(m);
            if (m.Ring != null && Shared.Train.Sources[i].Kind == TrainLayout.Kind.Horn) _carriesHorn = false;
        }

        // Every carried bogie's blows in this block, at the sample each falls on. The block runs from the
        // cursor to the end of the clock's block (the speed is steady across it).
        _blows.Clear();
        _nextBlow = 0;
        int count = (int)(TrainVoiceState.BlockSamples - _cursor % TrainVoiceState.BlockSamples);
        foreach (var m in _order)
        {
            if (m.Axles == null) continue;
            int n = m.Axles.AdvanceBlock(_block.Speed, _dt, count, _blowAt, _blowImpact);
            for (int j = 0; j < n; j++) _blows.Add((_blowAt[j], m.Chain, _blowImpact[j], m.Weight));
        }
        if (_blows.Count > 1) _blows.Sort(static (a, b) => a.At.CompareTo(b.At));
        _blockStart = _cursor;
    }

    private long _blockStart;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    protected override float StepSynth()
    {
        long at = _cursor;
        if (at / TrainVoiceState.BlockSamples != _blockIndex) NewBlock();
        float v = _block.Speed;
        float y = 0f;
        var order = _order;
        for (int k = 0; k < order.Count; k++)
        {
            var m = order[k];
            if (m.Ring == null) continue;
            if (m.Lane!.Rendered > at)
            {
                m.Last = m.Ring[(int)(at & TrainVoiceState.RingMask)];
                m.Gate = MathF.Min(1f, m.Gate + 1f / 64f);
            }
            else m.Gate = MathF.Max(0f, m.Gate - 1f / 64f);
            y += m.Weight * m.Gate * m.Last;
        }
        int here = (int)(at - _blockStart);
        while (_nextBlow < _blows.Count && _blows[_nextBlow].At <= here)
        {
            var b = _blows[_nextBlow++];
            _chains[b.Chain].Bogie!.Inject(b.Impact, b.Weight);
        }
        for (int c = 0; c < _chains.Count; c++)
        {
            var chain = _chains[c];
            if (chain.Bogie != null) y += chain.Bogie.StepShared(v, chain.Creep, chain.Weight);
            else if (chain.Drum != null) y += chain.Drum.StepShared(v, chain.Weight);
        }
        Volatile.Write(ref _cursor, at + 1);
        return y;
    }
}
