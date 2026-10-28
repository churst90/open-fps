using System.Numerics;
using OpenFPS.Physics;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Physics stage 4, step 6 (docs/PHYSICS_BOUNDARY.md 18.10): Jolt's contacts arrive in a <see cref="ContactStream"/> with
/// the meaning the stage-3 contacts have. Each test steps by ticks the way a host would: begin the stream, tick the world
/// into it, close the stream.
/// </summary>
public class BodyContactsTests
{
    private readonly ITestOutputHelper _out;
    public BodyContactsTests(ITestOutputHelper output) => _out = output;

    private const float BallRadius = 0.1f, BallKg = 0.5f;
    private static readonly float TickSeconds = BodyWorld.StepsPerTick * BodyWorld.StepSeconds;

    private static int Floor(BodyWorld w)
    {
        int shape = w.AddBox(new Vector3(50f, 0.5f, 50f));
        return w.Add(shape, BodyMotion.Static, new Vector3(0f, -0.5f, 0f), Quaternion.Identity, "Concrete");
    }

    private static int Ball(BodyWorld w, float bottom, string material = "Wood")
    {
        int shape = w.AddSphere(BallRadius);
        return w.Add(shape, BodyMotion.Dynamic, new Vector3(0f, bottom + BallRadius, 0f), Quaternion.Identity, material, BallKg);
    }

    private static List<ContactEvent> Tick(BodyWorld w, ContactStream s)
    {
        s.Begin();
        w.Tick(s);
        s.Close();
        return s.Events.ToList();
    }

    [Fact]
    public void ABallDroppedBeginsOnceAtTheSpeedOfItsFall()
    {
        const float height = 2f;
        using var w = new BodyWorld { ReportContacts = true };
        int floor = Floor(w), ball = Ball(w, height);
        var stream = new ContactStream();
        var began = new List<ContactEvent>();
        Vector3 before = default, after = default;
        for (int tick = 0; tick < 60 && began.Count == 0; tick++)
        {
            before = w.Velocity(ball);
            var events = Tick(w, stream);
            after = w.Velocity(ball);
            began.AddRange(events.Where(e => e.Phase == ContactPhase.Began));
            Assert.All(events, e => Assert.NotEqual(ContactPhase.Continued, e.Phase));
        }
        var hit = Assert.Single(began);
        float expectedSpeed = MathF.Sqrt(2f * Earth.StandardGravity * height);
        // The impulse the contact gave over the tick is the change of momentum plus what gravity took in the same time.
        float expectedImpulse = BallKg * (after.Y - before.Y) + BallKg * Earth.StandardGravity * TickSeconds;
        _out.WriteLine($"closing {hit.ClosingSpeed:F4} m/s against sqrt(2gh) {expectedSpeed:F4}; impulse {hit.NormalImpulse:F4} N s against m*dv+m*g*t {expectedImpulse:F4}; "
                       + $"restitution form m*(1+e)*v {BallKg * (1f + w.Restitution(ball)) * expectedSpeed:F4}");
        Assert.Equal(BodyWorld.ContactId(ball), hit.Body);
        Assert.Equal(BodyWorld.ContactId(floor), hit.Other);
        Assert.InRange(hit.ClosingSpeed, expectedSpeed - 0.12f, expectedSpeed + 0.12f);
        Assert.InRange(hit.NormalImpulse, expectedImpulse * 0.98f, expectedImpulse * 1.02f);
        Assert.True(hit.NormalImpulse > BallKg * expectedSpeed * 0.99f, "at least the momentum stopped");
        Assert.InRange(hit.SlipSpeed, 0f, 0.01f);
        // The way out of the floor is up; the point is on the ball's foot.
        Assert.InRange(hit.Normal.Y, 0.999f, 1.001f);
        Assert.InRange(hit.Point.Y, -0.03f, 0.03f);
        Assert.Equal("Wood", hit.Surface);
        Assert.Equal("Concrete", hit.OtherSurface);
    }

    [Fact]
    public void ARestingBallContinuesWithTheWeightItCarries()
    {
        using var w = new BodyWorld { ReportContacts = true };
        Floor(w); Ball(w, 0.001f);
        var stream = new ContactStream();
        // Before it falls asleep (half a second at rest), when no contact is being reported at all.
        for (int i = 0; i < 3; i++) Tick(w, stream);
        var last = new ContactEvent();
        for (int i = 0; i < 6; i++)
        {
            last = Assert.Single(Tick(w, stream));
            Assert.Equal(ContactPhase.Continued, last.Phase);
        }
        float weight = BallKg * Earth.StandardGravity * TickSeconds;
        _out.WriteLine($"resting impulse per tick {last.NormalImpulse:F5} against m*g*t {weight:F5}; closing {last.ClosingSpeed:F4}");
        Assert.InRange(last.NormalImpulse, weight * 0.95f, weight * 1.05f);
    }

    [Fact]
    public void ABallSleepingOnTheFloorStaysInContact()
    {
        using var w = new BodyWorld { ReportContacts = true };
        Floor(w); int ball = Ball(w, 0.001f);
        var stream = new ContactStream();
        // Jolt puts a body to sleep after half a second at rest.
        for (int i = 0; i < 90; i++) Tick(w, stream);
        Assert.True(w.State(ball).Sleeping, "the ball has gone to sleep");
        var e = Assert.Single(Tick(w, stream));
        Assert.Equal(ContactPhase.Continued, e.Phase);
        Assert.Equal(0f, e.NormalImpulse);
    }

    [Fact]
    public void ABallLiftedAwayEnds()
    {
        using var w = new BodyWorld { ReportContacts = true };
        Floor(w); int ball = Ball(w, 0.001f);
        var stream = new ContactStream();
        for (int i = 0; i < 10; i++) Tick(w, stream);
        w.SetVelocity(ball, new Vector3(0f, 4f, 0f));
        ContactEvent? ended = null;
        for (int i = 0; i < 10 && ended == null; i++)
            foreach (var e in Tick(w, stream)) if (e.Phase == ContactPhase.Ended) ended = e;
        Assert.NotNull(ended);
        Assert.Equal(0f, ended!.Value.NormalImpulse);
        Assert.Empty(Tick(w, stream));
    }

    [Fact]
    public void ABoxSlidingAlongTheFloorSlips()
    {
        using var w = new BodyWorld { ReportContacts = true };
        Floor(w);
        int shape = w.AddBox(new Vector3(0.3f, 0.2f, 0.3f));
        int box = w.Add(shape, BodyMotion.Dynamic, new Vector3(0f, 0.2f + 0.001f, 0f), Quaternion.Identity, "Steel", 20f);
        w.SetVelocity(box, new Vector3(3f, 0f, 0f));
        var stream = new ContactStream();
        var first = Assert.Single(Tick(w, stream));
        _out.WriteLine($"slip {first.SlipSpeed:F4} m/s, closing {first.ClosingSpeed:F4}, {first.Surface} on {first.OtherSurface}");
        Assert.InRange(first.SlipSpeed, 2.95f, 3.05f);
        Assert.Equal("Steel", first.Surface);
        Assert.Equal("Concrete", first.OtherSurface);
        // Friction slows it tick by tick; the slip follows.
        for (int i = 0; i < 5; i++) Tick(w, stream);
        var later = Assert.Single(Tick(w, stream));
        Assert.True(later.SlipSpeed < first.SlipSpeed - 0.1f, $"slip {later.SlipSpeed} fell from {first.SlipSpeed}");
    }

    [Fact]
    public void IdsNeverMeetTheEntityIds()
    {
        using var w = new BodyWorld { ReportContacts = true };
        Floor(w); Ball(w, 0.5f);
        var stream = new ContactStream();
        var all = new List<ContactEvent>();
        for (int i = 0; i < 30; i++) all.AddRange(Tick(w, stream));
        Assert.NotEmpty(all);
        Assert.All(all, e =>
        {
            Assert.True(BodyWorld.IsContactId(e.Body) && BodyWorld.IsContactId(e.Other));
            Assert.NotEqual(ContactEvent.NoOther, e.Other);
        });
    }

    private static string Scene(int ticks)
    {
        using var w = new BodyWorld { ReportContacts = true };
        Floor(w);
        var rng = new Random(7);
        int box = w.AddBox(new Vector3(0.25f, 0.25f, 0.25f)), ball = w.AddSphere(0.15f);
        for (int i = 0; i < 12; i++)
        {
            var p = new Vector3(rng.NextSingle() * 1.5f, 0.3f + i * 0.45f, rng.NextSingle() * 1.5f);
            int id = w.Add(i % 2 == 0 ? box : ball, BodyMotion.Dynamic, p, Quaternion.Identity, i % 3 == 0 ? "Steel" : "Wood", 2f + i);
            w.SetVelocity(id, new Vector3(rng.NextSingle() - 0.5f, 0f, rng.NextSingle() - 0.5f));
        }
        var stream = new ContactStream();
        var text = new System.Text.StringBuilder();
        for (int t = 0; t < ticks; t++)
        {
            var events = Tick(w, stream);
            // Ended events come after the tick's own, in the stream's order; the rest are sorted by pair.
            var live = events.Where(e => e.Phase != ContactPhase.Ended).ToList();
            for (int i = 1; i < live.Count; i++)
                Assert.True(live[i - 1].Body < live[i].Body || (live[i - 1].Body == live[i].Body && live[i - 1].Other < live[i].Other), "sorted by pair");
            foreach (var e in events)
                text.Append(t).Append(' ').Append(e.Phase).Append(' ').Append(e.Body).Append('>').Append(e.Other).Append(' ')
                    .Append(BitConverter.SingleToInt32Bits(e.NormalImpulse)).Append(' ').Append(BitConverter.SingleToInt32Bits(e.ClosingSpeed)).Append(' ')
                    .Append(BitConverter.SingleToInt32Bits(e.SlipSpeed)).Append(' ').Append(BitConverter.SingleToInt32Bits(e.Point.X)).Append('\n');
        }
        return text.ToString();
    }

    [Fact]
    public void ThePairsComeInTheSameOrderWithTheSameNumbersEveryRun()
    {
        string first = Scene(150), second = Scene(150);
        Assert.True(first.Length > 1000, "the scene made contacts");
        Assert.Equal(first, second);
    }

    [Fact]
    public void ContactsSurviveTheCollectorBetweenSteps()
    {
        using var w = new BodyWorld { ReportContacts = true };
        Floor(w); Ball(w, 0.3f);
        var stream = new ContactStream();
        int events = 0;
        for (int i = 0; i < 30; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            events += Tick(w, stream).Count;
            GC.Collect(2, GCCollectionMode.Forced, true);
        }
        Assert.True(events > 10, "contacts kept arriving");
    }

    [Fact]
    public void WithReportingOffNothingIsCollectedAndTheStepIsTheSame()
    {
        (float, float) Run(bool on)
        {
            using var w = new BodyWorld { ReportContacts = on };
            Floor(w); int b = Ball(w, 1f);
            var stream = new ContactStream();
            for (int i = 0; i < 40; i++) { stream.Begin(); w.Tick(stream); stream.Close(); if (!on) Assert.Empty(stream.Events); }
            var s = w.State(b);
            return (s.Position.Y, s.Velocity.Y);
        }
        Assert.Equal(Run(false), Run(true));
    }
}
