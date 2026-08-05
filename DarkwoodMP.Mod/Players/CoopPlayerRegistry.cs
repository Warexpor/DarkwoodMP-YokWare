using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Players
{
    public static class CoopPlayerRegistry
    {
        public static void GetAllPlayers(List<Player> outList)
        {
            if (PlayerControlRouter.MainPlayer != null)
                outList.Add(PlayerControlRouter.MainPlayer);

            foreach (Player proxy in PlayerControlRouter.GetAllProxies())
            {
                if (proxy != null)
                    outList.Add(proxy);
            }
        }
    }
}