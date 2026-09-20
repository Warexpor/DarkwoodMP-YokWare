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
                    EnterNodesNearRemotes(__instance.grids[g], activationRange);
            }
            else
                EnterNodesNearRemotes(__instance.currentGrid, activationRange);
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
                    if (CoopWorldPresencePolicy.ShouldKeepNodeForRemote(true,
                        Mathf.Abs(proxyPos.x - np.x) <= activationRange
                        && Mathf.Abs(proxyPos.z - np.y) <= activationRange))
                        nodes[i].enter(true);
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
    /// position for the duration of the call.
    /// </summary>
    internal static class HostGridOccupancy
    {
        [System.ThreadStatic]
        private static int _depth;
        [System.ThreadStatic]
        private static WorldGrid.Grid _saved;

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
            if (GridContains(wg, wg.currentGrid, pos))
                return wg.currentGrid;
            for (int i = 0; i < wg.grids.Count; i++)
            {
                WorldGrid.Grid g = wg.grids[i];
                if (g == null || g == wg.currentGrid) continue;
                if (GridContains(wg, g, pos))
                    return g;
            }
            return null;
        }

        private static bool GridContains(WorldGrid wg, WorldGrid.Grid g, Vector3 pos)
        {
            if (wg == null || g == null || g.nodes == null) return false;
            for (int i = 0; i < g.nodes.Count; i++)
            {
                if (wg.inVicinityOfNode(pos, g.nodes[i]))
                    return true;
            }
            return false;
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

    /// <summary>
    /// After forceAttackClosestCharacter runs, if the entity fell through to
    /// attackPlayer() because the proxy has no Character component, redirect
    /// to the proxy only when that proxy is closer than the host.
    /// </summary>
}
