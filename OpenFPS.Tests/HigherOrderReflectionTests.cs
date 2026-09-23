using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Copies of copies: second- and third-order reflections. Indoors they fuse into the tail; between
/// two facades across a street they are the flutter — the clap handed back and forth, each crossing
/// a street's width later than the last.
/// </summary>
public class HigherOrderReflectionTests
{
    private readonly ITestOutputHelper _o;
    public HigherOrderReflectionTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }

    private static readonly Quaternion Q = Quaternion.Identity;

    /// <summary>Two brick facades 20 m apart, 12 m high, 60 m long, and a road — a street canyon.</summary>
    private static List<EarlyReflections.Solid> Canyon() => new()
    {
        new(new Vector3(0, -0.25f, 0), new Vector3(200, 0.5f, 200), Q, "Asphalt"),
        new(new Vector3(-10.2f, 6f, 0), new Vector3(0.4f, 12f, 60f), Q, "Brick"),
        new(new Vector3(10.2f, 6f, 0), new Vector3(0.4f, 12f, 60f), Q, "Brick"),
    };

    [Fact]
    public void BetweenTwoFacadesTheClapComesBackMoreThanOnce()
    {
        var into = new List<EarlyReflections.Arrival>();
        // A clap on one pavement, heard from a few metres along it.
        var source = new Vector3(-7f, 1.5f, 0f);
        var ear = new Vector3(-7f, 1.6f, 4f);
        EarlyReflections.Find(source, ear, Canyon(), into, maxOrder: EarlyReflections.MaxOrder, separateFirst: true);
        foreach (var a in into.OrderBy(a => a.ExtraDelaySeconds))
            _o.WriteLine($"order {a.Order}: +{a.ExtraDelaySeconds * 1000:F0} ms, gain {a.GainMid:F2}, separate {EarlyReflections.IsSeparateEvent(a)}");
        Assert.Contains(into, a => a.Order >= 2 && EarlyReflections.IsSeparateEvent(a));
        // Each later order is later: the flutter is a train, not a pile.
        var cross = into.Where(a => a.Order >= 1).OrderBy(a => a.Order).ToList();
        Assert.True(cross.Last().ExtraDelaySeconds > cross.First().ExtraDelaySeconds);
    }

    /// <summary>In a closed room the copies of copies are all inside the fusion window — the room's
    /// tail, which the reverb is — so none of them is a separate event.</summary>
    [Fact]
    public void InASmallRoomTheCopiesOfCopiesAreTheTail()
    {
        var room = new List<EarlyReflections.Solid>
        {
            new(new Vector3(0, -0.25f, 0), new Vector3(5, 0.5f, 4), Q, "Concrete"),
            new(new Vector3(0, 2.75f, 0), new Vector3(5, 0.5f, 4), Q, "Concrete"),
            new(new Vector3(2.75f, 1.25f, 0), new Vector3(0.5f, 2.5f, 4), Q, "Concrete"),
            new(new Vector3(-2.75f, 1.25f, 0), new Vector3(0.5f, 2.5f, 4), Q, "Concrete"),
            new(new Vector3(0, 1.25f, 2.25f), new Vector3(5, 2.5f, 0.5f), Q, "Concrete"),
            new(new Vector3(0, 1.25f, -2.25f), new Vector3(5, 2.5f, 0.5f), Q, "Concrete"),
        };
        var into = new List<EarlyReflections.Arrival>();
        // Asked for third order in a closed room anyway (the renderer would not), the copies of copies
        // it finds are the room's DENSE tail: many, close together, and quieter at every order.
        EarlyReflections.Find(new Vector3(-1, 1.5f, 0), new Vector3(1, 1.6f, 0.5f), room, into,
                              maxOrder: EarlyReflections.MaxOrder);
        Assert.DoesNotContain(into, a => a.Order >= 2 && EarlyReflections.IsSeparateEvent(a));
    }

    /// <summary>What it costs on the real city, where it runs for every sound's echoes.</summary>
    [Fact]
    public void OnTheCityItStaysCheap()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out _));
        var solids = new List<EarlyReflections.Solid>();
        foreach (var d in EntityDefinitionFactory.StaticDefinitions(world))
        {
            if (!d.Collider.IsSolid || d.Collider.Shape != OpenFPS.Common.Components.ColliderShape.Box) continue;
            if (!string.IsNullOrEmpty(d.SoundEmitter.SoundId)) continue;
            solids.Add(new EarlyReflections.Solid(d.Transform.Position, d.Collider.Size, d.Transform.Rotation, d.Material.Material));
        }
        var into = new List<EarlyReflections.Arrival>();
        var sw = new Stopwatch();
        double total = 0, worst = 0; int n = 0, higher = 0;
        for (float z = -120f; z <= 40f; z += 4f)
        {
            sw.Restart();
            EarlyReflections.Find(new Vector3(3f, 1.5f, z + 6f), new Vector3(-7.5f, 1.6f, z), solids, into,
                                  maxOrder: EarlyReflections.MaxOrder, separateFirst: true);
            double ms = sw.Elapsed.TotalMilliseconds;
            total += ms; worst = Math.Max(worst, ms); n++;
            higher += into.Count(a => a.Order >= 2);
            if (z == -40f) _o.WriteLine("z=-40 kept: " + string.Join(", ", into.Select(a => $"order {a.Order} +{a.ExtraDelaySeconds * 1000:F0} ms")));
        }
        _o.WriteLine($"{n} searches on Main Street: mean {total / n:F1} ms, worst {worst:F1} ms; {higher} higher-order arrivals kept");
        Assert.True(total / n < 25, $"a search costs {total / n:F1} ms");
    }
}
