using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.Core;

namespace OpenFPS.Client.AudioEngine.Acoustics;

public class AcousticPathfinder
{
    private readonly SpatialService _spatial;
    private Dictionary<int, List<(int toRegion, int portalEntityId)>>? _adj;
    private Dictionary<int, Vector3>? _portalPos;
    private Dictionary<int, float>? _portalApertures;
    private AcousticMap? _lastMap;

    private readonly Dictionary<(int, int), (int firstPortalId, float portalToPortalDist, float minAperture, float mLow, float mMid, float mHigh)> _pathCache = new();

    public AcousticPathfinder() : this(new SpatialService()) { }

    public AcousticPathfinder(SpatialService spatial)
    {
        _spatial = spatial;
    }

    public (Vector3 ApparentPos, float EffectiveDist, float MinAperture, float MuffleL, float MuffleM, float MuffleH, bool Found) FindPath(WorldSnapshot world, Vector3 start, Vector3 end)
    {
        if (world.AcousticMap == null) return (end, 0, 1.0f, 0f, 0f, 0f, false);
        
        if (world.AcousticMap != _lastMap)
        {
            RebuildGraph(world.AcousticMap);
            _lastMap = world.AcousticMap;
        }

        int startReg = _spatial.GetRegionAt(world, start);
        int endReg = _spatial.GetRegionAt(world, end);

        // Same-region: no portal routing needed — caller uses the direct unoccluded path.
        if (startReg == endReg && startReg != AcousticConstants.GlobalRegionId)
            return (end, Vector3.Distance(start, end), 1.0f, 0f, 0f, 0f, false);

        if (_adj == null || !_adj.ContainsKey(startReg) || !_adj.ContainsKey(endReg))
            return (end, 0, 1.0f, 0f, 0f, 0f, false);

        var dists = new Dictionary<int, float>();
        var prevs = new Dictionary<int, (int region, int portalId)>();
        var pq = new PriorityQueue<int, float>();

        foreach (var k in _adj.Keys) dists[k] = float.MaxValue;
        dists[startReg] = 0;
        pq.Enqueue(startReg, 0);

        while (pq.Count > 0)
        {
            int curr = pq.Dequeue();
            if (curr == endReg) break;

            foreach (var edge in _adj[curr])
            {
                int next = edge.toRegion;
                Vector3 pPos = _portalPos![edge.portalEntityId];
                float segmentDist = (curr == startReg) ? Vector3.Distance(start, pPos) : Vector3.Distance(_portalPos[prevs[curr].portalId], pPos);
                float newDist = dists[curr] + segmentDist;
                if (newDist < dists[next])
                {
                    dists[next] = newDist;
                    prevs[next] = (curr, edge.portalEntityId);
                    pq.Enqueue(next, newDist + Vector3.Distance(pPos, end));
                }
            }
        }

        if (!prevs.ContainsKey(endReg)) return (end, 0, 1.0f, 0f, 0f, 0f, false);

        int r = endReg;
        int listenerSidePortalId = prevs[r].portalId;
        float p2pDist = 0;
        float minAperture = float.MaxValue;
        float mLow = 0, mMid = 0, mHigh = 0;
        Vector3 lastPoint = end;
        Vector3 lastPortalPos = Vector3.Zero;

        // Trace path from endReg back to startReg (listener side)
        int currentR = endReg;
        while (currentR != startReg)
        {
            if (!prevs.ContainsKey(currentR)) break;

            int pId = prevs[currentR].portalId;
            Vector3 pPos = _portalPos![pId];
            if (lastPortalPos == Vector3.Zero) lastPortalPos = pPos;

            if (lastPoint != end) p2pDist += Vector3.Distance(lastPoint, pPos);
            minAperture = Math.Min(minAperture, _portalApertures![pId]);

            // Add muffle for the region we are passing through
            if (world.AcousticMap.Regions.TryGetValue(currentR, out var reg))
            {
                var matProps = (reg.Materials != null && reg.Materials.Length > 0) ? AcousticRegistry.GetPropertiesByResonanceIndex(reg.Materials[0]) : AcousticRegistry.GetProperties("Generic");
                float dFactor = Vector3.Distance(lastPoint, pPos) / 10.0f;
                mLow += matProps.AbsorptionLow * dFactor;
                mMid += matProps.AbsorptionMid * dFactor;
                mHigh += matProps.AbsorptionHigh * dFactor;
            }

            lastPoint = pPos;
            listenerSidePortalId = pId; // Last portal traced = the one closest to the listener
            currentR = prevs[currentR].region;
        }

        // Fix: Also add muffle for the start region (listener's room)
        if (world.AcousticMap.Regions.TryGetValue(startReg, out var startRegComp))
        {
            var matProps = (startRegComp.Materials != null && startRegComp.Materials.Length > 0) ? AcousticRegistry.GetPropertiesByResonanceIndex(startRegComp.Materials[0]) : AcousticRegistry.GetProperties("Generic");
            float dFactor = Vector3.Distance(lastPoint, start) / 10.0f;
            mLow += matProps.AbsorptionLow * dFactor;
            mMid += matProps.AbsorptionMid * dFactor;
            mHigh += matProps.AbsorptionHigh * dFactor;
        }
        
        float finalTotalDist = p2pDist + Vector3.Distance(lastPoint, start) + Vector3.Distance(end, lastPortalPos);
        Vector3 listenerSidePortalPos = _portalPos![listenerSidePortalId];

        // C3: Correct portal apparent position projection.
        // Project the listener→source line onto the portal plane and clamp to the full aperture radius.
        // This correctly handles oblique listening angles where the old 0.4x cap was too conservative.
        {
            Vector3 lineDir = Vector3.Normalize(end - start); // listener → source
            float t = Vector3.Dot(listenerSidePortalPos - start, lineDir);
            Vector3 closestPointOnLine = start + lineDir * t;
            Vector3 shiftVector = closestPointOnLine - listenerSidePortalPos;
            float shiftMag = shiftVector.Length();
            if (shiftMag > 0.001f)
            {
                float maxShift = minAperture; // Full aperture radius, not 0.4x
                listenerSidePortalPos += (shiftVector / shiftMag) * Math.Min(shiftMag, maxShift);
            }
        }

        return (listenerSidePortalPos, finalTotalDist, minAperture, mLow, mMid, mHigh, true);
    }

    private void RebuildGraph(AcousticMap map)
    {
        _adj = new(); _portalPos = new(); _portalApertures = new();
        foreach (var kvp in map.Portals)
        {
            int portalId = kvp.Key; var portal = kvp.Value.Portal; var pos = kvp.Value.Position;
            if (portal.ApertureSize > 0)
            {
                int rA = portal.RegionAId; int rB = portal.RegionBId;
                if (!_adj.ContainsKey(rA)) _adj[rA] = new();
                if (!_adj.ContainsKey(rB)) _adj[rB] = new();
                _adj[rA].Add((rB, portalId)); _adj[rB].Add((rA, portalId));
                _portalPos[portalId] = pos; _portalApertures[portalId] = portal.ApertureSize;
            }
        }
    }
}
