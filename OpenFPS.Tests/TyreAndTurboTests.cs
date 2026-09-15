using System;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A tyre that is being asked for more than it has, and an engine with a turbocharger on it.
///
/// Both are properties of the VEHICLE rather than of the map or the audio engine, which is the point:
/// pick a vehicle and its grip, the note it squeals at, its boost and its lag come with it. Nothing
/// here knows about racetracks, corners or this particular map.
/// </summary>
public class TyreAndTurboTests
{
    // ── The friction circle ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Longitudinal and lateral demand combine as a VECTOR, not a sum. A tyre at its cornering limit
    /// has nothing left for braking — which is why trail-braking into a corner makes a car let go,
    /// and why the same tyre can do either alone. Nobody writes that rule down; it is what taking the
    /// magnitude of two components does.
    /// </summary>
    [Fact]
    public void BrakingAndCorneringShareOneFrictionBudget()
    {
        const float grip = 1.0f;
        float g = TyreFriction.G;

        Assert.Equal(1.0f, TyreFriction.Demand(0f, g, grip), 2);        // all of it sideways
        Assert.Equal(1.0f, TyreFriction.Demand(g, 0f, grip), 2);        // all of it braking

        // Half of each is NOT half the budget — it is 0.707 of it, and two of those is over the limit.
        float both = TyreFriction.Demand(g * 0.8f, g * 0.8f, grip);
        Assert.True(both > 1.1f, $"0.8 g of each should exceed a 1 g tyre; got {both:F2}");
    }

    /// <summary>A tyre with more grip is asked for less by the same manoeuvre. The denominator is the
    /// tyre's, so putting slicks on something makes it quieter at the same speed, not louder.</summary>
    [Fact]
    public void MoreGripMeansLessDemandForTheSameCorner()
    {
        float road = TyreFriction.Demand(0f, 12f, TyreProfile.SportsOnAsphalt.PeakGripG);
        float slick = TyreFriction.Demand(0f, 12f, TyreProfile.RaceSlick.PeakGripG);
        Assert.True(slick < road * 0.5f, $"a slick should be far less bothered; road {road:F2}, slick {slick:F2}");
    }

    // ── Chirp, squeal, skid: one curve ──────────────────────────────────────────────────────────

    /// <summary>
    /// Below the onset a tyre rolls silently, at the limit it sings, and past it the note is given up
    /// to a broadband slide. They are not three sounds to be triggered — they are one curve sampled
    /// at three demands, and the handover has to be continuous or a corner entry clicks.
    /// </summary>
    [Fact]
    public void TheSlideCurveIsContinuousAndHandsOver()
    {
        Assert.Equal(0f, TyreFriction.SquealAmount(0.5f), 3);
        Assert.Equal(0f, TyreFriction.SkidAmount(0.5f), 3);

        // At the limit: all squeal, no slide.
        Assert.True(TyreFriction.SquealAmount(1.0f) > 0.9f);
        Assert.True(TyreFriction.SkidAmount(1.0f) < 0.05f);

        // Well past it: all slide, no note left.
        Assert.True(TyreFriction.SkidAmount(1.5f) > 0.95f);
        Assert.True(TyreFriction.SquealAmount(1.5f) < 0.05f);

        // And no step anywhere along it, in either component.
        float prevS = 0f, prevK = 0f;
        for (float d = 0f; d <= 2f; d += 0.01f)
        {
            float sq = TyreFriction.SquealAmount(d), sk = TyreFriction.SkidAmount(d);
            Assert.True(MathF.Abs(sq - prevS) < 0.08f, $"squeal stepped {MathF.Abs(sq - prevS):F3} at demand {d:F2}");
            Assert.True(MathF.Abs(sk - prevK) < 0.08f, $"skid stepped {MathF.Abs(sk - prevK):F3} at demand {d:F2}");
            prevS = sq; prevK = sk;
        }
    }

    /// <summary>The note rises as the tyre is worked harder — each element completes its
    /// stick-deflect-release cycle faster. That slide up is most of what makes it recognisable.</summary>
    [Fact]
    public void TheSquealRisesInPitchTowardTheLimit()
    {
        float low = TyreFriction.SquealPitch(0.8f);
        float high = TyreFriction.SquealPitch(1.05f);
        Assert.True(high > low * 1.1f, $"the note should climb; {low:F2} -> {high:F2}");
    }

    /// <summary>
    /// A shift chirps in proportion to the ratio step and the torque behind it. A short shift at part
    /// throttle does nothing; a full-throttle one-two in something with a close first gear chirps the
    /// tyres. Derived, so any gearbox anyone builds gets it in proportion.
    /// </summary>
    [Fact]
    public void AShiftChirpsInProportionToTheRatioStep()
    {
        Assert.Equal(0f, TyreFriction.ShiftChirp(1.0f, 1f), 3);              // no step, no chirp
        Assert.Equal(0f, TyreFriction.ShiftChirp(1.7f, 0f), 3);              // no torque, no chirp

        float close = TyreFriction.ShiftChirp(1.25f, 1f);
        float wide = TyreFriction.ShiftChirp(1.7f, 1f);
        Assert.True(wide > close * 1.5f, $"a wider step should chirp harder; {close:F2} vs {wide:F2}");
        Assert.True(wide > TyreFriction.SquealOnset, "a full-throttle wide-ratio shift should actually reach the tyre's limit");
    }

    // ── It comes with the vehicle ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Pick a vehicle and its tyres come with it, each describing its own limit and its own note.
    /// Bigger tyres squeal LOWER, because the element that is sticking and slipping is bigger — a
    /// kart shrieks, a truck groans. And a slick has no tread, so it has no block tone at all.
    /// </summary>
    [Fact]
    public void EveryVehicleBringsItsOwnGripAndItsOwnNote()
    {
        var road = VehicleProfile.ByName("v8_muscle").Tyres;
        var slick = VehicleProfile.ByName("nascar_v8").Tyres;
        var truck = VehicleProfile.ByName("diesel_truck").Tyres;

        Assert.True(slick.PeakGripG > road.PeakGripG, "a stock car holds more than a street car");
        Assert.True(truck.PeakGripG < road.PeakGripG, "a loaded truck holds less");

        Assert.True(truck.SquealHz < road.SquealHz, "a truck tyre groans below a car tyre");
        Assert.True(slick.SquealHz < road.SquealHz, "a slick's contact patch is bigger, so its note is lower");
        Assert.Equal(0, slick.TreadBlocks);   // no tread, so no block tone at all

        foreach (var key in VehicleProfile.Presets.Keys)
        {
            var t = VehicleProfile.ByName(key).Tyres;
            Assert.InRange(t.PeakGripG, 0.2f, 6f);
            Assert.InRange(t.SquealHz, 200f, 2500f);
            Assert.True(t.SquealDb > t.ReferenceDb, $"{key}: a sliding tyre is louder than a rolling one");
        }
    }

    // ── Forced induction ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A turbocharged engine is not a naturally aspirated one with a whistle added. Compression comes
    /// down because you cannot run eleven-to-one on boost; the torque arrives far earlier and that is
    /// what makes it feel and sound lazy; and the engine is geared longer to suit, so it spends a
    /// given speed at fewer revs. All consequence, not styling.
    /// </summary>
    [Fact]
    public void ABoostedEngineIsADifferentEngineNotAnEffect()
    {
        var na = VehicleProfile.ByName("i4_sport");
        var turbo = VehicleProfile.ByName("i4_turbo");

        Assert.Equal(Induction.NaturallyAspirated, na.Engine.Induction);
        Assert.Equal(Induction.Turbocharged, turbo.Engine.Induction);
        Assert.True(turbo.Engine.BoostBar > 1f);

        Assert.True(turbo.Engine.CompressionRatio < na.Engine.CompressionRatio - 1.5f,
            "boost and high compression do not go together");
        Assert.True(turbo.Engine.PeakTorqueNm > na.Engine.PeakTorqueNm * 1.4f, "boost is torque");
        Assert.True(turbo.Engine.PeakTorqueRpm < na.Engine.PeakTorqueRpm * 0.75f, "and it arrives early");
        Assert.True(turbo.Engine.Mechanical.TurboWhistleLevel > 0f, "there should be something to hear");
        Assert.True(turbo.Engine.Mechanical.TurboLagSeconds > 0f, "and it should take a moment to arrive");

        // Longer geared: the same road speed at fewer revs in top.
        Assert.True(turbo.Gearbox.RpmFor(50f, turbo.Gearbox.TopGear) < na.Gearbox.RpmFor(50f, na.Gearbox.TopGear),
            "torque low down means taller gearing, which is why a turbo car sounds lazy");
    }

    // ── The live path, end to end ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The voice the GAME renders — the same DSP state the mixer drives — has to actually get louder
    /// when the road is working its tyres. This runs it offline, which is what its Render is for.
    ///
    /// Worth testing at this level rather than trusting the unit below it: the slip has to cross a
    /// volatile field, survive being smoothed twice, and land in a synthesis that is also producing
    /// an engine. A model that is right and a plumbing run that drops it sound identical from here.
    /// </summary>
    [Fact]
    public void ACarAtTheLimitIsAudiblyLouderThanOneCruising()
    {
        float Rms(float slip)
        {
            var voice = new OpenFPS.Client.AudioEngine.Fmod.EngineVoiceState(
                VehicleProfile.ByName("nascar_v8"), 44100f, seed: 4);
            voice.PlaceAtSpeed(60f);
            voice.TargetSpeed = 60f;
            voice.RoadSlip = slip;
            // The tyres are mixed about thirty decibels under the exhaust, which is right — a stock
            // car's engine is 124 dB at a metre and its tyres 99 even when sliding — and it means the
            // whole voice's level barely moves whatever the tyres do. That is a MIX balance, not the
            // thing under test: what is under test is whether the slip reaches the synthesis at all,
            // so the tyres are brought up to where they can be measured through the same plumbing.
            voice.TyreMix = 40f;
            // Long enough for the asymmetric smoothing inside the synthesis to settle.
            var warm = new float[44100 * 2];
            voice.Render(warm);
            var buf = new float[44100];
            voice.Render(buf);
            double acc = 0;
            foreach (float x in buf) acc += x * x;
            return (float)Math.Sqrt(acc / buf.Length);
        }

        float cruising = Rms(0.2f);
        float atTheLimit = Rms(1.0f);
        float sliding = Rms(1.5f);

        Assert.True(cruising > 0f, "a car at speed has to be making SOME noise");
        Assert.True(atTheLimit > cruising * 1.15f,
            $"a car at the limit should be audibly louder than one cruising; {cruising:F4} -> {atTheLimit:F4}");
        Assert.True(sliding > cruising * 1.15f,
            $"and a sliding one louder still; {cruising:F4} -> {sliding:F4}");
    }

    /// <summary>Every profile that claims boost has to describe it, or it is a flag with no sound.</summary>
    [Fact]
    public void EveryBoostedEngineDescribesItsTurbo()
    {
        foreach (var key in VehicleProfile.Presets.Keys)
        {
            var e = VehicleProfile.ByName(key).Engine;
            if (e.Induction == Induction.NaturallyAspirated) continue;
            Assert.True(e.BoostBar > 0f, $"{key}: boosted but no boost pressure");
            Assert.True(e.Mechanical.TurboWhistleLevel > 0f || e.Mechanical.BlowerWhineLevel > 0f,
                $"{key}: boosted but silent about it");
        }
    }
}
