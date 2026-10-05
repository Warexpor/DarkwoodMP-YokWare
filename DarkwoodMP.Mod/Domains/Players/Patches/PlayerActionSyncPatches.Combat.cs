using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{

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

        // The capture travels in __state (not a static map): a nested throw cannot overwrite it
        // and an exception between Prefix and Postfix cannot leave a stale entry behind.
        private static bool Prefix(Player __instance, out ThrowCapture __state)
        {
            __state = null;
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

            __state = capture;
            return true;
        }

        private static void Postfix(Player __instance, ThrowCapture __state)
        {
            ThrowCapture capture = __state;
            if (capture == null)
                return;

            if (string.IsNullOrEmpty(capture.ItemType)) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (ModRuntime.Network.Role == NetworkRole.Offline) return;
            if (TraverseHack.ApplyingFromNetwork) return;

            Vector3 pos = __instance.transform.position;

            // After vanilla throwItem: heldItem field is null, but the GO we captured
            // still has landTarget + rigidbody velocity. Prefer those over Prefix estimates.
            float vx = 0f, vy = 0f, vz = 0f;
            float landX = 0f, landY = 0f, landZ = 0f;
            bool hasLand = false;
            float distance = capture.Distance;
            if (capture.HeldItem != null)
            {
                Rigidbody rb = capture.HeldItem.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    vx = rb.velocity.x;
                    vy = rb.velocity.y;
                    vz = rb.velocity.z;
                }
                ThrownItem ti = capture.HeldItem.GetComponent<ThrownItem>();
                if (ti != null && ti.thrown)
                {
                    landX = ti.landTarget.x;
                    landY = ti.landTarget.y;
                    landZ = ti.landTarget.z;
                    hasLand = true;
                    // Authoritative cursor range from vanilla landTarget (matches flyTime).
                    float td = Vector3.Distance(
                        new Vector3(pos.x, 0f, pos.z),
                        new Vector3(landX, 0f, landZ));
                    if (td > 1f)
                        distance = Mathf.Clamp(td, 10f, 370f);
                }
            }

            // Reconstruct velocity if capture missed it (kinematic hold / timing).
            // Vanilla: vel = facing * distance * 2.5 when ThrownItem.initialVelocity == 0.
            if (vx * vx + vy * vy + vz * vz < 0.01f)
            {
                Vector3 dir = __instance.transform.up;
                if (dir.sqrMagnitude < 0.01f)
                    dir = Quaternion.Euler(0f, capture.AimY, 0f) * Vector3.forward;
                else
                    dir.Normalize();
                float initV = 0f;
                if (capture.HeldItem != null)
                {
                    ThrownItem ti0 = capture.HeldItem.GetComponent<ThrownItem>();
                    if (ti0 != null) initV = ti0.initialVelocity;
                }
                Vector3 rebuilt = initV > 0f ? dir * initV : dir * distance * 2.5f;
                vx = rebuilt.x; vy = rebuilt.y; vz = rebuilt.z;
            }
            bool isFlare = !string.IsNullOrEmpty(capture.ItemType)
                && capture.ItemType.IndexOf("flare", System.StringComparison.OrdinalIgnoreCase) >= 0;
            // A flare burns from when it was lit in the hand (vanilla Flare.Start on aim): peers
            // start their copy that far into vanilla's clock.
            float flareAge = isFlare ? Sync.FlareClock.AgeOf(capture.HeldItem) : -1f;
            // Vanilla throwItem put the thrown weapon itself (wear, upgrades) in the thrown object's
            // slot when it can be picked back up; peers' copies carry the same weapon.
            bool recoverable = false;
            float recDurability = 0f;
            string[] recUpgrades = null;
            Inventory thrownInv = capture.HeldItem != null ? capture.HeldItem.GetComponent<Inventory>() : null;
            InvItemClass thrownItem = thrownInv != null && thrownInv.slots != null && thrownInv.slots.Count > 0
                ? thrownInv.slots[0].invItem : null;
            if (!InvItemClass.isNull(thrownItem) && thrownItem.baseClass != null && thrownItem.baseClass.recoverableAfterThrown)
            {
                recoverable = true;
                recDurability = thrownItem.durability;
                recUpgrades = Sync.InvItemUpgradeWire.CollectNames(thrownItem);
            }
            ModRuntime.Network.SendThrowableSpawn(new ThrowableSpawnMessage
            {
                Recoverable = recoverable,
                Durability = recDurability,
                Upgrades = recUpgrades,
                ItemType = capture.ItemType,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                AimY = capture.AimY,
                Distance = distance,
                VelX = vx,
                VelY = vy,
                VelZ = vz,
                FlareAge = flareAge,
                LandX = landX,
                LandY = landY,
                LandZ = landZ,
                HasLandTarget = hasLand
            });

            // The thrower's own flare keeps vanilla's clock from the aim. Listed for joiners (and for
            // this peer if it is promoted to host); peers list the copies they spawn.
            if (isFlare && capture.HeldItem != null)
                Sync.WorldPhysicsSyncService.NoteThrownFlare(capture.HeldItem);

            // Client thrower: local projectile is FX-only. Host spawns the combat copy
            // via ThrowableSpawn so damage is not applied twice (local explode + host sim).
            if (ModRuntime.Network.Role == NetworkRole.Client && capture.HeldItem != null)
            {
                Sync.WorldPhysicsSyncService.MuteThrownCombat(capture.HeldItem);
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[ThrowableSync] muted client throw combat for {capture.ItemType}");
            }

            // Always log throws (esp. flares) — playtests had silent host TX.
            ModLog.Event(LogCat.World, "[ThrowableSync] sent " + capture.ItemType
                + " flareAge=" + flareAge.ToString("F1")
                + " dist=" + distance.ToString("F0")
                + " vel=" + Mathf.Sqrt(vx * vx + vy * vy + vz * vz).ToString("F0")
                + " land=" + (hasLand ? "y" : "n")
                + " role=" + ModRuntime.Network.Role
                + " from=" + pos);

            // Continuous held light drops next pose (heldItem null). Force full light rebuild.
            LightStateHelper.SendLightState(__instance, "afterThrow");
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
        private static readonly AccessTools.FieldRef<Explodes, bool> Activated =
            AccessTools.FieldRefAccess<Explodes, bool>("activated");

        // Vanilla onActivate does nothing once activated (every extra pellet, a later ignite):
        // only the real activation is sent, or each one replayed a boom on every peer.
        private static void Prefix(Explodes __instance, out bool __state)
        {
            __state = __instance != null && Activated(__instance);
        }

        private static void Postfix(Explodes __instance, bool __state)
        {
            if (__state || __instance == null || !Activated(__instance)) return;
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
                    ModRuntime.LegacyInfo($"[ExplosionSync] skip host-synced ThrownItem explosion at {__instance.transform.position}");
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
            // (A muted throw copy spawned none: it must still get the host's.)
            if (__instance.spawnObject != null && __instance.objectAmount > 0)
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

            ModRuntime.LegacyInfo($"[ExplosionSync] sent explosion at {pos} name={__instance.name} sound={soundId} prefab={prefabName} flaming={flaming}");
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
