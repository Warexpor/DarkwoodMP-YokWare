using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>GameEventsFired + late-join GameEventsBulk handlers composed for 0.8.</summary>
    internal sealed partial class GameEventNetHandlers
    {
        /// <returns>True when the event is resolved (fired, already fired, or intentionally skipped).</returns>
        private bool ApplyGameEventsFired(GameEventsFiredMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);

            // Post-teardown dream GEs (e.g. fov_trigger_* after LocationExit) must not apply.
            // A dream GE is one at the dream pad, not one with "dream_" in its name: overworld
            // events after a dream (the church entrance, the hideout aftermath) were dropped.
            if (DreamSyncManager.IsAtDreamPad(pos)
                && !DreamSyncManager.IsDreamActive
                && (Dreams.Instance == null || !Dreams.Instance.dreaming))
            {
                ModRuntime.LegacyInfo(
                    $"[GameEventsSync] drop post-dream GE '{msg.EventName}'");
                return true;
            }

            // Ending camera pan is personal to peers already in the epilogue.
            // A living player outside must not start EpilogueOutcomes because
            // someone else finished the crawl.
            if (string.Equals(msg.EventName, EpilogueNetHandlers.EpilogueCameraPanEvent,
                    StringComparison.Ordinal)
                && !EpilogueNetHandlers.IsLocalInEpilogue())
            {
                ModRuntime.LegacyInfo(
                    "[Epilogue] drop camera pan GE — local peer not in epilogue");
                return true;
            }

            GameEvents best = null;

            // Resolve dream events only under the active pad. A name-only
            // fallback can select the overworld bunker copy, so queue events
            // until the pad is loaded and finishedLoading is true.
            // Dream events are the ones at the dream pad's slot; before the pad
            // exists here, any pad-slot position counts (outside locations share them).
            bool padCoords = ClientStateBackup.IsDreamPadCoordinate(pos);
            bool dreamSceneFx = padCoords && !string.IsNullOrEmpty(msg.EventName)
                && (msg.EventName.IndexOf("def_glow", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("def_shadow", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("podmiana", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("carousel", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("karuzela", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("newborn", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("dimLight", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("SWITCH_", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("Lamp_dream", System.StringComparison.OrdinalIgnoreCase) >= 0);
            Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
            Location dreamLoc = Dreams.Instance != null ? Dreams.Instance.dreamLocation : null;
            bool padNotReady = DreamSyncManager.IsDreamActive
                && (dreamRoot == null
                    || dreamLoc == null || !dreamLoc.finishedLoading
                    || Dreams.Instance == null || !Dreams.Instance.dreaming);
            bool onDreamPad = DreamSyncManager.IsDreamActive
                && (dreamRoot != null ? DreamSyncManager.IsAtDreamPad(pos) : padCoords);
            if ((onDreamPad || dreamSceneFx) && padNotReady)
            {
                if (queueIfMissing)
                {
                    QueuePendingGameEvent(msg);
                    return false;
                }
                ModRuntime.LegacyInfo(
                    $"[GameEventsSync] defer dream GE (pad not ready) '{msg.EventName}'");
                return false;
            }

            // Dream footstep and sound-area events parent audio to the local
            // Player. Proxy OnFootstep owns peer footsteps.
            if (DreamSyncManager.IsDreamActive
                && !string.IsNullOrEmpty(msg.EventName)
                && (msg.EventName.IndexOf("footsteps", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("area_footsteps", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.EventName.IndexOf("soundarea", System.StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ModRuntime.LegacyInfo(
                    $"[GameEventsSync] skip dream ambient GE '{msg.EventName}' (proxy footsteps own peer steps)");
                return true;
            }

            // Named events: SoftMatch only. A prior FindNearestByName (exact, no Clone
            // strip) often missed, then nameless FindNearest stole a nearby already-fired
            // GE and returned "success" — setActive / renderer / remove never ran on the
            // peer. SoftMatch strips (Clone), prefers the dream pad, and prefers unfired.
            // Only the pad's own events are held to the pad: an overworld or outside-location
            // event fired during a dream (the cellar's dream start, oneChance's dream end) never
            // resolved under the pad root.
            float softMax = (onDreamPad || padCoords) ? 250f : 8f;
            if (!string.IsNullOrEmpty(msg.EventName))
            {
                best = SoftMatchGameEvents(msg.EventName, pos, onDreamPad ? dreamRoot : null, softMax);
            }
            else
            {
                float posR = onDreamPad ? 12f : 2.5f;
                best = WorldQueryHelper.FindNearest<GameEvents>(pos, posR);
            }

            if (best == null)
            {
                // Host-spawned spirit FX (def_glow / def_shadow) often have no durable
                // GameEvents on the client. Queuing them would call FindObjectsOfType every 0.5s
                        // for 90 seconds, causing periodic stutters and dream-end failure.
                if (IsEphemeralDreamFxEvent(msg.EventName)
                    && DreamSyncManager.IsDreamActive
                    && !padNotReady)
                {
                    ModRuntime.LegacyInfo(
                        $"[GameEventsSync] drop missing ephemeral FX '{msg.EventName}' (no client GE — not queued)");
                    return true;
                }

                // Not here yet (a just-spawned object settles its height in its Start, the frame
                // after the spawn): queued, and the flush retries until it ages out, which warns.
                if (queueIfMissing)
                    QueuePendingGameEvent(msg);
                return false;
            }

            // Must run under NetworkApplyGuard on every apply path (receive + pending flush).
            // Without it GameEventsFiredPatch blocks client one-shots and fire() is a no-op.
            // Vanilla fired+!multipleFire guard prevents double-fire if client already ran it.
            bool wasFired = best.fired;
            // A one-shot move the host just ran again for this player (PerPlayerTransportOneShots):
            // the copy here latched on the first player's replay.
            if (wasFired && !best.multipleFire && msg.ActorPlayerId > 0 && msg.ActorPlayerId == _net.LocalPlayerId
                && PerPlayerTransportOneShots.Qualifies(best))
            {
                // Unless this player had it here already (a joiner fires the hideout's lesson
                // offline on waking, before the host sees its stand-in walk in).
                if (PerPlayerTransportOneShots.LocalServed(best.GetInstanceID()))
                {
                    ModRuntime.LegacyInfo($"[GameEventsSync] '{best.name}' already had here — not replayed");
                    return true;
                }
                best.fired = false;
                wasFired = false;
            }
            if (wasFired && !best.multipleFire)
            {
                ModRuntime.LegacyInfo(
                    $"[GameEventsSync] skip already-fired '{best.name}' at {best.transform.position}");
                return true;
            }

            string geName = best.name ?? msg.EventName ?? "";
            bool leaveDoor = geName.IndexOf("onLeaveDoor", System.StringComparison.OrdinalIgnoreCase) >= 0
                || geName.IndexOf("DoorDialogue", System.StringComparison.OrdinalIgnoreCase) >= 0;

            // Location/proximity flavor (displayMessage + HelpMessage) is personal.
            // fire() only StartCoroutines delayed GameEvent actions — do NOT set a
            // process-wide HUD blacklist here (blanks local examine/help). Delayed
            // text checks the event owner when MoveNext runs
            // (PersonalFlavorHud + GameEventFireFlavorSourcePatch).
            // Personal Player.Instance effects (bag / recipes / transport) only for
            // the stamped actor; late-join bulk uses ActorPlayerId=0 → world only.
            bool runPersonal = GameEventPersonalPolicy.ShouldRunPersonalEffectsOnApply(
                msg.ActorPlayerId, _net.LocalPlayerId);
            bool prevSuppress = GameEventPersonalActorPatch.SuppressPersonalForLocalPlayer;
            if (!runPersonal)
                GameEventPersonalActorPatch.SuppressPersonalForLocalPlayer = true;
            // dontRunIfInactive starts the steps on the GameEvents object itself; this peer's copy
            // can be inactive (culled, far away) where the host's was not, and the start then
            // failed with the event latched as fired. The host ran them: run them here too.
            bool prevDontRun = best.dontRunIfInactive;
            if (prevDontRun && !best.gameObject.activeInHierarchy)
                best.dontRunIfInactive = false;
            // Who the event belongs to, for story steps that act around that player's body.
            bool pushedActor = msg.ActorPlayerId > 0;
            if (pushedActor)
                GeFireActorContext.Push(msg.ActorPlayerId);
            try
            {
                using (new NetworkApplyGuard())
                {
                    best.fire();
                }
            }
            finally
            {
                if (pushedActor)
                    GeFireActorContext.Pop();
                best.dontRunIfInactive = prevDontRun;
                GameEventPersonalActorPatch.SuppressPersonalForLocalPlayer = prevSuppress;
            }
            if (!best.fired && !best.multipleFire)
            {
                ModRuntime.LegacyInfo(
                    $"[GameEventsSync] fire no-op '{best.name}' at {best.transform.position}");
                return false;
            }
            ModRuntime.LegacyInfo(
                $"[GameEventsSync] applied '{best.name}' wasFired={wasFired} firedNow={best.fired} at {best.transform.position}");

            // Keep the destroyOnFire identity here too (host does it at fire time) so a client
            // promoted to host can still send these shells to late joiners.
            if (best.destroyOnFire)
            {
                Vector3 bp = best.transform.position;
                RecordDestroyedOnFireGameEvent(new GameEventsFiredMessage
                {
                    PosX = Mathf.Round(bp.x * 10f) / 10f,
                    PosY = Mathf.Round(bp.y * 10f) / 10f,
                    PosZ = Mathf.Round(bp.z * 10f) / 10f,
                    EventName = geName,
                    ActorPlayerId = 0
                }, DreamSyncManager.IsOnDreamPad(best.transform));
            }

            // After GE (which owns openSound): mute late DoorOpen / skip ForceOpen.
            if (!leaveDoor)
            {
                DWMPHorde.Patches.DialogueDoorAftermath.OnClientGameEventsApplied(
                    geName, best.transform.position);
            }
            else
            {
                DWMPHorde.Patches.DialogueDoorAftermath.OnClientLeaveDoorGameEventsApplied(
                    geName, best.transform.position);
            }
            return true;
        }

        private GameEvents[] _geSoftIndexSource;
        private readonly Dictionary<string, List<GameEvents>> _geSoftByNormName =
            new Dictionary<string, List<GameEvents>>(128, System.StringComparer.OrdinalIgnoreCase);

        private static string NormalizeGeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            if (name.EndsWith("(Clone)", System.StringComparison.Ordinal))
                name = name.Substring(0, name.Length - 7);
            return name.Trim();
        }

        private void EnsureGeSoftIndex(GameEvents[] all)
        {
            if (ReferenceEquals(all, _geSoftIndexSource))
                return;
            _geSoftIndexSource = all;
            _geSoftByNormName.Clear();
            for (int i = 0; i < all.Length; i++)
            {
                GameEvents ge = all[i];
                if (ge == null) continue;
                string key = NormalizeGeName(ge.name);
                if (key.Length == 0) continue;
                if (!_geSoftByNormName.TryGetValue(key, out List<GameEvents> bucket))
                {
                    bucket = new List<GameEvents>(2);
                    _geSoftByNormName[key] = bucket;
                }
                bucket.Add(ge);
            }
        }

        /// <summary>
        /// Soft GE resolve: exact/normalized name index first, full IndexOf walk only on miss.
        /// Index rebuilds when the WorldQueryHelper GameEvents cache array identity changes.
        /// When <paramref name="dreamRoot"/> is set, only pad children (or within 250 of the
        /// pad) are eligible — never the overworld bunker twin. Prefer an unfired copy so an
        /// already-latched twin cannot swallow setActive / renderer / remove.
        /// </summary>
        private GameEvents SoftMatchGameEvents(
            string want, Vector3 pos, Transform dreamRoot, float softMax)
        {
            GameEvents[] all = WorldQueryHelper.GetCachedSceneComponents<GameEvents>();
            EnsureGeSoftIndex(all);

            string wantNorm = NormalizeGeName(want);
            GameEvents best = null;
            float bestD = float.MaxValue;
            bool bestLatched = false;
            float softMaxSq = softMax * softMax;
            float rootMaxSq = 250f * 250f;

            void Consider(GameEvents ge)
            {
                if (ge == null || ge.transform == null) return;
                if (dreamRoot != null)
                {
                    if (!ge.transform.IsChildOf(dreamRoot)
                        && (ge.transform.position - dreamRoot.position).sqrMagnitude > rootMaxSq)
                        return;
                }
                float dSq = (ge.transform.position - pos).sqrMagnitude;
                if (dSq > softMaxSq) return;

                // One-shot already latched — keep searching for an unfired twin first.
                bool latched = ge.fired && !ge.multipleFire;
                if (best == null)
                {
                    best = ge;
                    bestD = dSq;
                    bestLatched = latched;
                    return;
                }
                if (bestLatched && !latched)
                {
                    best = ge;
                    bestD = dSq;
                    bestLatched = false;
                    return;
                }
                if (!bestLatched && latched)
                    return;
                if (dSq < bestD)
                {
                    best = ge;
                    bestD = dSq;
                    bestLatched = latched;
                }
            }

            if (wantNorm.Length > 0
                && _geSoftByNormName.TryGetValue(wantNorm, out List<GameEvents> exact))
            {
                for (int i = 0; i < exact.Count; i++)
                    Consider(exact[i]);
                // Exact-name bucket existed: IndexOf walk cannot find a better same-name hit.
                return best;
            }

            // Rare: Clone/suffix drift that NormalizeGeName did not collapse.
            // The object name must contain the full wanted name. A shorter name
            // that merely sits inside the wanted name is a different event.
            string wantForContains = wantNorm.Length > 0 ? wantNorm : want;
            for (int i = 0; i < all.Length; i++)
            {
                GameEvents ge = all[i];
                if (ge == null) continue;
                string n = NormalizeGeName(ge.name);
                if (n.Length == 0) continue;
                if (!n.Equals(wantForContains, System.StringComparison.OrdinalIgnoreCase)
                    && n.IndexOf(wantForContains, System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                Consider(ge);
            }
            return best;
        }

        private void QueuePendingGameEvent(GameEventsFiredMessage msg)
        {
            for (int i = _pendingGameEvents.Count - 1; i >= 0; i--)
            {
                var p = _pendingGameEvents[i];
                if (Mathf.Abs(p.PosX - msg.PosX) < 0.2f
                    && Mathf.Abs(p.PosY - msg.PosY) < 0.2f
                    && Mathf.Abs(p.PosZ - msg.PosZ) < 0.2f
                    && string.Equals(p.EventName, msg.EventName, System.StringComparison.Ordinal))
                    _pendingGameEvents.RemoveAt(i);
            }
            if (_pendingGameEvents.Count >= MaxPendingGameEvents)
                _pendingGameEvents.RemoveAt(0);
            _pendingGameEvents.Add(msg);
            ModRuntime.LegacyInfo(
                $"[GameEventsSync] queued (not loaded) at ({msg.PosX:F1},{msg.PosZ:F1}) name={msg.EventName}");
        }

        /// <summary>Drop queued dream GEs so they cannot re-fire after pad teardown.</summary>
        internal void ClearPendingDreamGameEvents()
        {
            DropDreamPadDestroyedRecords();
            if (_pendingGameEvents.Count == 0) return;
            // The pad is still standing here (vanilla destroys it 4 s after the dream): drop only
            // what was queued for its slot. Without it, any pad slot.
            bool padKnown = DreamSyncManager.GetLoadedDreamPad() != null;
            int removed = 0;
            for (int i = _pendingGameEvents.Count - 1; i >= 0; i--)
            {
                var msg = _pendingGameEvents[i];
                string n = msg.EventName ?? "";
                bool ephemeral = IsEphemeralDreamFxEvent(n);
                Vector3 msgPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                bool padPos = padKnown
                    ? DreamSyncManager.IsAtDreamPad(msgPos)
                    : ClientStateBackup.IsDreamPadCoordinate(msgPos);
                if (ephemeral || padPos)
                {
                    int key = msg.EventName != null ? msg.EventName.GetHashCode() : 0;
                    key ^= (int)(msg.PosX * 10f) ^ ((int)(msg.PosZ * 10f) << 10);
                    _pendingGameEventQueuedAt.Remove(key);
                    _pendingGameEvents.RemoveAt(i);
                    removed++;
                }
            }
            if (removed > 0)
                ModRuntime.LegacyInfo($"[GameEventsSync] cleared {removed} pending dream GE(s) on dream end");
        }

        private static bool IsEphemeralDreamFxEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return false;
            return eventName.IndexOf("def_glow", System.StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("def_shadow", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private float _nextPendingGameEventsFlushTime;
        private const float PendingGameEventsFlushInterval = 0.5f;
        private const float PendingGameEventsMaxAge = 60f;
        private const float PendingDreamGameEventsMaxAge = 90f;
        private readonly System.Collections.Generic.Dictionary<int, float> _pendingGameEventQueuedAt
            = new System.Collections.Generic.Dictionary<int, float>(16);

        /// <summary>After remote dream pad spawn, apply queued entry events.</summary>
        internal void TryFlushPendingGameEventsAfterDreamLoad()
        {
            // Pad spawn after dream-start Invalidate can leave a 3s TTL GameEvents scan
            // without pad children; SoftMatch would miss leave-door / setActive shells.
            WorldQueryHelper.InvalidateSceneScanCache<GameEvents>();
            WorldQueryHelper.InvalidateSceneScanCache<UniqueObject>();
            _geSoftIndexSource = null;
            _geSoftByNormName.Clear();
            _nextPendingGameEventsFlushTime = 0f;
            TryFlushPendingGameEvents();
        }

        internal void TryFlushPendingGameEvents()
        {
            if (_net.Role != NetworkRole.Client || _pendingGameEvents.Count == 0)
                return;
            // On the title (or loading) there is no world to find them in: each try warned "no
            // GameEvents near" and their age ran out before the world arrived.
            if (!LanNetworkManager.ClientCanApplyWorldBulk())
                return;
            // Throttle scans for events whose scene objects are not loaded yet.
            float now = Time.unscaledTime;
            if (now < _nextPendingGameEventsFlushTime) return;
            _nextPendingGameEventsFlushTime = now + PendingGameEventsFlushInterval;

            for (int i = _pendingGameEvents.Count - 1; i >= 0; i--)
            {
                var msg = _pendingGameEvents[i];
                int key = msg.EventName != null ? msg.EventName.GetHashCode() : 0;
                key ^= (int)(msg.PosX * 10f) ^ ((int)(msg.PosZ * 10f) << 10);
                if (!_pendingGameEventQueuedAt.ContainsKey(key))
                    _pendingGameEventQueuedAt[key] = now;

                // Pad-slot events (a dream's onEnterLocation, its FX) wait for the pad to load.
                bool dreamish = ClientStateBackup.IsDreamPadCoordinate(
                    new Vector3(msg.PosX, msg.PosY, msg.PosZ));
                float maxAge = dreamish ? PendingDreamGameEventsMaxAge : PendingGameEventsMaxAge;
                if (now - _pendingGameEventQueuedAt[key] > maxAge)
                {
                    ModLog.WarnRate(LogCat.Session, "ge-none-near:" + msg.EventName,
                        $"[GameEventsSync] no GameEvents near ({msg.PosX:F1},{msg.PosY:F1},{msg.PosZ:F1}) name='{msg.EventName}' after {maxAge:F0}s, dropped");
                    _pendingGameEvents.RemoveAt(i);
                    _pendingGameEventQueuedAt.Remove(key);
                    continue;
                }

                // Dream onEnterLocation / pad FX: keep queued until pad finished.
                if (dreamish)
                {
                    Location dLoc = Dreams.Instance != null ? Dreams.Instance.dreamLocation : null;
                    if (DreamSyncManager.IsDreamActive
                        && (dLoc == null || !dLoc.finishedLoading
                            || Dreams.Instance == null || !Dreams.Instance.dreaming))
                        continue;
                }

                // Always go through Apply (soft name search); pre-find skipped def_glow.
                // A throwing event is dropped instead of aborting the flush (and retried
                // forever behind everything after it).
                bool resolved;
                try
                {
                    resolved = ApplyGameEventsFired(msg, queueIfMissing: false);
                }
                catch (Exception ex)
                {
                    ModLog.WarnRate(LogCat.Session, "ge-flush-item-throw",
                        "[GameEventsSync] pending GE '" + msg.EventName + "' apply threw: " + ex.Message);
                    resolved = true;
                }
                if (resolved)
                {
                    _pendingGameEvents.RemoveAt(i);
                    _pendingGameEventQueuedAt.Remove(key);
                }
            }
        }

    
    }
}
