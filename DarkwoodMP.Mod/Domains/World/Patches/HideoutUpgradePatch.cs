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

    /// <summary>
    /// Each player's home is their own lit oven (vanilla <c>Player.experienceMachine</c>, also the
    /// respawn home). Vanilla puts out your previous oven when you move home, and that took the
    /// home (and the shadow ward) away from whoever else still lived there. An oven now goes out
    /// only when nobody calls it home any more. Homes travel with each player's effect sync.
    /// </summary>
    internal static class OvenHomes
    {
        private const float SameOvenSq = 1f;

        /// <summary>
        /// The local player's lit home oven is out (the host's world save, which holds the host's
        /// home): light it again next frame, outside any apply scope, so peers hear it. Only for a
        /// home this player already lit: an oven never examined stays unlit, as in vanilla.
        /// </summary>
        internal static void RelightOwnHomeNextFrame()
        {
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null)
                return;
            ctrl.waitFramesAndRun(() =>
            {
                Player p = Player.Instance;
                ExperienceMachine home = p != null ? p.experienceMachine : null;
                if (home != null && !home.isOn)
                    home.enable();
            }, 1);
        }

        /// <summary>Another player (not <paramref name="exceptPlayerId"/>) has this oven as home.</summary>
        internal static bool IsOtherPlayersHome(ExperienceMachine oven, int exceptPlayerId)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (oven == null || net == null || !net.IsConnected)
                return false;
            Vector3 p = oven.transform.position;
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null || proxy.PlayerId == exceptPlayerId || !proxy.RemoteHomeOven.HasValue)
                    continue;
                if ((proxy.RemoteHomeOven.Value - p).sqrMagnitude <= SameOvenSq)
                    return true;
            }
            return false;
        }
    }

    /// <summary>Moving home keeps the old oven lit while another player lives there.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.setExperienceMachine))]
    public static class OvenMoveHomePatch
    {
        private static bool Prefix(Player __instance, ExperienceMachine machine, bool doEnable)
        {
            if (machine == null || __instance.experienceMachine == null || __instance.experienceMachine == machine)
                return true;
            if (!OvenHomes.IsOtherPlayersHome(__instance.experienceMachine, exceptPlayerId: -1))
                return true;
            ModRuntime.LegacyInfo("[HideoutUpgrade] moving home — old oven stays lit (another player's home)");
            if (doEnable)
                machine.enable();
            else
                machine.setAsDefaultExpMachine();
            return false;
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

            // A peer's lit oven hums here too, as a lit oven does in vanilla.
            if (LanNetworkManager.IsApplyingRemoteState)
                return;

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
