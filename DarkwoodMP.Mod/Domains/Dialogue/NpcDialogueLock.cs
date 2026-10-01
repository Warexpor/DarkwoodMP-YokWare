using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Host-authoritative one-speaker-per-NPC lock.
    /// Multiple NPCs may be spoken to in parallel (Dictionary); same NPC is serialized.
    /// </summary>
    public static class NpcDialogueLock
    {
        private struct Hold
        {
            public string NpcName;
            public int OwnerId;
            public float ExpireAt;
            public bool Dream;
        }

        /// <summary>Seconds between lease renewals while a dialogue window stays open.</summary>
        private const float LeaseRenewIntervalSeconds = 30f;

        /// <summary>An NPC this close to the dream pad (or under it) is the pad twin.</summary>
        private const float DreamPadRadius = 250f;

        // Keyed by (name, world). The bunker's door_underground (and every other dream NPC)
        // exists on the dream pad and in the overworld; one peer talking to the pad twin must not
        // lock the overworld one, and release must find the lock it took. The world travels on
        // the wire (DialogNpcLockMessage.Dream): each peer's own dream flags disagree while only
        // some peers are dreaming.
        private static readonly Dictionary<string, Hold> _locks = new Dictionary<string, Hold>();
        private static readonly HashSet<string> _renewing = new HashSet<string>();

        private static string KeyFor(string npcName, bool dream)
        {
            return dream ? npcName + "@dream" : npcName;
        }

        /// <summary>
        /// The world of a talk this peer is opening: the NPC itself is the dream-pad twin, or
        /// (no pad) this peer is itself dreaming. Sent as the sender's view on the wire.
        /// </summary>
        internal static bool IsDreamWorldNpc(NPC npc)
        {
            Transform pad = DreamSyncManager.GetDreamLocationTransform();
            if (pad == null || npc == null)
                return DreamSyncManager.IsLocalDreamActive;
            Transform t = npc.transform;
            return t.IsChildOf(pad) || Vector3.Distance(t.position, pad.position) <= DreamPadRadius;
        }

        /// <summary>Hold under <paramref name="preferDream"/>'s key first, then the other world's.</summary>
        private static bool TryFindHold(string npcName, bool preferDream, out string key, out Hold hold)
        {
            key = KeyFor(npcName, preferDream);
            if (_locks.TryGetValue(key, out hold))
                return true;
            key = KeyFor(npcName, !preferDream);
            return _locks.TryGetValue(key, out hold);
        }

        /// <summary>
        /// The hold <paramref name="ownerId"/> took on this NPC in either world (expired or not).
        /// A dream starting or ending mid-talk must not turn a renewal into a fresh grant.
        /// </summary>
        private static bool TryFindOwnedHold(string npcName, int ownerId, out string key, out Hold hold)
        {
            key = KeyFor(npcName, false);
            if (_locks.TryGetValue(key, out hold) && hold.OwnerId == ownerId)
                return true;
            key = KeyFor(npcName, true);
            if (_locks.TryGetValue(key, out hold) && hold.OwnerId == ownerId)
                return true;
            hold = default;
            return false;
        }

        /// <summary>Count of active (non-expired) locks for tests and diagnostics.</summary>
        public static int ActiveCount
        {
            get
            {
                float now = Time.unscaledTime;
                int n = 0;
                foreach (var kvp in _locks)
                {
                    if (now < kvp.Value.ExpireAt)
                        n++;
                }
                return n;
            }
        }

        public static void Reset()
        {
            _locks.Clear();
            _renewing.Clear();
        }

        public static bool TryAcquire(string npcName, int ownerPlayerId, bool dream, float leaseSeconds = -1f)
        {
            if (string.IsNullOrEmpty(npcName) || ownerPlayerId < 0)
                return false;

            float now = Time.unscaledTime;
            float lease = leaseSeconds > 0f ? leaseSeconds : NpcDialogueLockPolicy.DefaultLeaseSeconds;
            string key = KeyFor(npcName, dream);

            int heldOwner = -1;
            float heldExpire = 0f;
            if (_locks.TryGetValue(key, out Hold existing))
            {
                heldOwner = existing.OwnerId;
                heldExpire = existing.ExpireAt;
            }

            if (!NpcDialogueLockPolicy.CanAcquireNpcSlot(heldOwner, heldExpire, ownerPlayerId, now))
                return false;

            _locks[key] = new Hold
            {
                NpcName = npcName,
                OwnerId = ownerPlayerId,
                ExpireAt = now + lease,
                Dream = dream
            };
            return true;
        }

        /// <summary>Drop <paramref name="ownerPlayerId"/>'s hold (prefer the given world's key).</summary>
        /// <returns>The world of the hold that was released, or <paramref name="preferDream"/>.</returns>
        public static bool Release(string npcName, int ownerPlayerId, bool preferDream)
        {
            if (string.IsNullOrEmpty(npcName)) return preferDream;
            float now = Time.unscaledTime;
            string key = KeyFor(npcName, preferDream);
            if (!(_locks.TryGetValue(key, out Hold hold)
                  && NpcDialogueLockPolicy.IsNpcSlotHeldBy(hold.OwnerId, hold.ExpireAt, ownerPlayerId, now)))
            {
                key = KeyFor(npcName, !preferDream);
                if (!(_locks.TryGetValue(key, out hold)
                      && NpcDialogueLockPolicy.IsNpcSlotHeldBy(hold.OwnerId, hold.ExpireAt, ownerPlayerId, now)))
                    return preferDream;
            }

            _locks.Remove(key);
            return hold.Dream;
        }

        public static bool IsLockedByOther(string npcName, int localPlayerId, bool dream)
        {
            if (string.IsNullOrEmpty(npcName)) return false;
            if (!_locks.TryGetValue(KeyFor(npcName, dream), out Hold hold))
                return false;
            float now = Time.unscaledTime;
            if (now >= hold.ExpireAt) return false;
            return hold.OwnerId != localPlayerId;
        }

        /// <summary>Holder of this NPC in <paramref name="dream"/>'s world; -1 when free / expired.</summary>
        public static int GetOwner(string npcName, bool dream)
        {
            if (string.IsNullOrEmpty(npcName)) return -1;
            if (!_locks.TryGetValue(KeyFor(npcName, dream), out Hold hold)) return -1;
            if (Time.unscaledTime >= hold.ExpireAt) return -1;
            return hold.OwnerId;
        }

        /// <summary>Holder of this NPC in either world (this peer's own world first); -1 when none.</summary>
        public static int GetOwner(string npcName)
        {
            if (string.IsNullOrEmpty(npcName)) return -1;
            if (!TryFindHold(npcName, DreamSyncManager.IsLocalDreamActive, out _, out Hold hold)) return -1;
            if (Time.unscaledTime >= hold.ExpireAt) return -1;
            return hold.OwnerId;
        }

        /// <summary>Extend this owner's own hold (either world), or take this peer's world slot.</summary>
        public static bool RenewLease(string npcName, int ownerPlayerId)
        {
            if (string.IsNullOrEmpty(npcName) || ownerPlayerId < 0) return false;
            if (TryFindOwnedHold(npcName, ownerPlayerId, out string key, out Hold hold))
            {
                hold.ExpireAt = Time.unscaledTime + NpcDialogueLockPolicy.DefaultLeaseSeconds;
                _locks[key] = hold;
                return true;
            }
            return TryAcquire(npcName, ownerPlayerId, DreamSyncManager.IsLocalDreamActive);
        }

        /// <summary>
        /// Host: extend lease when sender was the recorded holder (either world, including
        /// expired). Does not grant a new holder. Used at trade accept so long sessions stay valid.
        /// </summary>
        public static void HostRenewLeaseForSender(string npcName, int ownerPlayerId)
        {
            TryExtendOwned(npcName, ownerPlayerId, out _);
        }

        private static bool TryExtendOwned(string npcName, int ownerPlayerId, out bool dream)
        {
            dream = false;
            if (string.IsNullOrEmpty(npcName) || ownerPlayerId < 0) return false;
            if (!TryFindOwnedHold(npcName, ownerPlayerId, out string key, out Hold hold))
                return false;
            hold.ExpireAt = Time.unscaledTime + NpcDialogueLockPolicy.DefaultLeaseSeconds;
            _locks[key] = hold;
            dream = hold.Dream;
            return true;
        }

        /// <summary>
        /// Host: the holder is still talking. Extend the lease and refresh every client's mirror
        /// (their copy expires on the same 90s clock). No onEnterDialogue replay.
        /// </summary>
        /// <returns>False when this owner holds no lock on the NPC (nothing renewed).</returns>
        public static bool HostRenewHeld(LanNetworkManager net, string npcName, int ownerPlayerId)
        {
            if (net == null || net.Role != NetworkRole.Host) return false;
            if (!TryExtendOwned(npcName, ownerPlayerId, out bool dream))
                return false;
            BroadcastState(net, npcName, ownerPlayerId, granted: true, release: false, dream);
            return true;
        }

        /// <summary>
        /// The 90s lease was taken once at acquire and never renewed, so a conversation or trade
        /// longer than that let a second speaker in. While this peer's window stays open on the
        /// NPC, renew every 30s: the host extends locally and fans the grant out; a client sends
        /// a renewal request, which the host only ever extends (never a fresh grant).
        /// </summary>
        internal static void BeginLeaseRenewal(NPC npc)
        {
            if (npc == null || string.IsNullOrEmpty(npc.name)) return;
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;
            string npcName = npc.name;
            if (!_renewing.Add(npcName)) return;
            ctrl.StartCoroutine(RenewWhileOpen(npcName, IsDreamWorldNpc(npc)));
        }

        private static System.Collections.IEnumerator RenewWhileOpen(string npcName, bool dream)
        {
            try
            {
                while (true)
                {
                    yield return new WaitForSecondsRealtime(LeaseRenewIntervalSeconds);

                    // Role, not IsConnected: a host talking alone (peers come and go) still owns the
                    // lock, and a joiner must not get a second speaker when the lease lapses.
                    var net = ModRuntime.Network;
                    if (net == null || net.Role == NetworkRole.Offline)
                        yield break;
                    var dw = Singleton<UI>.Instance?.dialogueWindow;
                    if (dw == null || dw.npc == null || !dw.opened
                        || !string.Equals(dw.npc.name, npcName, System.StringComparison.Ordinal))
                        yield break;

                    int localId = net.LocalPlayerId;
                    if (net.Role == NetworkRole.Host)
                    {
                        if (!HostRenewHeld(net, npcName, localId))
                            HostTryGrant(net, npcName, localId, dream, fireEnterDialogue: false);
                    }
                    else
                    {
                        // The world the talk was opened in, not whatever this peer sees now.
                        if (TryExtendOwned(npcName, localId, out bool heldDream))
                            dream = heldDream;
                        else
                            TryAcquire(npcName, localId, dream);
                        bool sendDream = dream;
                        net.Send(NetMessageType.DialogNpcLock,
                            w => new DialogNpcLockMessage
                            {
                                NpcName = npcName,
                                OwnerPlayerId = localId,
                                Granted = false,
                                Release = false,
                                IsRequest = true,
                                Dream = sendDream,
                                Renewal = true
                            }.Serialize(w),
                            DeliveryMethod.ReliableOrdered);
                    }
                }
            }
            finally
            {
                _renewing.Remove(npcName);
            }
        }

        /// <summary>Host: attempt lock in <paramref name="dream"/>'s world and notify requestor (and peers).</summary>
        /// <param name="fireEnterDialogue">False for a renewal that lost its hold: re-take the slot
        /// without replaying the client's onEnterDialogue triggers.</param>
        public static bool HostTryGrant(LanNetworkManager net, string npcName, int ownerPlayerId, bool dream,
            bool fireEnterDialogue = true)
        {
            if (net == null || net.Role != NetworkRole.Host) return false;
            bool ok = TryAcquire(npcName, ownerPlayerId, dream);
            BroadcastState(net, npcName, ownerPlayerId, granted: ok, release: false, dream);
            if (ok)
            {
                ModLog.Event(LogCat.Session,
                    $"[DialogLock] granted NPC={npcName} owner={ownerPlayerId} dream={dream}");
                // Host talkTo already fired onEnterDialogue. A client talk only runs it
                // locally, and client one-shot GameEvents are blocked.
                if (fireEnterDialogue && ownerPlayerId != net.LocalPlayerId)
                    FireRemoteEnterDialogue(npcName, ownerPlayerId, dream);
            }
            else
                ModLog.Event(LogCat.Session,
                    $"[DialogLock] denied NPC={npcName} owner={ownerPlayerId} dream={dream} heldBy={GetOwner(npcName, dream)}");
            return ok;
        }

        /// <summary>
        /// Client opened talk. Replay vanilla NPC.talkTo's onEnterDialogue on the host
        /// so one-shot GameEvents fan out. Wrapped in the host apply guard so the
        /// inbound lock packet does not swallow the broadcast.
        /// </summary>
        private static void FireRemoteEnterDialogue(string npcName, int ownerPlayerId, bool dream)
        {
            // Strict: for a dream talk only the pad twin qualifies. The overworld twin shares
            // the name, and this method SetActive(true)s and fires triggers on whatever it gets.
            NPC npc = DialogOutcomeCloseNetHandlers.FindNpcByName(npcName, dream, strictPad: true);
            if (npc == null || npc.gameObject == null)
            {
                ModLog.WarnRate(LogCat.Session, "dlg-enter-npc-miss:" + npcName,
                    "[DialogLock] onEnterDialogue skip — NPC '" + npcName + "' not found"
                    + (dream ? " on the dream pad" : ""));
                return;
            }
            if (dream)
            {
                Transform pad = DreamSyncManager.GetDreamLocationTransform();
                if (pad == null
                    || (!npc.transform.IsChildOf(pad)
                        && Vector3.Distance(npc.transform.position, pad.position) > DreamPadRadius))
                {
                    ModRuntime.LegacyInfo(
                        "[DialogLock] skip onEnterDialogue — NPC '" + npcName + "' not on the dream pad");
                    return;
                }
            }

            ModRuntime.LegacyInfo("[DialogLock] host onEnterDialogue for " + npcName);
            // Stamp dialogue owner before BeginWorldOnly so GameEventsFired carries the speaker.
            bool pushed = ownerPlayerId > 0;
            if (pushed) GeFireActorContext.Push(ownerPlayerId);
            DialogHostApplyGuard.BeginWorldOnly();
            try
            {
                if (!npc.gameObject.activeInHierarchy)
                {
                    try { npc.gameObject.SetActive(true); }
                    catch { /* ignore */ }
                }

                Core.sendTriggerInfo(npc.gameObject, EventTrigger.Type.onEnterDialogue);
                EventTriggers[] ets = npc.GetComponentsInChildren<EventTriggers>(true);
                for (int i = 0; i < ets.Length; i++)
                {
                    EventTriggers et = ets[i];
                    if (et == null || et.gameObject == npc.gameObject) continue;
                    try { et.fireEventTrigger(EventTrigger.Type.onEnterDialogue); }
                    catch { /* ignore */ }
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DialogLock] onEnterDialogue: " + ex.Message);
            }
            finally
            {
                DialogHostApplyGuard.EndWorldOnly();
                if (pushed) GeFireActorContext.Pop();
            }
        }

        public static void HostRelease(LanNetworkManager net, string npcName, int ownerPlayerId, bool preferDream)
        {
            if (net == null || net.Role != NetworkRole.Host) return;
            bool dream = Release(npcName, ownerPlayerId, preferDream);
            BroadcastState(net, npcName, ownerPlayerId, granted: true, release: true, dream);
        }

        /// <summary>
        /// Host disconnect: drop every NPC lock held by the leaver and fan release so
        /// remaining peers are not stuck "Someone is already talking…" for up to the
        /// 90s lease. Local-only <see cref="ReleaseAllForPlayer"/> covers roster prune.
        /// </summary>
        public static void HostReleaseAllForPlayer(LanNetworkManager net, int playerId)
        {
            if (playerId < 0) return;
            if (_locks.Count == 0) return;

            var toRelease = new List<string>();
            foreach (var kvp in _locks)
            {
                if (kvp.Value.OwnerId == playerId)
                    toRelease.Add(kvp.Key);
            }
            if (toRelease.Count == 0) return;

            for (int i = 0; i < toRelease.Count; i++)
            {
                string key = toRelease[i];
                Hold hold = _locks[key];
                // Drop by key: the holder may have taken it in the other world.
                _locks.Remove(key);
                if (net != null && net.Role == NetworkRole.Host && net.IsConnected)
                    BroadcastState(net, hold.NpcName, playerId, granted: true, release: true, hold.Dream);
            }
            ModLog.Event(LogCat.Session,
                "[DialogLock] released " + toRelease.Count + " NPC lock(s) for disconnect p" + playerId);
        }

        /// <summary>Local clear of locks owned by a pruned roster peer (clients).</summary>
        public static void ReleaseAllForPlayer(int playerId)
        {
            if (playerId < 0 || _locks.Count == 0) return;
            var toRelease = new List<string>();
            foreach (var kvp in _locks)
            {
                if (kvp.Value.OwnerId == playerId)
                    toRelease.Add(kvp.Key);
            }
            for (int i = 0; i < toRelease.Count; i++)
                _locks.Remove(toRelease[i]);
        }

        private static void BroadcastState(LanNetworkManager net, string npcName, int ownerPlayerId,
            bool granted, bool release, bool dream)
        {
            if (net == null || !net.IsConnected) return;
            net.Broadcast(NetMessageType.DialogNpcLock,
                w => new DialogNpcLockMessage
                {
                    NpcName = npcName ?? "",
                    OwnerPlayerId = ownerPlayerId,
                    Granted = granted,
                    Release = release,
                    Dream = dream
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }
    }
}
