using System;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{

    /// <summary>
    /// Harmony patch: intercepts InteractiveItem.switchMe() (player-toggled levers, switches,
    /// buttons) and broadcasts the new toggle state to all peers.
    /// For wells: only sync the false→true transition (fix/repair) so both players see it
    /// as repaired; true→false (use/heal) is per-player so each can heal independently.
    /// </summary>
    [HarmonyPatch(typeof(InteractiveItem), "switchMe")]
    public static class InteractiveItemSwitchPatch
    {
        /// <summary>
        /// True while vanilla switchMe runs: it calls switchOn/switchOff itself, and those patches
        /// must not send a second (duplicate, well-heal-ignoring) message for the same toggle.
        /// </summary>
        [ThreadStatic] private static int _insideSwitchMe; // process-scoped: call-scoped, unwound by Finalizer
        internal static bool InsideSwitchMe => _insideSwitchMe > 0;

        private static void Prefix(InteractiveItem __instance, out bool __state)
        {
            __state = __instance.isOn;
            _insideSwitchMe++;
        }

        // Finalizer (not Postfix): switchMe's event triggers can throw; the scope must close.
        private static void Finalizer()
        {
            if (_insideSwitchMe > 0) _insideSwitchMe--;
        }

        private static void Postfix(InteractiveItem __instance, bool __state)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;
            if (__state == __instance.isOn)
                return; // no actual change

            // For wells: only sync the fix/repair (false→true), not the use/heal (true→false).
            // Non-well items: sync both directions as before.
            if (IsWellInteractiveItem(__instance))
            {
                if (__state || !__instance.isOn)
                    return; // was already on, or turned off (heal) — skip
                // __state==false && isOn==true → fix/repair → sync
            }

            Vector3 p = __instance.transform.position;
            Vector3 key = WorldPos.Key(p);

            ModRuntime.Network.SendInteractiveItemSwitch(new InteractiveItemSwitchMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                IsOn = __instance.isOn
            });
            ModRuntime.LegacyInfo($"[InteractiveItemSync] switchMe at {key} isOn={__instance.isOn}");
        }

        internal static bool IsWellInteractiveItem(InteractiveItem ii)
        {
            Transform t = ii.transform;
            while (t != null)
            {
                if (t.name.IndexOf("well", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                t = t.parent;
            }
            return false;
        }
    }

    /// <summary>
    /// Syncs InteractiveItem.switchOn() calls (GameEvents-triggered, non-player).
    /// </summary>
    [HarmonyPatch(typeof(InteractiveItem), "switchOn")]
    public static class InteractiveItemSwitchOnPatch
    {
        private static void Prefix(InteractiveItem __instance, out bool __state)
        {
            __state = __instance.isOn;
        }

        private static void Postfix(InteractiveItem __instance, bool __state)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            // switchMe (player toggle) is reported by InteractiveItemSwitchPatch, once.
            if (InteractiveItemSwitchPatch.InsideSwitchMe) return;
            if (__state == __instance.isOn) return; // no actual change

            Vector3 p = __instance.transform.position;
            Vector3 key = WorldPos.Key(p);

            ModRuntime.Network.SendInteractiveItemSwitch(new InteractiveItemSwitchMessage
            {
                PosX = key.x, PosY = key.y, PosZ = key.z,
                IsOn = true
            });
        }
    }

    /// <summary>
    /// Syncs InteractiveItem.switchOff() calls (GameEvents-triggered, non-player).
    /// </summary>
    [HarmonyPatch(typeof(InteractiveItem), "switchOff")]
    public static class InteractiveItemSwitchOffPatch
    {
        private static void Prefix(InteractiveItem __instance, out bool __state)
        {
            __state = __instance.isOn;
        }

        private static void Postfix(InteractiveItem __instance, bool __state)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            // switchMe (player toggle) is reported by InteractiveItemSwitchPatch, once.
            if (InteractiveItemSwitchPatch.InsideSwitchMe) return;
            if (__state == __instance.isOn) return; // no actual change
            // A well's use/heal is per-player: only the fix/repair (off→on) is shared.
            if (InteractiveItemSwitchPatch.IsWellInteractiveItem(__instance)) return;

            Vector3 p = __instance.transform.position;
            Vector3 key = WorldPos.Key(p);

            ModRuntime.Network.SendInteractiveItemSwitch(new InteractiveItemSwitchMessage
            {
                PosX = key.x, PosY = key.y, PosZ = key.z,
                IsOn = false
            });
        }
    }

    /// <summary>
    /// Harmony patch: intercepts Padlock.unlock() (correct combination entered)
    /// and broadcasts the unlock to all peers.
    /// </summary>
    [HarmonyPatch(typeof(Padlock), "unlock", new[] { typeof(bool) })]
    public static class PadlockUnlockPatch
    {
        private static void Postfix(Padlock __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = WorldPos.Key(p);

            ModRuntime.Network.SendPadlockUnlock(new PadlockUnlockMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z
            });
            ModRuntime.LegacyInfo($"[PadlockSync] unlock at {key}");
        }
    }

    /// <summary>
    /// Harmony patch: intercepts Locked.unlock() (key/lockpick unlock)
    /// and broadcasts the unlock to all peers.
    /// </summary>
    [HarmonyPatch(typeof(Locked), "unlock")]
    public static class LockedUnlockPatch
    {
        private static void Postfix(Locked __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = WorldPos.Key(p);

            ModRuntime.Network.SendLockedUnlock(new LockedUnlockMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z
            });
            ModRuntime.LegacyInfo($"[LockedSync] unlock at {key}");
        }
    }

    /// <summary>
    /// Generator.addFuel: clients send FuelDelta for host-auth accumulation (concurrent pour
    /// no longer last-writer absolute underfuel). Host pours stay absolute (FuelDelta=0).
    /// </summary>
    [HarmonyPatch(typeof(Generator), "addFuel")]
    public static class GeneratorAddFuelPatch
    {
        private static void Prefix(float addFuelAmount, out float __state)
        {
            __state = addFuelAmount;
        }

        private static void Postfix(Generator __instance, float __state)
        {
            if (ModRuntime.Network == null)
                return;
            if (TraverseHack.ApplyingFromNetwork || LanNetworkManager.IsApplyingRemoteState)
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = WorldPos.Key(p);

            Item itemComp = __instance.GetComponent<Item>();
            string itemType = itemComp != null && itemComp.invItem != null ? itemComp.invItem.type : "";

            // Client → host: delta so concurrent pours sum. Host → peers: absolute.
            float delta = 0f;
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client && __state > 0.01f)
                delta = __state;

            ModRuntime.Network.SendGeneratorState(new GeneratorState
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                IsOn = __instance.isOn,
                Fuel = __instance.fuel,
                LowPower = __instance.lowPower,
                ItemType = itemType,
                FuelDelta = delta
            });
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[GeneratorSync] send addFuel at {key} fuel={__instance.fuel} delta={delta}");
        }
    }

    /// <summary>Harmony patch: intercepts Generator.setLowPower() and broadcasts low-power state.</summary>
    [HarmonyPatch(typeof(Generator), "setLowPower")]
    public static class GeneratorSetLowPowerPatch
    {
        private static void Postfix(Generator __instance)
        {
            if (ModRuntime.Network == null)
                return;
            if (TraverseHack.ApplyingFromNetwork || LanNetworkManager.IsApplyingRemoteState)
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = WorldPos.Key(p);

            Item itemComp = __instance.GetComponent<Item>();
            string itemType = itemComp != null && itemComp.invItem != null ? itemComp.invItem.type : "";

            ModRuntime.Network.SendGeneratorState(new GeneratorState
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                IsOn = __instance.isOn,
                Fuel = __instance.fuel,
                LowPower = __instance.lowPower,
                ItemType = itemType
            });
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[GeneratorSync] send setLowPower={__instance.lowPower} at {key}");
        }
    }
}
