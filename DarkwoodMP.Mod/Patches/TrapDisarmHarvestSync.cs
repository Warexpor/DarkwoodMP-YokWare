using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Successful Item.disarm / harvest: peers get silent triggered state (vanilla
    /// switchToTriggered keeps the GO + sprite). Stomp still sends full boom TrapState.
    /// failDisarm does NOT set the flag.
    /// </summary>
    public static class TrapDisarmHarvestTracker
    {
        public static int SilentDisarmDepth;
        public static bool IsSilentDisarm => SilentDisarmDepth > 0;
    }

    [HarmonyPatch(typeof(Item), "disarm")]
    public static class ItemDisarmSilentTrapPatch
    {
        private static void Prefix()
        {
            TrapDisarmHarvestTracker.SilentDisarmDepth++;
        }

        private static void Postfix()
        {
            if (TrapDisarmHarvestTracker.SilentDisarmDepth > 0)
                TrapDisarmHarvestTracker.SilentDisarmDepth--;
        }
    }

    /// <summary>
    /// After successful disarm → switchToTriggered: broadcast silent TrapState so peers
    /// call switchToTriggered (keep object) without explosion FX. Do NOT WorldObjectRemoved
    /// (that made mushrooms vanish entirely — not vanilla staysAfterDisarming behavior).
    /// Destroy path (!staysAfterDisarming) is handled by ObjectDestroyTrapPatch alone.
    /// </summary>
    [HarmonyPatch(typeof(Trigger), "switchToTriggered")]
    public static class TrapSwitchSilentHarvestPatch
    {
        private static void Postfix(Trigger __instance)
        {
            if (__instance == null) return;
            if (!TrapDisarmHarvestTracker.IsSilentDisarm) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (!(ModRuntime.Network is LanNetworkManager net) || !net.IsConnected) return;
            if (net.Role == NetworkRole.Offline) return;

            // Only host broadcasts world trap authority; client harvests go via
            // TrapTriggered or WorldObjectRemoved (destroy path). For silent client harvest
            // of staysAfterDisarming mushrooms, still tell host so peers update.
            string name = __instance.name.ToLowerInvariant();
            if (!name.Contains("mushroom") && !name.Contains("trap") && !name.Contains("bio")
                && !name.Contains("exp") && !name.Contains("snap") && !name.Contains("bear"))
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

            int trapId = 0;
            if (net.Role == NetworkRole.Host)
                trapId = TrapNetworkId.GetOrMintHost(__instance.gameObject);
            else
                trapId = TrapNetworkId.GetId(__instance.gameObject);

            var ts = new TrapState
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                Triggered = true,
                TrapNetId = trapId,
                OccupantPlayerId = TrapState.OccupantSilentDisarm
            };

            if (net.Role == NetworkRole.Host)
            {
                net.SendTrapState(ts);
            }
            else
            {
                // Client harvested: host must apply + fan-out silent state.
                net.SendTrapState(ts);
            }

            ModRuntime.LegacyInfo("[HarvestSync] silent triggered (keep GO) \"" + __instance.name
                + "\" at " + key + " role=" + net.Role);
        }
    }
}
