using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// The hideout oven (vanilla <c>ExperienceMachine</c>) is lit or put out. Only a real change
    /// of <c>isOn</c> outside a load is sent: vanilla <c>Start</c> calls <c>disable()</c> on every
    /// unlit oven and <c>enable()</c> in story scenes, and sending those made a peer's world load
    /// put out the oven the party had lit (and with it the hideout's shadow ward).
    /// </summary>
    internal static class HideoutOvenSync
    {
        internal static bool ShouldSend(bool wasOn, ExperienceMachine oven)
        {
            if (oven == null || oven.isOn == wasOn)
                return false;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return false;
            if (LanNetworkManager.IsApplyingRemoteState || Core.loadingGame)
                return false;
            var ol = Singleton<OutsideLocations>.Instance;
            return ol == null || !ol.loading;
        }

        internal static void Send(ExperienceMachine oven)
        {
            Vector3 p = oven.transform.position;
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);
            ModRuntime.Network.SendHideoutUpgrade(new HideoutUpgradeMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                IsOn = oven.isOn
            });
            ModRuntime.LegacyInfo($"[HideoutUpgrade] {(oven.isOn ? "enable" : "disable")} at {key}");
        }
    }

    [HarmonyPatch(typeof(ExperienceMachine), "enable")]
    public static class HideoutUpgradeEnablePatch
    {
        private static void Prefix(ExperienceMachine __instance, out bool __state)
        {
            __state = __instance != null && __instance.isOn;
        }

        private static void Postfix(ExperienceMachine __instance, bool __state)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

            if (LanNetworkManager.IsApplyingRemoteState)
            {
                // Client side: enable() just ran and called sound.SetActive(true),
                // starting the oven's ambient hum.  Suppress it — closing the
                // cooking menu doesn't call disable(), so the sound would persist
                // on the client forever.
                if (__instance.sound != null)
                    __instance.sound.SetActive(false);
                return;
            }

            if (HideoutOvenSync.ShouldSend(__state, __instance))
                HideoutOvenSync.Send(__instance);
        }
    }

    [HarmonyPatch(typeof(ExperienceMachine), "disable")]
    public static class HideoutUpgradeDisablePatch
    {
        private static void Prefix(ExperienceMachine __instance, out bool __state)
        {
            __state = __instance != null && __instance.isOn;
        }

        private static void Postfix(ExperienceMachine __instance, bool __state)
        {
            if (HideoutOvenSync.ShouldSend(__state, __instance))
                HideoutOvenSync.Send(__instance);
        }
    }
}
