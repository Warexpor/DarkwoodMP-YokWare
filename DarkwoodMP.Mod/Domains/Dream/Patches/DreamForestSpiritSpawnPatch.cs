using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Dream bunker forest spirit: vanilla always spawns around + attacks
    /// <see cref="Player.Instance"/>. When a peer's step set the scene off, spawn around and
    /// chase that peer instead. Aggro sticks to the spawn owner
    /// so a later peer entering the volume cannot steal the chase.
    /// </summary>
    [HarmonyPatch(typeof(Player), "special_spawnDreamForestSpirit")]
    public static class DreamForestSpiritSpawnPatch
    {
        private static bool Prefix(Player __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;
            // The host spawns the spirit and streams it; a client's own spawn (a replayed game
            // event's runFunction) was an unsynced second spirit chasing only that client.
            if (ModRuntime.Network.Role != NetworkRole.Host)
                return false;
            if (!PlayerPositionManager.HasRemotePlayer)
                return true;

            var net = ModRuntime.Network;
            // The player whose step into the forest set the scene off (the area trigger's actor):
            // vanilla's "the player". A recent trigger near the pad's origin was guessed before,
            // within 2000 of it: the forest lies some 8000 out, so the spirit always took the host.
            Transform prefer = GeFireActorContext.Depth > 0 ? GeFireActorContext.ActorBody() : null;
            if (prefer == null)
            {
                Transform pad = DreamSyncManager.GetDreamLocationTransform();
                prefer = pad != null ? ThreatTriggerContext.TryGetRecentProxyNear(pad.position, 2000f, 8f) : null;
            }
            if (prefer != null && prefer.GetComponentInParent<RemotePlayerProxy>() == null)
                prefer = null; // the host's own body: the host branch below
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
                PlayerTargetArbiter.Commit(component, sticky, "dreamSpiritSpawn");
            else
                component.attackPlayer();
            ModRuntime.LegacyInfo(
                $"[DreamSpirit] spawned forestSpirit_bunkerDream near {who} stickyOwner={ownerId} at {position}");
            return false;
        }
    }
}
