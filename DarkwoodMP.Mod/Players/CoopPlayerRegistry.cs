using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Players
{
    public static class CoopPlayerRegistry
    {
        public static bool HasMultiplePlayers =>
            PlayerControlRouter.MainPlayer != null && PlayerControlRouter.MainPlayer.alive &&
            PlayerControlRouter.HasSecond;

        public static IEnumerable<Player> AllPlayers()
        {
            if (PlayerControlRouter.MainPlayer != null)
                yield return PlayerControlRouter.MainPlayer;

            foreach (Player proxy in PlayerControlRouter.GetAllProxies())
            {
                if (proxy != null)
                    yield return proxy;
            }
        }

        public static Player GetNearestLivingPlayer(Vector3 worldPos)
        {
            Player best = null;
            float bestDist = float.MaxValue;

            foreach (Player player in AllPlayers())
            {
                if (player == null || !player.alive || !player.gameObject.activeInHierarchy)
                    continue;

                float dist = Core.trueDistance(worldPos, player.transform.position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = player;
                }
            }

            return best;
        }

        public static Transform GetNearestLivingPlayerTransform(Vector3 worldPos)
        {
            Player player = GetNearestLivingPlayer(worldPos);
            return player != null ? player.transform : null;
        }

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