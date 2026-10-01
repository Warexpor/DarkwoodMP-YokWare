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
        }

        /// <summary>Seconds between lease renewals while a dialogue window stays open.</summary>
        private const float LeaseRenewIntervalSeconds = 30f;

        // Keyed by (name, world). The bunker's door_underground (and every other dream NPC)
        // exists on the dream pad and in the overworld; one peer talking to the pad twin must not
        // lock the overworld one, and release must find the lock it took.
        private static readonly Dictionary<string, Hold> _locks = new Dictionary<string, Hold>();
        private static readonly HashSet<string> _renewing = new HashSet<string>();

        private static string KeyFor(string npcName, bool dream)
        {
            return dream ? npcName + "@dream" : npcName;
        }

        private static string CurrentKey(string npcName)
        {
            return KeyFor(npcName, DreamSyncManager.IsDreamActive);
        }

        /// <summary>
        /// Current-world key first, then the other world. A dream can start (or end) while a lock
        /// is held; releasing by name must still find the key the lock was taken under.
        /// </summary>
        private static bool TryFindHold(string npcName, out string key, out Hold hold)
        {
            bool dream = DreamSyncManager.IsDreamActive;
            key = KeyFor(npcName, dream);
            if (_locks.TryGetValue(key, out hold))
                return true;
            key = KeyFor(npcName, !dream);
            return _locks.TryGetValue(key, out hold);
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

        public static bool TryAcquire(string npcName, int ownerPlayerId, float leaseSeconds = -1f)
        {
            if (string.IsNullOrEmpty(npcName) || ownerPlayerId < 0)
                return false;

            float now = Time.unscaledTime;
            float lease = leaseSeconds > 0f ? leaseSeconds : NpcDialogueLockPolicy.DefaultLeaseSeconds;
            string key = CurrentKey(npcName);

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
                ExpireAt = now + lease
            };
            return true;
        }

        public static void Release(string npcName, int ownerPlayerId)
        {
            if (string.IsNullOrEmpty(npcName)) return;
            if (!TryFindHold(npcName, out string key, out Hold hold))
                return;

            float now = Time.unscaledTime;
            if (!NpcDialogueLockPolicy.IsNpcSlotHeldBy(hold.OwnerId, hold.ExpireAt, ownerPlayerId, now))
                return;

            _locks.Remove(key);
        }

        public static bool IsLockedByOther(string npcName, int localPlayerId)
        {
            if (string.IsNullOrEmpty(npcName)) return false;
            if (!_locks.TryGetValue(CurrentKey(npcName), out Hold hold))
                return false;
            float now = Time.unscaledTime;
            if (now >= hold.ExpireAt) return false;
            return hold.OwnerId != localPlayerId;
        }

        public static int GetOwner(string npcName)
        {
            if (string.IsNullOrEmpty(npcName)) return -1;
            if (!_locks.TryGetValue(CurrentKey(npcName), out Hold hold)) return -1;
            if (Time.unscaledTime >= hold.ExpireAt) return -1;
            return hold.OwnerId;
        }

        /// <summary>Renew or acquire lease (same owner, or expired slot).</summary>
        public static bool RenewLease(string npcName, int ownerPlayerId)
        {
            return TryAcquire(npcName, ownerPlayerId);
        }

        /// <summary>
        /// Host: extend lease when sender was the recorded holder (including expired).
        /// Does not grant a new holder. Used at trade accept so long sessions stay valid.
        /// </summary>
        public static void HostRenewLeaseForSender(string npcName, int ownerPlayerId)
        {
            if (string.IsNullOrEmpty(npcName) || ownerPlayerId < 0) return;
            if (!TryFindHold(npcName, out string key, out Hold hold) || hold.OwnerId != ownerPlayerId)
                return;

            float now = Time.unscaledTime;
            hold.ExpireAt = now + NpcDialogueLockPolicy.DefaultLeaseSeconds;
            _locks[key] = hold;
        }

        /// <summary>
        /// Host: the holder is still talking. Extend the lease and refresh every client's mirror
        /// (their copy expires on the same 90s clock). No onEnterDialogue replay.
        /// </summary>
        public static void HostRenewHeld(LanNetworkManager net, string npcName, int ownerPlayerId)
        {
            if (net == null || net.Role != NetworkRole.Host) return;
            HostRenewLeaseForSender(npcName, ownerPlayerId);
            BroadcastState(net, npcName, ownerPlayerId, granted: true, release: false);
        }

        /// <summary>
        /// The 90s lease was taken once at acquire and never renewed, so a conversation or trade
        /// longer than that let a second speaker in. While this peer's window stays open on the
        /// NPC, renew every 30s: the host extends locally and fans the grant out; a client re-sends
        /// its request, which the host treats as a renewal because it already holds the slot.
        /// </summary>
        internal static void BeginLeaseRenewal(NPC npc)
        {
            if (npc == null || string.IsNullOrEmpty(npc.name)) return;
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;
            string npcName = npc.name;
            if (!_renewing.Add(npcName)) return;
            ctrl.StartCoroutine(RenewWhileOpen(npcName));
        }

        private static System.Collections.IEnumerator RenewWhileOpen(string npcName)
        {
            try
            {
                while (true)
                {
                    yield return new WaitForSecondsRealtime(LeaseRenewIntervalSeconds);

                    // Role, not IsConnected: a host talking alone (peers come and go) still owns the
                    // lock, and a joiner must not get a second speaker when the lease lapses.
                    var net = ModRuntime.Network as LanNetworkManager;
                    if (net == null || net.Role == NetworkRole.Offline)
                        yield break;
                    var dw = Singleton<UI>.Instance?.dialogueWindow;
                    if (dw == null || dw.npc == null || !dw.opened
                        || !string.Equals(dw.npc.name, npcName, System.StringComparison.Ordinal))
                        yield break;

                    int localId = net.LocalPlayerId;
                    if (net.Role == NetworkRole.Host)
                    {
                        HostRenewHeld(net, npcName, localId);
                    }
                    else
                    {
                        TryAcquire(npcName, localId);
                        net.Send(NetMessageType.DialogNpcLock,
                            w => new DialogNpcLockMessage
                            {
                                NpcName = npcName,
                                OwnerPlayerId = localId,
                                Granted = false,
                                Release = false,
                                IsRequest = true
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

        /// <summary>Host: attempt lock and notify requestor (and peers).</summary>
        public static bool HostTryGrant(LanNetworkManager net, string npcName, int ownerPlayerId)
        {
            if (net == null || net.Role != NetworkRole.Host) return false;
            bool ok = TryAcquire(npcName, ownerPlayerId);
            BroadcastState(net, npcName, ownerPlayerId, granted: ok, release: false);
            if (ok)
            {
                ModLog.Event(LogCat.Session, $"[DialogLock] granted NPC={npcName} owner={ownerPlayerId}");
                // Host talkTo already fired onEnterDialogue. A client talk only runs it
                // locally, and client one-shot GameEvents are blocked.
                if (ownerPlayerId != net.LocalPlayerId)
                    FireRemoteEnterDialogue(npcName, ownerPlayerId);
            }
            else
                ModLog.Event(LogCat.Session,
                    $"[DialogLock] denied NPC={npcName} owner={ownerPlayerId} heldBy={GetOwner(npcName)}");
            return ok;
        }

        /// <summary>
        /// Client opened talk. Replay vanilla NPC.talkTo's onEnterDialogue on the host
        /// so one-shot GameEvents fan out. Wrapped in the host apply guard so the
        /// inbound lock packet does not swallow the broadcast.
        /// </summary>
        private static void FireRemoteEnterDialogue(string npcName, int ownerPlayerId)
        {
            bool dream = DreamSyncManager.IsDreamActive;
            // Strict: with a dream active only the pad twin qualifies. The overworld twin shares
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
                        && Vector3.Distance(npc.transform.position, pad.position) > 250f))
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

        public static void HostRelease(LanNetworkManager net, string npcName, int ownerPlayerId)
        {
            if (net == null || net.Role != NetworkRole.Host) return;
            Release(npcName, ownerPlayerId);
            BroadcastState(net, npcName, ownerPlayerId, granted: true, release: true);
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
                string npc = _locks[key].NpcName;
                // Drop by key: the holder may have taken it in the other world.
                _locks.Remove(key);
                if (net != null && net.Role == NetworkRole.Host && net.IsConnected)
                    BroadcastState(net, npc, playerId, granted: true, release: true);
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

        private static void BroadcastState(LanNetworkManager net, string npcName, int ownerPlayerId, bool granted, bool release)
        {
            if (net == null || !net.IsConnected) return;
            net.Broadcast(NetMessageType.DialogNpcLock,
                w => new DialogNpcLockMessage
                {
                    NpcName = npcName ?? "",
                    OwnerPlayerId = ownerPlayerId,
                    Granted = granted,
                    Release = release
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }
    }
}
