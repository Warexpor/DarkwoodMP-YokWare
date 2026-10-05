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

            if (!NetGuard.Host(out var net)) return;
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
    [HarmonyPatch(typeof(CharacterEffect), "initialize", typeof(InvItemEffect))]
    public static class ClientBurnDoTSkipPatch
    {
        [HarmonyPriority(Priority.Last)]
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

    /// <summary>
    /// Client: a burning door, window or destructible item takes its fire damage on the host
    /// only. Vanilla <c>Burn.waitToGetHit</c> ticks on whichever peer has the Burn, and every
    /// peer had it (WorldBurnState): each tick sent the object's health to the others, so with
    /// N players it burned down up to N times as fast. The client's own burning player still
    /// ticks here (its health is its own).
    /// </summary>
    [HarmonyPatch(typeof(Burn), nameof(Burn.waitToGetHit))]
    public static class ClientWorldBurnTickSkipPatch
    {
        private static bool Prefix(Burn __instance, ref System.Collections.IEnumerator __result)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Client || !net.IsConnected)
                return true;
            if (__instance == null || __instance.GetComponent<Player>() != null)
                return true;
            __result = DWMPHorde.Harmony.HarmonyCoroutineUtil.Empty();
            return false;
        }
    }

    /// <summary>Host→client sync of entity burn stop (Burn.stop).</summary>
    [HarmonyPatch(typeof(Burn), "stop")]
    public static class HostBurnStopSyncPatch
    {
        private static void Postfix(Burn __instance)
        {
            if (!NetGuard.Host(out var net)) return;
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
    [HarmonyPatch(typeof(CharacterEffect), "initialize", typeof(InvItemEffect))]
    public static class PlayerBurnSyncPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(CharacterEffect __instance, object[] __args)
        {
            InvItemEffect effect = (InvItemEffect)__args[0];

            if (effect.type != CharacterEffectType.burn && effect.type != CharacterEffectType.burnSpecial) return;
            if (TraverseHack.ApplyingFromNetwork) return;

            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role == NetworkRole.Offline) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            if (__instance.GetComponent<Player>() == null) return;

            net.SendPlayerBurning(true, effect.duration, effect.type == CharacterEffectType.burnSpecial);
            ModRuntime.LegacyInfo("[PlayerBurnSync] sent player burn start");
        }
    }

    /// <summary>Bidirectional sync of player burn stop (Burn.stop on a Player).</summary>
    [HarmonyPatch(typeof(Burn), "stop")]
    public static class PlayerBurnStopSyncPatch
    {
        [HarmonyPriority(Priority.Last)]
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
        // Burning-before travels in __state (not a static): stopBurning can chain into another
        // puddle's stopBurning and a static would be overwritten before this Postfix.
        private static void Prefix(Liquid __instance, out bool __state)
        {
            __state = _IsBurning(__instance);
        }

        private static void Postfix(Liquid __instance, bool __state)
        {
            if (!__state) return;
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

    /// <summary>
    /// Host Flame / molotov secondaries (ExplosionSpawnObject) also spawn on clients.
    /// Contact <em>damage</em> there would stack with host proxy <c>DamagePlayer</c>, so only
    /// that <c>getHit</c> is muted. The rest of vanilla's CharBase contact stays: the burn effect
    /// is activated and the flame ends (<c>stopEmission</c>) exactly as it would.
    /// World Item/Door/Window burn still runs locally for fire-spread visuals.
    /// </summary>
    [HarmonyPatch(typeof(Flame), "onCollideWith")]
    public static class ClientFlameCharDamageMutePatch
    {
        /// <summary>The object vanilla Flame would burn and hit (destructible item, door, window), or null.</summary>
        private static GameObject FlameWorldTarget(Transform t, Collider c)
        {
            Item item = c != null ? c.transform.GetComponent<Item>() : null;
            if (item == null && t != null)
                item = t.GetComponent<Item>();
            if (item != null && item.destructible)
                return item.gameObject;
            Door door = c != null ? Door.getDoorScript(c.transform) : null;
            if (door != null)
                return door.gameObject;
            Window window = c != null ? c.GetComponent<Window>() : null;
            return window != null ? window.gameObject : null;
        }

        private static bool Prefix(Flame __instance, Transform _transform, Collider _collider, bool destroyMeAfterCollision)
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return true;
            if (__instance == null || __instance.gameObject == null || !__instance.isActive)
                return true;
            // This player's own flamethrower: its hits are this player's attacks, as with a gun
            // (creature hits go to the host as attacks; door and crate hits are reported).
            // Muting them left a client's flamethrower harmless.
            FlameOrigin origin = __instance.GetComponent<FlameOrigin>();
            if (origin != null && origin.LocalShot)
                return true;

            CharBase body = null;
            if (_collider != null)
                body = _collider.transform.GetComponent<CharBase>();
            if (body == null && _transform != null)
                body = _transform.GetComponent<CharBase>();

            var hitList = Traverse.Create(__instance)
                .Field("transformsAlreadyCollidedWith")
                .GetValue<System.Collections.Generic.List<Transform>>();

            if (body == null)
            {
                // A burnable crate, door or window: it catches fire here as in vanilla, but its
                // damage is the host's. This copy's hit sent its own absolute health up and
                // overwrote the host's (healing the door or breaking it early).
                GameObject burnable = FlameWorldTarget(_transform, _collider);
                if (burnable == null)
                    return true;
                if (hitList != null && _transform != null)
                    hitList.Add(_transform);
                if (burnable.GetComponent<Burn>() == null)
                    burnable.AddComponent<Burn>().burnTime = 20f;
                if (destroyMeAfterCollision)
                    __instance.stopEmission();
                return false;
            }

            if (hitList != null && _transform != null)
                hitList.Add(_transform);

            // Vanilla CharBase branch minus getHit(contactDamage) — host owns that damage.
            if (body.effects != null)
                body.effects.activate(__instance.effect);
            if (destroyMeAfterCollision)
                __instance.stopEmission();
            return false;
        }
    }
}
