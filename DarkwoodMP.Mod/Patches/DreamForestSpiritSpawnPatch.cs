using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Dream bunker forest spirit: vanilla always spawns around + attacks
    /// <see cref="Player.Instance"/>. When a remote proxy entered the runaway
    /// volume, spawn/chase that peer instead (host still works via sticky AI).
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

            Vector3? proxyPos = ThreatTriggerContext.TryGetRecentProxyPosition(8f);
            Vector3 anchor = proxyPos
                ?? (Player.Instance != null ? Player.Instance._transform.position : __instance._transform.position);

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
            // attackPlayer patch picks recent proxy / nearest player.
            component.attackPlayer();
            ModRuntime.LegacyInfo(
                "[DreamSpirit] spawned forestSpirit_bunkerDream near "
                + (proxyPos.HasValue ? "proxy trigger" : "host")
                + " at " + position);
            return false;
        }
    }
}
