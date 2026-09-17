using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;

namespace OpenFPS.Client.AudioEngine.Acoustics;

/// <summary>
/// The walls answering a live engine.
///
/// A vehicle's engine is synthesized in the mixer, so there is no file to play a delayed copy of and
/// the ordinary reflection paths in ClientAudioSystem — which all work by re-triggering the same
/// sound id — have nothing to work with. What it does have is a ring buffer: EngineVoiceState keeps
/// a few seconds of everything it has rendered, and an echo voice is that buffer read back at the
/// delay the mirrored path implies, placed at the image source. The pitch of the echo then glides on
/// its own as the car moves and the path length changes, because the read position is slewed rather
/// than stepped — which is the Doppler of the reflection, and the reason the echo voice carries no
/// velocity of its own.
///
/// This existed only in the lab. The piece that was missing in the game was the geometry: which faces
/// of which walls are worth bouncing off, kept between frames so a reflection holds one voice instead
/// of retriggering as a click every time the world is rescanned.
///
/// The surfaces are built once per distinct world geometry and reused. A face that is small in both
/// directions is dropped before it ever reaches the image-source pass: the Fresnel test inside
/// ImageSource would return almost nothing for it anyway, and on a track made of two hundred wall
/// segments the pruning is the difference between a scan that costs nothing and one that shows.
/// </summary>
public sealed class EngineReflections
{
    /// <summary>A face smaller than this in BOTH in-plane directions is not a mirror at engine
    /// frequencies; it is something sound goes round. Dropping it costs nothing and saves the scan.</summary>
    private const float MinFaceHalfExtent = 1.5f;

    /// <summary>How many walls may answer one car. Two is enough for a track: the retaining wall in
    /// front and whatever big flat thing is behind you.</summary>
    public const int MaxEchoesPerEngine = 2;

    /// <summary>
    /// How many are actually rendered right now — the first thing given up when the mixer runs short.
    ///
    /// A reflection voice is cheap to GENERATE (a delayed read of a ring buffer the engine has
    /// already filled) and expensive to PLACE: it carries the same HRTF convolution and filtering as
    /// any other source. Eight of them alongside four engines is most of a mixer.
    ///
    /// Shedding them before shedding cars is the right order, and getting that order wrong was
    /// audible: with the budget taking engines away first, cars entering the mix pushed out cars that
    /// had not finished passing, so the field sounded like voices swapping rather than like traffic.
    /// A listener notices a car vanishing. Nobody notices its second reflection.
    /// </summary>
    public int EchoesPerEngine { get; set; } = MaxEchoesPerEngine;

    /// <summary>
    /// How many reflection voices may sound AT ONCE, across every source on the map.
    ///
    /// A voice budget, which is a real resource — a reflection is cheap to generate and expensive to
    /// PLACE, carrying the same HRTF convolution and filtering as any other source — and not a rule
    /// about racetracks. What it explicitly is NOT is a limit on how many CARS may be answered. That
    /// was the first attempt and it was wrong: it made "loud things reflect more" a property of a
    /// special case rather than of the physics, so it would have been right on this map and silently
    /// wrong on the next one.
    ///
    /// The budget is spent on the reflections that are actually AUDIBLE, wherever they come from —
    /// see the audibility floor below. A single loud car beside a concrete wall can legitimately take
    /// several of these; thirty distant ones off a soft surface take none, because nobody can hear
    /// them, not because they were counted.
    /// </summary>
    public int MaxReflectionVoices { get; set; } = 16;

    /// <summary>
    /// The level a reflection has to reach to be worth a voice, set each frame so that about
    /// <see cref="MaxReflectionVoices"/> of them clear it.
    ///
    /// This is the generalisation of what used to be a per-car cap. Every candidate reflection on the
    /// map reports what it would actually deliver to the ear — the source's own level, times what the
    /// surface kept, spread over the path it took — and the budget goes to the loudest, whichever
    /// source they belong to. Nothing here knows what a car is, or that this map is a racetrack.
    ///
    /// Sorted rather than servo'd: a control loop that nudged a threshold up and down would hunt, and
    /// hunting is a reflection fading in and out. Taking the Nth value outright settles in one frame
    /// and cannot oscillate. It is one frame stale, which at sixty a second is not a thing anybody
    /// can hear.
    /// </summary>
    public float AudibilityFloor => _audibilityFloor;
    private float _audibilityFloor;
    private readonly List<float> _audibilities = new();

    /// <summary>
    /// Closes the frame: works out the level the next one will demand of a reflection.
    ///
    /// Call once per audio update, after every source has been offered to <see cref="Update"/>.
    /// </summary>
    public void EndFrame()
    {
        if (_audibilities.Count <= MaxReflectionVoices)
        {
            // Everything that wants a voice can have one; the only floor is the one the image-source
            // search already applies for being inaudible in absolute terms.
            _audibilityFloor = 0f;
        }
        else
        {
            _audibilities.Sort();                                  // ascending
            _audibilityFloor = _audibilities[_audibilities.Count - MaxReflectionVoices];
        }
        _audibilities.Clear();
    }

    /// <summary>Beyond this from the listener a surface is not worth testing. A reflection off
    /// something further away than this has a path long enough to belong in the reverb tail.</summary>
    private const float SurfaceSearchRadius = 260f;

    /// <summary>An echo that stops being found fades for this long before its voice is let go, so a
    /// car passing behind a gap in the wall does not click on the way out.</summary>
    private const float ReleaseSeconds = 0.5f;

    private readonly List<ReflectingSurface> _surfaces = new();
    /// <summary>The solid boxes the surfaces came from, kept so a mirrored path can be asked whether
    /// anything is standing in it. Same list, so the mirror and the obstruction can never disagree.</summary>
    private List<OpenFPS.Client.Core.AudioEngine.SteamAudio.SteamAudioScene.Box> _boxes = new();
    private int _builtFrom = -1;
    private int _lastEntityCount = -1;

    private sealed class Echo
    {
        public int VoiceId;
        public float Silent;
        public bool Seen;
        /// <summary>The last reflection this wall gave, so the release can fade that path out rather
        /// than inventing a new one or moving it while it dies.</summary>
        public Reflection Last;
        public float Gain;
    }

    // Keyed by engine entity, then by the surface that answers for it.
    private readonly Dictionary<int, Dictionary<int, Echo>> _voices = new();
    private int _nextVoiceId = EchoVoiceIdBase;

    /// <summary>Echo voices live in their own band of negative ids so nothing else collides with
    /// them and ClientAudioSystem can recognise one on sight.</summary>
    public const int EchoVoiceIdBase = -600000;

    /// <summary>
    /// Rebuilds the reflecting surfaces if the world's static geometry has changed.
    ///
    /// "Changed" is judged by the number of solid boxes, which is cheap and right for a map whose
    /// walls do not move. A map with moving walls would want the checksum instead; nothing in this
    /// game has one yet, and rebuilding two hundred surfaces every frame to catch a case that does
    /// not exist would be the more expensive mistake.
    /// </summary>
    public void SyncGeometry(WorldSnapshot world)
    {
        // Cheap test FIRST. BoxesFromWorld walks every entity and allocates a list; doing that sixty
        // times a second to discover nothing had changed is pure garbage on the game thread.
        if (world.Entities.Count == _lastEntityCount) return;
        _lastEntityCount = world.Entities.Count;

        var boxes = OpenFPS.Client.Core.AudioEngine.SteamAudio.SteamAudioScene.BoxesFromWorld(world);
        if (boxes.Count == _builtFrom) return;
        _builtFrom = boxes.Count;
        _boxes = boxes;

        _surfaces.Clear();
        Span<ReflectingSurface> six = stackalloc ReflectingSurface[6];
        int id = 1;
        foreach (var b in boxes)
        {
            var props = AcousticRegistry.GetProperties(b.Material);
            int n = ImageSource.FacesOfBox(b.Center, b.Size, b.Rotation, props.Absorption, id, six,
                                           props.Scattering);
            id += 6;
            for (int i = 0; i < n; i++)
            {
                var f = six[i];
                if (f.HalfU.Length() < MinFaceHalfExtent && f.HalfV.Length() < MinFaceHalfExtent) continue;
                // Identify the face by the PLANE it lies in, not by which box it came from.
                //
                // A long wall is authored as a row of blocks — the speedway's grandstand is twelve —
                // and a car going past sweeps the bounce point along it at the speed of the car. Keyed
                // per box, that means the reflection is a DIFFERENT surface every time the bounce
                // crosses a seam: the voice answering for it is torn down and a new one started
                // several times a second per car, each one re-entering the Steam Audio pool with its
                // own DSPs while the old one is still fading. Keyed by the plane, the twelve blocks
                // are one wall, the bounce slides along it, and one voice follows it the whole way —
                // which is both what is physically happening and a great deal less work.
                _surfaces.Add(f with { SurfaceId = PlaneId(f) });
            }
        }
    }

    /// <summary>
    /// A stable id for the infinite plane a face lies in: its normal and its distance from the
    /// origin, both quantised so that coplanar faces of neighbouring blocks agree.
    /// </summary>
    private static int PlaneId(in ReflectingSurface f)
    {
        // A tenth of a unit on the normal and a tenth of a metre on the offset: far finer than any
        // wall is crooked, far coarser than floating-point noise between two boxes built the same way.
        int nx = (int)MathF.Round(f.Normal.X * 10f);
        int ny = (int)MathF.Round(f.Normal.Y * 10f);
        int nz = (int)MathF.Round(f.Normal.Z * 10f);
        int d = (int)MathF.Round(Vector3.Dot(f.Normal, f.Centre) * 10f);
        unchecked
        {
            int h = 17;
            h = h * 31 + nx; h = h * 31 + ny; h = h * 31 + nz; h = h * 31 + d;
            return h == 0 ? 1 : h;
        }
    }

    /// <summary>How many reflection voices are alive right now, across every car. Diagnostic.</summary>
    public int VoiceCount { get { int n = 0; foreach (var m in _voices.Values) n += m.Count; return n; } }

    /// <summary>How many reflecting faces the current geometry offers. Diagnostic.</summary>
    public int SurfaceCount => _surfaces.Count;


    /// <summary>
    /// Brings one engine's echo voices up to date. <paramref name="direct"/> is the emitter that was
    /// just submitted for the engine itself; the echoes copy its placement so they attenuate with
    /// distance the same way, and carry only the surface's own share in their volume.
    /// </summary>
    public void Update(int entityId, in SpatialEmitter direct, in AcousticPathData directPath,
                       Vector3 listener, float speedOfSound, float dt, AudioEngineFacade audio)
    {
        if (_surfaces.Count == 0) return;
        if (!_voices.TryGetValue(entityId, out var mine)) _voices[entityId] = mine = new Dictionary<int, Echo>();
        foreach (var e in mine.Values) e.Seen = false;

        Vector3 source = direct.Position;
        float directDist = MathF.Max(1f, Vector3.Distance(source, listener));

        int want = Math.Clamp(EchoesPerEngine, 0, MaxEchoesPerEngine);
        if (want == 0 && mine.Count == 0) return;
        Span<Reflection> found = stackalloc Reflection[MaxEchoesPerEngine];
        int n = want == 0 ? 0 : Math.Min(want, FirstOrderNear(source, listener, speedOfSound, found));

        for (int i = 0; i < n; i++)
        {
            var r = found[i];
            // FMOD attenuates the mirrored position itself, so the voice's own volume must carry
            // only what the SURFACE did — the extra spreading is already in the placement. Undoing
            // the distance term here and letting the emitter reapply it is what keeps an echo off a
            // far wall quiet without making one off a near wall louder than the car.
            float gain = Math.Clamp(r.Gain * r.PathLength / directDist, 0f, 1f);
            if (gain < ImageSource.MinGain) continue;

            // ── Is this reflection loud enough to be worth a voice? ──────────────────────────
            //
            // Asked of the REFLECTION, not of the car. What arrives at the ear is the source's own
            // level, times what the surface kept, spread over the path it took — so a loud car off a
            // hard wall nearby wins, a quiet one off a soft wall far away loses, and neither of those
            // outcomes is written down anywhere. It falls out of the materials and the geometry, which is
            // the only way it stays true on a map nobody has authored yet.
            float audibility = direct.Volume * gain * MathF.Max(0.1f, direct.MinDistance) / MathF.Max(1f, r.PathLength);
            _audibilities.Add(audibility);
            if (audibility < _audibilityFloor) continue;

            if (!mine.TryGetValue(r.SurfaceId, out var echo))
            {
                echo = new Echo { VoiceId = _nextVoiceId-- };
                mine[r.SurfaceId] = echo;
            }
            // Starting the voice can fail on the frame the car first comes into earshot, because the
            // echo reads the ENGINE's ring buffer and the engine's own voice is created later in the
            // same tick. Asking whether it is playing rather than remembering that we asked for it
            // is what makes the next frame try again instead of leaving a silent wall.
            var e = Make(echo.VoiceId, entityId, direct, r, gain);
            if (audio.IsPlaying(echo.VoiceId)) audio.UpdateSpatialAttributes(e);
            else audio.PlayPhysicalSoundDirect(e);
            ApplyPath(audio, echo.VoiceId, directPath, r, listener);
            echo.Seen = true;
            echo.Silent = 0f;
            echo.Last = r;
            echo.Gain = gain;
        }

        // Walls that have stopped answering fade out and are let go.
        //
        // FADE, not "wait half a second and then cut". The first version of this counted the silence
        // down and then stopped the voice, which left the echo playing at its last gain for the whole
        // release and ended it on a step — a click every time a car passed the end of a wall, which
        // on an oval is several a lap. The gain is ramped to zero across the release instead, and the
        // voice is only stopped once it is already silent.
        List<int>? drop = null;
        foreach (var kv in mine)
        {
            var echo = kv.Value;
            if (echo.Seen) continue;
            echo.Silent += dt;
            if (echo.Silent >= ReleaseSeconds)
            {
                audio.StopSound(echo.VoiceId);
                (drop ??= new List<int>()).Add(kv.Key);
                continue;
            }
            // Hold the last path — only the level goes. Moving a dying echo would Doppler it as it
            // faded, which is a whistle rather than a wall going quiet.
            float fade = 1f - echo.Silent / ReleaseSeconds;
            audio.UpdateSpatialAttributes(Make(echo.VoiceId, entityId, direct, echo.Last, echo.Gain * fade));
            ApplyPath(audio, echo.VoiceId, directPath, echo.Last, listener);
        }
        if (drop != null) foreach (int k in drop) mine.Remove(k);
    }

    /// <summary>Lets go of every echo for one car without forgetting the car itself — for when the
    /// mixer has no room for reflections at all.</summary>
    private static void ReleaseAll(AudioEngineFacade audio, Dictionary<int, Echo> mine)
    {
        foreach (var e in mine.Values) audio.StopSound(e.VoiceId);
        mine.Clear();
    }

    /// <summary>
    /// Everything this engine's echoes were using is released. Call when the car goes.
    ///
    /// These CAN be cut rather than faded: an echo is a delayed copy of the engine, so when the
    /// engine itself is fading out the echo is already following it down a fraction of a second
    /// later. Stopping them together is what keeps the tail from outliving the car.
    /// </summary>
    public void Forget(int entityId, AudioEngineFacade audio)
    {
        if (!_voices.TryGetValue(entityId, out var mine)) return;
        foreach (var e in mine.Values) audio.StopSound(e.VoiceId);
        _voices.Remove(entityId);
    }

    /// <summary>True if this id is one of ours.</summary>
    public static bool IsEchoVoice(int id) => id <= EchoVoiceIdBase;

    /// <summary>
    /// The reflections the world offers a sound at this point, obstruction-tested.
    ///
    /// Public because a wall does not care what made the sound. The crowd in the grandstand, a door
    /// slamming and a rifle going off need exactly this search, and the two things that make it right
    /// — only mirroring through faces near the path, and testing both legs for something standing in
    /// the way — are the two things a caller doing it itself forgets. The transient path DID forget
    /// the second one, and reproduced the fault this class was written to fix: an echo that is never
    /// obstruction-tested keeps sounding off a wall the source can no longer see, so where the direct
    /// sound is blocked the echo is all that is left.
    /// </summary>
    public int FindReflections(Vector3 source, Vector3 listener, float speedOfSound, Span<Reflection> into,
                               int diffuseTaps = 0)
        => FirstOrderNear(source, listener, speedOfSound, into, diffuseTaps);

    private int FirstOrderNear(Vector3 source, Vector3 listener, float speedOfSound, Span<Reflection> into,
                               int diffuseTaps = 0)
    {
        // Only the faces near the path are worth mirroring through. Judged from the midpoint, which
        // is where a specular bounce off anything between the two has to land.
        Vector3 mid = (source + listener) * 0.5f;
        float r2 = SurfaceSearchRadius * SurfaceSearchRadius;
        _near.Clear();
        foreach (var s in _surfaces)
            if (Vector3.DistanceSquared(s.Centre, mid) <= r2) _near.Add(s);
        return ImageSource.FirstOrder(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_near),
                                      source, listener, speedOfSound, into, Blocked, diffuseTaps);
    }

    private readonly List<ReflectingSurface> _near = new();

    /// <summary>
    /// Is there anything standing in this leg of the mirrored path?
    ///
    /// ImageSource has always offered this test and nothing ever passed it, which is most of what "the
    /// cars disappear and I hear the ghost of their reflections" was. A reflection that is never
    /// obstruction-tested keeps sounding at full level off a wall the car can no longer see, so the
    /// moment the direct sound is taken away the echo is all that is left — thirty silenced cars and
    /// twenty bright copies of them.
    ///
    /// Both legs are pulled in slightly before the test, because a bounce point lies exactly ON a
    /// surface and a leg that correctly ends there would otherwise report itself blocked by it.
    /// </summary>
    private bool Blocked(Vector3 a, Vector3 b)
    {
        var boxes = _boxes;
        if (boxes.Count == 0) return false;
        Vector3 d = b - a;
        float len = d.Length();
        if (len < 1e-3f) return false;
        Vector3 unit = d / len;
        const float Skin = 0.05f;   // stand clear of the surfaces at either end
        Vector3 from = a + unit * Skin, to = b - unit * Skin;
        for (int i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            if (GeometryUtils.LineIntersectsOBB(from, to, box.Center, box.Size, box.Rotation)) return true;
        }
        return false;
    }

    /// <summary>
    /// Gives an echo the acoustics of the source it is an echo OF.
    ///
    /// A reflection is the same sound taking a longer route, so whatever is muffling the car muffles
    /// its reflections too. Without this the echoes were the one thing on the map exempt from the
    /// acoustic model — never sent to the worker (their ids are below the threshold step 5 scans),
    /// never given a path, permanently unoccluded and full-bandwidth.
    ///
    /// Distance and delay are the echo's OWN: it has travelled further, so it is placed at the
    /// mirrored source and carries the length of the path it actually took.
    /// </summary>
    private static void ApplyPath(AudioEngineFacade audio, int voiceId, in AcousticPathData directPath,
                                  in Reflection r, Vector3 listener)
    {
        var p = directPath;
        p.ApparentPosition = r.ApparentPosition;
        p.EffectiveDistance = MathF.Max(r.PathLength, Vector3.Distance(listener, r.ApparentPosition));
        p.IsReflection = true;
        audio.SetAcousticPath(voiceId, p);
    }

    private static SpatialEmitter Make(int voiceId, int engineId, in SpatialEmitter direct,
                                       in Reflection r, float gain) => new()
    {
        EntityId = voiceId,
        SoundId = "engine-echo",
        IsSynth = true,
        EchoOfEntity = engineId,
        EchoDelaySeconds = r.DelaySeconds,
        EchoGain = gain,
        EngineKey = "",
        Mode = PlaybackMode.LoopOne,
        Type = EmitterType.WorldLocked,
        Position = r.ApparentPosition,
        ApparentPosition = r.ApparentPosition,
        // The mirrored source is not moving on its own; the glide comes from the delay slewing.
        Velocity = Vector3.Zero,
        Volume = direct.Volume,
        Range = direct.Range,
        MinDistance = direct.MinDistance,
        Pitch = 1f,
        Priority = 2,
        IsReflection = true,
        TargetRegionId = direct.TargetRegionId,
        // A reflection is already a reflection; sending it to the reverb would count the room twice.
        EnableReverb = false,
    };
}
