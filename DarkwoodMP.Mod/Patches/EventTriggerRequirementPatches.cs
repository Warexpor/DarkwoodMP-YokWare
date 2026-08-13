using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// 4.3: host EventTriggerRequirement.haveItem / locationState must see any living peer.
    /// Journal/haveKey use shared journal (host copy). Skills/health stay per-body.
    /// </summary>
    [HarmonyPatch(typeof(EventTriggerRequirement), "requirementsMet")]
    public static class EventTriggerRequirementAnyPeerPatch
    {
        private static void Postfix(EventTriggerRequirement __instance, ref bool __result)
        {
            if (__result || __instance == null) return;
            if (!EventTriggersAuth.IsMultiplayerConnected()) return;

            if (__instance.type == EventTriggerRequirement.Type.locationState)
            {
                if (AnyPeerLocationMatches(__instance.locationState))
                    __result = true;
                return;
            }

            if (__instance.type == EventTriggerRequirement.Type.haveKey)
            {
                // Shared journal — host dict is enough. Nothing extra.
                return;
            }

            if (__instance.type == EventTriggerRequirement.Type.playerState
                && __instance.playerState == Player.State.haveItem)
            {
                string key = ItemTypeKey(__instance);
                if (string.IsNullOrEmpty(key)) return;
                bool has = PeerItemPresence.AnyPeerHas(key, __instance.amount > 0 ? __instance.amount : 1);
                if (!has)
                    has = JournalHas(key);
                if (has && __instance.activeModifier)
                    __result = true;
            }
        }

        private static string ItemTypeKey(EventTriggerRequirement req)
        {
            try
            {
                if (req.itemType == null) return null;
                GameObject go = req.itemType as GameObject;
                if (go == null) return null;
                InvItem inv = go.GetComponent<InvItem>();
                return inv != null ? inv.type : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool JournalHas(string key)
        {
            Journal journal = Singleton<UI>.Instance != null ? Singleton<UI>.Instance.journal : null;
            if (journal == null) return false;
            if (journal.notesDict != null && journal.notesDict.ContainsKey(key)) return true;
            if (journal.keysDict != null && journal.keysDict.ContainsKey(key)) return true;
            if (journal.itemsDict != null && journal.itemsDict.ContainsKey(key)) return true;
            return false;
        }

        private static bool AnyPeerLocationMatches(Location.GetState locState)
        {
            if (locState == null) return false;
            if (Player.Instance != null && Player.Instance.whereAmI != null
                && Player.Instance.whereAmI.bigLocation != null)
            {
                if (locState.getBool(Player.Instance.whereAmI.bigLocation))
                    return true;
            }

            var net = LanNetworkManager.Instance;
            if (net == null) return false;
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null) continue;
                Location loc = LocationForProxy(proxy);
                if (loc != null && locState.getBool(loc))
                    return true;
            }
            return false;
        }

        private static Location LocationForProxy(RemotePlayerProxy proxy)
        {
            WhereAmI where = proxy.GetComponent<WhereAmI>();
            if (where == null)
                where = proxy.GetComponentInChildren<WhereAmI>();
            if (where == null) return null;
            try { where.checkWhereAmI(); }
            catch { /* clone may lack colliders */ }
            return where.bigLocation;
        }
    }
}
