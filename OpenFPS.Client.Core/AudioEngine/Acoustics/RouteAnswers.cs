using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Acoustics;

/// <summary>
/// Each voice's last answer from the routes through openings (AsyncAcousticWorker.AskRoutes), kept while
/// neither end has moved enough to change it.
///
/// An answer is only good for the graph it was asked of, and it remembers which by holding the graph.
/// The graph holds its scene, and the scene every tile's triangles. A voice that stopped kept its answer,
/// so it kept the graph of its day alive, with every tile of that graph; with streaming a new graph is
/// built at every tile change, and the heap grew some 400 MB a kilometre driven (found by the Resonance
/// team, 2026-10-06). So a new graph lets every answer about an old one go, and a voice the worker
/// forgets takes its answer with it.
/// </summary>
public sealed class RouteAnswers
{
    public readonly record struct Held(OpeningRoutes Model, Vector3 Source, Vector3 Listener, OpeningRoutes.Answer? Answer, long At);

    private readonly Dictionary<int, Held> _held = new();
    private OpeningRoutes? _current;

    /// <summary>For the lab's before-and-after only (AudioLab --stream-walk keep=old): answers kept as they
    /// were before 2026-10-06, about any graph, and a forgotten voice's left behind.</summary>
    public static bool RetainSuperseded { get; set; }

    public int Count => _held.Count;

    /// <summary>The answer kept for a voice, if it was asked of <paramref name="model"/>.</summary>
    public bool TryGet(int id, OpeningRoutes model, out Held held)
        => _held.TryGetValue(id, out held) && ReferenceEquals(held.Model, model);

    /// <summary>Keeps a voice's answer. One asked of a graph that is no longer the current one is not kept.</summary>
    public void Put(int id, Held held)
    {
        if (RetainSuperseded) { _held[id] = held; return; }
        if (_current != null && !ReferenceEquals(held.Model, _current)) return;
        _current ??= held.Model;
        _held[id] = held;
    }

    /// <summary>A new graph: every answer about any other is dropped.</summary>
    public void Published(OpeningRoutes model)
    {
        if (RetainSuperseded) return;
        if (ReferenceEquals(model, _current)) return;
        _current = model;
        if (_held.Count == 0) return;
        var stale = new List<int>();
        foreach (var kv in _held) if (!ReferenceEquals(kv.Value.Model, model)) stale.Add(kv.Key);
        foreach (int id in stale) _held.Remove(id);
    }

    /// <summary>A voice the worker no longer traces.</summary>
    public void Forget(int id) { if (!RetainSuperseded) _held.Remove(id); }

    /// <summary>Drops answers older than <paramref name="maxAgeMs"/> once there are more than
    /// <paramref name="limit"/>.</summary>
    public void Trim(long now, long maxAgeMs, int limit)
    {
        if (_held.Count <= limit) return;
        var old = new List<int>();
        foreach (var kv in _held) if (now - kv.Value.At > maxAgeMs) old.Add(kv.Key);
        foreach (int id in old) _held.Remove(id);
    }
}
