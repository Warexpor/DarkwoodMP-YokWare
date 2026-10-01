using System;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>Rounds a world position to a 0.1-unit key used by sync messages.</summary>
    internal static class WorldPos
    {
        public static Vector3 Key(Vector3 p) =>
            new Vector3(Mathf.Round(p.x * 10f) / 10f, Mathf.Round(p.y * 10f) / 10f, Mathf.Round(p.z * 10f) / 10f);
    }

    // Door.open fan-out lives solely in Patches.DoorOpenSyncPatch (DoorOpen + DoorState).
    // The old Sync.DoorOpenPatch was removed in 0.8.0 — it double-fired DoorState.

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
            Vector3 key = WorldPos.Key(p);

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
            ModRuntime.LegacyInfo($"[DoorSync] send close {__instance.name} at {key} bodyY={bodyRotY}");
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

            // Successful harvest/disarm: do NOT send TrapState boom (peer explosion FX).
            // TrapDisarmHarvestSync sends silent WorldObjectRemoved instead.
            if (TrapDisarmHarvestTracker.IsSilentDisarm)
            {
                ModRuntime.LegacyInfo($"[TrapSync] skip boom — silent disarm/harvest {__instance.name}");
                return;
            }

            if (!TrapNetworkId.IsWorldTrap(__instance.gameObject))
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = WorldPos.Key(p);

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
            ModRuntime.LegacyInfo($"[TrapSync] send triggered {__instance.name} id={trapId} at {key}");
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

        // Placement capture travels in __state (not statics) so a re-entrant progressBarCompleted
        // cannot overwrite it; the previous InsideTrapPlacement value is restored, not zeroed.
        private struct State
        {
            public bool PrevInside;
            public string Type;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        private static void Prefix(Player __instance, out State __state)
        {
            __state = default;
            __state.PrevInside = InsideTrapPlacement;
            // Only while placing. The previous condition also matched disarm and craft,
            // ObjectDestroyTrapPatch when beartraps Destroy() on successful disarm.
            InsideTrapPlacement = __instance != null && __instance.placingItem;
            if (!InsideTrapPlacement) return;
            if (InvItemClass.isNull(__instance.currentItem)) return;
            ProxyItem proxy = __instance.proxyItem;
            if (proxy == null) return;

            // Capture the item type and placement transform before the placement completes
            __state.Type = __instance.currentItem.type;
            __state.Pos = proxy.transform.localPosition;
            __state.Rot = proxy.transform.rotation;
        }

        private static void Postfix(Player __instance, State __state)
        {
            if (string.IsNullOrEmpty(__state.Type))
                return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

            Vector3 euler = __state.Rot.eulerAngles;
            ModRuntime.Network.SendItemSpawn(new ItemSpawnMessage
            {
                ItemType = __state.Type,
                PosX = __state.Pos.x,
                PosY = __state.Pos.y,
                PosZ = __state.Pos.z,
                RotX = euler.x,
                RotY = euler.y,
                RotZ = euler.z
            });
            ModRuntime.LegacyInfo($"[ItemSpawn] sent {__state.Type} at {__state.Pos}");
        }

        // progressBarCompleted can throw; stuck true suppresses WorldObject harvest/destroy forever.
        private static void Finalizer(State __state)
        {
            InsideTrapPlacement = __state.PrevInside;
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
            Vector3 key = WorldPos.Key(p);

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
            ModRuntime.LegacyInfo($"[GeneratorSync] send turnOn at {key} type={itemType}");
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
            Vector3 key = WorldPos.Key(p);

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
            ModRuntime.LegacyInfo($"[GeneratorSync] send turnOff at {key} type={itemType}");
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
            Vector3 key = WorldPos.Key(p);

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
            ModRuntime.LegacyInfo($"[GeneratorSync] send powerDown at {key} type={itemType}");

            // Do NOT fan-out LightState IsOn=false for powerItems.
            // Vanilla powerDown/cutPower keeps lamp isOn and only drops hasPower / visuals;
            // LightState turnOff overwrote isOn and broke generator restart;
            // restorePower requires the original state.
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
            ModRuntime.LegacyInfo($"[LightSync] send turnOn {__instance.name} type={itemType}");
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
            ModRuntime.LegacyInfo($"[LightSync] send turnOff {__instance.name} type={itemType}");
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
            Vector3 key = WorldPos.Key(p);

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
            ModRuntime.LegacyInfo($"[ConstructibleSync] sent construct at {key} option={option}");
        }

        internal static void RegisterConstructed(Constructible c, int forceOption)
        {
            if (c == null) return;
            Vector3 p = c.transform.position;
            Vector3 key = WorldPos.Key(p);
            int option = forceOption >= 0 ? forceOption : c.chosenOption;
            RegisterConstructed(key, option);
        }

        internal static void RegisterConstructed(Vector3 key, int optionIndex)
        {
            var net = ModRuntime.Network;
            net?.LockHandlers?.RegisterConstructedSite(key, optionIndex);
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
            ModRuntime.LegacyInfo($"[LightSync] send empDisable {__instance.name} type={itemType}");
        }
    }
}
