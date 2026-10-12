using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A player leaves the world for the title screen ("exit to menu", the permadeath box, the
    /// end of the credits): the session ends for that machine, as the multiplayer menu's
    /// Disconnect would. A client left connected at the title still counted as a player in the
    /// host's world, and the next save it loaded played on as part of the old session.
    /// A host with other players still in the world hands it to one of them now, while this
    /// machine still has the world loaded; left hosting from the title, nobody ran the world for
    /// them (no AI, no clock) and the title scene's own Controller answered for the world's.
    /// </summary>
    [HarmonyPatch(typeof(Core), nameof(Core.returnToMainMenu))]
    public static class ReturnToMenuPatch
    {
        private static void Prefix()
        {
            LanNetworkManager net = ModRuntime.Network;
            if (net == null || net.Role == NetworkRole.Offline)
                return;

            if (net.Role == NetworkRole.Host)
            {
                // A player bound for the credits leaves the world too: never the next host.
                var inEnding = new HashSet<int>();
                foreach (var proxy in net.GetAllProxies())
                {
                    if (proxy != null && proxy.PlayerId > 0 && proxy.RemoteInEpilogue)
                        inEnding.Add(proxy.PlayerId);
                }
                ModLog.Event(LogCat.Session, "Host left the world for the title — leaving the session");
                MainMenuMultiplayerInject.Disconnect(id => net.IsPeerInWorld(id) && !inEnding.Contains(id));
                return;
            }

            ModLog.Event(LogCat.Session, "Left the world for the title — leaving the session");
            MainMenuMultiplayerInject.Disconnect();
        }
    }
}
