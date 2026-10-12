using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host leaves the world for the title screen ("exit to menu", the permadeath box) while other
    /// players are still in it: one of them takes the host now, while this machine still has the
    /// world loaded. Left hosting from the title, nobody ran the world for them (no AI, no clock)
    /// and the title scene's own Controller answered for the world's.
    /// </summary>
    [HarmonyPatch(typeof(Core), nameof(Core.returnToMainMenu))]
    public static class HostReturnToMenuPatch
    {
        private static void Prefix()
        {
            LanNetworkManager net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;

            // A player bound for the credits leaves the world too: never the next host.
            var inEnding = new HashSet<int>();
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy != null && proxy.PlayerId > 0 && proxy.RemoteInEpilogue)
                    inEnding.Add(proxy.PlayerId);
            }
            bool started = net.TryGracefulHostLeave(id => net.IsPeerInWorld(id) && !inEnding.Contains(id));
            if (started)
                ModLog.Event(LogCat.Session, "Host left for the title — the world handed to a player still in it");
        }
    }
}
