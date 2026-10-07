using System.Numerics;
using System.Text.Json;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using Serilog;

namespace OpenFPS.Client.Core;

/// <summary>
/// The beacons a listener hears: a short blip from each of the nearest doors, things to pick up, cars
/// to get into and other players, in whichever categories are on. See <see cref="Beacons"/> for who
/// decides that. A player in your own team calls in a tone of their own.
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
        [Beacons.Door] = ("SYNTH/beacon_door_chime", 523f, 12f, 3),
        [Beacons.Item] = ("SYNTH/beacon_item_ring", 1046f, 10f, 3),
        [Beacons.Vehicle] = ("SYNTH/beacon_vehicle_hum", 262f, 25f, 2),
        // One a floor, and only your floor's: the foot of the flight up from it, a stair marker the map
        // put on the landing, and on the roof the top of the flight down (StairCues.FloorBeacons).
        // The bottom and the top of the stairwell alone left "the levels in between" to be found
        // without seeing where the stairs are (Cody, 2026-10-04); every flight end on every floor
        // was two blips a landing and a shaft full of them. Heard to 15 m: on the city's towers a
        // floor's way up is as much as 11.7 m from the stairwell's door, at the far end of the shaft
        // on every other floor, and from that door at 10 m it was silent.
        [Beacons.Stairs] = ("SYNTH/beacon_stairs_steps", 392f, 15f, 2),
        [Beacons.Player] = ("SYNTH/beacon_player_call", 392f, 30f, 4),
    };

    /// <summary>A player in your own team: the player's call on another instrument. See <see cref="TeammateTone"/>.</summary>
    internal const string TeammateSound = "SYNTH/beacon_player_team";

    /// <summary>How often one beacon sounds unless the player says otherwise, seconds.</summary>
    public const double DefaultEvery = 1.6;
    /// <summary>The range the player may set it in, seconds.</summary>
    public const double MinEvery = 0.5, MaxEvery = 10.0;

    /// <summary>A blip at one metre, dB: a doorbell's worth, well under speech and footsteps' echo.</summary>
    private const float BlipDb = 66f;
    /// <summary>The player's own lift on every beacon, dB: 4 up from the start (Cody, 2026-10-02: louder),
    /// 2 a step with /beacons louder and quieter.</summary>
    public const float DefaultLevelDb = 4f, LevelStepDb = 2f, MinLevelDb = -12f, MaxLevelDb = 18f;
    /// <summary>Where a door's beacon hangs: on the face toward you, at face height above its threshold.</summary>
    private const float FaceHeightMetres = 1.6f;

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
    ///
    /// Asked only of a beacon you cannot SEE — see <see cref="InSight"/>. The occlusion figure alone
    /// cannot tell a door round the corner from one in front of you at an angle: a door set into a
    /// facade is partly hidden by its own jamb and reads 0.4 to 0.6 from a few metres off to one side,
    /// and on that figure alone 162 of the city's 470 doors fell silent from four metres out and two
    /// to the side — "I don't hear the beacons for doors now where I heard them before".
    /// </summary>
    private const float MaxOcclusion = 0.5f;

    /// <summary>The map's policies, from its manifest.</summary>
    public void SetMapPolicy(IEnumerable<string>? entries) => _policy = Beacons.ReadPolicies(entries);

    public Beacons.Policy PolicyFor(string category)
        => _policy.TryGetValue(category, out var p) ? p : Beacons.Unset;

    /// <summary>Whether a category is heard right now.</summary>
    public bool IsOn(string? category)
        => string.IsNullOrEmpty(category) || Beacons.IsOn(PolicyFor(category), _prefs.Choice(category));

    /// <param name="selfId">The listener's own body, which is a player like any other on the wire and
    /// must never blip at its own ears; and whose team decides which players are teammates.</param>
    public void Update(WorldSnapshot world, Vector3 listener, double now, int selfId = -1)
    {
        EnsureSounds();
        if (!_registered) return;
        string myTeam = TeamOf(world, selfId);
        foreach (var (category, kind) in Kinds)
        {
            if (!IsOn(category)) continue;
            bool people = category == Beacons.Player;
            foreach (var (id, feet) in Nearest(world, listener, category, kind.Range, kind.Nearest, skip: selfId))
            {
                // A body's position is its feet; a person is heard from their head, as their breath is.
                var at = people ? feet + new Vector3(0f, OtherBodies.HeadHeight, 0f) : feet;
                if (!_next.TryGetValue(id, out double due))
                {
                    // First heard: start on a beat of its own, so two doors side by side do not blip
                    // in unison for ever.
                    _next[id] = now + (Math.Abs(id) % 16) / 16.0 * _prefs.Every;
                    continue;
                }
                if (now < due) continue;
                _next[id] = now + _prefs.Every;
                Blip(world, id, people ? SoundFor(world, id, myTeam) : kind.Sound, at, listener);
            }
        }
    }

    /// <summary>
    /// One blip from a thing picked out with comma or period (<see cref="MapTracker"/>), so the ear
    /// finds what the words described: the door's chime from the door, the item's ring from the item.
    /// Played whether or not that category's beacons are on, since you asked for this one — but not
    /// where the map forbids it. A place has no sound. Through the same path as every blip, so one
    /// on the far side of a wall is not heard through it.
    /// </summary>
    public void Ping(WorldSnapshot world, int id, TrackCategory category, Vector3 listener, int selfId = -1)
    {
        string? kind = category switch
        {
            TrackCategory.Doors or TrackCategory.Entrances => Beacons.Door,
            TrackCategory.Stairs => Beacons.Stairs,
            TrackCategory.Items => Beacons.Item,
            TrackCategory.People => Beacons.Player,
            TrackCategory.Vehicles => Beacons.Vehicle,
            _ => null,
        };
        if (kind == null || PolicyFor(kind) == Beacons.Policy.Forbidden) return;
        if (!world.Entities.TryGetValue(id, out var e)) return;
        EnsureSounds();
        if (!_registered) return;
        var at = e.Transform.Position;
        string sound = Kinds[kind].Sound;
        if (kind == Beacons.Player)
        {
            at += new Vector3(0f, OtherBodies.HeadHeight, 0f);
            sound = SoundFor(world, id, TeamOf(world, selfId));
        }
        Blip(world, id, sound, at, listener);
    }

    private void Blip(WorldSnapshot world, int sourceId, string sound, Vector3 at, Vector3 listener)
    {
        var (gain, reference) = Loudness.Place(BlipDb + (float)_prefs.LevelDb);
        // A door's beacon is a thing fixed to the door at face height, on your side of it, and it rings
        // the room it faces, the one you are in, with that room's reflections. From the middle of the
        // doorway it was in no room at all, and the room it rang was whichever zone the doorway fell to.
        int region = -1;
        if (TryDoorFace(world, sourceId, listener, out var face, out var inRoom))
        {
            at = face;
            if (_acoustics != null) region = _acoustics.GetRegionAt(world, inRoom);
        }
        // A vehicle's beacon is on the side of its body toward you. From where the vehicle rests, the
        // middle of its footprint at road level, it was inside its own floor and doors: every path out
        // read 0.95 blocked, so no vehicle beacon was ever heard (Cody, 2026-10-05: "I'm not sure I
        // hear vehicle beacons").
        else if (TryVehicleSide(world, sourceId, listener, out var side)) at = side;
        // Through the same acoustic path every one-off sound takes: blocked by what is in the way,
        // bent round what it can bend round. The door's own leaf does not block its own blip.
        if (!Reaches(world, sourceId, listener, at, out var path)) return;
        _audio.Submit(new SpatialEmitter
        {
            EntityId = BaseId - (_idx++ % Pool),
            SoundId = sound,
            Mode = OpenFPS.Common.Components.PlaybackMode.Single,
            Position = at,
            ApparentPosition = path?.ApparentPosition ?? at,
            EffectiveDistance = path?.EffectiveDistance ?? Vector3.Distance(listener, at),
            Occlusion = path?.Occlusion ?? 0f,
            EqLow = path?.EqLow ?? 1f, EqMid = path?.EqMid ?? 1f, EqHigh = path?.EqHigh ?? 1f,
            AirLowDb = path?.AirLowDb ?? 0f, AirMidDb = path?.AirMidDb ?? 0f, AirHighDb = path?.AirHighDb ?? 0f,
            ApertureFactor = path?.ApertureFactor ?? 1f,
            TransmissionBleed = path?.TransmissionBleed ?? 0f,
            TargetRegionId = region != -1 ? region : path?.RegionId ?? -1,
            Volume = gain,
            EarLevelDb = BlipDb + (float)_prefs.LevelDb,
            MinDistance = reference,
            Range = 40f,
            IsEvent = true,
            Type = EmitterType.WorldLocked,
        });
    }

    /// <summary>Whether a blip from this beacon reaches you, and by what path. A beacon you can see
    /// always does; one you cannot must be no more than half blocked — round a near corner, not
    /// through a wall.</summary>
    internal bool Reaches(WorldSnapshot world, int sourceId, Vector3 listener, Vector3 at,
                          out OpenFPS.Client.AudioEngine.Data.AcousticPathData? path)
    {
        path = null;
        if (_acoustics == null) return true;
        try { path = _acoustics.CalculateAcousticPath(world, sourceId, listener, at); } catch { }
        return path is not { } blocked || blocked.Occlusion <= MaxOcclusion || InSight(world, sourceId, listener, at);
    }

    /// <summary>
    /// Whether nothing stands between you and the FACE of the thing on your side of it.
    ///
    /// One ray, from a point just off the face toward you, ignoring the thing itself. A door's face
    /// is its thinnest side; stepping off it clears the jamb and the wall it is set into, which is
    /// what made the path model call a door in plain view half-blocked.
    /// </summary>
    private bool InSight(WorldSnapshot world, int sourceId, Vector3 listener, Vector3 at)
    {
        if (_acoustics == null || !world.Entities.TryGetValue(sourceId, out var e)) return false;
        // A vehicle's blip is already off its body, on your side of it (TryVehicleSide): the sight
        // line starts there. Stepped off its narrowest face, as a door's is, it would leave from the
        // car's flank and cut across its bonnet to reach somebody standing in front of it.
        if (IsVehicle(e))
        {
            var ray = listener - at;
            float len = ray.Length();
            return len < 0.05f || !_acoustics.Spatial.RaycastMaterial(world, at, ray / len, len - 0.05f,
                                                                     out _, out _, out _, ignoreEntityId: sourceId);
        }
        var size = e.Definition.Collider.Size;
        Vector3 axis = size.X <= size.Y && size.X <= size.Z ? Vector3.UnitX
                     : size.Z <= size.Y ? Vector3.UnitZ : Vector3.UnitY;
        float half = axis == Vector3.UnitX ? size.X : axis == Vector3.UnitZ ? size.Z : size.Y;
        var normal = Vector3.Transform(axis, e.Transform.Rotation);
        if (Vector3.Dot(listener - at, normal) < 0) normal = -normal;
        var face = at + normal * (0.5f * half + FaceClearance);
        var toEar = listener - face;
        float dist = toEar.Length();
        if (dist < 0.05f) return true;
        return !_acoustics.Spatial.RaycastMaterial(world, face, toEar / dist, dist - 0.05f,
                                                   out _, out _, out _, ignoreEntityId: sourceId);
    }

    /// <summary>A door's face toward the listener at face height, and a point half a metre into the room
    /// in front of it.</summary>
    internal static bool TryDoorFace(WorldSnapshot world, int sourceId, Vector3 listener, out Vector3 face, out Vector3 inRoom)
    {
        face = inRoom = default;
        if (!world.Entities.TryGetValue(sourceId, out var e)
            || !string.Equals(e.Definition.Identity.BeaconCategory, Beacons.Door, StringComparison.OrdinalIgnoreCase)) return false;
        var size = e.Definition.Collider.Size;
        if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f) return false;
        Vector3 axis = size.X <= size.Z ? Vector3.UnitX : Vector3.UnitZ;
        float thick = axis == Vector3.UnitX ? size.X : size.Z;
        var normal = Vector3.Transform(axis, e.Transform.Rotation);
        normal.Y = 0f;
        if (normal.LengthSquared() < 1e-6f) return false;
        normal = Vector3.Normalize(normal);
        var centre = e.Transform.Position;
        if (Vector3.Dot(listener - centre, normal) < 0f) normal = -normal;
        float bottom = centre.Y - size.Y * 0.5f, top = centre.Y + size.Y * 0.5f;
        face = new Vector3(centre.X, MathF.Min(bottom + FaceHeightMetres, top - 0.1f), centre.Z) + normal * (thick * 0.5f + 0.05f);
        inRoom = face + normal * 0.5f;
        return true;
    }

    /// <summary>
    /// Where a vehicle's blip sounds from: the point on its body nearest you, a little out from it,
    /// and between its sills and its roof at the height nearest your ears. The body is the box its
    /// collider gives, resting where the vehicle stands, as VehicleShadow takes it. A line from the
    /// nearest point of a box to you never passes back through the box, so the vehicle does not
    /// block its own beacon wherever round it you stand. False for anything that is not a vehicle
    /// beacon, one with no body, and one you are inside.
    /// </summary>
    internal static bool TryVehicleSide(WorldSnapshot world, int sourceId, Vector3 listener, out Vector3 side)
    {
        side = default;
        if (!world.Entities.TryGetValue(sourceId, out var e) || !IsVehicle(e)) return false;
        var size = e.Definition.Collider.Size;
        if (size.X <= 0f || size.Z <= 0f || Inside(e, listener)) return false;
        var rotation = e.Transform.Rotation;
        var local = Vector3.Transform(listener - e.Transform.Position, Quaternion.Inverse(rotation));
        float hx = size.X * 0.5f + VehicleClearance, hz = size.Z * 0.5f + VehicleClearance;
        float roof = MathF.Max(size.Y, 0.4f);
        float low = MathF.Min(VehicleSillMetres, roof * 0.5f), high = MathF.Max(low, roof - 0.1f);
        var nearest = new Vector3(Math.Clamp(local.X, -hx, hx), Math.Clamp(local.Y, low, high), Math.Clamp(local.Z, -hz, hz));
        side = e.Transform.Position + Vector3.Transform(nearest, rotation);
        return true;
    }

    /// <summary>How far out from a vehicle's body its blip hangs, metres: clear of its panels and
    /// mirrors, and still at the vehicle.</summary>
    private const float VehicleClearance = 0.3f;
    /// <summary>The lowest a vehicle's blip sounds above where it rests: about its sills.</summary>
    private const float VehicleSillMetres = 0.5f;

    private static bool IsVehicle(EntitySnapshot e)
        => string.Equals(e.Definition.Identity.BeaconCategory, Beacons.Vehicle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether you are inside a vehicle's body, in plan and below its roof: sitting in it.
    /// A car you are in is not one you are looking for, and does not take a nearer one's place.</summary>
    internal static bool Inside(EntitySnapshot e, Vector3 listener)
    {
        var size = e.Definition.Collider.Size;
        var local = Vector3.Transform(listener - e.Transform.Position, Quaternion.Inverse(e.Transform.Rotation));
        return MathF.Abs(local.X) < size.X * 0.5f && MathF.Abs(local.Z) < size.Z * 0.5f
            && local.Y > -0.5f && local.Y < MathF.Max(size.Y, 0.4f) + 0.5f;
    }

    /// <summary>How far off a face to start the sight line: past the half-thickness of any wall on
    /// the city (brick is 175 mm), so the wall a door is set into is not what the ray hits.</summary>
    private const float FaceClearance = 0.35f;

    /// <summary>The team a body says it is in, or "" for none or no such body.</summary>
    internal static string TeamOf(WorldSnapshot world, int id)
        => id >= 0 && world.Entities.TryGetValue(id, out var e) ? e.Definition.Team ?? "" : "";

    /// <summary>
    /// Which call a player's beacon makes: the teammate's when they are in the team you are in, the
    /// player's otherwise. Nobody is a teammate of a listener in no team, and two players in no team
    /// are not on the same side.
    /// </summary>
    internal static string SoundFor(WorldSnapshot world, int id, string myTeam)
        => myTeam.Length > 0 && string.Equals(TeamOf(world, id), myTeam, StringComparison.OrdinalIgnoreCase)
            ? TeammateSound : Kinds[Beacons.Player].Sound;

    /// <summary>The nearest things of a category within reach, and where to blip them from.</summary>
    private static List<(int Id, Vector3 At)> Nearest(WorldSnapshot world, Vector3 listener, string category, float range, int count,
                                                      int skip = -1)
    {
        var found = new List<(int Id, Vector3 At, float D)>();
        var floorBeacons = category == Beacons.Stairs ? StairCues.FloorBeacons(world) : null;
        void Consider(EntitySnapshot e)
        {
            if (e.Id == skip) return;
            if (!string.Equals(e.Definition.Identity.BeaconCategory, category, StringComparison.OrdinalIgnoreCase)) return;
            // At the middle of the thing, and at ear height at most: a door's blip comes from the
            // door, not from the floor under it.
            var at = e.Transform.Position;
            float d = Vector3.Distance(listener, at);
            if (d > range) return;
            // A stairwell is open from bottom to top, so the floors above and below are in sight and
            // a few metres off: nearer, from a corridor door, than your own floor's stairs at the far
            // end of the landing. They are not the way up from here.
            if (floorBeacons != null && (!floorBeacons.Contains(e.Id) || MathF.Abs(at.Y - listener.Y) > StairCues.OtherFloorMetres))
                return;
            if (category == Beacons.Vehicle && Inside(e, listener)) return;
            found.Add((e.Id, at, d));
        }
        if (world.StaticGrid != null)
            foreach (int id in world.StaticGrid.GetItemsInRadius(listener, range))
                if (world.Entities.TryGetValue(id, out var e)) Consider(e);
        // A door leaf that swings, a car that has been driven: things that move are not in the grid.
        foreach (var e in world.DynamicEntities) Consider(e);
        // Nor is a marker nothing can bump into, such as the end of a flight of stairs.
        foreach (int id in world.MarkerEntityIds)
            if (world.Entities.TryGetValue(id, out var e)) Consider(e);
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
            float[] pcm = Tone(category, rate);
            ok &= _audio.RegisterSynthesisedSoundFloat(kind.Sound, pcm, rate);
        }
        ok &= _audio.RegisterSynthesisedSoundFloat(TeammateSound, TeammateTone(rate), rate);
        _registered = ok;
    }

    /// <summary>
    /// What each kind of beacon sounds like: soft sine notes, told apart by their SHAPE as much as their
    /// pitch, so a glance of an ear says which it is (Cody, 2026-09-29: "unique sine, easy on the ears
    /// ... unobtrusive but easily picked out when nearby").
    ///
    /// Two rules from the ones before. Nothing high and chirpy: a clean high beep repeating by a
    /// doorway IS a pedestrian crossing's chirp, and was heard as one, so everything here sits between
    /// middle C and the C two octaves up. And nothing clicks: every note rises over a few milliseconds
    /// and dies away rather than stopping, with a quiet octave above it for warmth.
    ///
    ///   door      two notes rising a fourth, C5 then F5 — a soft ding-dong, upward
    ///   vehicle   two low warm pulses on one note, C4 — a hum, twice
    ///   item      one small ring, C6, that dies away — a glass tapped once
    ///   exit      a rising major chord, C5 E5 G5 — the way out
    ///   stairs    four quick notes climbing by tones, G4 A4 B4 C#5 — steps
    ///   waypoint  one slow swell on A4 — somewhere to go
    ///   player    two notes falling a minor third, G4 then E4 — the interval a voice calls a name on
    ///   teammate  the same call, G4 then E4, on another instrument: hollow and reedy, a triangle's odd
    ///             harmonics and the login chime's octave — the same person-shape, one of yours
    /// </summary>
    internal static float[] Tone(string category, int rate) => category switch
    {
        Beacons.Door => Notes(rate, (523.25f, 0.00f, 0.16f, 1.0f), (698.46f, 0.13f, 0.22f, 0.9f)),
        Beacons.Vehicle => Notes(rate, (261.63f, 0.00f, 0.16f, 1.0f), (261.63f, 0.22f, 0.16f, 0.85f)),
        Beacons.Item => Notes(rate, (1046.5f, 0.00f, 0.35f, 0.8f)),
        Beacons.Exit => Notes(rate, (523.25f, 0.00f, 0.13f, 0.9f), (659.25f, 0.10f, 0.13f, 0.9f), (783.99f, 0.20f, 0.25f, 1.0f)),
        Beacons.Stairs => Notes(rate, (392.00f, 0.00f, 0.09f, 0.8f), (440.00f, 0.08f, 0.09f, 0.85f),
                                      (493.88f, 0.16f, 0.09f, 0.9f), (554.37f, 0.24f, 0.14f, 1.0f)),
        Beacons.Player => Call(rate, 1f),
        _ => Swell(rate, 440f, 0.45f),
    };

    /// <summary>
    /// A player in your team: the player's call, the same two notes at the same pitch and in the same
    /// time, played by another instrument (Cody, 2026-10-03: "use that same sound and change the wave
    /// form ... they should just be able to be told apart if they were side by side"). The player's call
    /// is nearly a pure sine; this one carries the octave the login chime has and the odd harmonics a
    /// triangle wave has, which make it hollow and a little reedy without making it bright. Brought to
    /// the player's call's loudness, so the harmonics do not make a teammate louder than a stranger.
    /// </summary>
    internal static float[] TeammateTone(int rate)
    {
        var mate = Notes(rate, 0.012f, TeammateTimbre, (392.00f, 0.00f, 0.14f, 1.0f), (329.63f, 0.15f, 0.22f, 0.9f));
        var player = Call(rate, 1f);
        double rp = 0, rm = 0;
        foreach (var v in player) rp += v * v;
        foreach (var v in mate) rm += v * v;
        float k = (float)Math.Sqrt(rp / Math.Max(rm, 1e-12));
        // ...but never over the 0.9 peak every beacon is made to: the harmonics line up into higher peaks.
        float peak = 1e-6f;
        foreach (var v in mate) peak = MathF.Max(peak, MathF.Abs(v));
        k = MathF.Min(k, 0.9f / peak);
        for (int i = 0; i < mate.Length; i++) mate[i] *= k;
        return mate;
    }

    /// <summary>The teammate's instrument: (harmonic, amplitude). The octave at the login chime's quarter;
    /// the 3rd, 5th and 7th at a triangle wave's 1/n², alternating in sign as a triangle's do.</summary>
    internal static readonly (float Harmonic, float Amplitude)[] TeammateTimbre =
        { (2f, 0.25f), (3f, -1f / 9f), (5f, 1f / 25f), (7f, -1f / 49f) };

    /// <summary>The other beacons' instrument: the octave above at a tenth.</summary>
    private static readonly (float Harmonic, float Amplitude)[] PlainTimbre = { (2f, 0.1f) };

    /// <summary>
    /// The player's call: G4 falling to E4, the falling minor third a voice calls somebody's name on,
    /// and the only figure in the set that falls — every other beacon is a thing, and a person is the
    /// one that calls out. Both notes scaled by <paramref name="ratio"/>, and the timing the same at
    /// any pitch, so the teammate's version is the same rhythm. Each note swells in over 12 ms rather
    /// than the struck 6 ms of the others: a voice calling does not strike, and a low note that comes
    /// in as fast as a chime's reaches most of its height inside two milliseconds, which is a click.
    /// </summary>
    private static float[] Call(int rate, float ratio)
        => Notes(rate, 0.012f, PlainTimbre, (392.00f * ratio, 0.00f, 0.14f, 1.0f), (329.63f * ratio, 0.15f, 0.22f, 0.9f));

    /// <summary>Soft sine notes: (Hz, starts at s, rings for s, level). A 6 ms rise, an exponential
    /// fall over the note, and the octave above at a tenth of the level.</summary>
    private static float[] Notes(int rate, params (float Hz, float At, float Ring, float Level)[] notes)
        => Notes(rate, 0.006f, PlainTimbre, notes);

    /// <summary>The same, with a rise of <paramref name="riseSeconds"/> and the harmonics in <paramref name="timbre"/>.</summary>
    private static float[] Notes(int rate, float riseSeconds, (float Harmonic, float Amplitude)[] timbre,
                                 params (float Hz, float At, float Ring, float Level)[] notes)
    {
        float end = 0f;
        foreach (var n in notes) end = MathF.Max(end, n.At + n.Ring * 1.6f);
        var buf = new float[(int)(end * rate) + 1];
        foreach (var n in notes)
        {
            int a = (int)(n.At * rate), len = (int)(n.Ring * 1.6f * rate);
            for (int i = 0; i < len && a + i < buf.Length; i++)
            {
                float t = i / (float)rate;
                float rise = MathF.Min(1f, t / riseSeconds);
                rise = rise * rise * (3f - 2f * rise);                        // smooth, no click
                float fall = MathF.Exp(-t * 4.6f / n.Ring);                  // -40 dB at 1.0 x Ring
                float tail = i > len - rate * 0.02f ? (len - i) / (rate * 0.02f) : 1f;
                float ph = MathF.Tau * n.Hz * t;
                float w = MathF.Sin(ph);
                foreach (var (h, amp) in timbre) w += amp * MathF.Sin(h * ph);
                buf[a + i] += n.Level * rise * fall * tail * w;
            }
        }
        float peak = 1e-6f;
        foreach (var v in buf) peak = MathF.Max(peak, MathF.Abs(v));
        for (int i = 0; i < buf.Length; i++) buf[i] *= 0.9f / peak;
        return buf;
    }

    /// <summary>One note that swells in and out, raised-cosine, with a slight slow vibrato.</summary>
    private static float[] Swell(int rate, float hz, float seconds)
    {
        var buf = new float[(int)(seconds * rate)];
        double ph = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            float t = i / (float)rate;
            float env = 0.5f - 0.5f * MathF.Cos(MathF.Tau * t / seconds);
            ph += MathF.Tau * hz * (1f + 0.004f * MathF.Sin(MathF.Tau * 5f * t)) / rate;
            buf[i] = 0.9f * env * (float)(Math.Sin(ph) + 0.1 * Math.Sin(2 * ph));
        }
        return buf;
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
            string list = string.Join(". ", parts);
            return char.ToUpperInvariant(list[0]) + list[1..] + $". Each sounds every {_prefs.Every:0.#} seconds,"
                 + $" at {_prefs.LevelDb:+0;-0;0} decibels."
                 + " Say slash beacons and a name to switch one, slash beacons every and a number of seconds,"
                 + " or slash beacons louder or quieter.";
        }

        // /beacons louder | quieter — 2 dB a step, kept with the rest.
        if (args[0].Equals("louder", StringComparison.OrdinalIgnoreCase) || args[0].Equals("quieter", StringComparison.OrdinalIgnoreCase)
            || args[0].Equals("softer", StringComparison.OrdinalIgnoreCase))
        {
            float step = args[0].Equals("louder", StringComparison.OrdinalIgnoreCase) ? LevelStepDb : -LevelStepDb;
            double before = _prefs.LevelDb;
            _prefs.SetLevel(before + step);
            if (_prefs.LevelDb == before)
                return step > 0 ? "Beacons are as loud as they go." : "Beacons are as quiet as they go.";
            return $"Beacons {(step > 0 ? "louder" : "quieter")}, {_prefs.LevelDb:+0;-0;0} decibels.";
        }

        // /beacons every 2.5 — how long between soundings, the player's own.
        if (args[0].Equals("every", StringComparison.OrdinalIgnoreCase) || args[0].Equals("interval", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2 || !double.TryParse(args[1], System.Globalization.NumberStyles.Float,
                                                    System.Globalization.CultureInfo.InvariantCulture, out double every))
                return $"Beacons sound every {_prefs.Every:0.#} seconds. Say slash beacons every and a number of seconds, "
                     + $"from {MinEvery:0.#} to {MaxEvery:0}.";
            _prefs.SetEvery(every);
            _next.Clear();          // start the new rhythm now, not after the old gap
            return $"Beacons sound every {_prefs.Every:0.#} seconds.";
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

    /// <summary>Seconds between one beacon's soundings, the player's choice.</summary>
    public double Every { get; private set; } = BeaconAids.DefaultEvery;
    /// <summary>How much louder than the doorbell level every beacon is, dB, the player's choice.</summary>
    public double LevelDb { get; private set; } = BeaconAids.DefaultLevelDb;

    private BeaconPreferences(string? path, Dictionary<string, bool> choices, double every = BeaconAids.DefaultEvery,
                              double levelDb = BeaconAids.DefaultLevelDb)
    {
        _path = path;
        _choices = choices;
        Every = every;
        LevelDb = levelDb;
    }

    /// <summary>An in-memory store that is never written — for tests.</summary>
    public static BeaconPreferences InMemory() => new(null, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));

    public static BeaconPreferences Load()
    {
        // Beside client.json (see ClientSettings.DefaultPath): %APPDATA%\openfps on Windows. Linux keeps
        // the XDG folder it always used, which is the same place ClientSettings resolves to there.
        string dir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "openfps")
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
                ? Path.Combine(x, "openfps")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "openfps");
        string path = Path.Combine(dir, "beacons.json");
        var choices = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        double every = BeaconAids.DefaultEvery, level = BeaconAids.DefaultLevelDb;
        try
        {
            // One object: a true or false per category, and "every", the seconds between soundings.
            // A file from before the interval existed is the same object without it.
            if (File.Exists(path))
                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (p.Name == "every" && p.Value.ValueKind == JsonValueKind.Number) every = p.Value.GetDouble();
                        else if (p.Name == "level" && p.Value.ValueKind == JsonValueKind.Number) level = p.Value.GetDouble();
                        else if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) choices[p.Name] = p.Value.GetBoolean();
                    }
        }
        catch (Exception ex) { Log.Warning("Beacon preferences at {Path} could not be read: {Error}", path, ex.Message); }
        return new BeaconPreferences(path, choices, Math.Clamp(every, BeaconAids.MinEvery, BeaconAids.MaxEvery),
                                     Math.Clamp(level, BeaconAids.MinLevelDb, BeaconAids.MaxLevelDb));
    }

    public void SetEvery(double seconds)
    {
        Every = Math.Clamp(seconds, BeaconAids.MinEvery, BeaconAids.MaxEvery);
        Save();
    }

    public void SetLevel(double db)
    {
        LevelDb = Math.Clamp(db, BeaconAids.MinLevelDb, BeaconAids.MaxLevelDb);
        Save();
    }

    public bool? Choice(string category) => _choices.TryGetValue(category, out bool on) ? on : null;

    public void Set(string category, bool on)
    {
        _choices[category] = on;
        Save();
    }

    private void Save()
    {
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var all = new Dictionary<string, object>();
            foreach (var (k, v) in _choices) all[k] = v;
            all["every"] = Every;
            all["level"] = LevelDb;
            File.WriteAllText(_path, JsonSerializer.Serialize(all));
        }
        catch (Exception ex) { Log.Warning("Beacon preferences could not be saved to {Path}: {Error}", _path, ex.Message); }
    }
}
