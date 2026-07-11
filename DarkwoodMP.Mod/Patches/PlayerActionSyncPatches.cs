using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Helper used by both PlayerLightTogglePatch and PlayerLightOnSwitchPatch.
    /// </summary>
    internal static class LightStateHelper
    {
        internal static PlayerLightStateMessage BuildLightState(Player __instance)
        {
            var msg = new PlayerLightStateMessage { LightOn = false };

            // Capture ambient light from hotbar items FIRST (before early return),
            // so toggling a non-activated held item doesn't turn off the ambient dot.
            // This reads the Player's lightDot radius — if it's larger than default,
            // a hotbar lantern is providing ambient light.
            if (Player.Instance != null)
            {
                var t = HarmonyLib.Traverse.Create(Player.Instance);
                Light2D lightDot = t.Field("lightDot").GetValue<Light2D>();
                float defaultRadius = t.Field("lightDotDefaultRadius").GetValue<float>();
                if (lightDot != null && lightDot.LightRadius > defaultRadius)
                {
                    msg.HasAmbientLight = true;
                    msg.LightRadius = lightDot.LightRadius;
                    msg.LightOn = true;
                    msg.LightIntensity = 1f;
                    msg.LightColorR = 1f;
                    msg.LightColorG = 1f;
                    msg.LightColorB = 1f;
                }
            }

            // Flare/match B+: held burn lights owned by PlayerState continuous stream only.
            string curType = __instance.currentItem != null ? __instance.currentItem.type : null;
            if (!string.IsNullOrEmpty(curType) &&
                curType.IndexOf("flare", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return msg;
            if (LanNetworkManager.IsMatchLightItem(__instance))
                return msg;

            if (InvItemClass.isNull(__instance.currentItem) || !__instance.currentItem.activated)
                return msg;

            msg.ItemType = __instance.currentItem.type;

            if (__instance.currentItem.baseClass.isFlashlight)
            {
                msg.IsFlashlight = true;
                msg.LightOn = true;
                Light2D flash = HarmonyLib.Traverse.Create(__instance).Field("Flashlight").GetValue<Light2D>();
                if (flash != null)
                {
                    msg.LightRadius = flash.LightRadius;
                    msg.LightColorR = flash.LightColor.r;
                    msg.LightColorG = flash.LightColor.g;
                    msg.LightColorB = flash.LightColor.b;
                    msg.LightIntensity = flash.LightIntensity;
                }
            }
            else if (__instance.currentItem.baseClass.lightEmitter != null)
            {
                msg.HasLightEmitter = true;
                msg.LightOn = true;
                msg.LightRadius = __instance.currentItem.baseClass.lightRadius;
            }
            else if (__instance.currentItem.baseClass.lightRadius > 0f)
            {
                // Ambient light via lightDot (lantern, etc. — no flame emitter prefab).
                // Vanilla expand the lightDot radius; the receiver will activate the
                // proxy's PlayerLightDot.  Separating this from HasLightEmitter prevents
                // both the vision cone bug and the "lightEmitter is null" warning.
                msg.HasAmbientLight = true;
                msg.LightOn = true;
                msg.LightRadius = __instance.currentItem.baseClass.lightRadius;
            }
            else
            {
                // Non-flare held item with a Light2D (candles, etc.). Flares never reach here.
                if (__instance.heldItem != null)
                {
                    Light2D itemLight = __instance.heldItem.GetComponentInChildren<Light2D>(true);

                    if (itemLight != null)
                    {
                        msg.HasItemLight = true;
                        msg.LightOn = true;
                        msg.LightRadius = itemLight.LightRadius;
                        msg.LightColorR = itemLight.LightColor.r;
                        msg.LightColorG = itemLight.LightColor.g;
                        msg.LightColorB = itemLight.LightColor.b;
                        msg.LightIntensity = itemLight.LightIntensity;
                    }
                }
            }

            return msg;
        }
    }

    [HarmonyPatch(typeof(Player), "onActivateItem")]
    public static class PlayerLightTogglePatch
    {
        private static void Postfix(Player __instance)
        {
            if (ModRuntime.Network == null) return;
            if (ModRuntime.Network.Role == NetworkRole.Offline) return;
            if (TraverseHack.ApplyingFromNetwork) return;

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[Light] onActivateItem fired: type=" + (__instance.currentItem?.type ?? "null") + " activated=" + __instance.currentItem?.activated);
            ModRuntime.Network.SendPlayerLightState(LightStateHelper.BuildLightState(__instance), LiteNetLib.DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>
    /// Also sync light state when switching items (torch/flashlight might activate on equip).
    /// </summary>
    [HarmonyPatch(typeof(Player), "onDoneSwitchingItem")]
    public static class PlayerLightOnSwitchPatch
    {
        private static void Postfix(Player __instance)
        {
            if (ModRuntime.Network == null) return;
            if (ModRuntime.Network.Role == NetworkRole.Offline) return;
            if (TraverseHack.ApplyingFromNetwork) return;

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[Light] onDoneSwitchingItem fired: type=" + (__instance.currentItem?.type ?? "null") + " activated=" + __instance.currentItem?.activated);
            ModRuntime.Network.SendPlayerLightState(LightStateHelper.BuildLightState(__instance), LiteNetLib.DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>
    /// Captures throwable spawn data and relays it to peers.
    /// Host-side projectile is combat-authoritative; client projectiles are FX-only
    /// (see MuteThrownCombat + visualOnly SpawnThrownItem).
    /// </summary>
    [HarmonyPatch(typeof(Player), "throwItem")]
    public static class ThrowableSyncPatch
    {
        private sealed class ThrowCapture
        {
            internal string ItemType;
            internal float AimY;
            internal float Distance;
            internal GameObject HeldItem;
        }

        private static readonly Dictionary<int, ThrowCapture> _captures = new Dictionary<int, ThrowCapture>(4);

        public static void Reset() => _captures.Clear();

        private static bool Prefix(Player __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return true;
            if (ModRuntime.Network.Role == NetworkRole.Offline) return true;
            if (TraverseHack.ApplyingFromNetwork) return true;

            var capture = new ThrowCapture();
            try
            {
                if (!InvItemClass.isNull(__instance.currentItem))
                    capture.ItemType = __instance.currentItem.type;
                capture.AimY = __instance.transform.eulerAngles.y;
                capture.Distance = Mathf.Clamp(__instance.distanceToCursor(), 10f, 370f);
                capture.HeldItem = __instance.heldItem;
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogError("[ThrowableSync] capture failed: " + ex);
                return true;
            }

            _captures[__instance.GetInstanceID()] = capture;
            return true;
        }

        private static void Postfix(Player __instance)
        {
            int playerId = __instance.GetInstanceID();
            if (!_captures.TryGetValue(playerId, out ThrowCapture capture))
                return;
            _captures.Remove(playerId);

            if (string.IsNullOrEmpty(capture.ItemType)) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (ModRuntime.Network.Role == NetworkRole.Offline) return;
            if (TraverseHack.ApplyingFromNetwork) return;

            float vx = 0f, vy = 0f, vz = 0f;
            if (capture.HeldItem != null)
            {
                Rigidbody rb = capture.HeldItem.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    vx = rb.velocity.x;
                    vy = rb.velocity.y;
                    vz = rb.velocity.z;
                }
            }

            Vector3 pos = __instance.transform.position;
            int throwId = 0;
            float longevity = 0f;
            if (ModRuntime.Network is LanNetworkManager lan)
                throwId = lan.MintThrowId();
            if (!string.IsNullOrEmpty(capture.ItemType)
                && capture.ItemType.IndexOf("flare", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                longevity = 5f;
                if (capture.HeldItem != null)
                {
                    Flare fl = capture.HeldItem.GetComponent<Flare>()
                        ?? capture.HeldItem.GetComponentInChildren<Flare>(true);
                    if (fl != null && fl.longevity > 0.05f)
                        longevity = fl.longevity + 2f;
                }
            }
            ModRuntime.Network.SendThrowableSpawn(new ThrowableSpawnMessage
            {
                ItemType = capture.ItemType,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                AimY = capture.AimY,
                Distance = capture.Distance,
                VelX = vx,
                VelY = vy,
                VelZ = vz,
                ThrowId = throwId,
                LongevitySec = longevity
            });

            // Client thrower: local projectile is FX-only. Host spawns the combat copy
            // via ThrowableSpawn so damage is not applied twice (local explode + host sim).
            if (ModRuntime.Network.Role == NetworkRole.Client && capture.HeldItem != null)
            {
                Sync.WorldPhysicsSyncService.MuteThrownCombat(capture.HeldItem);
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[ThrowableSync] muted client throw combat for " + capture.ItemType);
            }

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[ThrowableSync] sent " + capture.ItemType + " from " + pos);

            string thrownType = capture.ItemType;
            // After throw: full light rebuild (flare continuous path drops on next pose tick
            // when currentItem is no longer flare; this clears ambient / other held lights).
            ModRuntime.Network.SyncCurrentLightState();
        }
    }

    /// <summary>
    /// When an Explodes component activates on either peer, relays the explosion
    /// position to the other side. The host runs the authoritative explosion;
    /// the client spawns the visual effect (prefab + sound).
    /// </summary>
    [HarmonyPatch(typeof(Explodes), "onActivate", new System.Type[0])]
    public static class ExplosionTriggerPatch
    {
        private static void Postfix(Explodes __instance)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role == NetworkRole.Offline) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (Sync.WorldPhysicsSyncService._suppressBroadcast) return;

            // Suppress explosion trigger for host-synced ThrownItems (SpawnThrownItem).
            // The host's spawned ThrownItem explosion is a local side-effect; the
            // authoritative explosion comes from the client's own ThrownItem via its
            // ExplosionTriggerMessage. Without this suppression, the host's spawned
            // ThrownItem sends a duplicate explosion trigger to the client, causing
            // confusing double-FX at potentially different positions.
            ThrownItem ti = __instance.GetComponent<ThrownItem>();
            if (ti != null && ti.objectThatSpawnedMe != null)
            {
                bool isProxySpawned = false;
                foreach (var proxy in net.GetAllProxies())
                {
                    if (proxy != null && ti.objectThatSpawnedMe == proxy.transform)
                    {
                        isProxySpawned = true;
                        break;
                    }
                }
                if (isProxySpawned)
                {
                    ModRuntime.LegacyInfo("[ExplosionSync] skip host-synced ThrownItem explosion at " + __instance.transform.position);
                    return;
                }
            }

            bool flaming = false;
            try { flaming = (bool)HarmonyLib.Traverse.Create(__instance).Field("flaming").GetValue(); }
            catch (System.Exception) { /* optional field */ }

            string prefabName = "";
            try
            {
                if (__instance.explosionPrefab != null)
                    prefabName = __instance.explosionPrefab.name;
            }
            catch (System.Exception)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[Explosion] prefab name reflect failed");
            }

            // Public field — prefer direct read; resolve mushroom fallbacks if empty.
            string soundId = __instance.explodeSound ?? "";
            soundId = Sync.WorldPhysicsSyncService.ResolveExplosionSoundId(
                soundId, __instance.name, __instance) ?? "";

            Vector3 pos = __instance.transform.position;
            // Local activation already ran spawnObjects — debounce host ExplosionSpawnObject
            // so the stomper does not get a second set of secondary debris.
            ExplosionSpawnFlagTracker.NoteLocalExplodeFx(pos);

            net.SendExplosionTrigger(new ExplosionTriggerMessage
            {
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                ObjectName = __instance.name,
                Flaming = flaming,
                PrefabName = prefabName,
                SoundId = soundId
            });

            ModRuntime.LegacyInfo("[ExplosionSync] sent explosion at " + pos
                + " name=" + __instance.name + " sound=" + soundId + " prefab=" + prefabName
                + " flaming=" + flaming);
        }
    }

    /// <summary>
    /// Flare.Start no longer sends PlayerLightState (protocol 18 flare B+).
    /// Held flare light is streamed via PlayerState.FlareActive only.
    /// Patch kept as a no-op documentation hook so we do not reintroduce dual path.
    /// </summary>
    [HarmonyPatch(typeof(Flare), "Start")]
    public static class FlareStartPatch
    {
        private static void Postfix(Flare __instance)
        {
            // Intentionally empty — continuous PlayerState path owns held flare light.
            // Thrown flares get light from ThrowableSpawn prefab / EnsureThrownFlareLight.
        }
    }

    /// <summary>Harmony patch: intercepts InvItemClass.drainDurability when a light item burns out
    /// and syncs the off-state to the remote peer so the proxy light disappears.</summary>
    [HarmonyPatch(typeof(InvItemClass), "drainDurability")]
    public static class ItemDurabilityDrainPatch
    {
        private static void Postfix(InvItemClass __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (TraverseHack.ApplyingFromNetwork)
                return;

            // Only fire on burnout (durability <= 0 after drain) for light-emitting items
            if (__instance.durability > 0f)
                return;
            if (__instance.baseClass == null)
                return;

            bool isLightItem = __instance.baseClass.isFlashlight
                || __instance.baseClass.lightEmitter != null
                || __instance.baseClass.lightRadius > 0f
                || (!string.IsNullOrEmpty(__instance.type)
                    && __instance.type.IndexOf("flare", System.StringComparison.OrdinalIgnoreCase) >= 0);
            if (!isLightItem)
                return;

            // Full snapshot so ambient + emitters stay consistent (never ambient-only clobber).
            ModRuntime.Network.SyncCurrentLightState();
        }
    }

    /// <summary>
    /// Syncs the ambient light dot when a hotbar item changes it (e.g. lantern placed in
    /// or removed from the hotbar).  Vanilla calls Player.modifyLightDot(radius) when
    /// InvItemClass.switchActive toggles a "needsToBeOnHotbar" item like the lantern.
    /// Always rebuilds the full light state so ambient updates cannot strip torch/flashlight.
    /// </summary>
    [HarmonyPatch(typeof(Player), "modifyLightDot")]
    public static class PlayerAmbientLightPatch
    {
        private static void Postfix(Player __instance, float _destRadius)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (TraverseHack.ApplyingFromNetwork)
                return;

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[Light] modifyLightDot: radius={_destRadius} → full SyncCurrentLightState");

            ModRuntime.Network.SyncCurrentLightState();
        }
    }
}
