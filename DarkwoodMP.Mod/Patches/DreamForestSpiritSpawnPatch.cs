using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Dream bunker forest spirit: vanilla always spawns around + attacks
    /// <see cref="Player.Instance"/>. When a remote proxy entered the runaway
    /// volume, spawn/chase that peer instead. Aggro sticks to the spawn owner
    /// so a later peer entering the volume cannot steal the chase.
    /// </summary>
    [HarmonyPatch(typeof(Player), "special_spawnDreamForestSpirit")]
    public static class DreamForestSpiritSpawnPatch
    {
        private static bool Prefix(Player __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (!PlayerPositionManager.HasRemotePlayer)
                return true;

            var net = LanNetworkManager.Instance;
            Transform prefer = ThreatTriggerContext.TryGetRecentProxyTransform(8f);
            int ownerId;
            Vector3 anchor;
            string who;
            if (prefer != null && net != null)
            {
                RemotePlayerProxy proxy = prefer.GetComponentInParent<RemotePlayerProxy>();
                ownerId = proxy != null ? proxy.PlayerId : net.LocalPlayerId;
                anchor = prefer.position;
                who = "proxy trigger p" + ownerId;
            }
            else
            {
                ownerId = net != null ? net.LocalPlayerId : 1;
                anchor = Player.Instance != null
                    ? Player.Instance._transform.position
                    : __instance._transform.position;
                who = "host";
            }

            DreamForestSpiritAggro.BindOwner(ownerId);

            Vector3 position = Core.randomPosAround(anchor, 1000f, 1500f, canBeInside: true, mustBeInsideGraph: false);
            GameObject go = Core.AddPrefab(
                "characters/forestSpirit_bunkerDream",
                position,
                Quaternion.Euler(90f, Random.Range(0, 360), 0f),
                __instance.whereAmI != null && __instance.whereAmI.bigLocation != null
                    ? __instance.whereAmI.bigLocation.gameObject
                    : null,
                worldSpace: true);
            if (go == null) return false;

            Character component = go.GetComponent<Character>();
            if (component == null) return false;

            component.isActive = true;
            Transform sticky = DreamForestSpiritAggro.TryGetStickyTarget() ?? prefer;
            if (sticky != null)
                component.attackCharacter(sticky);
            else
                component.attackPlayer();
            ModRuntime.LegacyInfo(
                "[DreamSpirit] spawned forestSpirit_bunkerDream near " + who
                + " stickyOwner=" + ownerId + " at " + position);
            return false;
        }
    }
}
