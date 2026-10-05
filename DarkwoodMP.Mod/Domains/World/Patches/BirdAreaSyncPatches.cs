using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// BirdArea co-op presence (vanilla-parity, host-simulated birds).
    ///
    /// Decompile: Start spawns AreaBird children; OnTriggerEnter/Exit require
    /// GetComponent&lt;Player&gt;(). RemotePlayerProxy strips Player, so host never
    /// sees clients walk into the volume — birds stay idle for peer presence.
    ///
    /// Fix (no new messages):
    /// 1) Client: Prefix Start + trigger enter/exit — no local spawn / AI.
    /// 2) Host: treat RemotePlayerProxy as presence (refcount), same aggro
    ///    routine as Player; sendBirdToAttackPlayer targets the enterer
    ///    (attackCharacter on proxy CharBase, not only Player.Instance).
    /// </summary>
    internal static class BirdAreaPresence
    {
        private sealed class State
        {
            public int Count;
            public Transform Target;
        }

        private static readonly Dictionary<BirdArea, State> States =
            new Dictionary<BirdArea, State>(16);

        /// <summary>
        /// Registered with NetworkResetRegistry. Presence counts are session state: when the
        /// session ends an area a remote was inside must not keep playerIsInside / its attack
        /// routine running with no one left to clear it (vanilla would only clear it on a Player
        /// trigger exit).
        /// </summary>
        internal static void Reset()
        {
            foreach (KeyValuePair<BirdArea, State> kv in States)
            {
                BirdArea area = kv.Key;
                if (area == null || kv.Value == null || kv.Value.Count <= 0)
                    continue;
                // Only the remote presence is dropped; the local player standing in the area keeps
                // vanilla's own playerIsInside and the attack routine.
                if (area.playerIsInside && !LocalPlayerInside(area))
                {
                    area.playerIsInside = false;
                    area.stopRoutine(area.sendBirdToAttackPlayer);
                }
            }
            States.Clear();
        }

        /// <summary>The local player is standing inside the area's trigger volume.</summary>
        private static bool LocalPlayerInside(BirdArea area)
        {
            try
            {
                Player p = Player.Instance;
                if (p == null) return false;
                Collider col = area.GetComponent<Collider>();
                if (col == null) return false;
                Vector3 pos = p._transform != null ? p._transform.position : p.transform.position;
                return col.bounds.Contains(new Vector3(pos.x, col.bounds.center.y, pos.z));
            }
            catch { return false; }
        }

        internal static bool IsClientConnected()
        {
            return ModRuntime.Network != null
                && ModRuntime.Network.IsConnected
                && ModRuntime.Network.Role == NetworkRole.Client;
        }

        internal static bool IsHostConnected()
        {
            return ModRuntime.Network != null
                && ModRuntime.Network.IsConnected
                && ModRuntime.Network.Role == NetworkRole.Host;
        }

        internal static Transform ResolvePresence(Collider col)
        {
            if (col == null) return null;

            Player player = col.GetComponent<Player>();
            if (player == null)
                player = col.GetComponentInParent<Player>();
            if (player != null)
                return player._transform != null ? player._transform : player.transform;

            RemotePlayerProxy proxy = col.GetComponentInParent<RemotePlayerProxy>();
            if (proxy != null)
                return proxy.transform;

            return null;
        }

        private static State Get(BirdArea area)
        {
            if (!States.TryGetValue(area, out State s) || s == null)
            {
                s = new State();
                States[area] = s;
            }
            return s;
        }

        internal static void NoteEnter(BirdArea area, Transform presence)
        {
            State s = Get(area);
            s.Count++;
            s.Target = presence;
            if (!area.playerIsInside)
            {
                area.playerIsInside = true;
                area.timePlayerEnteredCollider = Time.time;
                area.startRoutine(area.sendBirdToAttackPlayer, 5f);
                if (ModRuntime.VerboseLogging)
                {
                    ModLog.Event(LogCat.Entity,
                        "[BirdArea] presence enter on " + area.name
                        + " count=" + s.Count + " target=" + presence.name);
                }
            }
        }

        internal static void NoteExit(BirdArea area, Transform presence)
        {
            State s = Get(area);
            if (s.Count > 0)
                s.Count--;
            if (s.Target == presence)
                s.Target = null;

            if (s.Count <= 0)
            {
                s.Count = 0;
                s.Target = null;
                if (area.playerIsInside)
                {
                    area.playerIsInside = false;
                    area.stopRoutine(area.sendBirdToAttackPlayer);
                    if (ModRuntime.VerboseLogging)
                    {
                        ModLog.Event(LogCat.Entity,
                            "[BirdArea] presence clear on " + area.name);
                    }
                }
            }
        }

        internal static Transform GetAttackTarget(BirdArea area)
        {
            State s;
            if (!States.TryGetValue(area, out s) || s == null)
                return null;
            return s.Target;
        }
    }

    /// <summary>
    /// Clients must not spawn AreaBird flocks — host owns simulation; peers
    /// observe via entity state broadcast (same idea as ClientAIDisable).
    /// </summary>
    [HarmonyPatch(typeof(BirdArea), "Start")]
    public static class BirdAreaClientStartPatch
    {
        private static bool Prefix(BirdArea __instance)
        {
            if (!BirdAreaPresence.IsClientConnected())
                return true;

            if (ModRuntime.VerboseLogging)
            {
                ModLog.Event(LogCat.Entity,
                    "[BirdArea] client skipped Start (host-authoritative birds) on "
                    + (__instance != null ? __instance.name : "?"));
            }
            return false;
        }
    }

    /// <summary>
    /// Host MP: Player and RemotePlayerProxy both count as presence.
    /// Offline: vanilla. Client connected: suppress (no local birds).
    /// </summary>
    [HarmonyPatch(typeof(BirdArea), "OnTriggerEnter", new[] { typeof(Collider) })]
    public static class BirdAreaTriggerEnterPatch
    {
        private static bool Prefix(BirdArea __instance, Collider _collider)
        {
            if (BirdAreaPresence.IsClientConnected())
                return false;
            if (!BirdAreaPresence.IsHostConnected())
                return true;

            Transform presence = BirdAreaPresence.ResolvePresence(_collider);
            if (presence == null)
                return false;

            BirdAreaPresence.NoteEnter(__instance, presence);
            return false;
        }
    }

    [HarmonyPatch(typeof(BirdArea), "OnTriggerExit", new[] { typeof(Collider) })]
    public static class BirdAreaTriggerExitPatch
    {
        private static bool Prefix(BirdArea __instance, Collider _collider)
        {
            if (BirdAreaPresence.IsClientConnected())
                return false;
            if (!BirdAreaPresence.IsHostConnected())
                return true;

            Transform presence = BirdAreaPresence.ResolvePresence(_collider);
            if (presence == null)
                return false;

            BirdAreaPresence.NoteExit(__instance, presence);
            return false;
        }
    }

    /// <summary>
    /// Vanilla sendBirdToAttackPlayer always attackPlayer() → Player.Instance.
    /// With remote players the bird dives at the body that walked in (host or stand-in), the way
    /// vanilla attackPlayer does it (attack-on-sight unless defensive, then attackCharacter). The
    /// host's own entry used to go through attackPlayer's "player" pick instead, which could send
    /// the bird at a client standing outside the area.
    /// </summary>
    [HarmonyPatch(typeof(BirdArea), "sendBirdToAttackPlayer")]
    public static class BirdAreaSendBirdAttackPatch
    {
        private static bool Prefix(BirdArea __instance)
        {
            if (!BirdAreaPresence.IsHostConnected() || !HostPlayerIdentity.HostWithRemotes())
                return true;
            if (__instance == null || !__instance.playerIsInside || __instance.birds == null
                || __instance.birds.Count == 0)
                return true;

            Transform target = BirdAreaPresence.GetAttackTarget(__instance);
            if (target == null)
                return true;

            Character bird = __instance.birds[Random.Range(0, __instance.birds.Count)];
            if (bird == null)
                return false;

            if (!bird.dummy)
            {
                if (bird.aggressiveness != Aggressiveness.defensive)
                    bird.aggressiveness = Aggressiveness.attackOnSight;
                PlayerTargetArbiter.Commit(bird, target, "birdArea");
            }
            if (bird.flier != null)
                bird.flier.diving = true;
            __instance.startRoutine(__instance.sendBirdToAttackPlayer, 4f, 7f);
            return false;
        }
    }
}
