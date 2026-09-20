using System.Runtime.CompilerServices;
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

        private static readonly ConditionalWeakTable<BirdArea, State> States =
            new ConditionalWeakTable<BirdArea, State>();

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

        private static State Get(BirdArea area) => States.GetOrCreateValue(area);

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
    /// When the enterer was a remote proxy, dive at that proxy's CharBase instead.
    /// </summary>
    [HarmonyPatch(typeof(BirdArea), "sendBirdToAttackPlayer")]
    public static class BirdAreaSendBirdAttackPatch
    {
        private static bool Prefix(BirdArea __instance)
        {
            if (!BirdAreaPresence.IsHostConnected())
                return true;
            if (__instance == null || !__instance.playerIsInside || __instance.birds == null
                || __instance.birds.Count == 0)
                return true;

            Transform target = BirdAreaPresence.GetAttackTarget(__instance);
            if (target == null)
                return true;
            if (target.GetComponentInParent<RemotePlayerProxy>() == null)
                return true;

            Character bird = __instance.birds[Random.Range(0, __instance.birds.Count)];
            if (bird == null)
                return false;

            bird.attackCharacter(target);
            if (bird.flier != null)
                bird.flier.diving = true;
            __instance.startRoutine(__instance.sendBirdToAttackPlayer, 4f, 7f);
            return false;
        }
    }
}
