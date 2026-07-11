using System;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>Harmony patch: intercepts Door.open() and broadcasts the open state to all peers.</summary>
    [HarmonyPatch(typeof(Door), "open")]
    public static class DoorOpenPatch
    {
        private static void Postfix(Door __instance, object[] __args)
        {
            if (ModRuntime.Network == null)
                return;

            // Don't re-broadcast if we're applying a remote snapshot
            if (TraverseHack.ApplyingFromNetwork)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            // During dreams the dedicated DoorOpen message handles sync (entity broadcast paused).
            if (DreamSyncManager.IsDreamActive)
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(Mathf.Round(p.x * 10f) / 10f, Mathf.Round(p.y * 10f) / 10f, Mathf.Round(p.z * 10f) / 10f);

            Vector3 openerPos = Player.Instance != null ? Player.Instance.transform.position : Vector3.zero;

            float bodyRotY = 0f;
            Vector3 angVel = Vector3.zero;
            if (__instance.body != null)
            {
                bodyRotY = __instance.body.eulerAngles.y;
                Rigidbody rb = __instance.body.GetComponent<Rigidbody>();
                if (rb != null)
                    angVel = rb.angularVelocity;
            }

            // Use the actual OpenForce from Door.open() so the receiver plays
            // the correct sound ("door_hit_run" at thumpForce=45000).
            float openForce = __args.Length > 2 ? (float)__args[2] : 0f;

            ModRuntime.Network.SendDoorState(new DoorState
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                Opened = true,
                OpenerPosX = openerPos.x,
                OpenerPosY = openerPos.y,
                OpenerPosZ = openerPos.z,
                OpenForce = openForce,
                BodyRotY = bodyRotY,
                AngVelX = angVel.x,
                AngVelY = angVel.y,
                AngVelZ = angVel.z
            });
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[DoorSync] send open " + __instance.name + " at " + key + " force=" + openForce);
        }
    }

    /// <summary>Harmony patch: intercepts Door.close() and broadcasts the close state to all peers.</summary>
    [HarmonyPatch(typeof(Door), "close")]
    public static class DoorClosePatch
    {
        private static void Postfix(Door __instance)
        {
            if (ModRuntime.Network == null)
                return;

            if (TraverseHack.ApplyingFromNetwork)
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(Mathf.Round(p.x * 10f) / 10f, Mathf.Round(p.y * 10f) / 10f, Mathf.Round(p.z * 10f) / 10f);

            Vector3 openerPos = Player.Instance != null ? Player.Instance.transform.position : Vector3.zero;

            float bodyRotY = __instance.body != null ? __instance.body.eulerAngles.y : 0f;

            ModRuntime.Network.SendDoorState(new DoorState
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                Opened = false,
                OpenerPosX = openerPos.x,
                OpenerPosY = openerPos.y,
                OpenerPosZ = openerPos.z,
                BodyRotY = bodyRotY
            });
            ModRuntime.LegacyInfo("[DoorSync] send close " + __instance.name + " at " + key + " bodyY=" + bodyRotY);
        }
    }

    /// <summary>
    /// Harmony patch: intercepts Trigger.switchToTriggered() (traps) and broadcasts the triggered state.
    /// This is more reliable than hooking OnAfterTrigger because switchToTriggered() is public
    /// and is the common final path for all trap triggering.
    /// </summary>
    [HarmonyPatch(typeof(Trigger), "switchToTriggered")]
    public static class TrapSwitchPatch
    {
        private static void Postfix(Trigger __instance)
        {
            if (ModRuntime.Network == null)
                return;

            // Prevent re-broadcasting when applying a remote snapshot
            if (TraverseHack.ApplyingFromNetwork)
                return;

            // Only sync objects whose name suggests they are a trap
            string name = __instance.name.ToLowerInvariant();
            if (!name.Contains("trap") && !name.Contains("bear") && !name.Contains("snap") && !name.Contains("animal") && !name.Contains("mushroom") && !name.Contains("chain") && !name.Contains("glass"))
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(Mathf.Round(p.x * 10f) / 10f, Mathf.Round(p.y * 10f) / 10f, Mathf.Round(p.z * 10f) / 10f);

            int trapId = 0;
            if (ModRuntime.Network.Role == NetworkRole.Host)
                trapId = TrapNetworkId.GetOrMintHost(__instance.gameObject);
            else
                trapId = TrapNetworkId.GetId(__instance.gameObject);

            // Host owns world mutation broadcast; client fires TrapTriggered instead.
            if (ModRuntime.Network.Role != NetworkRole.Host)
                return;

            short occupant = WorldPhysicsSyncService.ResolveTrapOccupant(trapId, key);
            ModRuntime.Network.SendTrapState(new TrapState
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                Triggered = true,
                TrapNetId = trapId,
                OccupantPlayerId = occupant
            });
            ModRuntime.LegacyInfo("[TrapSync] send triggered " + __instance.name
                + " id=" + trapId + " at " + key);
        }
    }

    /// <summary>Harmony patch: intercepts Player.progressBarCompleted (item placement) and broadcasts the spawn.</summary>
    [HarmonyPatch(typeof(Player), "progressBarCompleted")]
    public static class TrapPlacementPatch
    {
        /// <summary>
        /// Set to true while inside progressBarCompleted, so ObjectDestroyTrapPatch
        /// can suppress fake removal messages caused by the inventory item being destroyed.
        /// </summary>
        internal static bool InsideTrapPlacement;

        private static string _pendingType;
        private static Vector3 _pendingPos;
        private static Quaternion _pendingRot;

        private static void Prefix(Player __instance)
        {
            InsideTrapPlacement = true;
            _pendingType = null;
            if (!__instance.placingItem) return;
            if (InvItemClass.isNull(__instance.currentItem)) return;
            ProxyItem proxy = __instance.proxyItem;
            if (proxy == null) return;

            // Capture the item type and placement transform before the placement completes
            _pendingType = __instance.currentItem.type;
            _pendingPos = proxy.transform.localPosition;
            _pendingRot = proxy.transform.rotation;
        }

        private static void Postfix(Player __instance)
        {
            InsideTrapPlacement = false;

            if (string.IsNullOrEmpty(_pendingType))
                return;
            if (ModRuntime.Network == null)
                return;

            Vector3 euler = _pendingRot.eulerAngles;
            ModRuntime.Network.SendItemSpawn(new ItemSpawnMessage
            {
                ItemType = _pendingType,
                PosX = _pendingPos.x,
                PosY = _pendingPos.y,
                PosZ = _pendingPos.z,
                RotX = euler.x,
                RotY = euler.y,
                RotZ = euler.z
            });
            ModRuntime.LegacyInfo("[ItemSpawn] sent " + _pendingType + " at " + _pendingPos);
        }
    }

    /// <summary>Harmony patch: intercepts Generator.turnOn() and broadcasts the state.</summary>
    [HarmonyPatch(typeof(Generator), "turnOn")]
    public static class GeneratorTurnOnPatch
    {
        private static void Postfix(Generator __instance)
        {
            if (ModRuntime.Network == null)
                return;
            if (TraverseHack.ApplyingFromNetwork || LanNetworkManager.IsApplyingRemoteState)
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(Mathf.Round(p.x * 10f) / 10f, Mathf.Round(p.y * 10f) / 10f, Mathf.Round(p.z * 10f) / 10f);

            Item itemComp = __instance.GetComponent<Item>();
            string itemType = itemComp != null && itemComp.invItem != null ? itemComp.invItem.type : "";

            ModRuntime.Network.SendGeneratorState(new GeneratorState
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                IsOn = true,
                Fuel = __instance.fuel,
                LowPower = __instance.lowPower,
                ItemType = itemType
            });
            ModRuntime.LegacyInfo("[GeneratorSync] send turnOn at " + key + " type=" + itemType);
        }
    }

    /// <summary>Harmony patch: intercepts Generator.turnOff() and broadcasts the state.</summary>
    [HarmonyPatch(typeof(Generator), "turnOff")]
    public static class GeneratorTurnOffPatch
    {
        private static void Postfix(Generator __instance)
        {
            if (ModRuntime.Network == null)
                return;
            if (TraverseHack.ApplyingFromNetwork || LanNetworkManager.IsApplyingRemoteState)
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(Mathf.Round(p.x * 10f) / 10f, Mathf.Round(p.y * 10f) / 10f, Mathf.Round(p.z * 10f) / 10f);

            Item itemComp = __instance.GetComponent<Item>();
            string itemType = itemComp != null && itemComp.invItem != null ? itemComp.invItem.type : "";

            ModRuntime.Network.SendGeneratorState(new GeneratorState
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                IsOn = false,
                Fuel = __instance.fuel,
                LowPower = false,
                ItemType = itemType
            });
            ModRuntime.LegacyInfo("[GeneratorSync] send turnOff at " + key + " type=" + itemType);
        }
    }

    /// <summary>Harmony patch: intercepts Generator.powerDown() and broadcasts the state.</summary>
    [HarmonyPatch(typeof(Generator), "powerDown")]
    public static class GeneratorPowerDownPatch
    {
        private static void Postfix(Generator __instance)
        {
            if (ModRuntime.Network == null)
                return;
            if (TraverseHack.ApplyingFromNetwork || LanNetworkManager.IsApplyingRemoteState)
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(Mathf.Round(p.x * 10f) / 10f, Mathf.Round(p.y * 10f) / 10f, Mathf.Round(p.z * 10f) / 10f);

            Item itemComp = __instance.GetComponent<Item>();
            string itemType = itemComp != null && itemComp.invItem != null ? itemComp.invItem.type : "";

            ModRuntime.Network.SendGeneratorState(new GeneratorState
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                IsOn = false,
                Fuel = __instance.fuel,
                LowPower = false,
                ItemType = itemType
            });
            ModRuntime.LegacyInfo("[GeneratorSync] send powerDown at " + key + " type=" + itemType);

            // Do NOT fan-out LightState IsOn=false for powerItems.
            // Vanilla powerDown/cutPower keeps lamp isOn and only drops hasPower / visuals;
            // LightState turnOff stomped isOn and broke gen re-start (restorePower needs isOn).
            // Peers apply GeneratorState → gen.turnOff/powerDown → cutPower/powerDown locally.
        }
    }

    // Generator turnOn/turnOff no longer emit LightState for connected lamps.
    // Vanilla only restorePower/cutPower (isOn sticky). GeneratorState is enough.
    // Per-lamp player toggles still go through Item.turnOn/turnOff → LightState.

    /// <summary>Harmony patch: intercepts Item.turnOn (lights, switchable items) and broadcasts the state.</summary>
    [HarmonyPatch(typeof(Item), "turnOn")]
    public static class ItemTurnOnPatch
    {
        private static void Postfix(Item __instance)
        {
            if (ModRuntime.Network == null)
                return;
            if (TraverseHack.ApplyingFromNetwork)
                return;
            if (__instance.GetComponent<Generator>() != null)
                return;
            if (!__instance.isLight && !__instance.switchable)
                return;

            Vector3 p = __instance.transform.position;
            string itemType = __instance.invItem != null ? __instance.invItem.type : "";
            ModRuntime.Network.SendLightState(new LightStateMessage
            {
                PosX = p.x,
                PosY = p.y,
                PosZ = p.z,
                IsOn = true,
                ItemName = __instance.name,
                ItemType = itemType
            });
            ModRuntime.LegacyInfo("[LightSync] send turnOn " + __instance.name + " type=" + itemType);
        }
    }

    /// <summary>Harmony patch: intercepts Item.turnOff (lights, switchable items) and broadcasts the state.</summary>
    [HarmonyPatch(typeof(Item), "turnOff")]
    public static class ItemTurnOffPatch
    {
        private static void Postfix(Item __instance)
        {
            if (ModRuntime.Network == null)
                return;
            if (TraverseHack.ApplyingFromNetwork)
                return;
            if (__instance.GetComponent<Generator>() != null)
                return;
            if (!__instance.isLight && !__instance.switchable)
                return;

            Vector3 p = __instance.transform.position;
            string itemType = __instance.invItem != null ? __instance.invItem.type : "";
            ModRuntime.Network.SendLightState(new LightStateMessage
            {
                PosX = p.x,
                PosY = p.y,
                PosZ = p.z,
                IsOn = false,
                ItemName = __instance.name,
                ItemType = itemType
            });
            ModRuntime.LegacyInfo("[LightSync] send turnOff " + __instance.name + " type=" + itemType);
        }
    }

    /// <summary>
    /// Harmony patch: intercepts Constructible.construct() (construction menu items:
    /// furniture, workbenches, traps built via construction menu) and broadcasts
    /// the construction to remote peers. Host tracks sites for late-join bulk.
    /// </summary>
    [HarmonyPatch(typeof(Constructible), "construct", new[] { typeof(bool), typeof(int) })]
    public static class ConstructibleConstructPatch
    {
        private static void Postfix(Constructible __instance, object[] __args)
        {
            int forceOption = (int)__args[1];

            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

            if (LanNetworkManager.IsApplyingRemoteState)
            {
                // Still record for host late-join bulk when applying remote construct.
                RegisterConstructed(__instance, forceOption);
                return;
            }

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

            int option = forceOption >= 0 ? forceOption : __instance.chosenOption;
            RegisterConstructed(key, option);

            ModRuntime.Network.SendConstructible(new ConstructibleMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                UseIngredients = false,
                OptionIndex = option
            });
            ModRuntime.LegacyInfo("[ConstructibleSync] sent construct at " + key + " option=" + option);
        }

        internal static void RegisterConstructed(Constructible c, int forceOption)
        {
            if (c == null) return;
            Vector3 p = c.transform.position;
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);
            int option = forceOption >= 0 ? forceOption : c.chosenOption;
            RegisterConstructed(key, option);
        }

        internal static void RegisterConstructed(Vector3 key, int optionIndex)
        {
            var net = LanNetworkManager.Instance;
            net?.RegisterConstructedSite(key, optionIndex);
        }

        public static void Reset() { /* session clear is on LanNetworkManager */ }
    }

    /// <summary>Harmony patch: intercepts Item.empDisable and broadcasts the light-off state.</summary>
    [HarmonyPatch(typeof(Item), "empDisable")]
    public static class ItemEmpDisablePatch
    {
        private static void Postfix(Item __instance)
        {
            if (ModRuntime.Network == null)
                return;
            if (TraverseHack.ApplyingFromNetwork || LanNetworkManager.IsApplyingRemoteState)
                return;
            if (__instance.GetComponent<Generator>() != null)
                return;
            if (!__instance.isLight && !__instance.switchable)
                return;

            Vector3 p = __instance.transform.position;
            string itemType = __instance.invItem != null ? __instance.invItem.type : "";
            ModRuntime.Network.SendLightState(new LightStateMessage
            {
                PosX = p.x,
                PosY = p.y,
                PosZ = p.z,
                IsOn = false,
                ItemName = __instance.name,
                ItemType = itemType
            });
            ModRuntime.LegacyInfo("[LightSync] send empDisable " + __instance.name + " type=" + itemType);
        }
    }

    /// <summary>
    /// Harmony patch: intercepts InteractiveItem.switchMe() (player-toggled levers, switches,
    /// buttons) and broadcasts the new toggle state to all peers.
    /// For wells: only sync the false→true transition (fix/repair) so both players see it
    /// as repaired; true→false (use/heal) is per-player so each can heal independently.
    /// </summary>
    [HarmonyPatch(typeof(InteractiveItem), "switchMe")]
    public static class InteractiveItemSwitchPatch
    {
        private static void Prefix(InteractiveItem __instance, out bool __state)
        {
            __state = __instance.isOn;
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
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

            ModRuntime.Network.SendInteractiveItemSwitch(new InteractiveItemSwitchMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                IsOn = __instance.isOn
            });
            ModRuntime.LegacyInfo("[InteractiveItemSync] switchMe at " + key + " isOn=" + __instance.isOn);
        }

        private static bool IsWellInteractiveItem(InteractiveItem ii)
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
        private static void Postfix(InteractiveItem __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

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
        private static void Postfix(InteractiveItem __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

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
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

            ModRuntime.Network.SendPadlockUnlock(new PadlockUnlockMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z
            });
            ModRuntime.LegacyInfo("[PadlockSync] unlock at " + key);
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
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

            ModRuntime.Network.SendLockedUnlock(new LockedUnlockMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z
            });
            ModRuntime.LegacyInfo("[LockedSync] unlock at " + key);
        }
    }

    /// <summary>Harmony patch: intercepts Generator.addFuel() and broadcasts updated state.</summary>
    [HarmonyPatch(typeof(Generator), "addFuel")]
    public static class GeneratorAddFuelPatch
    {
        private static void Postfix(Generator __instance)
        {
            if (ModRuntime.Network == null)
                return;
            if (TraverseHack.ApplyingFromNetwork || LanNetworkManager.IsApplyingRemoteState)
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(Mathf.Round(p.x * 10f) / 10f, Mathf.Round(p.y * 10f) / 10f, Mathf.Round(p.z * 10f) / 10f);

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
                ModRuntime.LegacyInfo("[GeneratorSync] send addFuel at " + key + " fuel=" + __instance.fuel);
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
            Vector3 key = new Vector3(Mathf.Round(p.x * 10f) / 10f, Mathf.Round(p.y * 10f) / 10f, Mathf.Round(p.z * 10f) / 10f);

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
                ModRuntime.LegacyInfo("[GeneratorSync] send setLowPower=" + __instance.lowPower + " at " + key);
        }
    }
}
