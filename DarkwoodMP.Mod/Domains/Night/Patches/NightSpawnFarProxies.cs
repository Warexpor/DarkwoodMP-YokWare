using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Scratch-backed far-proxy filter for night spawn redirects (no LINQ / ToList).
    /// Buffer valid until the next Fill call.
    /// </summary>
    internal static class NightSpawnFarProxies
    {
        private static readonly List<RemotePlayerProxy> Buf = new List<RemotePlayerProxy>(8); // process-scoped: scratch buffer, cleared before each use
        private static readonly float FarSqr =
            NightSpawnConstants.FarProxyMinDist * NightSpawnConstants.FarProxyMinDist;

        public static List<RemotePlayerProxy> Fill(LanNetworkManager net, Vector3 from)
        {
            Buf.Clear();
            if (net == null)
                return Buf;
            foreach (RemotePlayerProxy p in net.GetAllProxies())
            {
                if (p == null)
                    continue;
                Vector3 d = p.transform.position - from;
                if (d.sqrMagnitude >= FarSqr)
                    Buf.Add(p);
            }
            return Buf;
        }

        public static int CountAll(LanNetworkManager net)
        {
            if (net == null)
                return 0;
            int n = 0;
            foreach (RemotePlayerProxy p in net.GetAllProxies())
            {
                if (p != null)
                    n++;
            }
            return n;
        }
    }
}
