using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// 4.3 Event triggers / requirements:
    /// Vanilla EventTriggers.OnTriggerEnter/Exit only reacts to Player.Instance.
    /// Remote peers are RemotePlayerProxy — without proxy enter, a client walking
    /// into a volume never fires host GameEvents (4.2 GameEventsFired).
    ///
    /// multipleFire GEs (dream karuzela RotateIt, ambients) are intentionally NOT
    /// broadcast (GameEventsFiredPatch) — they were meant to run locally on each
    /// peer. That only works if:
    ///   1) Client Player.Instance still gets vanilla OnTriggerEnter (do NOT suppress),
    ///   2) Each peer also runs proxy enter for other players' proxies (host body on
    ///      client, client body on host).
    /// One-shots stay host-auth: GameEventsFiredPatch Prefix blocks client one-shot
    /// fire(); host broadcasts; client Apply under NetworkApplyGuard.
    ///
    /// Occupancy: vanilla <c>entered</c>/<c>exited</c> assumes one body.
    /// The prior proxy guard (<c>entered != 0 &amp;&amp; isComponentAtPos</c>) treated a
    /// second peer as a multi-collider of the first — skipped <c>entered++</c> entirely,
    /// so the first body leaving fired exit while the second was still inside.
    /// Per-proxy id set + "fire only when volume was empty" fixes N-peer latching.
    /// </summary>
    internal static class EventTriggersAuth
    {
        internal static bool IsMultiplayerConnected()
        {
            return ModRuntime.Network != null && ModRuntime.Network.IsConnected;
        }

        internal static bool IsHost()
        {
            return IsMultiplayerConnected() && ModRuntime.Network.Role == NetworkRole.Host;
        }

        internal static bool CanFireTriggers(EventTriggers et)
        {
            if (et == null) return false;
            if ((Core.loadingGame || Singleton<SaveManager>.Instance.dontFireTriggers) && !et.canFireWhenLoadingGame)
                return false;
            if (!Core.worldGenFinished())
                return false;
            return true;
        }

        /// <summary>Footstep / SoundArea volumes are local-body only; proxy path owns peer steps.</summary>
        internal static bool IsLocalBodyOnlyVolume(string etName)
        {
            if (string.IsNullOrEmpty(etName)) return false;
            return etName.IndexOf("footsteps", System.StringComparison.OrdinalIgnoreCase) >= 0
                || etName.IndexOf("soundarea", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// A one-shot in this volume has not fired yet. The first body may have
        /// entered before requirements were met (no key). A later peer must retry.
        /// </summary>
        internal static bool HasPendingOneShot(EventTriggers triggers)
        {
            if (triggers == null || triggers.eventTriggers == null) return false;
            for (int i = 0; i < triggers.eventTriggers.Count; i++)
            {
                EventTrigger t = triggers.eventTriggers[i];
                if (t == null || t.disabled || t.multipleFire) continue;
                if (!t.fired) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Per-EventTriggers set of proxy player ids currently counted inside the volume.
    /// Local Player.Instance still uses vanilla entered/exited; proxies use this set so
    /// multi-collider on the same proxy does not double-count, while a second peer does.
    /// </summary>
    internal static class EventTriggersProxyOccupancy
    {
        private static readonly Dictionary<int, HashSet<int>> _proxyIdsByEt =
            new Dictionary<int, HashSet<int>>(64);
        private static readonly Dictionary<int, EventTriggers> _etByKey =
            new Dictionary<int, EventTriggers>(64);
        private static readonly List<int> _scratchKeys = new List<int>(16); // process-scoped: scratch, cleared before each use

        internal static void Reset()
        {
            _proxyIdsByEt.Clear();
            _etByKey.Clear();
        }

        /// <summary>
        /// Vanilla <c>OnDisable</c> zeroes entered/exited; Unity sends no exit for bodies still
        /// inside, and on re-enable it sends a fresh enter. Drop the proxy ids with it, or the
        /// re-enter is swallowed as "already counted" and the stale id defers every exit.
        /// </summary>
        internal static void Forget(EventTriggers et)
        {
            if (et == null) return;
            int key = et.GetInstanceID();
            _proxyIdsByEt.Remove(key);
            _etByKey.Remove(key);
        }

        /// <summary>
        /// A proxy was destroyed (peer left, died out of the world, re-created): Unity sends no
        /// exit for it. Count it out of every volume it was in, firing the area exit when it was
        /// the last body, as if it had walked out.
        /// </summary>
        internal static void ForgetPlayer(int playerId)
        {
            if (playerId <= 0 || _proxyIdsByEt.Count == 0) return;
            _scratchKeys.Clear();
            foreach (KeyValuePair<int, HashSet<int>> kv in _proxyIdsByEt)
            {
                if (kv.Value.Contains(playerId))
                    _scratchKeys.Add(kv.Key);
            }
            for (int i = 0; i < _scratchKeys.Count; i++)
            {
                int key = _scratchKeys[i];
                _etByKey.TryGetValue(key, out EventTriggers et);
                if (et == null)
                {
                    _proxyIdsByEt.Remove(key);
                    _etByKey.Remove(key);
                    continue;
                }
                if (TryRemove(et, playerId)
                    && EventTriggersAuth.IsMultiplayerConnected()
                    && et.isActiveAndEnabled
                    && EventTriggersAuth.CanFireTriggers(et))
                    EventTriggersProxyExitPatch.CountExit(et, playerId);
            }
            _scratchKeys.Clear();
        }

        /// <returns>True when this proxy was not yet counted (first collider enter).</returns>
        internal static bool TryAdd(EventTriggers et, int playerId)
        {
            if (et == null || playerId <= 0) return false;
            int key = et.GetInstanceID();
            if (!_proxyIdsByEt.TryGetValue(key, out HashSet<int> set))
            {
                set = new HashSet<int>();
                _proxyIdsByEt[key] = set;
                _etByKey[key] = et;
            }
            return set.Add(playerId);
        }

        /// <returns>True when this proxy was counted and is now removed.</returns>
        internal static bool TryRemove(EventTriggers et, int playerId)
        {
            if (et == null || playerId <= 0) return false;
            int key = et.GetInstanceID();
            if (!_proxyIdsByEt.TryGetValue(key, out HashSet<int> set))
                return false;
            if (!set.Remove(playerId))
                return false;
            if (set.Count == 0)
            {
                _proxyIdsByEt.Remove(key);
                _etByKey.Remove(key);
            }
            return true;
        }

        /// <summary>True when any proxy id is still counted inside this volume.</summary>
        internal static bool HasAny(EventTriggers et)
        {
            if (et == null) return false;
            return _proxyIdsByEt.TryGetValue(et.GetInstanceID(), out HashSet<int> set) && set.Count > 0;
        }
    }

    /// <summary>
    /// After vanilla Player.Instance handling, fire area enter for remote proxies
    /// on every peer (host + client). Host: client walked in. Client: host walked in
    /// (multipleFire world FX like karuzela RotateIt).
    /// </summary>
    [HarmonyPatch(typeof(EventTriggers), "OnTriggerEnter", new[] { typeof(Collider) })]
    public static class EventTriggersProxyEnterPatch
    {
        private static void Prefix(EventTriggers __instance, Collider other, out int __state)
        {
            // Snapshot entered so Postfix can detect vanilla skipping local increment
            // when a proxy already occupies the volume (N-peer occupancy parity).
            __state = __instance != null ? __instance.entered : 0;
        }

        private static void Postfix(EventTriggers __instance, Collider other, int __state)
        {
            if (!EventTriggersAuth.IsMultiplayerConnected()) return;
            if (__instance == null || other == null) return;
            if (!__instance.reactsToPlayer) return;
            if (!EventTriggersAuth.CanFireTriggers(__instance)) return;
            if (Singleton<OutsideLocations>.Instance != null && Singleton<OutsideLocations>.Instance.loading)
                return;

            // Local Player.Instance enter — host clears proxy threat preference.
            // When a proxy already bumped entered, vanilla skips entered++ (condition
            // entered==0 || !isAtPos fails). Count the local body so exit stays paired.
            if (other.GetComponentInParent<Player>() != null)
            {
                if (EventTriggersAuth.IsHost())
                    ThreatTriggerContext.NoteHostEnter();
                if (__instance.entered == __state
                    && Player.Instance != null
                    && other.gameObject == Player.Instance.gameObject)
                {
                    int mask = 1 << __instance.gameObject.layer;
                    if (Helpers.isComponentAtPos(Player.Instance._transform.position, mask, __instance))
                        __instance.entered++;
                }
                return;
            }

            RemotePlayerProxy proxy = other.GetComponentInParent<RemotePlayerProxy>();
            if (proxy == null) return;

            if (EventTriggersAuth.IsLocalBodyOnlyVolume(__instance.name))
                return;

            // Multi-collider on the SAME proxy — do not re-count / re-fire.
            if (!EventTriggersProxyOccupancy.TryAdd(__instance, proxy.PlayerId))
                return;

            if (EventTriggersAuth.IsHost())
                ThreatTriggerContext.NoteProxyEnter(proxy, __instance.transform.position);

            bool volumeWasEmpty = __instance.entered <= __instance.exited;
            __instance.entered++;
            // First body into an empty volume fires. A later peer still fires when a
            // one-shot never latched (first body failed requirements). Already-fired
            // one-shots and multipleFire ambients are not blasted again.
            if (volumeWasEmpty || EventTriggersAuth.HasPendingOneShot(__instance)
                || PerPlayerTransportOneShots.HasReopenable(__instance, proxy.PlayerId))
            {
                // Host: stamp proxy as GE actor and suppress host Player.Instance
                // personal grants/teleports. Client: still fire multipleFire locally;
                // one-shots are blocked by GameEventsFiredPatch until host apply.
                if (EventTriggersAuth.IsHost())
                {
                    DialogHostApplyGuard.RunHostWorldFanoutForPlayer(proxy.PlayerId, () =>
                        __instance.fireEventTrigger(EventTrigger.Type.area));
                }
                else
                {
                    __instance.fireEventTrigger(EventTrigger.Type.area);
                }
                ModRuntime.LegacyInfo(
                    $"[EventTriggers] proxy enter area p{proxy.PlayerId} on {__instance.name} entered={__instance.entered}"
                    + (volumeWasEmpty ? "" : " retry"));
            }
            else
            {
                ModRuntime.LegacyInfo(
                    $"[EventTriggers] proxy occupy (no re-fire) p{proxy.PlayerId} on {__instance.name} entered={__instance.entered}");
            }
        }
    }

    /// <summary>
    /// Proxy exit on every peer — pairs with enter (multipleFire EventTrigger resets on exit).
    /// </summary>
    [HarmonyPatch(typeof(EventTriggers), "OnTriggerExit", new[] { typeof(Collider) })]
    public static class EventTriggersProxyExitPatch
    {
        private static void Postfix(EventTriggers __instance, Collider other)
        {
            if (!EventTriggersAuth.IsMultiplayerConnected()) return;
            if (__instance == null || other == null) return;
            if (!__instance.reactsToPlayer) return;
            if (!EventTriggersAuth.CanFireTriggers(__instance)) return;

            RemotePlayerProxy proxy = other.GetComponentInParent<RemotePlayerProxy>();
            if (proxy == null) return;

            if (EventTriggersAuth.IsLocalBodyOnlyVolume(__instance.name))
                return;

            Vector3 pos = proxy.transform.position;
            int mask = 1 << __instance.gameObject.layer;
            // Still overlapping (other collider on same proxy) — ignore.
            if (Helpers.isComponentAtPos(pos, mask, __instance))
                return;

            if (!EventTriggersProxyOccupancy.TryRemove(__instance, proxy.PlayerId))
                return;

            CountExit(__instance, proxy.PlayerId);
        }

        internal static void CountExit(EventTriggers et, int playerId)
        {
            et.exited++;
            if (et.exited >= et.entered)
            {
                // Belt: if another proxy is still in the occupancy set, counters
                // drifted vs the set (local Postfix enter++ / multi-collider). Defer exit
                // fire so delayed one-shots do not latch while a peer body remains inside.
                if (EventTriggersProxyOccupancy.HasAny(et))
                {
                    ModRuntime.LegacyInfo(
                        $"[EventTriggers] proxy exit deferred (peers remain) p{playerId} on {et.name} exited={et.exited}");
                    return;
                }
                // Flavor HUD gated at delayed GameEvent.fire MoveNext (proximity), not here.
                if (EventTriggersAuth.IsHost())
                {
                    DialogHostApplyGuard.RunHostWorldFanoutForPlayer(playerId, () =>
                        et.fireEventTriggerExit(EventTrigger.Type.area));
                }
                else
                {
                    et.fireEventTriggerExit(EventTrigger.Type.area);
                }
                ModRuntime.LegacyInfo(
                    $"[EventTriggers] proxy exit area p{playerId} on {et.name} exited={et.exited}");
            }
        }
    }

    [HarmonyPatch(typeof(EventTriggers), "OnDisable")]
    public static class EventTriggersProxyDisablePatch
    {
        private static void Postfix(EventTriggers __instance) => EventTriggersProxyOccupancy.Forget(__instance);
    }

    /// <summary>
    /// Host: onInSight / onOutOfSight use the same <c>Player.isInSight</c> path as
    /// <see cref="HostPlayerIdentity.AnyInSight"/> (Porter / InSightOfPlayer), including
    /// optional <c>inSightOfPlayerRadius</c>. Replaces the prior simplified
    /// angle + <c>Core.canSee</c> proxy check that diverged from vanilla FOV/dot.
    /// </summary>
    [HarmonyPatch(typeof(EventTriggers), "isCurrentlyInSightOfPlayer")]
    public static class EventTriggersProxySightPatch
    {
        private static bool Prefix(EventTriggers __instance, ref bool __result)
        {
            if (!HostPlayerIdentity.HostWithRemotes())
                return true;
            if (__instance == null)
                return true;

            // Decompile: radius 0 → isInSight(transform); else isInSight(transform, false, radius).
            int radius = (int)__instance.inSightOfPlayerRadius;
            __result = HostPlayerIdentity.AnyInSight(
                __instance.transform, canBeFarAway: false, radius);
            return false;
        }
    }
}
