using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core;

/// <summary>
/// The birds.
///
/// Nobody places a bird. Sparrows live in foliage and pigeons and crows on roofs, so where they are
/// falls out of the map: every foliage volume and every roof is a place a species could be, and a
/// stable hash of the thing decides whether one is — so the same hedge always has its sparrows and the
/// same tower its pigeons, and a map built tomorrow has birds without being told. Geese go over now and
/// then, wherever there is sky.
///
/// A group is never a recording of a group. Each bird has its own place in the hedge, its own voice
/// (a fixed pitch a few percent off its neighbours'), a handful of the species' calls as its own
/// repertoire, and its own rhythm — bouts of calls with quiet between them, a little contagious, so a
/// hedge breaks out together and settles together. Each call is a one-off sound at that bird, heard
/// through the same acoustic path as everything else: behind a wall it is behind the wall.
///
/// And they notice things. A loud enough sound where they are — a gunshot, a slammed door, a horn
/// close by — shuts them up for a while, and they come back one at a time. Walk right up to a hedge
/// and the ones in it go quiet until you move on.
///
/// Client-side, like footsteps: nothing about a sparrow is a fact the server needs to agree on.
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
        public double QuietUntil;
        public bool Awake;
        /// <summary>Geese only: where the skein is and where it is going.</summary>
        public Vector3 Velocity;
        public double Leaves;
    }

    private readonly AudioEngineFacade _audio;
    private readonly SpatialAcoustics? _acoustics;
    private readonly Random _rng = new();
    private readonly List<Group> _perched = new();
    private Group? _skein;
    private double _nextSkein = -1;
    private int _foundFrom = -1;
    private double _nextSurvey;
    private int _voice;
    private double _lastNow = -1;

    /// <summary>Beyond this from the listener a group is left asleep. The loudest perched bird, a crow,
    /// is lost under a street well inside it.</summary>
    private const float AwakeRadius = 160f;
    private const int VoiceBase = -970_000, VoicePool = 48;
    /// <summary>A roof is a flat top this high up, with nothing over it.</summary>
    private const float RoofMinHeight = 10f, RoofMinArea = 60f;
    /// <summary>A skein of geese goes over, on average, this often.</summary>
    private const float SkeinEverySeconds = 420f;

    public BirdLife(AudioEngineFacade audio, SpatialAcoustics? acoustics)
    {
        _audio = audio;
        _acoustics = acoustics;
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

    /// <summary>Looks for habitat once the map's static geometry has arrived, and again if it changes.</summary>
    private void Survey(WorldSnapshot world)
    {
        int count = world.Entities.Count;
        if (count == _foundFrom) return;
        _foundFrom = count;
        _perched.Clear();
        foreach (var e in world.Entities.Values)
        {
            var def = e.Definition;
            if (def == null || def.Collider.Shape != ColliderShape.Box || e.Velocity != Vector3.Zero) continue;
            var size = def.Collider.Size;
            if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f) continue;
            float hash = Hash(e.Id);

            if (string.Equals(def.Material.Material, "Foliage", StringComparison.OrdinalIgnoreCase))
            {
                // A sparrow group in one hedge in six, a dove in one in twelve.
                if (hash < 0.16f) _perched.Add(Settle(BirdSpecies.HouseSparrow, e, size, hash, onTop: false));
                else if (hash > 0.92f) _perched.Add(Settle(BirdSpecies.Dove, e, size, hash, onTop: false));
                continue;
            }

            if (!def.Collider.IsSolid) continue;
            float top = e.Transform.Position.Y + size.Y * 0.5f;
            if (top < RoofMinHeight || size.X * size.Z < RoofMinArea) continue;
            if (!NothingAbove(world, e, top)) continue;
            if (hash < 0.5f) _perched.Add(Settle(BirdSpecies.Pigeon, e, size, hash, onTop: true));
            else if (hash > 0.85f) _perched.Add(Settle(BirdSpecies.Crow, e, size, hash, onTop: true));
        }
        Serilog.Log.Information("Birds: {Census}", string.Join(", ", CensusText()));
    }

    private IEnumerable<string> CensusText()
    {
        foreach (var kv in Census) yield return $"{kv.Value} {kv.Key}";
    }

    /// <summary>
    /// Open sky over most of it: a grid of points across the slab, each asked whether anything is
    /// above. A floor inside a tower has the next floor over every point; a roof has sky over nearly
    /// all of it, with a condenser or a stair head here and there — which is why one ray up the middle
    /// found one roof on the whole city.
    /// </summary>
    private bool NothingAbove(WorldSnapshot world, EntitySnapshot e, float top)
    {
        if (_acoustics == null) return true;
        var size = e.Definition.Collider.Size;
        int open = 0, asked = 0;
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                var local = new Vector3((i + 0.5f) / 4f - 0.5f, 0f, (j + 0.5f) / 4f - 0.5f) * new Vector3(size.X, 0f, size.Z);
                var at = e.Transform.Position + Vector3.Transform(local, e.Transform.Rotation);
                var from = new Vector3(at.X, top + 0.3f, at.Z);
                asked++;
                if (!_acoustics.Spatial.RaycastMaterial(world, from, Vector3.UnitY, 40f, out _, out _, out _, e.Id)) open++;
            }
        return open >= asked * 0.6f;
    }

    /// <summary>A group of one species on one perch. Deterministic in the perch, so every client and
    /// every visit finds the same birds in the same places.</summary>
    private Group Settle(BirdSpecies species, EntitySnapshot perch, Vector3 size, float hash, bool onTop)
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
                At = perch.Transform.Position + Vector3.Transform(local, perch.Transform.Rotation),
                Pitch = 1f + species.PitchSpread * (2f * (float)rng.NextDouble() - 1f),
                Repertoire = Pick(calls, rng, 4),
                CallsLeft = 0,
            };
        }
        return new Group { Species = species, Centre = perch.Transform.Position, Birds = birds };
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

    public void Update(WorldSnapshot world, Vector3 listener, double now)
    {
        if (now >= _nextSurvey) { _nextSurvey = now + 2.0; Survey(world); }

        foreach (var g in _perched)
        {
            bool near = Vector3.DistanceSquared(g.Centre, listener) < AwakeRadius * AwakeRadius;
            if (!near) { g.Awake = false; continue; }
            if (!g.Awake)
            {
                // Coming into earshot of a group mid-afternoon, not at its dawn: each bird is
                // somewhere in its own cycle already.
                g.Awake = true;
                foreach (var b in g.Birds)
                    b.Next = now + Uniform(0f, g.Species.BoutGapMax * 0.5f);
            }
            Live(world, g, listener, now);
        }

        UpdateSkein(world, listener, now);
        _lastNow = now;
    }

    private void Live(WorldSnapshot world, Group g, Vector3 listener, double now)
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
            Call(world, sp, b.Repertoire[_rng.Next(b.Repertoire.Length)], b.At, g.Velocity, b.Pitch, listener);
            b.CallsLeft--;
            b.Next = now + (b.CallsLeft > 0 ? Uniform(sp.CallGapMin, sp.CallGapMax) : Uniform(sp.BoutGapMin, sp.BoutGapMax));
            if (b.CallsLeft <= 0) b.RestingSince = now;

            // One breaking out sets others off — once per bout it starts, and only a bird that has
            // rested properly. Pulled on every call instead, a hedge never rested at all: six sparrows
            // chattered ten times a second, which is a machine and not a hedge.
            if (starting && sp.Contagion > 0f)
                foreach (var other in g.Birds)
                    if (other != b && other.CallsLeft <= 0 && now - other.RestingSince >= sp.BoutGapMin
                        && _rng.NextDouble() < sp.Contagion)
                        other.Next = Math.Min(other.Next, now + Uniform(0.4f, 3f));
        }
    }

    /// <summary>
    /// Something made a noise. Anything loud enough where a group is sends it quiet for a while;
    /// the birds come back one at a time as they settle.
    /// </summary>
    public void Heard(WorldAudioEvent message, double now)
    {
        if (message.Sounds == null) return;
        foreach (var s in message.Sounds)
            foreach (var g in _perched)
            {
                if (!g.Awake) continue;
                float d = MathF.Max(1f, Vector3.Distance(s.Position, g.Centre));
                if (s.LevelDb - 20f * MathF.Log10(d) < g.Species.StartleDb) continue;
                g.QuietUntil = Math.Max(g.QuietUntil, now + Uniform(8f, 30f));
                foreach (var b in g.Birds) b.CallsLeft = 0;
            }
    }

    /// <summary>Every call, as it is made: which species and where. For tests and traces.</summary>
    internal Action<string, Vector3>? OnCall { get; set; }

    /// <summary>A call quieter than this at the ear, before anything is in the way, is not sent at
    /// all: the voice budget would drop it, and it would cost an acoustic path to find that out.</summary>
    private const float InaudibleGain = 0.001f;   // -60 dBFS

    private void Call(WorldSnapshot world, BirdSpecies sp, string sound, Vector3 at, Vector3 velocity, float pitch, Vector3 listener)
    {
        float range = Loudness.AudibleRange(sp.CallDb);
        float d = Vector3.Distance(at, listener);
        if (d > range) return;
        var (gain, reference) = Loudness.Place(sp.CallDb);
        if (Loudness.RenderedGain(gain, reference, range, d) < InaudibleGain) return;
        OnCall?.Invoke(sp.Name, at);
        AcousticPathData? path = null;
        if (_acoustics != null)
            try { path = _acoustics.CalculateAcousticPath(world, -1, listener, at); } catch { }
        _audio.Submit(new SpatialEmitter
        {
            EntityId = VoiceBase - (_voice++ % VoicePool),
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
            Velocity = velocity,
            Volume = gain,
            MinDistance = reference,
            Range = range,
            Pitch = pitch,
            IsEvent = true,
            EnableReverb = true,
        });
    }

    // ── Geese ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Now and then a skein of geese goes over: a V, sixty to a hundred metres up, at fourteen metres a
    /// second, honking. It passes somewhere near — not always overhead — and is gone in a minute and a
    /// half, which is how geese are actually met.
    /// </summary>
    private void UpdateSkein(WorldSnapshot world, Vector3 listener, double now)
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
