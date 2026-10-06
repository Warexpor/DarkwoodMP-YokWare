using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// The daytime redneck ambush (<c>RandomEvent.Type.spawnRedneck</c>): from day 2 vanilla sends a
    /// hit-and-run redneck at the player when it is out on the road (not under a roof, not in a
    /// location). Only the host's body was checked, so with the host at home a client walking the
    /// forest never met him, and with the host on the road the far-player redirect could put him
    /// next to a client sitting in a hideout. Every living player who meets vanilla's own condition
    /// can draw him now, one picked at random, as the one player would in its own game.
    /// </summary>
    [HarmonyPatch(typeof(RandomEvent), "fire")]
    public static class HostRedneckPartyPatch
    {
        /// <summary>The ambush picked its player; the far-player spawn redirect stays out of it.</summary>
        internal static bool Placing; // process-scoped: raised only inside one call

        private static readonly System.Action<RandomEvent> RemoveMe =
            AccessTools.MethodDelegate<System.Action<RandomEvent>>(AccessTools.Method(typeof(RandomEvent), "removeMe"));

        private static readonly List<GameObject> _bodies = new List<GameObject>(4); // process-scoped: scratch, cleared before each use

        private static bool Prefix(RandomEvent __instance, bool checkIfRequirementsMet, bool force)
        {
            if (__instance.type != RandomEvent.Type.spawnRedneck)
                return true;
            if (!NetGuard.ConnectedHost(out LanNetworkManager net) || LanNetworkManager.IsApplyingRemoteState)
                return true;

            RandomEvent e = __instance;
            // Vanilla fire's entry gate, unchanged.
            if ((e.disabled && !force)
                || !((((checkIfRequirementsMet && e.requirementsMet()) || !checkIfRequirementsMet)
                      && (!e.startedToday || e.multipleTimesPerDay)) || force))
                return false;

            bool fired = false;
            Controller ctrl = Singleton<Controller>.Instance;
            if (ctrl != null && ctrl.day >= 2)
            {
                CollectBodies(net);
                if (_bodies.Count > 0)
                {
                    GameObject target = _bodies[Random.Range(0, _bodies.Count)];
                    Character character;
                    Placing = true;
                    try
                    {
                        character = Singleton<CharacterSpawner>.Instance.spawnCharacterAround(target, Vector3.zero, 1500f,
                            "Redneck02", nocturnal: false, attackPlayer: true, relentlessPursuit: true);
                    }
                    finally { Placing = false; }
                    if (character != null)
                    {
                        character.attackType = AttackType.hitAndRun;
                        character.wantToDespawn = true;
                        character.maxStamina = 1000f;
                        fired = true;
                        ModRuntime.LegacyInfo("[Redneck] ambush around " + target.name);
                    }
                }
                _bodies.Clear();
            }

            e.randomizeStartTime();
            if (fired)
            {
                e.startedToday = true;
                if (e.removeOnFire)
                    RemoveMe(e);
            }
            return false;
        }

        /// <summary>Each living player out on the road: not under a roof, in no location, not in a dream.</summary>
        private static void CollectBodies(LanNetworkManager net)
        {
            _bodies.Clear();
            Player host = Player.Instance;
            Dreams dreams = Dreams.Instance;
            bool hostDreaming = dreams != null && (dreams.dreaming || dreams.dreamPrepared);
            if (host != null && host.alive && !host.isInside && host.whereAmI != null
                && host.whereAmI.bigLocation == null && !hostDreaming
                && !PersonalPrologue.LocalInPrologue && !NightEventAnchor.HostInOutsideLocation())
                _bodies.Add(host.gameObject);

            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null)
                    continue;
                // Not on a location pad, not loading, not in a dream.
                if (!net.RemotePlayers.TryGetValue(proxy.PlayerId, out RemotePlayerState st) || !st.InOpenWorld)
                    continue;
                CharBase cb = proxy.CachedCharBase;
                if (cb != null && !cb.alive)
                    continue;
                Vector3 pos = proxy.transform.position;
                if (Location.getAtPos(pos) != null)
                    continue;
                Ground ground = Ground.getGround(pos);
                if (ground != null && ground.isInside)
                    continue;
                _bodies.Add(proxy.gameObject);
            }
        }
    }
}
