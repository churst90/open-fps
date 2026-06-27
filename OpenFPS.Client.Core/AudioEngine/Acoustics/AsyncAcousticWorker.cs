using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Data;

namespace OpenFPS.Client.AudioEngine.Acoustics;

public struct AcousticRequest
{
    public int EntityId;
    public Vector3 ListenerPos;
    public Vector3 SourcePos;
    public bool IsImportant;
}

public class AsyncAcousticWorker : IDisposable
{
    private readonly SpatialAcoustics _acoustics;
    private readonly ConcurrentQueue<AcousticRequest> _requestQueue = new();
    private readonly ConcurrentDictionary<int, List<AcousticPathData>> _results = new();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _workerThread;
    
    // We hold a reference to the latest world snapshot to avoid queueing it per-request
    private WorldSnapshot? _latestWorld;
    private readonly object _worldLock = new();

    public AsyncAcousticWorker(SpatialAcoustics acoustics)
    {
        _acoustics = acoustics;
    }

    public void Start()
    {
        if (_workerThread != null) return;
        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "AcousticWorkerThread",
            Priority = ThreadPriority.AboveNormal
        };
        _workerThread.Start();
    }

    public void UpdateWorld(WorldSnapshot world)
    {
        lock (_worldLock)
        {
            _latestWorld = world;
        }
    }

    public void EnqueueRequest(AcousticRequest req)
    {
        _requestQueue.Enqueue(req);
    }

    public bool TryGetResult(int entityId, out List<AcousticPathData> paths)
    {
        return _results.TryGetValue(entityId, out paths!);
    }

    public WorldSnapshot? GetLastWorld()
    {
        lock (_worldLock) { return _latestWorld; }
    }

    private void WorkerLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            if (_requestQueue.TryDequeue(out var req))
            {
                WorldSnapshot? worldSnapshot;
                lock (_worldLock)
                {
                    worldSnapshot = _latestWorld;
                }

                if (worldSnapshot != null)
                {
                    try
                    {
                        var paths = _acoustics.CalculateAcousticPaths(worldSnapshot, req.EntityId, req.ListenerPos, req.SourcePos, req.IsImportant);
                        _results[req.EntityId] = paths;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AcousticWorker] Error processing acoustic path for {req.EntityId}: {ex.Message}");
                    }
                }
            }
            else
            {
                Thread.Sleep(1); // Yield to avoid maxing out CPU when queue is empty
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _workerThread?.Join();
        _cts.Dispose();
    }
}
