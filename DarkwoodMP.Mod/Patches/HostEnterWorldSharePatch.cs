using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Join flow fix: host starts LAN from the title menu, clients connect while host is still
    /// on profiles / loading — no world to share yet. When the host finally has a Player in
    /// chapter, push the save to every already-handshaked client still waiting.
    /// </summary>
    [HarmonyPatch(typeof(Player), "Start")]
    public static class HostEnterWorldSharePatch
    {
        private static float _lastShareAttempt = -999f;

        private static void Postfix(Player __instance)
        {
            try
            {
                if (__instance == null || __instance != Player.Instance)
                    return;

                var net = ModRuntime.Network as LanNetworkManager;
                if (net == null || net.Role != NetworkRole.Host)
                    return;
                if (!net.IsConnected || !net.IsHandshakeComplete)
                    return;
                if (net.WorldSaveShare != null && net.WorldSaveShare.IsBusy)
                    return;

                // Debounce: Player.Start can fire more than once across loads
                if (Time.unscaledTime - _lastShareAttempt < 8f)
                    return;
                _lastShareAttempt = Time.unscaledTime;

                ModLog.Event(LogCat.Save,
                    "Host entered world with " + net.ConnectedPlayerCount
                    + " peer(s) — auto world share to all (late joiners on title)");
                // Broadcast package; clients already in-game ignore begin (HandleBegin guard).
                net.WorldSaveShare?.ScheduleHostResend();
                // After files land, push journal/flags/entities (deferred from connect
                // when host was already in-world; also refreshes empty bulk if host was on title).
                net.ScheduleLateJoinBulkAfterWorldShare();
            }
            catch (System.Exception ex)
            {
                ModLog.Error(LogCat.Save, "HostEnterWorldSharePatch failed", ex);
            }
        }
    }
}
