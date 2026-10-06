using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Client never picks its own night scenario — host pushes via ScenarioSync /
    /// ScenarioStateSync and we assign currentScenario directly.
    /// </summary>
    [HarmonyPatch(typeof(NightScenarios), "setCurrentScenario")]
    public static class ClientScenarioBlockPatch
    {
        private static bool Prefix()
        {
            var net = ModRuntime.Network;
            if (net != null && net.IsConnected && net.Role == NetworkRole.Client)
                return false;
            return true;
        }
    }

    /// <summary>
    /// Vanilla picks the night from where the local player stands at dusk: the hideout's
    /// difficulty (story) or the biome (random generation). With the shared clock the host can
    /// be inside a location at dusk (no biome there: the easiest night, sent to everyone) or out
    /// in the forest while a peer is home. Pick from a peer standing in a hideout when the host
    /// is not in one, and from a peer in the open world when the host is inside a location.
    /// The pick goes into <c>currentScenario</c>, which vanilla then keeps (presets still win,
    /// pool and previous-night bookkeeping still run).
    /// </summary>
    [HarmonyPatch(typeof(NightScenarios), "setCurrentScenario")]
    public static class HostScenarioAnchorPatch
    {
        private static void Prefix(NightScenarios __instance)
        {
            if (__instance.currentScenario != null || !NetGuard.ConnectedHost(out var net))
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;
            Player host = Player.Instance;
            if (host == null || host.whereAmI == null)
                return;
            Location hostLoc = host.whereAmI.bigLocation;
            if (hostLoc != null && hostLoc.playerBase)
                return;

            var ol = Singleton<OutsideLocations>.Instance;
            bool hostInside = ol != null && ol.playerInOutsideLocation;
            if (!TryFindAnchor(net, needHideout: true, out Vector3 pos)
                && !(hostInside && TryFindAnchor(net, needHideout: false, out pos)))
                return;

            NightScenario pick = Pick(__instance, pos);
            if (pick == null)
                return;
            __instance.currentScenario = pick;
            ModRuntime.LegacyInfo($"[NightScenario] host not home at dusk — night '{pick.name}' picked at a peer {pos}");
        }

        private static bool TryFindAnchor(LanNetworkManager net, bool needHideout, out Vector3 pos)
        {
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null)
                    continue;
                CharBase cb = proxy.CachedCharBase;
                if (cb != null && !cb.alive)
                    continue;
                pos = proxy.transform.position;
                if (needHideout)
                {
                    Location loc = Location.getAtPos(pos);
                    if (loc != null && loc.playerBase)
                        return true;
                }
                else if (net.RemotePlayers.TryGetValue(proxy.PlayerId, out RemotePlayerState st) && st.InOpenWorld)
                {
                    return true;
                }
            }
            pos = Vector3.zero;
            return false;
        }

        /// <summary>The location-dependent half of vanilla NightScenarios.setCurrentScenario, at <paramref name="pos"/>.</summary>
        private static NightScenario Pick(NightScenarios ns, Vector3 pos)
        {
            if (ns.scenarios == null || ns.scenarios.Count == 0)
                return null;
            if (!Core.randomGeneration || ns.nightPresets != null)
            {
                Location loc = Location.getAtPos(pos);
                return loc != null ? ns.getRandomScenarioOfDifficulty(loc.difficulty + 1) : ns.scenarios[0];
            }

            WorldChunk chunk = WorldChunk.getChunkAtPos(pos);
            Biome biome = chunk != null ? chunk.biome : null;
            if (biome == null)
                return ns.scenarios[0];
            switch (biome.type)
            {
                case Biome.Type.meadow:
                    if (ns.scenarioId == 1)
                        return Resources.Load("NightScenarios/Night_h1_1", typeof(NightScenario)) as NightScenario;
                    if (ns.scenarioId == 2)
                        return Resources.Load("NightScenarios/Night_h1_2", typeof(NightScenario)) as NightScenario;
                    if (ns.scenarioId == 3)
                        return Resources.Load("NightScenarios/Night_h1_3", typeof(NightScenario)) as NightScenario;
                    return ns.getRandomScenarioOfDifficulty(1);
                case Biome.Type.forest:
                    return ns.getRandomScenarioOfDifficulty(2);
                case Biome.Type.forest_mutated:
                case Biome.Type.forest_dense:
                    return ns.getRandomScenarioOfDifficulty(3);
                case Biome.Type.swamp:
                    return ns.getRandomScenarioOfDifficulty(4);
                default:
                    return ns.scenarios[0];
            }
        }
    }

    [HarmonyPatch(typeof(NightScenarios), "setCurrentScenario")]
    public static class HostScenarioSyncPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(NightScenarios __instance)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;
            if (__instance.currentScenario == null)
                return;

            net.SendScenarioSync(new ScenarioSyncMessage
            {
                ScenarioName = __instance.currentScenario.name
            });
        }
    }
}
