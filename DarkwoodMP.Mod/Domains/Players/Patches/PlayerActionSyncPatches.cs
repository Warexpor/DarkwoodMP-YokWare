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

        /// <summary>Session end: the first light TX of the next session always logs.</summary>
        internal static void ResetTxSignature() => _lastTxSig = null;

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
}
