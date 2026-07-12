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
    /// Helper used by both PlayerLightTogglePatch and PlayerLightOnSwitchPatch.
    /// </summary>
    internal static class LightStateHelper
    {
        /// <summary>Last sent event-path fingerprint (edge TX logs + dedupe optional).</summary>
        private static string _lastTxSig;

        internal static PlayerLightStateMessage BuildLightState(Player __instance)
        {
            var msg = new PlayerLightStateMessage { LightOn = false };

            // --- Hotbar lantern / ambient lightDot (vanilla InvItemClass + modifyLightDot) ---
            // Independent of currentItem: lantern can stay on while holding torch/fists/etc.
            TryPackAmbientLantern(ref msg, __instance);

            // Flare/match B+: held burn lights owned by PlayerState continuous stream only.
            string curType = __instance.currentItem != null ? __instance.currentItem.type : null;
            if (!string.IsNullOrEmpty(curType) &&
                curType.IndexOf("flare", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return msg;
            if (LanNetworkManager.IsMatchLightItem(__instance))
                return msg;

            if (InvItemClass.isNull(__instance.currentItem) || !__instance.currentItem.activated)
                return msg;

            // Held light layers on top of ambient — never clear HasAmbientLight / lantern type.
            if (__instance.currentItem.baseClass.isFlashlight)
            {
                msg.IsFlashlight = true;
                msg.LightOn = true;
                msg.ItemType = __instance.currentItem.type;
                Light2D flash = HarmonyLib.Traverse.Create(__instance).Field("Flashlight").GetValue<Light2D>();
                if (flash != null)
                {
                    // Keep ambient radius for lantern; flash cone is separate flag.
                    if (!msg.HasAmbientLight)
                        msg.LightRadius = flash.LightRadius;
                    msg.LightColorR = flash.LightColor.r;
                    msg.LightColorG = flash.LightColor.g;
                    msg.LightColorB = flash.LightColor.b;
                    msg.LightIntensity = flash.LightIntensity;
                }
            }
            else if (__instance.currentItem.baseClass.lightEmitter != null)
            {
                // Torch etc. + lantern ambient both on is valid SP (hotbar lantern + held torch).
                msg.HasLightEmitter = true;
                msg.LightOn = true;
                msg.ItemType = __instance.currentItem.type;
                if (!msg.HasAmbientLight && __instance.currentItem.baseClass.lightRadius > 0f)
                    msg.LightRadius = __instance.currentItem.baseClass.lightRadius;
            }
            else if (IsLanternItem(__instance.currentItem))
            {
                // Selected lantern (rare — usually hotbar-only via TryPackAmbient).
                msg.HasAmbientLight = true;
                msg.LightOn = true;
                msg.ItemType = "lantern";
                float r = __instance.currentItem.baseClass.lightRadius;
                if (r <= 0f) r = 450f;
                msg.LightRadius = r;
                msg.LightIntensity = 1f;
                msg.LightColorR = 1f;
                msg.LightColorG = 1f;
                msg.LightColorB = 1f;
            }
            else if (__instance.currentItem.baseClass.lightRadius > 0f
                     && __instance.currentItem.baseClass.lightEmitter == null)
            {
                // Other ambient-style held item — keep lantern ItemType if ambient already packed.
                msg.HasAmbientLight = true;
                msg.LightOn = true;
                if (string.IsNullOrEmpty(msg.ItemType))
                    msg.ItemType = __instance.currentItem.type;
                if (msg.LightRadius <= 0f)
                    msg.LightRadius = __instance.currentItem.baseClass.lightRadius;
            }
            else if (__instance.heldItem != null)
            {
                Light2D itemLight = __instance.heldItem.GetComponentInChildren<Light2D>(true);
                if (itemLight != null)
                {
                    msg.HasItemLight = true;
                    msg.LightOn = true;
                    if (string.IsNullOrEmpty(msg.ItemType))
                        msg.ItemType = __instance.currentItem.type;
                    if (!msg.HasAmbientLight)
                    {
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

        internal static bool IsLanternItem(InvItemClass item)
        {
            if (item == null || item.baseClass == null) return false;
            string t = item.type ?? "";
            if (t.IndexOf("lantern", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            // Hotbar-only natural light without emitter (vanilla lantern).
            return item.baseClass.needsToBeOnHotbar
                && item.baseClass.lightRadius > 0f
                && item.baseClass.lightEmitter == null
                && !item.baseClass.isFlashlight;
        }

        /// <summary>
        /// Vanilla (InvItemClass.checkForActiveSwitches / deactivateSwitches):
        /// lantern on hotbar → modifyLightDot(lightRadius) + logicLights + lightsPlayer.
        /// Always set ItemType=lantern when ambient is on — empty type thrash made peers
        /// re-apply every frame (A|lantern ↔ A||) and flicker the remote lightDot.
        /// </summary>
        internal static void TryPackAmbientLantern(ref PlayerLightStateMessage msg, Player player)
        {
            if (player == null) return;

            var t = HarmonyLib.Traverse.Create(player);
            Light2D lightDot = t.Field("lightDot").GetValue<Light2D>();
            float defaultRadius = t.Field("lightDotDefaultRadius").GetValue<float>();

            // 1) Explicit activated lantern on hotbar (authoritative).
            InvItemClass lantern = null;
            try
            {
                if (player.Hotbar != null)
                    lantern = player.Hotbar.getItem("lantern");
            }
            catch { /* optional */ }

            if (lantern != null && lantern.activated && lantern.baseClass != null
                && lantern.baseClass.lightRadius > 0f)
            {
                msg.HasAmbientLight = true;
                msg.LightOn = true;
                msg.LightRadius = lantern.baseClass.lightRadius;
                msg.LightIntensity = 1f;
                msg.LightColorR = 1f;
                msg.LightColorG = 1f;
                msg.LightColorB = 1f;
                msg.ItemType = "lantern";
                return;
            }

            // 2) activeItems (save/load / race where Hotbar.getItem misses).
            try
            {
                if (player.activeItems != null)
                {
                    for (int i = 0; i < player.activeItems.Count; i++)
                    {
                        InvItemClass ai = player.activeItems[i];
                        if (ai == null || !ai.activated || !IsLanternItem(ai)) continue;
                        float r = ai.baseClass != null && ai.baseClass.lightRadius > 0f
                            ? ai.baseClass.lightRadius : 450f;
                        msg.HasAmbientLight = true;
                        msg.LightOn = true;
                        msg.LightRadius = r;
                        msg.LightIntensity = 1f;
                        msg.LightColorR = 1f;
                        msg.LightColorG = 1f;
                        msg.LightColorB = 1f;
                        msg.ItemType = "lantern";
                        return;
                    }
                }
            }
            catch { /* optional */ }

            // 3) lightDot expanded past default = ambient still on.
            // Always stamp type=lantern — empty ItemType was the RX thrash critical.
            if (lightDot != null && lightDot.LightRadius > defaultRadius + 0.5f)
            {
                msg.HasAmbientLight = true;
                msg.LightOn = true;
                msg.LightRadius = lightDot.LightRadius;
                msg.LightIntensity = 1f;
                msg.LightColorR = 1f;
                msg.LightColorG = 1f;
                msg.LightColorB = 1f;
                msg.ItemType = "lantern";
            }
        }

        internal static void SendLightState(Player player, string reason)
        {
            if (ModRuntime.Network == null || player == null) return;
            var msg = BuildLightState(player);
            string sig = (msg.LightOn ? "1" : "0")
                + "|" + (msg.IsFlashlight ? "F" : "-")
                + "|" + (msg.HasLightEmitter ? "E" : "-")
                + "|" + (msg.HasItemLight ? "I" : "-")
                + "|" + (msg.HasAmbientLight ? "A" : "-")
                + "|" + (msg.ItemType ?? "")
                + "|" + msg.LightRadius.ToString("F0");
            if (sig != _lastTxSig)
            {
                ModLog.Event(LogCat.World,
                    $"[Light] TX {reason} {(_lastTxSig ?? "∅")} → {sig}");
                _lastTxSig = sig;
            }
            else if (ModRuntime.VerboseLogging || Config.ModConfig.IsVerboseLightSync)
            {
                ModRuntime.LegacyInfo($"[Light] TX {reason} same-sig {sig}");
            }
            ModRuntime.Network.SendPlayerLightState(msg, LiteNetLib.DeliveryMethod.ReliableOrdered);
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

            LightStateHelper.SendLightState(__instance, "onActivateItem");
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

            LightStateHelper.SendLightState(__instance, "onDoneSwitchingItem");
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
            int throwId = 0;
            float longevity = 0f;
            bool isFlare = !string.IsNullOrEmpty(capture.ItemType)
                && capture.ItemType.IndexOf("flare", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (ModRuntime.Network is LanNetworkManager lan)
                throwId = lan.MintThrowId();
            if (isFlare)
            {
                // Remaining until fully dark from aim-start clock (F4), not a fresh longevity+2.
                float lonFallback = 3f;
                if (capture.HeldItem != null)
                {
                    Flare fl = capture.HeldItem.GetComponent<Flare>()
                        ?? capture.HeldItem.GetComponentInChildren<Flare>(true);
                    if (fl != null && fl.longevity > 0.05f)
                        lonFallback = fl.longevity;
                }
                longevity = Sync.WorldPhysicsSyncService.GetFlareRemainingUntilDark(
                    capture.HeldItem, lonFallback);
            }
            ModRuntime.Network.SendThrowableSpawn(new ThrowableSpawnMessage
            {
                ItemType = capture.ItemType,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                AimY = capture.AimY,
                Distance = distance,
                VelX = vx,
                VelY = vy,
                VelZ = vz,
                ThrowId = throwId,
                LongevitySec = longevity,
                LandX = landX,
                LandY = landY,
                LandZ = landZ,
                HasLandTarget = hasLand
            });

            // Host must track own throw — never receives own ThrowableSpawn (F3).
            // ClaimFlareLifetime so vanilla waitToDie yields to host expire track (V4).
            if (isFlare && throwId > 0 && capture.HeldItem != null
                && ModRuntime.Network.Role == NetworkRole.Host)
            {
                Sync.WorldPhysicsSyncService.RegisterLocalThrownLight(
                    throwId, capture.HeldItem, longevity, capture.ItemType);
            }
            else if (isFlare && capture.HeldItem != null
                     && ModRuntime.Network.Role == NetworkRole.Client)
            {
                // Client thrower: keep aim-start waitToDie (correct clock). Host owns combat copy.
                // Do not Claim here — local vanilla die matches aim burn.
            }

            // Client thrower: local projectile is FX-only. Host spawns the combat copy
            // via ThrowableSpawn so damage is not applied twice (local explode + host sim).
            if (ModRuntime.Network.Role == NetworkRole.Client && capture.HeldItem != null)
            {
                Sync.WorldPhysicsSyncService.MuteThrownCombat(capture.HeldItem);
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[ThrowableSync] muted client throw combat for " + capture.ItemType);
            }

            // Always log throws (esp. flares) — playtests had silent host TX.
            ModLog.Event(LogCat.World, "[ThrowableSync] sent " + capture.ItemType
                + " throwId=" + throwId
                + " life=" + longevity.ToString("F2")
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
    /// Flare.Start runs at aim (heldItem spawn). Record burn clock so throw packets carry
    /// remaining life, not a fresh longevity+2 (vanilla waitToDie from Start).
    /// Continuous held light is streamed via PlayerState only when heldItem is live.
    /// </summary>
    [HarmonyPatch(typeof(Flare), "Start")]
    public static class FlareStartPatch
    {
        private static void Postfix(Flare __instance)
        {
            if (__instance == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (TraverseHack.ApplyingFromNetwork) return;

            Player p = Player.Instance;
            if (p == null || p.heldItem == null) return;
            // Only local player's aimed/held flare — not remote SpawnThrownItem prefabs.
            bool isHeld = __instance.gameObject == p.heldItem
                || __instance.transform.IsChildOf(p.heldItem.transform);
            if (!isHeld) return;

            float lon = __instance.longevity > 0.05f ? __instance.longevity : 3f;
            Sync.WorldPhysicsSyncService.NoteFlareBurnStart(__instance.gameObject, lon);
            // Also key the root held GO so GetFlareRemainingUntilDark(heldItem) works.
            if (__instance.gameObject != p.heldItem)
                Sync.WorldPhysicsSyncService.NoteFlareBurnStart(p.heldItem, lon);

            ModLog.Event(LogCat.World, "[LightSync] Flare.Start burn clock longevity=" + lon.ToString("F2")
                + " go=" + __instance.gameObject.name);
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
