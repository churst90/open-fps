using System.Numerics;
using OpenFPS.Audio.Fmod;
using OpenFPS.Audio.SteamAudio;
using Box = OpenFPS.Audio.SteamAudio.SteamAudioScene.Box;

namespace OpenFPS.Tests;

/// <summary>
/// The late field's answers are the place the listener stood in for them. In a small room the law fitted to them is
/// 0 (a far source rings as loud as a near one); taken out of its doorway into the open air, every car on the road
/// outside was sent into the tail at full until the first trace from outside.
/// </summary>
public class LateLawPlaceTests
{
    private const int Shop = 5, Outside = 0;
    private const long Now = 1_000_000, Fresh = Now - 30_000_000;
    private static readonly Vector3 InTheShop = new(0f, 1.6f, -1f), InTheDoorway = new(0f, 1.6f, 0.5f);

    /// <summary>Sources in the shop, each raising as much late sound as one at the listener: the shop's law is 0.</summary>
    private static LateField.Answer[] ShopAnswers() => Enumerable.Range(0, 4).Select(i =>
        new LateField.Answer(100 + i, 1f, Vector3.UnitX, 0.1f, InTheShop + new Vector3(2f + i, 0f, -1f), Now, Shop)).ToArray();

    /// <summary>Cars on the road, their late sound falling as distance^-4, as traced from the doorway.</summary>
    private static LateField.Answer[] RoadAnswers() => Enumerable.Range(0, 4).Select(i =>
    {
        var at = InTheDoorway + new Vector3(0f, 0f, 20f + 15f * i);
        return new LateField.Answer(200 + i, MathF.Pow(Vector3.Distance(InTheDoorway, at), -4f), -Vector3.UnitZ, 0.6f, at, Now, Outside);
    }).ToArray();

    [Fact]
    public void A_place_left_takes_its_late_law_and_answers_with_it()
    {
        const float enclosureOutside = 0.2f, car = 60f;
        var shop = ShopAnswers();
        Assert.Equal(0f, FmodAudioProvider.LateLaw(shop, InTheShop, Shop, Fresh), 3);

        // A step into the open air before anything is traced there: the shop's answers are fresh but not the listener's.
        float law = FmodAudioProvider.LateLaw(shop, InTheDoorway, Outside, Fresh);
        Assert.True(float.IsNaN(law), $"the shop's law {law:F2} is used outside");
        Assert.DoesNotContain(shop, a => FmodAudioProvider.SpeaksFor(a, Outside, Fresh));
        float standIn = MathF.Pow(car, enclosureOutside);
        float send = FmodAudioProvider.LateSend(car, null, law, enclosureOutside);
        Assert.Equal(standIn, send, 4);
        // Under the shop's law the car 60 m off was sent 25 dB over the stand-in.
        Assert.True(20f * MathF.Log10(FmodAudioProvider.LateSend(car, null, 0f, enclosureOutside) / send) > 24f);

        // Traced from outside, the road's own law and answers.
        var both = shop.Concat(RoadAnswers()).ToArray();
        law = FmodAudioProvider.LateLaw(both, InTheDoorway, Outside, Fresh);
        Assert.Equal(4f, law, 2);
        Assert.Equal(car * MathF.Pow(car, -2f), FmodAudioProvider.LateSend(car, null, law, enclosureOutside), 5);
        Assert.All(RoadAnswers(), a => Assert.True(FmodAudioProvider.SpeaksFor(a, Outside, Fresh)));
    }

    /// <summary>Back in the shop its own answers are used again while fresh, and an old one is not.</summary>
    [Fact]
    public void A_place_s_answers_are_used_in_it_while_fresh()
    {
        var shop = ShopAnswers();
        Assert.All(shop, a => Assert.True(FmodAudioProvider.SpeaksFor(a, Shop, Fresh)));
        var old = shop[0] with { When = Fresh - 1 };
        Assert.False(FmodAudioProvider.SpeaksFor(old, Shop, Fresh));
        Assert.Equal(3f * MathF.Sqrt(0.25f), FmodAudioProvider.LateSend(3f, shop[0] with { Ratio = 0.25f }, float.NaN, 0.5f), 5);
    }

    /// <summary>
    /// The tracer stamps each answer with the place it was asked for with the listener, on its own thread: the shop's
    /// answers are the shop's, and once asked from outside, the next trace's are outside's. Needs Steam Audio.
    /// </summary>
    [SteamAudioEmbreeFact]
    public void The_late_field_stamps_each_answer_with_the_listener_s_place()
    {
        IntPtr ctx = SteamAudioForTests.EmbreeContext;
        var q = Quaternion.Identity;
        using var scene = new SteamAudioScene(ctx);
        // A plastered shop four metres by six, three high, open to the south.
        scene.Build(new List<Box>
        {
            new(new Vector3(0, -0.1f, -3), new Vector3(4, 0.2f, 6), q, "Concrete"),
            new(new Vector3(0, 3.1f, -3), new Vector3(4, 0.2f, 6), q, "Plaster"),
            new(new Vector3(-2.1f, 1.5f, -3), new Vector3(0.2f, 3, 6), q, "Plaster"),
            new(new Vector3(2.1f, 1.5f, -3), new Vector3(0.2f, 3, 6), q, "Plaster"),
            new(new Vector3(0, 1.5f, -6.1f), new Vector3(4, 3, 0.2f), q, "Plaster"),
        });
        using var lf = new LateField(ctx);
        Assert.True(lf.IsValid);
        lf.SetScene(scene);
        var answers = new LateField.Answer[LateField.MaxSources];

        int[] ids = { 1, 2, 3 };
        Vector3[] inShop = { new(-1f, 1.2f, -4f), new(1f, 1.2f, -5f), new(0f, 1.2f, -2.5f) };
        int m = TracedFor(lf, new Vector3(0f, 1.6f, -1.5f), Shop, ids, inShop, answers);
        Assert.True(m > 0, "nothing traced in the shop");
        Assert.All(answers.Take(m), a => Assert.Equal(Shop, a.Place));

        Vector3[] onTheRoad = { new(-10f, 1.2f, 20f), new(10f, 1.2f, 30f), new(0f, 1.2f, 45f) };
        m = TracedFor(lf, new Vector3(0f, 1.6f, 1.5f), Outside, ids, onTheRoad, answers);
        Assert.True(m > 0, "nothing traced outside");
        Assert.All(answers.Take(m), a => Assert.Equal(Outside, a.Place));
        Assert.True(float.IsNaN(FmodAudioProvider.LateLaw(answers.AsSpan(0, m), new Vector3(0f, 1.6f, -1.5f), Shop, long.MinValue)),
            "outside's answers fitted the shop's law");
    }

    /// <summary>Asks for a trace and waits for one begun after the asking: the answers, as many as came.</summary>
    private static int TracedFor(LateField lf, Vector3 ear, int place, int[] ids, Vector3[] at, LateField.Answer[] into)
    {
        int runs = lf.Runs;
        lf.Want(ear, place, ids, at);
        for (int t = 0; t < 600 && lf.Runs < runs + 2; t++) Thread.Sleep(50);
        Assert.True(lf.Runs >= runs + 2, "the late field did not trace in 30 s");
        return lf.CopyAnswers(into);
    }
}
