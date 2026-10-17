using System.Numerics;
using OpenFPS.Audio.Acoustics;
using OpenFPS.Audio.Data;
using OpenFPS.Common;
using OpenFPS.Sound;
using OpenFPS.Geometry;
using OpenFPS.Acoustics;

namespace OpenFPS.Audio.Core;

/// <summary>
/// The birds. Nobody places one: sparrows and doves live in foliage, pigeons and crows on roofs, and a
/// stable hash of each hedge and roof decides whether a group is there, so the same hedge always has
/// its sparrows and a new map has birds untold. Geese go over now and then.
///
/// Each bird has its own place, pitch, small repertoire and rhythm of bouts, a little contagious, so a
/// hedge breaks out and settles together; each call is a one-off sound heard through the acoustic path.
/// A loud enough sound nearby quiets a group for a while, and walking right up to one quiets it until
/// you move on. Client-side, like footsteps: the server need not agree on a sparrow. Where they live is found
/// from the world input's bodies (foliage, flat roofs with open sky), as the snapshot's entities were walked.
/// </summary>
public sealed class BirdLife
{
    private sealed class Bird
    {
        public Vector3 At;
        public float Pitch;
        public string[] Repertoire = Array.Empty<string>();
        public double Next;
        public int CallsLeft;
        /// <summary>When its last bout ended. A neighbour can only draw it out again once it has
        /// rested at least the species' shortest rest.</summary>
        public double RestingSince = double.NegativeInfinity;
    }

    private sealed class Group
    {
        public required BirdSpecies Species;
        public required Vector3 Centre;
        public required Bird[] Birds;
        /// <summary>What the group perches on: the order groups are woken in, and so draw from the one
        /// generator in.</summary>
        public int Perch;
        /// <summary>Where the group's calls come from as one (the middle of its birds), as the acoustic worker
        /// is asked about it; each bird's call takes that answer moved to the bird.</summary>
        public Vector3 Heard;
        public double QuietUntil;
        public bool Awake;
        /// <summary>Geese only: where the skein is and where it is going.</summary>
        public Vector3 Velocity;
        public double Leaves;
    }

    private readonly AudioEngineFacade _audio;
    private readonly SpatialAcoustics? _acoustics;
    private readonly Random _rng;
    private readonly List<Group> _perched = new();
    private Group? _skein;
    private double _nextSkein = -1;
    private long _foundFrom = -1;
    /// <summary>The world's bodies, refilled for each look at the scenery.</summary>
    private readonly List<AcousticBody> _bodies = new();

    /// <summary>How many times the habitat has been looked for. Once per map.</summary>
    internal int SurveysRun { get; private set; }
    private double _nextSurvey;
    private int _voice;
    private double _lastNow = -1;

    /// <summary>Beyond this from the listener a group is left asleep. The loudest perched bird, a crow,
    /// is lost under a street well inside it.</summary>
    private const float AwakeRadius = 160f;
    /// <summary>A roof is a flat top this high up, with nothing over it.</summary>
    private const float RoofMinHeight = 10f, RoofMinArea = 60f;
    /// <summary>A skein of geese goes over, on average, this often.</summary>
    private const float SkeinEverySeconds = 420f;

    /// <summary><paramref name="seed"/> seeds the timing of calls, bouts and skeins: tests pass one, the
    /// game leaves it null.</summary>
    public BirdLife(AudioEngineFacade audio, SpatialAcoustics? acoustics, int? seed = null)
    {
        _audio = audio;
        Voices = new VoiceRegistry { Mixer = audio };
        _acoustics = acoustics;
        _rng = seed is int s ? new Random(s) : new Random();
    }

    /// <summary>Every perched group: its species and where it is. For tests.</summary>
    internal IEnumerable<(string Species, Vector3 Centre, int Birds)> Groups
    {
        get { foreach (var g in _perched) yield return (g.Species.Name, g.Centre, g.Birds.Length); }
    }

    /// <summary>How many birds live on this map, by species. Diagnostic.</summary>
    public IReadOnlyDictionary<string, int> Census
    {
        get
        {
            var d = new Dictionary<string, int>();
            foreach (var g in _perched) d[g.Species.Name] = d.GetValueOrDefault(g.Species.Name) + g.Birds.Length;
            return d;
        }
    }

    // ── Where they live ────────────────────────────────────────────────────────────────────────

    /// <summary>Looks for habitat once the map's static geometry has arrived, and again if it changes.
    /// Keyed on the scenery alone: keyed on the entity count, it reran every few seconds on the city as
    /// people and cars came and went (16 rays a roof on the game thread, 150 ms, 850 ms the first time)
    /// and threw away the birds already calling.</summary>
    private void Survey(IAcousticWorld world)
    {
        long scenery = ScenerySignature(world, _bodies);
        if (scenery == _foundFrom) return;
        _foundFrom = scenery;
        SurveysRun++;
        _perched.Clear();
        foreach (var e in _bodies)
        {
            if (!IsScenery(e)) continue;
            var size = e.Size;
            if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f) continue;
            float hash = Hash(e.Id);

            if (AcousticRegistry.IsLeaves(e.Material))
            {
                // A sparrow group in one hedge in six, a dove in one in twelve.
                if (hash < 0.16f) _perched.Add(Settle(BirdSpecies.HouseSparrow, e, size, onTop: false));
                else if (hash > 0.92f) _perched.Add(Settle(BirdSpecies.Dove, e, size, onTop: false));
                continue;
            }

            if (!e.Solid) continue;
            float top = e.Position.Y + size.Y * 0.5f;
            if (top < RoofMinHeight || size.X * size.Z < RoofMinArea) continue;
            if (!NothingAbove(world, e, top)) continue;
            if (hash < 0.5f) _perched.Add(Settle(BirdSpecies.Pigeon, e, size, onTop: true));
            else if (hash > 0.85f) _perched.Add(Settle(BirdSpecies.Crow, e, size, onTop: true));
        }
        // By perch, not the order the world listed them in (which follows the processor count): the groups
        // draw their timings from one generator in this order.
        _perched.Sort(static (a, b) => a.Perch.CompareTo(b.Perch));
        Serilog.Log.Information("Birds: {Census}", string.Join(", ", CensusText()));
    }

    /// <summary>A fixed box a bird could live on: not a vehicle, a person or anything else that moves.</summary>
    private static bool IsScenery(in AcousticBody e)
        => e.Fixed && !e.Moves && e.Velocity == Vector3.Zero && e.Shape == ColliderShape.Box;

    /// <summary>Which scenery is loaded, as one number: the count and the ids together, so a map swapped
    /// for another of the same size still counts as a change. A sum, so the order entities are held in
    /// does not matter. <paramref name="bodies"/> is left holding the world's bodies, in its order.</summary>
    internal static long ScenerySignature(IAcousticWorld world, List<AcousticBody>? bodies = null)
    {
        long count = 0, ids = 0;
        bodies ??= new List<AcousticBody>();
        world.AllBodies(bodies);
        foreach (var e in bodies)
            if (IsScenery(e)) { count++; ids = unchecked(ids + (long)Hash(e.Id).GetHashCode() * 2654435761L + e.Id); }
        return unchecked(count * 1_000_003L ^ ids);
    }

    private IEnumerable<string> CensusText()
    {
        foreach (var kv in Census) yield return $"{kv.Value} {kv.Key}";
    }

    /// <summary>
    /// Open sky over most of the slab, asked at a 4 x 4 grid of points: a roof has a condenser or a
    /// stair head here and there, and one ray up the middle found one roof on the whole city.
    /// </summary>
    private bool NothingAbove(IAcousticWorld world, in AcousticBody e, float top)
    {
        if (_acoustics == null) return true;
        var size = e.Size;
        int open = 0, asked = 0;
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                var local = new Vector3((i + 0.5f) / 4f - 0.5f, 0f, (j + 0.5f) / 4f - 0.5f) * new Vector3(size.X, 0f, size.Z);
                var at = e.Position + Vector3.Transform(local, e.Rotation);
                var from = new Vector3(at.X, top + 0.3f, at.Z);
                asked++;
                if (!_acoustics.RaycastMaterial(world, from, Vector3.UnitY, 40f, out _, out _, out _, e.Id)) open++;
            }
        return open >= asked * 0.6f;
    }

    /// <summary>A group of one species on one perch. Deterministic in the perch, so every client and
    /// every visit finds the same birds in the same places.</summary>
    private Group Settle(BirdSpecies species, in AcousticBody perch, Vector3 size, bool onTop)
    {
        var rng = new Random(perch.Id * 7349 + species.Folder.Length);
        // Bigger perches hold more of a flock, up to the species' own limit.
        float room = MathF.Max(size.X, size.Z);
        int n = Math.Clamp((int)MathF.Round(species.GroupMin + (species.GroupMax - species.GroupMin) * (float)rng.NextDouble()
                                             * Math.Clamp(room / 12f, 0.3f, 1f)), species.GroupMin, species.GroupMax);
        var birds = new Bird[n];
        var calls = _audio.SoundsIn("BIRDS/" + species.Folder);
        for (int i = 0; i < n; i++)
        {
            Vector3 local;
            if (onTop)
            {
                // Along an edge of the roof, where they perch: pick a side, then a point along it.
                bool alongX = rng.Next(2) == 0;
                float u = (float)rng.NextDouble() - 0.5f, side = rng.Next(2) == 0 ? -0.5f : 0.5f;
                local = alongX ? new Vector3(u * size.X, size.Y * 0.5f + 0.15f, side * size.Z)
                               : new Vector3(side * size.X, size.Y * 0.5f + 0.15f, u * size.Z);
            }
            else
            {
                // Inside the foliage, in its upper half.
                local = new Vector3(((float)rng.NextDouble() - 0.5f) * size.X,
                                    size.Y * (0.05f + 0.4f * (float)rng.NextDouble()),
                                    ((float)rng.NextDouble() - 0.5f) * size.Z);
            }
            birds[i] = new Bird
            {
                At = perch.Position + Vector3.Transform(local, perch.Rotation),
                Pitch = 1f + species.PitchSpread * (2f * (float)rng.NextDouble() - 1f),
                Repertoire = Pick(calls, rng, 4),
                CallsLeft = 0,
            };
        }
        var heard = Vector3.Zero;
        foreach (var b in birds) heard += b.At;
        return new Group { Species = species, Centre = perch.Position, Birds = birds, Perch = perch.Id, Heard = heard / Math.Max(1, n) };
    }

    private static string[] Pick(IReadOnlyList<string> calls, Random rng, int count)
    {
        if (calls.Count == 0) return Array.Empty<string>();
        var own = new string[Math.Min(count, calls.Count)];
        for (int i = 0; i < own.Length; i++) own[i] = calls[rng.Next(calls.Count)];
        return own;
    }

    private static float Hash(int id)
    {
        uint h = unchecked((uint)id * 2654435761u);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return (h & 0xFFFF) / 65536f;
    }

    // ── What they do ───────────────────────────────────────────────────────────────────────────

    public void Update(IAcousticWorld world, Vector3 listener, double now)
    {
        if (now >= _nextSurvey) { _nextSurvey = now + 2.0; Survey(world); }
        _askFrame++;

        foreach (var g in _perched)
        {
            bool near = Vector3.DistanceSquared(g.Centre, listener) < AwakeRadius * AwakeRadius;
            if (!near) { g.Awake = false; continue; }
            if (!g.Awake)
            {
                // Come into earshot mid-cycle, not at dawn.
                g.Awake = true;
                foreach (var b in g.Birds)
                    b.Next = now + Uniform(0f, g.Species.BoutGapMax * 0.5f);
                Ask(g, listener);
            }
            // Asked again twice a second, each group in a frame of its own, so its answer is fresh for the ear.
            else if (_askFrame % AskEveryFrames == (g.Perch & 0x7fffffff) % AskEveryFrames) Ask(g, listener);
            Live(world, g, listener, now);
        }

        UpdateSkein(world, listener, now);
        _lastNow = now;
    }

    private void Live(IAcousticWorld world, Group g, Vector3 listener, double now)
    {
        var sp = g.Species;
        // Somebody right up against them: they keep still and quiet until you go.
        foreach (var b in g.Birds)
            if (sp.ShyMetres > 0f && Vector3.DistanceSquared(b.At, listener) < sp.ShyMetres * sp.ShyMetres)
            {
                g.QuietUntil = Math.Max(g.QuietUntil, now + Uniform(3f, 8f));
                break;
            }

        foreach (var b in g.Birds)
        {
            if (now < b.Next || b.Repertoire.Length == 0) continue;
            if (now < g.QuietUntil) { b.Next = g.QuietUntil + Uniform(0f, 12f); continue; }

            bool starting = b.CallsLeft <= 0;
            if (starting) b.CallsLeft = _rng.Next(sp.BoutCallsMin, sp.BoutCallsMax + 1);
            Call(world, sp, b.Repertoire[_rng.Next(b.Repertoire.Length)], b.At, g.Velocity, b.Pitch, listener, g);
            b.CallsLeft--;
            b.Next = now + (b.CallsLeft > 0 ? Uniform(sp.CallGapMin, sp.CallGapMax) : Uniform(sp.BoutGapMin, sp.BoutGapMax));
            if (b.CallsLeft <= 0) b.RestingSince = now;

            // Once per bout, and only birds that have rested: pulled on every call, six sparrows
            // chattered ten times a second and the hedge never rested.
            if (starting && sp.Contagion > 0f)
                foreach (var other in g.Birds)
                    if (other != b && other.CallsLeft <= 0 && now - other.RestingSince >= sp.BoutGapMin
                        && _rng.NextDouble() < sp.Contagion)
                        other.Next = Math.Min(other.Next, now + Uniform(0.4f, 3f));
        }
    }

    /// <summary>A sound loud enough where a group is (over its StartleDb) quiets it for 8 to 30 s; the
    /// birds come back one at a time.</summary>
    public void Heard(IReadOnlyList<TransientSound> sounds, double now)
    {
        foreach (var s in sounds)
            foreach (var g in _perched)
            {
                if (!g.Awake) continue;
                float d = MathF.Max(1f, Vector3.Distance(s.Position, g.Centre));
                if (s.LevelDb - 20f * MathF.Log10(d) < g.Species.StartleDb) continue;
                g.QuietUntil = Math.Max(g.QuietUntil, now + Uniform(8f, 30f));
                foreach (var b in g.Birds) b.CallsLeft = 0;
            }
    }

    /// <summary>Where every call is given its id and declared as it starts: its audio world's registry, or
    /// one of its own on its own.</summary>
    internal VoiceRegistry Voices { get; set; }

    /// <summary>Every call, as it is made: which species and where. For tests and traces.</summary>
    internal Action<string, Vector3>? OnCall { get; set; }

    /// <summary>A call quieter than this at the ear, before anything is in the way, is not sent at
    /// all: the voice budget would drop it, and it would cost an acoustic path to find that out.</summary>
    private const float InaudibleGain = 0.001f;   // -60 dBFS

    /// <summary>The acoustic worker a perched group's calls are heard by (P6); null, the hand-rolled tracer alone.</summary>
    internal AsyncAcousticWorker? Worker { get; set; }

    /// <summary>How fast the ear moves, m/s: how far from here an answer traced a moment ago may have been asked
    /// (AcousticPathData.Answers).</summary>
    internal float EarSpeed { get; set; }

    /// <summary>Calls placed by the worker's answer rather than the tracer's, for a test.</summary>
    internal int AnsweredByWorker { get; private set; }

    /// <summary>How often an awake group is asked about again, frames.</summary>
    private const int AskEveryFrames = 30;
    private int _askFrame;

    /// <summary>The question a perched group is asked under: a probe of its own, by its perch.</summary>
    private int ProbeOf(Group g) => Voices.IdFor(VoiceKind.Probe, VoiceRegistry.NoEntity, g.Perch);

    /// <summary>Asks the worker about where a group's calls come from, as it wakes and now and then after.</summary>
    private void Ask(Group g, Vector3 listener)
    {
        Worker?.EnqueueRequest(new AcousticRequest
        {
            // Nothing left out, as before: a hedge's own leaves are between its sparrows and you.
            EntityId = ProbeOf(g), ListenerPos = listener, SourcePos = g.Heard, SourceRadius = AudioEmission.MinOcclusionRadius,
        });
    }

    /// <summary>
    /// A call's path: the worker's answer for its group (its transmission, the routes through openings, the way
    /// round one thing and through another, the edge it bends over), moved from the group's middle to the bird
    /// as a near source's answer is (FirstAnswer); the hand-rolled tracer's for a group it has not answered yet,
    /// and for the geese.
    /// </summary>
    private AcousticPathData? PathOf(IAcousticWorld world, Group? g, Vector3 at, Vector3 listener)
    {
        if (_acoustics == null) return null;
        if (g != null && Worker != null && Worker.TryGetDirectFor(ProbeOf(g), g.Heard, 0f, listener, EarSpeed, out var answer))
        {
            AnsweredByWorker++;
            Vector3 moved = at - g.Heard;
            float from = MathF.Max(0.1f, Vector3.Distance(listener, g.Heard));
            float dist = Vector3.Distance(listener, at);
            var p = answer;
            p.ApparentPosition = answer.ApparentPosition + moved;
            p.EffectiveDistance = answer.EffectiveDistance * dist / from;
            return p;
        }
        try { return _acoustics.CalculateAcousticPath(world, -1, listener, at); } catch { return null; }
    }

    private void Call(IAcousticWorld world, BirdSpecies sp, string sound, Vector3 at, Vector3 velocity, float pitch, Vector3 listener,
                      Group? group = null)
    {
        float range = _audio.Hearing.AudibleRange(sp.CallDb);
        float d = Vector3.Distance(at, listener);
        if (d > range) return;
        var (gain, reference) = _audio.Hearing.Place(sp.CallDb);
        if (Loudness.RenderedGain(gain, reference, range, d) < InaudibleGain) return;
        OnCall?.Invoke(sp.Name, at);
        AcousticPathData? path = PathOf(world, group, at, listener);
        int call = _voice++;
        int voiceId = Voices.IdFor(VoiceKind.Bird, VoiceRegistry.NoEntity, call);
        Voices.DeclareEvent(voiceId, VoiceRegistry.NoEntity, VoiceKind.Bird, at, sp.CallDb);
        Voices.Submit(new SpatialEmitter
        {
            EntityId = voiceId,
            Seed = VoiceRegistry.SeedOf(VoiceKind.Bird, VoiceRegistry.NoEntity, call),
            SoundId = sound,
            Mode = PlaybackMode.Single,
            Type = EmitterType.WorldLocked,
            Position = at,
            ApparentPosition = path?.ApparentPosition ?? at,
            EffectiveDistance = path?.EffectiveDistance ?? Vector3.Distance(listener, at),
            Occlusion = path?.Occlusion ?? 0f,
            ApertureFactor = path?.ApertureFactor ?? 1f,
            TransmissionBleed = path?.TransmissionBleed ?? 0f,
            TargetRegionId = path?.RegionId ?? -1,
            EqLow = path?.EqLow ?? 1f, EqMid = path?.EqMid ?? 1f, EqHigh = path?.EqHigh ?? 1f,
            AirLowDb = path?.AirLowDb ?? 0f, AirMidDb = path?.AirMidDb ?? 0f, AirHighDb = path?.AirHighDb ?? 0f,
            Velocity = velocity,
            Volume = gain,
            EarLevelDb = sp.CallDb,
            MinDistance = reference,
            Range = range,
            Pitch = pitch,
            IsEvent = true,
        });
    }

    // ── Geese ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Now and then a skein of geese goes over: a V 60 to 110 m up at 14 m/s, passing within 150 m of
    /// you and gone in about a minute and a half.
    /// </summary>
    private void UpdateSkein(IAcousticWorld world, Vector3 listener, double now)
    {
        if (_nextSkein < 0) _nextSkein = now + Uniform(60f, SkeinEverySeconds);
        if (_skein == null && now >= _nextSkein)
        {
            var sp = BirdSpecies.Goose;
            var calls = _audio.SoundsIn("BIRDS/" + sp.Folder);
            float heading = Uniform(0f, MathF.Tau);
            var dir = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading));
            var across = new Vector3(dir.Z, 0f, -dir.X);
            const float speed = 14f, half = 650f;
            var closest = listener + across * Uniform(-150f, 150f) + Vector3.UnitY * Uniform(60f, 110f);
            int n = _rng.Next(sp.GroupMin, sp.GroupMax + 1);
            var birds = new Bird[n];
            for (int i = 0; i < n; i++)
            {
                // A V: alternate sides, each a little further back and out than the last.
                int rank = (i + 1) / 2; float side = i % 2 == 0 ? 1f : -1f;
                birds[i] = new Bird
                {
                    At = -dir * (rank * 2.6f) + across * (side * rank * 2.2f) + Vector3.UnitY * Uniform(-1f, 1f),
                    Pitch = 1f + sp.PitchSpread * (2f * (float)_rng.NextDouble() - 1f),
                    Repertoire = Pick(calls, _rng, 5),
                    Next = now + Uniform(0f, 3f),
                };
            }
            _skein = new Group
            {
                Species = sp, Centre = closest - dir * half, Birds = birds, Awake = true,
                Velocity = dir * speed, Leaves = now + 2f * half / speed,
            };
            Serilog.Log.Information("Birds: {N} geese going over, closest {D:F0} m.", n, Vector3.Distance(closest, listener));
        }
        if (_skein == null) return;

        float dt = _lastNow < 0 ? 0f : (float)Math.Clamp(now - _lastNow, 0.0, 0.25);
        _skein.Centre += _skein.Velocity * dt;
        var sp2 = _skein.Species;
        foreach (var b in _skein.Birds)
        {
            if (now < b.Next || b.Repertoire.Length == 0) continue;
            if (b.CallsLeft <= 0) b.CallsLeft = _rng.Next(sp2.BoutCallsMin, sp2.BoutCallsMax + 1);
            Call(world, sp2, b.Repertoire[_rng.Next(b.Repertoire.Length)], _skein.Centre + b.At, _skein.Velocity, b.Pitch, listener);
            b.CallsLeft--;
            b.Next = now + (b.CallsLeft > 0 ? Uniform(sp2.CallGapMin, sp2.CallGapMax) : Uniform(sp2.BoutGapMin, sp2.BoutGapMax));
        }
        if (now >= _skein.Leaves)
        {
            _skein = null;
            _nextSkein = now + Uniform(SkeinEverySeconds * 0.5f, SkeinEverySeconds * 1.5f);
        }
    }

    private float Uniform(float a, float b) => a + (b - a) * (float)_rng.NextDouble();
}
