using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;

namespace OpenFPS.Client.AudioEngine.Acoustics;

/// <summary>
/// The walls answering a live engine. An engine is synthesized in the mixer, so there is no file to
/// replay; an echo voice reads the engine's ring buffer (EngineVoiceState) back at the mirrored path's
/// delay, placed at the image source. The read position is slewed, which is the reflection's Doppler,
/// so the echo voice carries no velocity of its own. The faces worth bouncing off are kept between
/// frames so a reflection holds one voice instead of clicking at every rescan.
/// </summary>
public sealed class EngineReflections
{
    /// <summary>A face smaller than this both ways is something sound goes round at engine frequencies,
    /// not a mirror (ImageSource's Fresnel test would give it almost nothing); dropped before the scan.</summary>
    private const float MinFaceHalfExtent = 1.5f;

    /// <summary>Two: the retaining wall in front and whatever big flat thing is behind you.</summary>
    public const int MaxEchoesPerEngine = 2;

    /// <summary>
    /// How many are rendered now: the first thing given up when the mixer runs short. A reflection is
    /// cheap to generate and as dear to place as any source (HRTF, filters); eight with four engines is
    /// most of a mixer. Shed before cars: a listener notices a car vanishing, not its second reflection.
    /// </summary>
    public int EchoesPerEngine { get; set; } = MaxEchoesPerEngine;

    /// <summary>
    /// Reflection voices at once across the map: a voice budget, not a limit on how many cars may be
    /// answered. It goes to the most audible reflections wherever they come from (<see cref="AudibilityFloor"/>):
    /// one loud car by a concrete wall may take several, thirty distant ones off a soft surface none.
    /// </summary>
    public int MaxReflectionVoices { get; set; } = 16;

    /// <summary>
    /// The level a reflection must deliver to the ear to be worth a voice, set each frame so that about
    /// <see cref="MaxReflectionVoices"/> clear it. The Nth value taken outright, not servoed: a control
    /// loop would hunt, and hunting is a reflection fading in and out. One frame stale.
    /// </summary>
    public float AudibilityFloor => _audibilityFloor;
    private float _audibilityFloor;
    private readonly List<float> _audibilities = new();

    /// <summary>Works out the next frame's floor. Once per audio update, after every source has been
    /// offered to <see cref="Update"/>.</summary>
    public void EndFrame()
    {
        if (_audibilities.Count <= MaxReflectionVoices)
        {
            // Everything can have a voice; the image-source search has its own absolute floor.
            _audibilityFloor = 0f;
        }
        else
        {
            _audibilities.Sort();
            _audibilityFloor = _audibilities[_audibilities.Count - MaxReflectionVoices];
        }
        _audibilities.Clear();
    }

    /// <summary>Metres from the path's middle: a reflection off anything further belongs in the reverb tail.</summary>
    private const float SurfaceSearchRadius = 260f;

    /// <summary>An echo no longer found fades this long before its voice goes, so a car passing a gap in
    /// the wall does not click.</summary>
    private const float ReleaseSeconds = 0.8f;

    private readonly List<ReflectingSurface> _surfaces = new();
    /// <summary>The boxes the surfaces came from, for the obstruction test: one list, so the mirror and the
    /// obstruction cannot disagree.</summary>
    private List<OpenFPS.Client.Core.AudioEngine.SteamAudio.SteamAudioScene.Box> _boxes = new();
    private int _builtFrom = -1;
    private int _lastEntityCount = -1;

    private sealed class Echo
    {
        public int VoiceId;
        public float Silent;
        public bool Seen;
        /// <summary>What the release fades out: a dying echo is not moved.</summary>
        public Reflection Last;
        public float Gain;
    }

    // Keyed by engine entity, then by the surface that answers for it.
    private readonly Dictionary<int, Dictionary<int, Echo>> _voices = new();
    private int _nextVoiceId = EchoVoiceIdBase;

    /// <summary>Echo voices have their own band of negative ids, so nothing collides with them and
    /// ClientAudioSystem knows one on sight.</summary>
    public const int EchoVoiceIdBase = -600000;

    /// <summary>
    /// Rebuilds the reflecting surfaces if the static geometry changed, judged by the entity count and then
    /// the number of solid boxes: right while no map has walls that move without changing the count.
    /// </summary>
    public void SyncGeometry(WorldSnapshot world) => SyncGeometry(world, null);

    /// <summary>
    /// With the scene's acoustic triangle world when there is one (SpatialAcoustics.ReflectionWorldFor): a
    /// leg of a mirrored path is then asked of its tree instead of every solid box.
    /// </summary>
    public void SyncGeometry(WorldSnapshot world, OpenFPS.Common.Geometry.TriangleWorld? acoustic)
    {
        _world = acoustic;
        // The cheap test first: BoxesFromWorld walks every entity and allocates, sixty times a second.
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
                // Keyed by the plane, not the box: a long wall is a row of blocks (the speedway's
                // grandstand is twelve), and keyed per box the bounce crossing a seam tore the voice
                // down and started another several times a second per car.
                _surfaces.Add(f with { SurfaceId = PlaneId(f) });
            }
        }
        BuildFaceTree();
    }

    // ── The faces in a tree ─────────────────────────────────────────────────────────────────────
    //
    // A search mirrors through the faces within SurfaceSearchRadius of the path's middle. The tree hands
    // back only those to measure, rather than every face on the map (six a box, 130,000 on Magnolia).
    private OpenFPS.Common.Geometry.BvhNode[]? _faceNodes;
    private int[] _faceOrder = Array.Empty<int>();
    private readonly List<int> _faceHits = new();

    private void BuildFaceTree()
    {
        if (_surfaces.Count == 0) { _faceNodes = null; return; }
        var lo = new Vector3[_surfaces.Count]; var hi = new Vector3[_surfaces.Count];
        for (int i = 0; i < _surfaces.Count; i++)
        {
            var s = _surfaces[i];
            var e = Vector3.Abs(s.HalfU) + Vector3.Abs(s.HalfV);
            lo[i] = s.Centre - e; hi[i] = s.Centre + e;
        }
        _faceNodes = OpenFPS.Common.Geometry.BvhBuilder.Build(lo, hi, _surfaces.Count, 4, out _faceOrder);
    }

    /// <summary>In the order the list holds them.</summary>
    private void FacesNear(Vector3 at, float radius, List<int> into)
    {
        into.Clear();
        var nodes = _faceNodes;
        if (nodes == null) return;
        var min = at - new Vector3(radius); var max = at + new Vector3(radius);
        Span<int> stack = stackalloc int[OpenFPS.Common.Geometry.BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref nodes[stack[--sp]];
            if (n.Max.X < min.X || n.Min.X > max.X || n.Max.Y < min.Y || n.Min.Y > max.Y || n.Max.Z < min.Z || n.Min.Z > max.Z) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++) into.Add(_faceOrder[i]);
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
        into.Sort();
    }

    /// <summary>
    /// A stable id for the infinite plane a face lies in: its normal and its distance from the
    /// origin, both quantised so that coplanar faces of neighbouring blocks agree.
    /// </summary>
    private static int PlaneId(in ReflectingSurface f)
    {
        // A tenth on the normal and a tenth of a metre on the offset: finer than any wall is crooked,
        // coarser than floating-point noise between two boxes built the same way.
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

    /// <summary>Reflection voices alive now, across every car. Diagnostic.</summary>
    public int VoiceCount { get { int n = 0; foreach (var m in _voices.Values) n += m.Count; return n; } }

    /// <summary>Reflecting faces in the current geometry. Diagnostic.</summary>
    public int SurfaceCount => _surfaces.Count;

    /// <summary>
    /// Brings one engine's echo voices up to date. They copy <paramref name="direct"/>, the engine's own
    /// emitter, so they attenuate the same way, with only the surface's share in their volume. With
    /// <paramref name="traced"/> (FmodAudioProvider.HasTracedEchoes) the mirror images fade out: the trace
    /// has them and every order past them.
    /// </summary>
    public void Update(int entityId, in SpatialEmitter direct, in AcousticPathData directPath,
                       Vector3 listener, float speedOfSound, float dt, AudioEngineFacade audio, bool traced = false)
    {
        if (_surfaces.Count == 0) return;
        if (!_voices.TryGetValue(entityId, out var mine)) _voices[entityId] = mine = new Dictionary<int, Echo>();
        foreach (var e in mine.Values) e.Seen = false;

        Vector3 source = direct.Position;
        float directDist = MathF.Max(1f, Vector3.Distance(source, listener));

        int want = traced ? 0 : Math.Clamp(EchoesPerEngine, 0, MaxEchoesPerEngine);
        if (want == 0 && mine.Count == 0) return;
        Span<Reflection> found = stackalloc Reflection[MaxEchoesPerEngine];
        int n = want == 0 ? 0 : Math.Min(want, FirstOrderNear(source, listener, speedOfSound, found, nearestPart: false));

        for (int i = 0; i < n; i++)
        {
            var r = found[i];
            // FMOD attenuates the mirrored position, so the volume carries only what the surface did:
            // the distance term is undone here and reapplied by the placement.
            float gain = Math.Clamp(r.Gain * r.PathLength / directDist, 0f, 1f);
            if (gain < ImageSource.MinGain) continue;

            // Asked of the reflection, not the car: the source's level, times what the surface kept, over
            // the path it took. It falls out of the materials and the geometry, on any map.
            float audibility = direct.Volume * gain * MathF.Max(0.1f, direct.MinDistance) / MathF.Max(1f, r.PathLength);
            _audibilities.Add(audibility);
            if (audibility < _audibilityFloor) continue;

            if (!mine.TryGetValue(r.SurfaceId, out var echo))
            {
                echo = new Echo { VoiceId = _nextVoiceId-- };
                mine[r.SurfaceId] = echo;
            }
            // Asked whether it plays, not remembered: on the car's first frame the engine's voice (whose
            // ring the echo reads) is made later in the tick, the start fails, and the next frame retries.
            var e = Make(echo.VoiceId, entityId, direct, r, gain);
            if (audio.IsPlaying(echo.VoiceId)) audio.UpdateSpatialAttributes(e);
            else audio.PlayPhysicalSoundDirect(e);
            ApplyPath(audio, echo.VoiceId, directPath, r, listener, directDist);
            echo.Seen = true;
            echo.Silent = 0f;
            echo.Last = r;
            echo.Gain = gain;
        }

        // Walls that stopped answering fade to zero and only then stop: cut at their last gain, they
        // clicked every time a car passed the end of a wall.
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
            // Only the level goes: moving a dying echo would Doppler it into a whistle.
            float fade = 1f - echo.Silent / ReleaseSeconds;
            audio.UpdateSpatialAttributes(Make(echo.VoiceId, entityId, direct, echo.Last, echo.Gain * fade));
            ApplyPath(audio, echo.VoiceId, directPath, echo.Last, listener, directDist);
        }
        if (drop != null) foreach (int k in drop) mine.Remove(k);
    }

    /// <summary>
    /// Stops this engine's echoes, when the car goes. Cut, not faded: an echo follows its engine's own
    /// fade a fraction of a second later, and stopping them together keeps the tail from outliving the car.
    /// </summary>
    public void Forget(int entityId, AudioEngineFacade audio)
    {
        if (!_voices.TryGetValue(entityId, out var mine)) return;
        foreach (var e in mine.Values) audio.StopSound(e.VoiceId);
        _voices.Remove(entityId);
    }

    public static bool IsEchoVoice(int id) => id <= EchoVoiceIdBase;

    /// <summary>
    /// The reflections the world offers a sound at this point, obstruction-tested, for any source (the
    /// grandstand crowd, a door, a rifle). Use this rather than a search of one's own: the transient path
    /// once skipped the leg test, and its echoes kept sounding off walls the source could not see.
    /// </summary>
    public int FindReflections(Vector3 source, Vector3 listener, float speedOfSound, Span<Reflection> into,
                               int diffuseTaps = 0)
        => FirstOrderNear(source, listener, speedOfSound, into, diffuseTaps, nearestPart: true);

    /// <summary>
    /// Squared, to the face's nearest part, not its centre: the city's ground is one box a kilometre
    /// across, and from its centre the ground under anyone 260 m out was never considered, nor the far
    /// half of any long facade.
    /// </summary>
    internal static float DistanceSquaredToFace(in ReflectingSurface s, Vector3 p)
    {
        Vector3 rel = p - s.Centre;
        float hu = s.HalfU.Length(), hv = s.HalfV.Length();
        Vector3 u = hu > 1e-6f ? s.HalfU / hu : Vector3.Zero, v = hv > 1e-6f ? s.HalfV / hv : Vector3.Zero;
        float du = Vector3.Dot(rel, u), dv = Vector3.Dot(rel, v);
        Vector3 nearest = s.Centre + u * Math.Clamp(du, -hu, hu) + v * Math.Clamp(dv, -hv, hv);
        return Vector3.DistanceSquared(nearest, p);
    }

    /// <summary>
    /// The first-order reflections through the faces near the path: found by their nearest part with
    /// <paramref name="nearestPart"/> (one-off sounds), by their centre otherwise (engines). Engines keep
    /// the centre until their echoes stop phasing: counted along their whole length, long facades gave
    /// every passing car coherent echoes, heard as cars passing "inside out".
    /// </summary>
    private int FirstOrderNear(Vector3 source, Vector3 listener, float speedOfSound, Span<Reflection> into,
                               int diffuseTaps = 0, bool nearestPart = true)
    {
        // Judged from the midpoint, near where a specular bounce between the two has to land.
        Vector3 mid = (source + listener) * 0.5f;
        float r2 = SurfaceSearchRadius * SurfaceSearchRadius;
        _near.Clear();
        FacesNear(mid, SurfaceSearchRadius, _faceHits);
        foreach (int i in _faceHits)
        {
            var s = _surfaces[i];
            if ((nearestPart ? DistanceSquaredToFace(s, mid) : Vector3.DistanceSquared(s.Centre, mid)) <= r2) _near.Add(s);
        }
        return ImageSource.FirstOrder(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_near),
                                      source, listener, speedOfSound, into, Blocked, diffuseTaps);
    }

    private readonly List<ReflectingSurface> _near = new();

    /// <summary>Legs are asked of it when there is one.</summary>
    private OpenFPS.Common.Geometry.TriangleWorld? _world;

    /// <summary>Whether a leg starts inside a solid, as the box test counted a segment wholly inside a box
    /// as crossing it.</summary>
    private static bool Inside(OpenFPS.Common.Geometry.TriangleWorld world, Vector3 p)
    {
        var inside = _inside ??= new List<OpenFPS.Common.Geometry.SolidRef>(4);
        inside.Clear();
        var all = new OpenFPS.Common.Geometry.AcceptAll();
        world.Containing(p, OpenFPS.Common.Geometry.GeometryLayers.Acoustics, ref all, inside);
        return inside.Count > 0;
    }

    [ThreadStatic] private static List<OpenFPS.Common.Geometry.SolidRef>? _inside;

    /// <summary>
    /// Whether anything stands in this leg of a mirrored path. Untested, a reflection sounds off a wall
    /// the car can no longer see ("the cars disappear and I hear the ghost of their reflections"). The
    /// ends are pulled in: a bounce point lies on a surface and would block its own leg.
    /// </summary>
    private bool Blocked(Vector3 a, Vector3 b)
    {
        var boxes = _boxes;
        if (boxes.Count == 0) return false;
        Vector3 d = b - a;
        float len = d.Length();
        if (len < 1e-3f) return false;
        Vector3 unit = d / len;
        const float Skin = 0.05f;
        if (_world is { } world)
        {
            if (len <= 2f * Skin) return false;
            var all = new OpenFPS.Common.Geometry.AcceptAll();
            return world.Any(a + unit * Skin, unit, len - 2f * Skin, OpenFPS.Common.Geometry.GeometryLayers.Acoustics,
                             OpenFPS.Common.Geometry.RayFaces.Both, ref all)
                   || Inside(world, a + unit * Skin);
        }
        Vector3 from = a + unit * Skin, to = b - unit * Skin;
        for (int i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            if (GeometryUtils.LineIntersectsOBB(from, to, box.Center, box.Size, box.Rotation)) return true;
        }
        return false;
    }

    /// <summary>
    /// Gives an echo an acoustic path: the worker never sees echo ids, so without one they were
    /// unoccluded and full-bandwidth. Distance, delay and air are the echo's own: with the car's air, an
    /// echo 120 m round a facade was brighter than the car 40 m away, and brightness is how the ear
    /// judges nearness.
    /// </summary>
    private static void ApplyPath(AudioEngineFacade audio, int voiceId, in AcousticPathData directPath,
                                  in Reflection r, Vector3 listener, float directDist)
    {
        var p = directPath;
        p.ApparentPosition = r.ApparentPosition;
        p.EffectiveDistance = MathF.Max(r.PathLength, Vector3.Distance(listener, r.ApparentPosition));
        (p.AirLowDb, p.AirMidDb, p.AirHighDb) = EchoAir(directPath, directDist, r.PathLength);
        // Not the direct path's blocking: both legs were found clear (Blocked), and copying the direct
        // path's occlusion muffled the echo a second time.
        p.Occlusion = 0f;
        p.EqLow = p.EqMid = p.EqHigh = 1f;
        p.TransmissionBleed = 0f;
        p.IsReflection = true;
        audio.SetAcousticPath(voiceId, p);
    }

    /// <summary>
    /// The air over the echo's path, per band: ISO 9613-1 is proportional to distance, so the direct
    /// path's per metre times the echo's length; the standard atmosphere when the direct path is too short.
    /// </summary>
    internal static (float Low, float Mid, float High) EchoAir(in AcousticPathData direct, float directDist, float pathLength)
    {
        if (directDist < 1f)
            return OpenFPS.Client.AudioEngine.Core.AudioPhysics.AirLossDb(pathLength, 0.5f, 20f, 1013.25f);
        float k = MathF.Max(0f, pathLength) / directDist;
        return (direct.AirLowDb * k, direct.AirMidDb * k, direct.AirHighDb * k);
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
        EchoScattering = r.Scattering,
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
        // Never sent to the reverb (the provider skips reflections): that would count the room twice.
        IsReflection = true,
        TargetRegionId = direct.TargetRegionId,
    };
}
