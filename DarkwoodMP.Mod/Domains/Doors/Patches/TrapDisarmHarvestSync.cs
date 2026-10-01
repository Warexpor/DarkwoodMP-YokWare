using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Successful Item.disarm / harvest: peers get silent triggered state (vanilla
    /// switchToTriggered keeps the GO + sprite) OR WorldObjectRemoved (Destroy path).
    /// A stomp still sends the full triggered TrapState.
    /// </summary>
    public static class TrapDisarmHarvestTracker
    {
        public static int SilentDisarmDepth; // process-scoped: call-scoped, unwound by its Finalizer/finally
        public static bool IsSilentDisarm => SilentDisarmDepth > 0;
    }

    [HarmonyPatch(typeof(Item), "disarm")]
    public static class ItemDisarmSilentTrapPatch
    {
        // Pre-disarm identity travels in __state: disarm can destroy the trap and chain into
        // another disarm, and statics would be overwritten before the Postfix reads them.
        private struct State
        {
            public Vector3 Pos;
            public string Name;
        }

        private static void Prefix(Item __instance, out State __state)
        {
            TrapDisarmHarvestTracker.SilentDisarmDepth++;
            __state = default;
            if (__instance == null || __instance.gameObject == null) return;
            __state.Pos = __instance.transform.position;
            __state.Name = __instance.gameObject.name;
        }

        private static void Postfix(Item __instance, State __state)
        {
            // SilentDisarmDepth cleared in Finalizer (covers throw before/during Postfix).

            // Destroy path: ObjectDestroyTrapPatch already SendWorldObjectRemoved.
            // Extra TrySendRemoved was a 2nd/3rd wire packet (debounce now drops them).
            if (__instance != null && __instance.disabled)
                return;

            TrySendSilentTrapState(__instance, "disarm-postfix", __state.Name, __state.Pos);
        }

        // Finalizer (not Postfix): Item.disarm throw after Prefix ++ leaves
        // SilentDisarmDepth sticky → IsSilentDisarm forever mis-routes
        // switchToTriggered / Object.Destroy trap wire as silent harvest.
        [HarmonyFinalizer]
        private static void Finalizer()
        {
            if (TrapDisarmHarvestTracker.SilentDisarmDepth > 0)
                TrapDisarmHarvestTracker.SilentDisarmDepth--;
        }

        internal static void TrySendRemoved(Item item, string nameHint, Vector3 posHint, string reason)
        {
            if (TraverseHack.ApplyingFromNetwork) return;
            if (!(ModRuntime.Network is LanNetworkManager net) || !net.IsConnected) return;
            if (net.Role == NetworkRole.Offline) return;

            Vector3 p = posHint;
            string name = nameHint;
            if (item != null && item.gameObject != null)
            {
                p = item.transform.position;
                name = item.gameObject.name;
            }
            if (string.IsNullOrEmpty(name)) return;

            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

            net.SendWorldObjectRemoved(new WorldObjectRemovedMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                ObjectName = name
            });
            ModLog.Event(LogCat.World, "[HarvestSync] remove trap (" + reason + ") \"" + name
                + "\" at " + key + " role=" + net.Role);
        }

        internal static void TrySendSilentTrapState(Item item, string reason, string nameHint = null, Vector3 posHint = default(Vector3))
        {
            if (item == null) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (!(ModRuntime.Network is LanNetworkManager net) || !net.IsConnected) return;
            if (net.Role == NetworkRole.Offline) return;

            // Destroyed mid-frame: treat as removal.
            if (item.gameObject == null || item.disabled)
            {
                TrySendRemoved(item, nameHint, posHint, reason + "-gone");
                return;
            }

            Trigger t = item.GetComponent<Trigger>();
            if (t == null) return;
            // Only after a successful spring / stay-after-disarm presentation.
            if (!t.triggered && t.active && t.canDisarm)
                return;

            Vector3 p = t.transform.position;
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

            int trapId = net.Role == NetworkRole.Host
                ? TrapNetworkId.GetOrMintHost(t.gameObject)
                : TrapNetworkId.GetId(t.gameObject);

            var ts = new TrapState
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                Triggered = true,
                TrapNetId = trapId,
                OccupantPlayerId = TrapState.OccupantSilentDisarm
            };

            net.SendTrapState(ts);
            ModLog.Event(LogCat.World, "[HarvestSync] silent trap (" + reason + ") \"" + t.name
                + "\" id=" + trapId + " bear=" + t.isBearTrap + " role=" + net.Role);
        }
    }

    /// <summary>
    /// After successful disarm → switchToTriggered: broadcast silent TrapState.
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

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

            int trapId = net.Role == NetworkRole.Host
                ? TrapNetworkId.GetOrMintHost(__instance.gameObject)
                : TrapNetworkId.GetId(__instance.gameObject);

            var ts = new TrapState
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                Triggered = true,
                TrapNetId = trapId,
                OccupantPlayerId = TrapState.OccupantSilentDisarm
            };

            net.SendTrapState(ts);
            ModLog.Event(LogCat.World, "[HarvestSync] silent triggered \"" + __instance.name
                + "\" id=" + trapId + " at " + key + " role=" + net.Role
                + " bear=" + __instance.isBearTrap);
        }
    }

    /// <summary>
    /// progressBarCompleted nulls trapBeingDisarmed in resetProgressBar; capture it in Prefix.
    /// Covers success + failDisarm (fail never sets SilentDisarmDepth; client boom path is host-only).
    /// </summary>
    [HarmonyPatch(typeof(Player), "progressBarCompleted")]
    public static class PlayerDisarmProgressTrapSyncPatch
    {
        // Captured before progressBarCompleted resets trapBeingDisarmed; travels in __state.
        private struct State
        {
            public Item Trap;
            public Vector3 Pos;
            public string Name;
        }

        private static void Prefix(Player __instance, out State __state)
        {
            __state = default;
            if (__instance == null || !__instance.disarmingTrap) return;
            Item trap = Traverse.Create(__instance).Field("trapBeingDisarmed").GetValue<Item>();
            if (trap == null || trap.gameObject == null) return;
            __state.Trap = trap;
            __state.Pos = trap.transform.position;
            __state.Name = trap.gameObject.name;
        }

        private static void Postfix(State __state)
        {
            Item trap = __state.Trap;
            string name = __state.Name;
            Vector3 pos = __state.Pos;
            if (trap == null && string.IsNullOrEmpty(name)) return;

            // Destroy path: ObjectDestroyTrapPatch owns the wire send. Only belt-send
            // If Destroy somehow never ran, SendWorldObjectRemoved is debounced.
            if (trap == null || trap.gameObject == null || trap.disabled)
            {
                // ObjectDestroy usually already claimed; this is a no-op then.
                ItemDisarmSilentTrapPatch.TrySendRemoved(trap, name, pos, "progressBar-destroy");
                return;
            }

            ItemDisarmSilentTrapPatch.TrySendSilentTrapState(trap, "progressBar");
        }
    }
}
