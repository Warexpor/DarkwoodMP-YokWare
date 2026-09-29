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
            public int OwnerId;
            public float ExpireAt;
        }

        private static readonly Dictionary<string, Hold> _locks = new Dictionary<string, Hold>();

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
        }

        public static bool TryAcquire(string npcName, int ownerPlayerId, float leaseSeconds = -1f)
        {
            if (string.IsNullOrEmpty(npcName) || ownerPlayerId < 0)
                return false;

            float now = Time.unscaledTime;
            float lease = leaseSeconds > 0f ? leaseSeconds : NpcDialogueLockPolicy.DefaultLeaseSeconds;

            int heldOwner = -1;
            float heldExpire = 0f;
            if (_locks.TryGetValue(npcName, out Hold existing))
            {
                heldOwner = existing.OwnerId;
                heldExpire = existing.ExpireAt;
            }

            if (!NpcDialogueLockPolicy.CanAcquireNpcSlot(heldOwner, heldExpire, ownerPlayerId, now))
                return false;

            _locks[npcName] = new Hold
            {
                OwnerId = ownerPlayerId,
                ExpireAt = now + lease
            };
            return true;
        }

        public static void Release(string npcName, int ownerPlayerId)
        {
            if (string.IsNullOrEmpty(npcName)) return;
            if (!_locks.TryGetValue(npcName, out Hold hold))
                return;

            float now = Time.unscaledTime;
            if (!NpcDialogueLockPolicy.IsNpcSlotHeldBy(hold.OwnerId, hold.ExpireAt, ownerPlayerId, now))
                return;

            _locks.Remove(npcName);
        }

        public static void ForceRelease(string reason = null)
        {
            if (_locks.Count > 0)
                ModLog.Event(LogCat.Session,
                    "[DialogLock] force release count=" + _locks.Count + " reason=" + (reason ?? ""));
            _locks.Clear();
        }

        public static bool IsLockedByOther(string npcName, int localPlayerId)
        {
            if (string.IsNullOrEmpty(npcName)) return false;
            if (!_locks.TryGetValue(npcName, out Hold hold))
                return false;
            float now = Time.unscaledTime;
            if (now >= hold.ExpireAt) return false;
            return hold.OwnerId != localPlayerId;
        }

        public static int GetOwner(string npcName)
        {
            if (string.IsNullOrEmpty(npcName)) return -1;
            if (!_locks.TryGetValue(npcName, out Hold hold)) return -1;
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
            if (!_locks.TryGetValue(npcName, out Hold hold) || hold.OwnerId != ownerPlayerId)
                return;

            float now = Time.unscaledTime;
            _locks[npcName] = new Hold
            {
                OwnerId = ownerPlayerId,
                ExpireAt = now + NpcDialogueLockPolicy.DefaultLeaseSeconds
            };
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
            NPC npc = DialogOutcomeCloseNetHandlers.FindNpcByName(npcName, dream);
            if (npc == null || npc.gameObject == null)
            {
                ModRuntime.Log?.LogWarning(
                    "[DialogLock] onEnterDialogue skip — NPC '" + npcName + "' not found");
                return;
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
                string npc = toRelease[i];
                if (net != null && net.Role == NetworkRole.Host && net.IsConnected)
                    HostRelease(net, npc, playerId);
                else
                    Release(npc, playerId);
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
