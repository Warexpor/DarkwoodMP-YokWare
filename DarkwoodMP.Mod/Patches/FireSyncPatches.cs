using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>Host→client sync of entity burn start (CharacterEffect.initialize with incendiary effect).</summary>
    [HarmonyPatch(typeof(CharacterEffect), "initialize", typeof(InvItemEffect))]
    public static class HostBurnStartSyncPatch
    {
        private static void Postfix(CharacterEffect __instance, object[] __args)
        {
            InvItemEffect effect = (InvItemEffect)__args[0];

            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host) return;
            if (effect.type != CharacterEffectType.burn && effect.type != CharacterEffectType.burnSpecial) return;
            if (TraverseHack.ApplyingFromNetwork) return;

            Character c = __instance.GetComponent<Character>();
            if (c == null) return;
            short stableId = CharacterTracker.GetStableId(c);
            if (stableId < 0) return;

            net.SendEntityBurning(stableId, true, effect.duration, effect.modifier, effect.interval);
            ModRuntime.LegacyInfo($"[BurnSync] Host sent Burn start for entity {stableId}");
        }
    }

    /// <summary>On client, stops coroutines for burn DoT on host-synced entities (host controls burn damage).</summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(CharacterEffect), "initialize", typeof(InvItemEffect))]
    public static class ClientBurnDoTSkipPatch
    {
        private static void Postfix(CharacterEffect __instance, object[] __args)
        {
            InvItemEffect effect = (InvItemEffect)__args[0];

            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Client) return;
            if (effect.type != CharacterEffectType.burn && effect.type != CharacterEffectType.burnSpecial) return;

            Character c = __instance.GetComponent<Character>();
            if (c == null) return;
            if (!ClientEntityInterpolationService.IsHostSynced(c)) return;

            var burn = c.GetComponent<Burn>();
            if (burn != null)
            {
                burn.StopAllCoroutines();
                ModRuntime.LegacyInfo("[BurnSync] Client stopped DoT coroutine for host-synced entity");
            }
        }
    }

    /// <summary>Host→client sync of entity burn stop (Burn.stop).</summary>
    [HarmonyPatch(typeof(Burn), "stop")]
    public static class HostBurnStopSyncPatch
    {
        private static void Postfix(Burn __instance)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host) return;
            if (TraverseHack.ApplyingFromNetwork) return;

            Character c = __instance.GetComponent<Character>();
            if (c == null) return;
            short stableId = CharacterTracker.GetStableId(c);
            if (stableId < 0) return;

            net.SendEntityBurning(stableId, false);
            ModRuntime.LegacyInfo($"[BurnSync] Host sent Burn stop for entity {stableId}");
        }
    }

    /// <summary>Bidirectional sync of player burn start (CharacterEffect.initialize on a Player).</summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(CharacterEffect), "initialize", typeof(InvItemEffect))]
    public static class PlayerBurnSyncPatch
    {
        private static void Postfix(CharacterEffect __instance, object[] __args)
        {
            InvItemEffect effect = (InvItemEffect)__args[0];

            if (effect.type != CharacterEffectType.burn && effect.type != CharacterEffectType.burnSpecial) return;
            if (TraverseHack.ApplyingFromNetwork) return;

            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role == NetworkRole.Offline) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            if (__instance.GetComponent<Player>() == null) return;

            net.SendPlayerBurning(true, effect.duration);
            ModRuntime.LegacyInfo("[PlayerBurnSync] sent player burn start");
        }
    }

    /// <summary>Bidirectional sync of player burn stop (Burn.stop on a Player).</summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Burn), "stop")]
    public static class PlayerBurnStopSyncPatch
    {
        private static void Postfix(Burn __instance)
        {
            if (TraverseHack.ApplyingFromNetwork || LanNetworkManager.IsApplyingRemoteState) return;

            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role == NetworkRole.Offline) return;

            if (__instance.GetComponent<Player>() == null) return;

            net.SendPlayerBurning(false);
            ModRuntime.LegacyInfo("[PlayerBurnSync] sent player burn stop");
        }
    }

    /// <summary>Host→client sync of liquid/gasoline stopBurning (gas puddle fire extinguishing).</summary>
    [HarmonyPatch(typeof(Liquid), "stopBurning")]
    public static class LiquidStopBurningSyncPatch
    {
        private static bool _wasBurningCached;

        private static void Prefix(Liquid __instance)
        {
            _wasBurningCached = _IsBurning(__instance);
        }

        private static void Postfix(Liquid __instance)
        {
            if (!_wasBurningCached) return;
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role == NetworkRole.Offline) return;
            if (TraverseHack.ApplyingFromNetwork || LanNetworkManager.IsApplyingRemoteState) return;
            if (__instance == null || __instance.transform == null) return;

            Vector3 pos = __instance.transform.position;
            net.SendLiquidStopBurning(pos);
        }

        private static bool _IsBurning(Liquid l)
        {
            var trv = Traverse.Create(l);
            var burning = trv.Field("burning");
            if (burning.FieldExists()) return burning.GetValue<bool>();
            var isBurning = trv.Field("isBurning");
            if (isBurning.FieldExists()) return isBurning.GetValue<bool>();
            return true;
        }
    }
}
