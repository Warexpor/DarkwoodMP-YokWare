using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// PorterSpawner co-op (host-authoritative NPC + multi-avatar sight).
    ///
    /// Decompile: <c>Start</c> wires <see cref="InSightOfPlayer"/> callbacks and
    /// <c>checkSight(force)</c>; out-of-sight starts <c>waitToSpawn</c> which
    /// <c>AddPrefab("Characters/NPC/Porter", …)</c>. Sight used only
    /// <c>Player.Instance.isInSight</c>.
    ///
    /// Fix (no new messages, no magic ranges):
    /// 1) Client: Prefix-skip <c>Start</c> / <c>waitToSpawn</c> — only host owns
    ///    the Porter NPC; peers observe via entity snapshots.
    /// 2) Host sight: existing <c>HostInSightOfPlayerCheckSightPatch</c> +
    ///    <c>HostPlayerIdentity.AnyInSight</c> (local Player OR remote proxy FOV).
    /// 3) Client bike-bell (<c>porterWhistle</c> → <c>Location.spawnPorter</c>):
    ///    request host via existing <see cref="NetMessageType.ItemSpawn"/> sentinel
    ///    (not a trap prefab). Host places <c>Events/porterSpawner</c>; peers see
    ///    Porter via entity snapshots. Personal item consume stays on the caller.
    /// </summary>
    internal static class PorterSpawnerAuth
    {
        /// <summary>
        /// ItemSpawn.ItemType sentinel for client→host bike-bell call.
        /// Must be handled before ItemsDatabase trap spawn (type also exists as InvItem).
        /// </summary>
        internal const string PorterWhistleItemSpawnType = "porterWhistle";

        internal static bool IsClientConnected()
        {
            return ModRuntime.Network != null
                && ModRuntime.Network.IsConnected
                && ModRuntime.Network.Role == NetworkRole.Client;
        }

        /// <summary>
        /// Host: place PorterSpawner at the hideout matching <paramref name="porterPos"/>
        /// (vanilla <c>Location.spawnPorter</c> body, no host Player toast).
        /// </summary>
        internal static bool TryApplyPorterWhistleOnHost(Vector3 porterPos)
        {
            if (!Core.isDay())
            {
                ModRuntime.LegacyInfo("[PorterWhistle] host reject — not day");
                return false;
            }
            if (Singleton<Flags>.Instance != null
                && Singleton<Flags>.Instance.isFlagTrue("porter_inTransit"))
            {
                ModRuntime.LegacyInfo("[PorterWhistle] host reject — porter_inTransit");
                return false;
            }
            if (Singleton<Flags>.Instance != null
                && Singleton<Flags>.Instance.isFlagTrue("porter_killed"))
            {
                ModRuntime.LegacyInfo("[PorterWhistle] host reject — porter_killed");
                return false;
            }

            Location loc = ResolveHideoutForPorter(porterPos);
            if (loc == null || loc.porterPosition == null)
            {
                ModRuntime.Log?.LogWarning(
                    "[PorterWhistle] host: no Location.porterPosition near " + porterPos);
                return false;
            }
            if (loc.porter != null)
            {
                ModRuntime.LegacyInfo("[PorterWhistle] host reject — porter already present");
                return false;
            }
            if (loc.characters == null)
            {
                ModRuntime.Log?.LogWarning("[PorterWhistle] host: Location.characters null");
                return false;
            }

            Vector3 spawnAt = loc.porterPosition.transform.position;
            Core.AddPrefab(
                "Events/porterSpawner",
                spawnAt,
                Quaternion.Euler(90f, 0f, 0f),
                loc.characters.gameObject,
                worldSpace: true);
            ModRuntime.LegacyInfo(
                $"[PorterWhistle] host placed porterSpawner at {spawnAt} loc={loc.name}");
            return true;
        }

        private static Location ResolveHideoutForPorter(Vector3 porterPos)
        {
            Location at = Location.getAtPos(porterPos);
            if (at != null && at.porterPosition != null)
                return at;

            // Porter pad can sit just outside the Location trigger volume.
            Location[] all = WorldQueryHelper.GetCachedSceneComponents<Location>();
            Location best = null;
            float bestDistSq = 25f * 25f;
            for (int i = 0; i < all.Length; i++)
            {
                Location loc = all[i];
                if (loc == null || loc.porterPosition == null) continue;
                float dSq = (loc.porterPosition.transform.position - porterPos).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    best = loc;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// Clients must not arm the out-of-sight spawn timer or place Porter.
    /// </summary>
    [HarmonyPatch(typeof(PorterSpawner), "Start")]
    public static class PorterSpawnerClientStartPatch
    {
        private static bool Prefix(PorterSpawner __instance)
        {
            if (!PorterSpawnerAuth.IsClientConnected())
                return true;

            if (ModRuntime.VerboseLogging)
            {
                ModLog.Event(LogCat.Entity,
                    "[PorterSpawner] client skipped Start (host-authoritative porter) on "
                    + (__instance != null ? __instance.name : "?"));
            }
            return false;
        }
    }

    /// <summary>
    /// Belt-and-suspenders if Start ran before Role became Client.
    /// </summary>
    [HarmonyPatch(typeof(PorterSpawner), "waitToSpawn")]
    public static class PorterSpawnerClientWaitToSpawnPatch
    {
        private static bool Prefix(PorterSpawner __instance)
        {
            if (!PorterSpawnerAuth.IsClientConnected())
                return true;

            if (ModRuntime.VerboseLogging)
            {
                ModLog.Event(LogCat.Entity,
                    "[PorterSpawner] client skipped waitToSpawn on "
                    + (__instance != null ? __instance.name : "?"));
            }
            return false;
        }
    }

    /// <summary>
    /// Client bike-bell: vanilla <c>InvItemClass.use</c> → <c>spawnPorter</c> → AddPrefab
    /// PorterSpawner, but client Start/waitToSpawn are skipped — whistle consumed, no NPC.
    /// Request host via ItemSpawn sentinel; keep the local "something happened" toast.
    /// Host/offline: vanilla.
    /// </summary>
    [HarmonyPatch(typeof(Location), "spawnPorter")]
    public static class LocationSpawnPorterClientPatch
    {
        private static bool Prefix(Location __instance)
        {
            if (!PorterSpawnerAuth.IsClientConnected())
                return true;
            if (LanNetworkManager.IsApplyingRemoteState || NetworkApplyGuard.IsActive)
                return true;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;

            Vector3 pos = __instance != null && __instance.porterPosition != null
                ? __instance.porterPosition.transform.position
                : (__instance != null ? __instance.transform.position : Vector3.zero);

            ModRuntime.Network.SendItemSpawn(new ItemSpawnMessage
            {
                ItemType = PorterSpawnerAuth.PorterWhistleItemSpawnType,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                RotX = 90f,
                RotY = 0f,
                RotZ = 0f
            });

            // Vanilla spawnPorter shows this; Prefix skip would drop it.
            if (Player.Instance != null)
                Player.Instance.displayMessage(Language.Get("Playermsg_somethingHappened", "UI"));

            ModRuntime.LegacyInfo(
                $"[PorterWhistle] client deferred spawnPorter → host at {pos}");
            return false;
        }
    }
}
