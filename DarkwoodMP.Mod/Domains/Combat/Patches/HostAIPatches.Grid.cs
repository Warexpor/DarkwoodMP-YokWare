using System.Collections.Generic;
using System.Linq;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    [HarmonyPatch(typeof(WorldGrid), "refreshPosition")]
    public static class HostWorldGridProxyCullPatch
    {
        private static void Postfix(WorldGrid __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (!PlayerPositionManager.HasRemotePlayer)
                return;
            if (__instance == null)
                return;

            float activationRange = GameplayConstants.EntityActivationRange;
            if (__instance.grids != null)
            {
                for (int g = 0; g < __instance.grids.Count; g++)
                {
                    WorldGrid.Grid grid = __instance.grids[g];
                    EnterNodesNearRemotes(grid, activationRange);
                    if (grid != __instance.currentGrid)
                        LeaveNodesNobodyIsNear(grid, activationRange);
                }
            }
            else
                EnterNodesNearRemotes(__instance.currentGrid, activationRange);
        }

        /// <summary>
        /// A grid the host is not on (the World while it is in a location, or another location):
        /// vanilla only ever refreshes the host's own grid, so nodes woken around a client stayed
        /// awake after it moved on, and creatures behind it kept running and chasing from afar.
        /// Nodes no remote is near any more go to sleep.
        /// </summary>
        private static void LeaveNodesNobodyIsNear(WorldGrid.Grid grid, float activationRange)
        {
            if (grid == null || grid.nodes == null)
                return;
            var nodes = grid.nodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!nodes[i].entered)
                    continue;
                Vector2 np = nodes[i].position;
                bool near = false;
                foreach (Vector3 proxyPos in PlayerPositionManager.GetAllRemotePositions())
                {
                    if (Mathf.Abs(proxyPos.x - np.x) <= activationRange && Mathf.Abs(proxyPos.z - np.y) <= activationRange)
                    {
                        near = true;
                        break;
                    }
                }
                if (!near)
                    nodes[i].leave(false);
            }
        }

        internal static void EnterNodesNearRemotes(WorldGrid.Grid grid, float activationRange)
        {
            if (grid == null || grid.nodes == null) return;
            var nodes = grid.nodes;
            foreach (Vector3 proxyPos in PlayerPositionManager.GetAllRemotePositions())
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    Vector2 np = nodes[i].position;
                    // Not forced: a forced enter re-ran Cullable.show() (SetActive, enableComponents)
                    // on every object in every node near a remote at each refresh. Nodes near a
                    // remote are never left (WorldGridNodeLeavePatch), so a plain enter only
                    // shows a node a remote just reached.
                    if (CoopWorldPresencePolicy.ShouldKeepNodeForRemote(true,
                        Mathf.Abs(proxyPos.x - np.x) <= activationRange
                        && Mathf.Abs(proxyPos.z - np.y) <= activationRange))
                        nodes[i].enter(false);
                }
            }
        }
    }

    /// <summary>
    /// Prevents WorldGridNode.leave() from deactivating nodes near a remote.
    /// Vanilla location transport calls <c>Grid.leave()</c> → every node
    /// <c>leave(force: true)</c>. The old force bypass wiped the forest while
    /// the host was in an outside location.
    /// </summary>
    [HarmonyPatch(typeof(WorldGrid.Node), "leave", new[] { typeof(bool) })]
    public static class WorldGridNodeLeavePatch
    {
        private static bool Prefix(WorldGrid.Node __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (!PlayerPositionManager.HasRemotePlayer)
                return true;

            Vector2 np = __instance.position;
            float activationRange = GameplayConstants.EntityActivationRange;

            foreach (Vector3 proxyPos in PlayerPositionManager.GetAllRemotePositions())
            {
                bool proxyNear = Mathf.Abs(proxyPos.x - np.x) <= activationRange
                              && Mathf.Abs(proxyPos.z - np.y) <= activationRange;
                if (CoopWorldPresencePolicy.ShouldKeepNodeForRemote(true, proxyNear))
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Host <c>getNode</c> / register paths key off <c>currentGrid</c> only.
    /// While the host is in a bunker, forest spawns near a client would bind to
    /// the bunker grid. Swap currentGrid to the grid that actually contains the
    /// position for the duration of the call (restored by the patch Finalizer).
    /// Lookups use per-grid node bounds plus a per-cell result cache so the full
    /// inVicinityOfNode walk only runs for new cells inside a grid's bounds.
    /// </summary>
    internal static class HostGridOccupancy
    {
        [System.ThreadStatic]
        private static int _depth; // process-scoped: call-scoped nesting, unwound by Finalizer
        [System.ThreadStatic]
        private static WorldGrid.Grid _saved; // process-scoped: call-scoped, restored by Finalizer

        private struct GridBounds
        {
            public int NodeCount;
            public float MinX, MaxX, MinZ, MaxZ;
        }

        /// <summary>Cell size for the result cache (world units; far below node spacing).</summary>
        private const float CellSize = 8f;
        private const int MaxCachedCells = 4096;
        /// <summary>Floor for the bounds margin; separate grids (interiors) sit far apart.</summary>
        private const float MinMargin = 500f;

        private static readonly Dictionary<WorldGrid.Grid, GridBounds> _bounds =
            new Dictionary<WorldGrid.Grid, GridBounds>();
        private static readonly Dictionary<long, WorldGrid.Grid> _cellResult =
            new Dictionary<long, WorldGrid.Grid>();
        private static WorldGrid _cacheOwner;
        private static WorldGrid.Grid _cacheCurrentGrid;
        private static int _cacheGridCount = -1;

        /// <summary>Drop cached bounds / cell results (session stop, scene change).</summary>
        internal static void ResetCaches()
        {
            _bounds.Clear();
            _cellResult.Clear();
            _cacheOwner = null;
            _cacheCurrentGrid = null;
            _cacheGridCount = -1;
        }

        internal static void PushOccupyingGrid(WorldGrid wg, Vector3 pos, bool search = true)
        {
            if (_depth++ > 0) return;
            if (!search || wg == null) return;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (!PlayerPositionManager.HasRemotePlayer)
                return;

            WorldGrid.Grid found = FindGridContaining(wg, pos);
            if (found != null && found != wg.currentGrid)
            {
                _saved = wg.currentGrid;
                wg.currentGrid = found;
            }
        }

        internal static void PopOccupyingGrid(WorldGrid wg)
        {
            if (_depth <= 0) return;
            _depth--;
            if (_depth == 0 && _saved != null && wg != null)
            {
                wg.currentGrid = _saved;
                _saved = null;
            }
        }

        internal static WorldGrid.Grid FindGridContaining(WorldGrid wg, Vector3 pos)
        {
            if (wg == null || wg.grids == null) return null;

            // Cached answers prefer currentGrid, so they are only valid for the same
            // WorldGrid / currentGrid / grid list.
            if (!ReferenceEquals(_cacheOwner, wg) || _cacheCurrentGrid != wg.currentGrid
                || _cacheGridCount != wg.grids.Count)
            {
                if (!ReferenceEquals(_cacheOwner, wg))
                    _bounds.Clear();
                _cellResult.Clear();
                _cacheOwner = wg;
                _cacheCurrentGrid = wg.currentGrid;
                _cacheGridCount = wg.grids.Count;
            }

            long cell = ((long)Mathf.FloorToInt(pos.x / CellSize) << 32)
                ^ (uint)Mathf.FloorToInt(pos.z / CellSize);
            if (_cellResult.TryGetValue(cell, out WorldGrid.Grid cached))
                return cached;

            WorldGrid.Grid result = null;
            if (GridContains(wg, wg.currentGrid, pos))
                result = wg.currentGrid;
            else
            {
                for (int i = 0; i < wg.grids.Count; i++)
                {
                    WorldGrid.Grid g = wg.grids[i];
                    if (g == null || g == wg.currentGrid) continue;
                    if (GridContains(wg, g, pos))
                    {
                        result = g;
                        break;
                    }
                }
            }

            if (_cellResult.Count >= MaxCachedCells)
                _cellResult.Clear();
            _cellResult[cell] = result;
            return result;
        }

        private static bool GridContains(WorldGrid wg, WorldGrid.Grid g, Vector3 pos)
        {
            if (wg == null || g == null || g.nodes == null) return false;
            if (!InBounds(g, pos)) return false;
            for (int i = 0; i < g.nodes.Count; i++)
            {
                if (wg.inVicinityOfNode(pos, g.nodes[i]))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Cheap reject: node-centre AABB grown by two node spacings (a node's vicinity
        /// never reaches further than its neighbours on a tiled grid), at least MinMargin.
        /// </summary>
        private static bool InBounds(WorldGrid.Grid g, Vector3 pos)
        {
            if (!_bounds.TryGetValue(g, out GridBounds b) || b.NodeCount != g.nodes.Count)
            {
                b = ComputeBounds(g);
                _bounds[g] = b;
            }
            if (b.NodeCount == 0) return false;
            return pos.x >= b.MinX && pos.x <= b.MaxX && pos.z >= b.MinZ && pos.z <= b.MaxZ;
        }

        private static GridBounds ComputeBounds(WorldGrid.Grid g)
        {
            var nodes = g.nodes;
            var b = new GridBounds { NodeCount = nodes.Count };
            if (nodes.Count == 0) return b;
            b.MinX = b.MinZ = float.MaxValue;
            b.MaxX = b.MaxZ = float.MinValue;
            for (int i = 0; i < nodes.Count; i++)
            {
                Vector2 p = nodes[i].position;
                if (p.x < b.MinX) b.MinX = p.x;
                if (p.x > b.MaxX) b.MaxX = p.x;
                if (p.y < b.MinZ) b.MinZ = p.y;
                if (p.y > b.MaxZ) b.MaxZ = p.y;
            }

            // Smallest non-zero axis gap between nodes (sampled) = node spacing.
            float spacing = float.MaxValue;
            int sample = Mathf.Min(nodes.Count, 64);
            for (int i = 0; i < sample; i++)
            {
                Vector2 a = nodes[i].position;
                for (int j = i + 1; j < sample; j++)
                {
                    Vector2 c = nodes[j].position;
                    float dx = Mathf.Abs(a.x - c.x);
                    float dz = Mathf.Abs(a.y - c.y);
                    if (dx > 0.01f && dx < spacing) spacing = dx;
                    if (dz > 0.01f && dz < spacing) spacing = dz;
                }
            }

            if (spacing == float.MaxValue)
            {
                // Single node (or all coincident): unknown vicinity, never reject.
                b.MinX = b.MinZ = float.MinValue;
                b.MaxX = b.MaxZ = float.MaxValue;
                return b;
            }

            float margin = Mathf.Max(spacing * 2f, MinMargin);
            b.MinX -= margin;
            b.MaxX += margin;
            b.MinZ -= margin;
            b.MaxZ += margin;
            return b;
        }
    }

    [HarmonyPatch(typeof(WorldGrid), "getNode")]
    public static class HostWorldGridGetNodeOccupyingPatch
    {
        private static void Prefix(WorldGrid __instance, Vector3 pos)
            => HostGridOccupancy.PushOccupyingGrid(__instance, pos);

        private static void Finalizer(WorldGrid __instance)
            => HostGridOccupancy.PopOccupyingGrid(__instance);
    }

    [HarmonyPatch(typeof(WorldGrid), "registerToNode")]
    public static class HostWorldGridRegisterToNodeOccupyingPatch
    {
        private static void Prefix(WorldGrid __instance, GameObject GO)
        {
            if (GO != null)
                HostGridOccupancy.PushOccupyingGrid(__instance, GO.transform.position);
            else
                HostGridOccupancy.PushOccupyingGrid(__instance, Vector3.zero, search: false);
        }

        private static void Finalizer(WorldGrid __instance)
            => HostGridOccupancy.PopOccupyingGrid(__instance);
    }

    [HarmonyPatch(typeof(WorldGrid), "registerToClosestNode")]
    public static class HostWorldGridRegisterClosestOccupyingPatch
    {
        private static void Prefix(WorldGrid __instance, GameObject GO)
        {
            if (GO != null)
                HostGridOccupancy.PushOccupyingGrid(__instance, GO.transform.position);
            else
                HostGridOccupancy.PushOccupyingGrid(__instance, Vector3.zero, search: false);
        }

        private static void Finalizer(WorldGrid __instance)
            => HostGridOccupancy.PopOccupyingGrid(__instance);
    }

    [HarmonyPatch(typeof(WorldGrid), "registerToNodes")]
    public static class HostWorldGridRegisterToNodesOccupyingPatch
    {
        private static void Prefix(WorldGrid __instance, GameObject GO)
        {
            if (GO != null)
                HostGridOccupancy.PushOccupyingGrid(__instance, GO.transform.position);
            else
                HostGridOccupancy.PushOccupyingGrid(__instance, Vector3.zero, search: false);
        }

        private static void Finalizer(WorldGrid __instance)
            => HostGridOccupancy.PopOccupyingGrid(__instance);
    }
}
