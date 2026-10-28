using System.Numerics;
using JoltPhysicsSharp;

namespace OpenFPS.Physics;

/// <summary>
/// Contacts from Jolt into a <see cref="ContactStream"/> (docs/PHYSICS_BOUNDARY.md 18.10): what the solver's listener
/// sees during a step is collected, turned into <see cref="ContactEvent"/>s with the meaning the stage-3 contacts have,
/// and handed over after the steps in a fixed order.
/// </summary>
public sealed partial class BodyWorld
{
    /// <summary>Body ids in a <see cref="ContactEvent"/> are this plus the world's handle, so they never meet the entity ids
    /// the movement and the vehicle step put in the same stream (an entity id is a small count; -1 is
    /// <see cref="ContactEvent.NoOther"/>).</summary>
    public const int ContactIdBase = 1 << 30;

    /// <summary>The id a body has in a <see cref="ContactEvent"/>.</summary>
    public static int ContactId(int bodyId) => ContactIdBase + bodyId;

    /// <summary>Whether an id in a <see cref="ContactEvent"/> is one of this world's bodies.</summary>
    public static bool IsContactId(int id) => id >= ContactIdBase;

    /// <summary>The body handle behind a contact id.</summary>
    public static int BodyOfContactId(int id) => id - ContactIdBase;

    // One contact as the listener saw it, before the step solved it. A is the lower handle; Normal runs from A towards B
    // (the way Jolt moves B out of A).
    private struct Raw
    {
        public int A, B;
        public Vector3 Point, Normal, VelocityA, VelocityB;
        public float Closing, Slip;
    }

    private struct TickPair
    {
        public Vector3 Point, Normal;
        public float Impulse, Closing, Slip;
    }

    // The solver keeps these delegates' pointers and does not own them: fields for the world's life, or the collector frees
    // them under it (the same lesson as the layer tables above).
    private readonly ContactAddedHandler _onAdded;
    private readonly ContactPersistedHandler _onPersisted;
    private readonly object _gate = new();
    private readonly List<Raw> _raw = new();
    private readonly SortedDictionary<(int Body, int Other), TickPair> _tick = new();
    private readonly SortedDictionary<(int Body, int Other), ContactEvent> _live = new();
    private bool _reporting;

    /// <summary>Whether the solver's contacts are collected. Off, the world does exactly what it did before.</summary>
    public bool ReportContacts
    {
        get => _reporting;
        set
        {
            if (_reporting == value) return;
            if (value) { _system.OnContactAdded += _onAdded; _system.OnContactPersisted += _onPersisted; }
            else { _system.OnContactAdded -= _onAdded; _system.OnContactPersisted -= _onPersisted; }
            _reporting = value;
        }
    }

    private void OnContactAdded(PhysicsSystem system, in Body body1, in Body body2, in ContactManifold manifold, ref ContactSettings settings)
        => Collect(body1, body2, manifold);

    private void OnContactPersisted(PhysicsSystem system, in Body body1, in Body body2, in ContactManifold manifold, ref ContactSettings settings)
        => Collect(body1, body2, manifold);

    // Runs inside the step, perhaps on a job thread: it only reads the two bodies and appends under a lock.
    private void Collect(Body body1, Body body2, ContactManifold m)
    {
        if (!_byJolt.TryGetValue(body1.ID.ID, out var first) || !_byJolt.TryGetValue(body2.ID.ID, out var second)) return;
        if (first.Entry.Motion != BodyMotion.Dynamic && second.Entry.Motion != BodyMotion.Dynamic) return;
        uint points = m.PointCount;
        if (points == 0) return;
        var sum = Vector3.Zero;
        for (uint i = 0; i < points; i++) sum += m.GetWorldSpaceContactPointOn1(i) + m.GetWorldSpaceContactPointOn2(i);
        var point = sum / (2f * points);
        var normal = m.WorldSpaceNormal;
        // Velocities as the solver is about to see them: gravity already applied, the contact not yet resolved.
        var va = body1.GetPointVelocity(point);
        var vb = body2.GetPointVelocity(point);
        var rel = va - vb;
        float along = Vector3.Dot(rel, normal);
        var raw = new Raw
        {
            A = first.Id, B = second.Id, Point = point, Normal = normal,
            VelocityA = body1.GetLinearVelocity(), VelocityB = body2.GetLinearVelocity(),
            Closing = MathF.Max(0f, along), Slip = (rel - normal * along).Length(),
        };
        if (raw.A > raw.B)
        {
            (raw.A, raw.B) = (raw.B, raw.A);
            (raw.VelocityA, raw.VelocityB) = (raw.VelocityB, raw.VelocityA);
            raw.Normal = -raw.Normal;
        }
        lock (_gate) _raw.Add(raw);
    }

    private void ForgetContacts(int bodyId, uint joltId)
    {
        _byJolt.Remove(joltId);
        int id = ContactId(bodyId);
        foreach (var key in _live.Keys.Where(k => k.Body == id || k.Other == id).ToList()) _live.Remove(key);
        foreach (var key in _tick.Keys.Where(k => k.Body == id || k.Other == id).ToList()) _tick.Remove(key);
    }

    // After a step: the impulse of each pair from the change in its bodies' velocities across the step, summed into the tick.
    private void FinishStepContacts()
    {
        Raw[] raws;
        lock (_gate)
        {
            if (_raw.Count == 0) return;
            raws = _raw.ToArray();
            _raw.Clear();
        }
        // Jolt's callback order is the job order; ours is not: by pair, then by place.
        Array.Sort(raws, static (x, y) =>
        {
            int c = x.A.CompareTo(y.A);
            if (c == 0) c = x.B.CompareTo(y.B);
            if (c == 0) c = y.Closing.CompareTo(x.Closing);
            if (c == 0) c = x.Point.X.CompareTo(y.Point.X);
            if (c == 0) c = x.Point.Y.CompareTo(y.Point.Y);
            if (c == 0) c = x.Point.Z.CompareTo(y.Point.Z);
            return c;
        });
        // One entry a pair: a mesh body meets a ball in several triangles. The hardest closing one speaks for the pair.
        var pairs = new List<Raw>();
        var slip = new List<float>();
        foreach (var r in raws)
        {
            if (pairs.Count > 0 && pairs[^1].A == r.A && pairs[^1].B == r.B) { slip[^1] = MathF.Max(slip[^1], r.Slip); continue; }
            pairs.Add(r);
            slip.Add(r.Slip);
        }
        // A body's change of momentum, and how the pairs touching it share it: what the other pushed with, along the push.
        var dp = new Dictionary<int, Vector3>();
        var pushes = new Dictionary<int, float>();
        Vector3 Change(int id, Vector3 before)
        {
            if (!dp.TryGetValue(id, out var d))
            {
                var e = _bodies[id];
                d = e.Motion == BodyMotion.Dynamic
                    ? Mass(id) * (_system.BodyInterface.GetLinearVelocity(e.Body.ID) - before)
                    : Vector3.Zero;
                dp[id] = d;
            }
            return d;
        }
        static float Push(Vector3 change, Vector3 pushDirection) => MathF.Max(0f, Vector3.Dot(change, pushDirection));
        foreach (var p in pairs)
        {
            // The push on A runs against Normal, the push on B along it.
            pushes[p.A] = pushes.GetValueOrDefault(p.A) + Push(Change(p.A, p.VelocityA), -p.Normal);
            pushes[p.B] = pushes.GetValueOrDefault(p.B) + Push(Change(p.B, p.VelocityB), p.Normal);
        }
        float Share(int id, Vector3 before, Vector3 pushDirection)
        {
            var change = Change(id, before);
            float own = Push(change, pushDirection), all = pushes[id];
            return all > 0f ? own * MathF.Min(1f, change.Length() / all) : 0f;
        }
        for (int i = 0; i < pairs.Count; i++)
        {
            var p = pairs[i];
            var ea = _bodies[p.A];
            var eb = _bodies[p.B];
            bool aDynamic = ea.Motion == BodyMotion.Dynamic, bDynamic = eb.Motion == BodyMotion.Dynamic;
            // Both moving: the lighter one's change of velocity is the larger, so the better measured.
            bool fromA = aDynamic && (!bDynamic || Mass(p.A) <= Mass(p.B));
            float impulse = fromA ? Share(p.A, p.VelocityA, -p.Normal) : Share(p.B, p.VelocityB, p.Normal);
            // The body is the one that was stepped: the more moving of the two, the lower handle between equals.
            bool aFirst = Rank(ea.Motion) >= Rank(eb.Motion);
            int body = aFirst ? p.A : p.B, other = aFirst ? p.B : p.A;
            // The way out of the other thing: from Other towards Body.
            var normal = aFirst ? -p.Normal : p.Normal;
            var key = (ContactId(body), ContactId(other));
            var was = _tick.TryGetValue(key, out var t) ? t : default;
            _tick[key] = new TickPair
            {
                Point = p.Point, Normal = normal, Impulse = was.Impulse + impulse,
                Closing = MathF.Max(was.Closing, p.Closing), Slip = MathF.Max(was.Slip, slip[i]),
            };
        }
    }

    private static int Rank(BodyMotion m) => m switch { BodyMotion.Dynamic => 2, BodyMotion.Kinematic => 1, _ => 0 };

    /// <summary>
    /// Adds the contacts of the steps since the last delivery to <paramref name="contacts"/>, sorted by (Body, Other): one
    /// event a pair of bodies, with Body the more moving of the two (the lower handle between equals) and ids from
    /// <see cref="ContactId"/>. The numbers mean what the stage-3 contacts' do: Normal is the way out of Other, towards
    /// Body; NormalImpulse (N s) is what passed along it over the steps; ClosingSpeed (m/s) the greatest relative speed along
    /// the normal at the contact point before the solver resolved it; SlipSpeed the greatest relative speed across it.
    /// A pair that touched before and is still touching without a report (a body gone to sleep) continues with zero speeds
    /// and impulse; one that no longer touches is left for <see cref="ContactStream.Close"/> to end.
    /// </summary>
    public void Deliver(ContactStream? contacts)
    {
        Check();
        var events = new List<ContactEvent>();
        foreach (var (key, t) in _tick)
        {
            var ea = _bodies[BodyOfContactId(key.Body)];
            var eb = _bodies[BodyOfContactId(key.Other)];
            var e = new ContactEvent(key.Body, key.Other, t.Point, t.Normal, t.Impulse, t.Closing, t.Slip, ea.Material, eb.Material, ContactPhase.Began);
            _live[key] = e;
            events.Add(e);
        }
        foreach (var key in _live.Keys.ToList())
        {
            if (_tick.ContainsKey(key)) continue;
            var a = _bodies[BodyOfContactId(key.Body)].Body.ID;
            var b = _bodies[BodyOfContactId(key.Other)].Body.ID;
            // Jolt does not look at a pair whose bodies both sleep (or are static), and says it was not in contact: they
            // still touch, until one of them wakes and reports.
            bool asleep = !_system.BodyInterface.IsActive(a) && !_system.BodyInterface.IsActive(b);
            if (!asleep && !_system.WereBodiesInContact(a, b)) { _live.Remove(key); continue; }
            events.Add(_live[key] with { NormalImpulse = 0f, ClosingSpeed = 0f, SlipSpeed = 0f });
        }
        events.Sort(static (x, y) => x.Body != y.Body ? x.Body.CompareTo(y.Body) : x.Other.CompareTo(y.Other));
        if (contacts != null) foreach (var e in events) contacts.Add(e);
        _tick.Clear();
    }
}
