using System.Collections.Generic;
using System.Linq;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    internal static class ProxyDistanceHelper
    {
        internal static bool ProxyIsFar(Character c)
        {
            var net = ModRuntime.Network;
            if (net == null || !PlayerPositionManager.HasRemotePlayer)
                return true;
            float range = (float)c.farViewDistance * c.aniSightRangeModifier;
            CanSeeComponentCache.Get(c, out Sniffer sniffer, out Collider _);
            if (sniffer != null && sniffer.radius > range)
                range = sniffer.radius;
            float threshold = range + 50f;
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy != null)
                {
                    float dist = (c.transform.position - proxy.transform.position).magnitude;
                    if (dist <= threshold)
                        return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Host-side "the player" identity: vanilla Character keys off Player.Instance;
    /// co-op treats host + living proxies as equal bodies.
    /// </summary>
    internal static class HostPlayerIdentity
    {
        internal static bool HostWithRemotes()
        {
            return ModRuntime.Network != null
                && ModRuntime.Network.Role == NetworkRole.Host
                && PlayerPositionManager.HasRemotePlayer;
        }

        internal static Transform NearestLiving(Vector3 from)
        {
            return PlayerTargetArbiter.NearestValid(from);
        }

        internal static GameObject NearestLivingGo(Vector3 from)
        {
            Transform t = NearestLiving(from);
            if (t != null)
                return t.gameObject;
            return Player.Instance != null ? Player.Instance.gameObject : null;
        }

        /// <summary>
        /// True if any session avatar sees <paramref name="dest"/>.
        /// Decompile <c>Player.isInSight(Transform, bool, int)</c> is viewer=this
        /// (<c>_transform</c> / <c>currentFOV</c> / <c>FOVDot</c>) and target=dest —
        /// not a static helper. Reuse that method for the local player, then for each
        /// <see cref="RemotePlayerProxy"/> by briefly pointing <c>_transform</c> at the
        /// proxy (facing/LOS from proxy pose; FOV/dot radii stay the session Player's).
        /// <paramref name="radius"/> maps to vanilla's third arg (0 = default FOV range).
        /// </summary>
        internal static bool AnyInSight(Transform dest, bool canBeFarAway, int radius = 0)
        {
            if (dest == null)
                return false;

            return NearestViewer(dest, canBeFarAway, radius) != null;
        }

        /// <summary>
        /// The living player body nearest to <paramref name="dest"/> among those that see it (vanilla
        /// <c>Player.isInSight</c> from each body's pose), or null. A corpse (a dead host waiting
        /// for morning, a night-dead peer) sees nothing.
        /// </summary>
        internal static Transform NearestViewer(Transform dest, bool canBeFarAway, int radius = 0)
        {
            if (dest == null)
                return null;
            Player player = Player.Instance;
            if (player == null)
                return null;
            Transform best = null;
            float bestD = float.MaxValue;
            if (player.alive && !DeathStateTracker.LocalNightDeath && player.isInSight(dest, canBeFarAway, radius))
            {
                best = player._transform;
                bestD = (player._transform.position - dest.position).sqrMagnitude;
            }

            var net = ModRuntime.Network;
            if (net == null)
                return best;

            Transform saved = player._transform;
            try
            {
                foreach (var proxy in net.GetAllProxies())
                {
                    if (proxy == null)
                        continue;
                    CharBase cb = proxy.CachedCharBase;
                    if ((cb != null && !cb.alive) || DeathStateTracker.IsRemoteNightDead(proxy.PlayerId))
                        continue;
                    float d = (proxy.transform.position - dest.position).sqrMagnitude;
                    if (d >= bestD)
                        continue;
                    player._transform = proxy.transform;
                    if (player.isInSight(dest, canBeFarAway, radius))
                    {
                        best = proxy.transform;
                        bestD = d;
                    }
                }
            }
            finally
            {
                player._transform = saved;
            }
            return best;
        }

        /// <summary>
        /// Vanilla <c>Player.isInSight</c> from one body's pose: the host's own, or a stand-in's by
        /// pointing the host's <c>_transform</c> at it for the call (as <see cref="NearestViewer"/>).
        /// </summary>
        internal static bool BodySees(Transform body, bool isHost, Transform dest, bool canBeFarAway, int radius = 0)
        {
            Player player = Player.Instance;
            if (player == null || body == null || dest == null)
                return false;
            if (isHost)
                return player.isInSight(dest, canBeFarAway, radius);
            Transform saved = player._transform;
            try
            {
                player._transform = body;
                return player.isInSight(dest, canBeFarAway, radius);
            }
            finally
            {
                player._transform = saved;
            }
        }

        /// <summary>
        /// Forest-spirit indoor cull: despawn only when every living player is inside.
        /// Proxy CharBase.isInside is refreshed via checkGround (proxy has no CharacterSounds tick).
        /// </summary>
        internal static bool AllPlayersInside()
        {
            if (Player.Instance != null && !Player.Instance.isInside)
                return false;

            var net = ModRuntime.Network;
            if (net == null)
                return Player.Instance != null && Player.Instance.isInside;

            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null)
                    continue;
                CharBase cb = proxy.CachedCharBase;
                if (cb == null)
                    continue;
                cb.checkGround();
                if (!cb.isInside)
                    return false;
            }
            return true;
        }
    }
}
