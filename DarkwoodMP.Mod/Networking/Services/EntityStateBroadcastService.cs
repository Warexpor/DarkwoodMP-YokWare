using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Periodically snapshots nearby entity positions and states, then broadcasts them
    /// to connected peers (LAN LiteNetLib or Steam P2P) on an unreliable channel.
    /// Bodies near a remote player go out at 20 Hz, the rest at 10 Hz; every batch carries
    /// the host session clock (<see cref="HostNow"/>) so clients interpolate on the host timeline.
    /// </summary>
    public static class EntityStateBroadcastService
    {
        private static float _sendTimer;
        /// <summary>Real time of the session clock's zero (-1: not started).</summary>
        private static double _epoch = -1;
        private static float _lastTickAt = -1f;

        /// <summary>
        /// Host session clock: unscaled seconds since this host's broadcast started. Every host
        /// time stamp clients compare (EntityState, EnemyAttack) uses it. A float of the process
        /// uptime lost precision over a long run (8 ms steps after 18 h); this one starts at 0.
        /// </summary>
        public static float HostNow
        {
            get
            {
                double now = Time.unscaledTimeAsDouble;
                if (_epoch < 0)
                    _epoch = now;
                return (float)(now - _epoch);
            }
        }
        private static uint _nextSnapshotSequence;
        private const float SendInterval = 0.05f;
        /// <summary>Bodies within this XZ radius of a remote player are sent every tick; the rest every other tick.</summary>
        private const float NearRemoteBand = 800f;
        /// <summary>Ticks between full resyncs (dirty cache cleared, descriptors re-sent): 1 s.</summary>
        private const int FullResyncTicks = 20;
        /// <summary>Sends of a new / changed id that carry its name and prefab path.</summary>
        private const int DescriptorSends = 3;
        private static int _tick;

        /// <summary>Per-tick entity cap (split across packets by <see cref="SendChunked"/>); keeps dense night scenes from starving later entities.</summary>
        private const int MaxEntitiesPerPacket = 256;
        private static EntitySnapshotNet[] _buffer = new EntitySnapshotNet[MaxEntitiesPerPacket]; // process-scoped: scratch buffer, cleared before each use
        /// <summary>Bodies past <see cref="NearRemoteBand"/> of every player: packed after the near ones so far wildlife cannot starve combat NPCs of the per-tick cap.</summary>
        private static EntitySnapshotNet[] _farBuffer = new EntitySnapshotNet[MaxEntitiesPerPacket]; // process-scoped: scratch buffer, cleared before each use
        /// <summary>Ids streamed last tick: they leave at <see cref="GameplayConstants.EntityInterestLeaveRange"/>, others enter at the activation range.</summary>
        private static readonly HashSet<short> _inRange = new HashSet<short>();
        private static readonly Dictionary<short, EntitySnapshotNet> _lastSent = new Dictionary<short, EntitySnapshotNet>();
        /// <summary>
        /// Stable stripped name / prefab path per id, keyed to the body that owned the id when
        /// cached (ids are recycled after a despawn grace) — avoids Unity <c>name</c> +
        /// Substring + GetComponent per send.
        /// </summary>
        private struct Descriptor
        {
            public Character Owner;
            public string Name;
            public string PrefabPath;
            public int SaveId;
            public int SendsLeft;
        }
        private static readonly Dictionary<short, Descriptor> _descriptors = new Dictionary<short, Descriptor>(128);
        /// <summary>Ids that changed last send: one more unchanged send pins the stop pose on clients.</summary>
        private static readonly HashSet<short> _settle = new HashSet<short>();
        private static readonly HashSet<short> _settleNext = new HashSet<short>();
        private static bool _fullResyncTick;
        /// <summary>Round-robin start index so a full tracker list is not starved by the per-packet cap.</summary>
        private static int _scanStart;

        /// <summary>
        /// Called every frame; accumulates time and sends a snapshot when the interval elapses.
        /// </summary>
        public static void Tick()
        {
            if (!NetGuard.ConnectedHost(out var net))
            {
                _clipTrackingOn = false;
                return;
            }
            _clipTrackingOn = !_paused;
            if (_paused) return;

            _sendTimer += Time.unscaledDeltaTime;
            if (_sendTimer < SendInterval)
                return;

            // Keep 20 Hz on average: the remainder carries over (a send lands on the first frame
            // past each 50 ms step instead of 50 ms after the last send, which ran 53-67 ms at
            // 60 fps). After a hitch at most half a step carries, so a stall does not burst.
            _sendTimer -= SendInterval;
            if (_sendTimer > SendInterval * 0.5f)
                _sendTimer = SendInterval * 0.5f;
            float now = HostNow;
            if (_lastTickAt >= 0f)
                DWMPHorde.Logging.ClientPerfProbe.NoteEntityTick(now - _lastTickAt);
            _lastTickAt = now;
            SendSnapshot(net);
        }

        private static readonly NetWriter _snapWriter = new NetWriter();
        private static byte[] _snapSendBuf = Array.Empty<byte>(); // process-scoped: scratch buffer, cleared before each use
        private static readonly NetWriter _bodyWriter = new NetWriter();
        private static byte[] _bodyBuf = Array.Empty<byte>(); // process-scoped: scratch buffer, cleared before each use
        /// <summary>End offset of entry i inside the serialized body (chunk boundaries).</summary>
        private static int[] _entryEnd = new int[MaxEntitiesPerPacket]; // process-scoped: scratch buffer, cleared before each use
        /// <summary>Framed bytes besides entries: type + Sequence + HostTime + count.</summary>
        private const int FrameBytes = 1 + 4 + 4 + 4;
        private static float _batchHostTime;

        /// <summary>
        /// Serialize the dirty entities once, then cut them into packets that each fit the smallest
        /// ready peer's single-datagram limit. Every packet carries its own newer sequence, so the
        /// receiver's stale-snapshot gate accepts all of them; a 256-entity scene no longer builds
        /// one multi-KB unreliable packet that LiteNetLib refuses.
        /// </summary>
        private static void SendChunked(LanNetworkManager net, int entityCount)
        {
            _bodyWriter.Reset();
            for (int i = 0; i < entityCount; i++)
            {
                _buffer[i].Serialize(_bodyWriter);
                _entryEnd[i] = _bodyWriter.Length;
            }
            _bodyWriter.CopyDataInto(ref _bodyBuf, out _);

            int cap = Math.Max(net.MinUnreliablePacketBytes(gameplayReadyOnly: true) - FrameBytes, 128);
            int start = 0;
            int startOff = 0;
            while (start < entityCount)
            {
                // At least one entity per packet; an entity that alone exceeds the limit is
                // promoted to ReliableOrdered by the transport.
                int end = start + 1;
                while (end < entityCount && _entryEnd[end] - startOff <= cap)
                    end++;
                int endOff = _entryEnd[end - 1];

                _snapWriter.Reset();
                _snapWriter.Put((byte)NetMessageType.EntityState);
                _snapWriter.Put(++_nextSnapshotSequence);
                _snapWriter.Put(_batchHostTime);
                _snapWriter.Put(end - start);
                _snapWriter.PutRaw(_bodyBuf, startOff, endOff - startOff);
                _snapWriter.CopyDataInto(ref _snapSendBuf, out int sendLen);
                // Walk the connected peers directly; ConnectedPlayerIds allocated a List every tick.
                net.SendRawToReadyPeers(_snapSendBuf, sendLen, DeliveryMethod.Unreliable);

                start = end;
                startOff = endOff;
            }
            net.FlushQueuedSends();
        }

        private static void SendSnapshot(LanNetworkManager net)
        {
            // CopyAll: no ToArray alloc every 100ms (dual-box host hitch with 100+ tracked AI).
            int nAll = CharacterTracker.CopyAll(out Character[] all);
            if (nAll == 0)
                return;

            int maxEntities = Mathf.Min(nAll, MaxEntitiesPerPacket);
            if (_buffer.Length < maxEntities)
                _buffer = new EntitySnapshotNet[maxEntities];
            if (_farBuffer.Length < maxEntities)
                _farBuffer = new EntitySnapshotNet[maxEntities];

            // Full resync every ~1s: every body in range is sent (lost packets, late joiners),
            // with its descriptor. Only real changes earn a settle send afterwards.
            _fullResyncTick = false;
            if (++_fullResyncCounter >= FullResyncTicks)
            {
                _fullResyncCounter = 0;
                _fullResyncTick = true;
            }
            // Far bodies (no remote within NearRemoteBand) only on even ticks: 10 Hz.
            bool farTick = (++_tick & 1) == 0 || _fullResyncTick;
            _batchHostTime = HostNow;
            _settleNext.Clear();

            int nearCount = 0;
            int farCount = 0;

            Vector3 hostPos = Player.Instance != null ? Player.Instance.transform.position : Vector3.zero;
            // Matches WorldGrid proxy cull / client interest (XZ), with the same leave margin.
            float enterSq = GameplayConstants.EntityActivationRange * GameplayConstants.EntityActivationRange;
            float leaveSq = GameplayConstants.EntityInterestLeaveRange * GameplayConstants.EntityInterestLeaveRange;
            float nearBandSq = NearRemoteBand * NearRemoteBand;

            if (_scanStart < 0 || _scanStart >= nAll)
                _scanStart = 0;

            // One pass: bodies near any player go first in the packet (combat / presentation
            // critical), the rest of the send range after them.
            for (int n = 0; n < nAll; n++)
            {
                int i = (_scanStart + n) % nAll;
                Character c = all[i];
                if (c == null) continue;
                // The host's own prologue creatures live on its private pads (PersonalPrologue).
                if (PersonalPrologue.IsOnProloguePad(c.transform))
                    continue;

                // During dreams, stream dream NPCs only; skip frozen overworld AI.
                if (Sync.DreamSyncManager.IsDreamActive
                    && Sync.DreamSyncManager.IsWorldFrozenForComponent(c))
                    continue;

                short sid = CharacterTracker.GetStableId(c);
                if (sid == 0)
                    continue;

                Vector3 cPos = c.transform.position;
                float dxh = cPos.x - hostPos.x;
                float dzh = cPos.z - hostPos.z;
                float dHost = dxh * dxh + dzh * dzh;
                bool wasInRange = _inRange.Contains(sid);
                float rangeSq = wasInRange ? leaveSq : enterSq;
                if (dHost > rangeSq && !PlayerPositionManager.IsAnyRemoteWithinSq(cPos, rangeSq))
                {
                    if (wasInRange)
                        _inRange.Remove(sid);
                    continue;
                }
                if (!wasInRange)
                    _inRange.Add(sid);

                bool nearRemote = PlayerPositionManager.IsAnyRemoteWithinSq(cPos, nearBandSq);
                if (!farTick && !nearRemote)
                {
                    // Not its tick: keep its settle send for the next one.
                    if (_settle.Contains(sid))
                        _settleNext.Add(sid);
                    continue;
                }

                bool settle = _settle.Contains(sid);
                if (!TryBuildSnapshot(c, sid, cPos, settle || _fullResyncTick, out EntitySnapshotNet snap))
                    continue;

                // Dirty-check: skip if nothing changed since last send
                bool changed = !_lastSent.TryGetValue(snap.Index, out var last) || HasChanged(last, snap);
                if (!changed && !settle && !_fullResyncTick)
                    continue;

                bool nearAny = nearRemote || dHost <= nearBandSq;
                if (nearAny ? nearCount >= maxEntities : farCount >= maxEntities)
                {
                    // Over the cap this tick: it counts as unsent, so it goes next tick.
                    _settleNext.Add(snap.Index);
                    continue;
                }
                // Moved this send: one more send after it stops pins the final pose.
                if (changed)
                    _settleNext.Add(snap.Index);

                if (nearAny)
                    _buffer[nearCount++] = snap;
                else
                    _farBuffer[farCount++] = snap;
            }

            int count = nearCount;
            for (int f = 0; f < farCount; f++)
            {
                if (count < maxEntities)
                {
                    _buffer[count++] = _farBuffer[f];
                    continue;
                }
                // Did not fit behind the near bodies: not sent, so not the last sent either.
                _settleNext.Add(_farBuffer[f].Index);
            }
            for (int k = 0; k < count; k++)
            {
                EntitySnapshotNet sent = _buffer[k];
                _lastSent[sent.Index] = sent;
                MarkClipSent(sent.Index, sent.Clip);
            }

            // Advance scan window for next tick
            _scanStart = (_scanStart + Mathf.Max(1, maxEntities / 2)) % nAll;

            // Ids not reached this tick (packet cap, out of range) keep their pending settle
            // send while they are still live.
            foreach (short pending in _settle)
            {
                if (!_inRange.Contains(pending) || CharacterTracker.FindByStableId(pending) == null)
                    continue;
                bool sentNow = false;
                for (int i = 0; i < count; i++)
                {
                    if (_buffer[i].Index == pending) { sentNow = true; break; }
                }
                if (!sentNow)
                    _settleNext.Add(pending);
            }
            _settle.Clear();
            foreach (short next in _settleNext)
                _settle.Add(next);

            if (count == 0)
                return;

            int entityCount = count;
            SendChunked(net, entityCount);
            DWMPHorde.Logging.ClientPerfProbe.NoteEntityBroadcast(entityCount);

            _sendCount++;
            if (!EntitySyncLog.On)
                return;
            // Rate-limited deep dump (not every 10 ticks StringBuilder under VerboseLogging).
            EntitySyncLog.Trace("ent:send", () =>
            {
                var sb = new System.Text.StringBuilder(128);
                sb.Append("[HostEntitySync] send n=").Append(entityCount)
                    .Append(" seq=").Append(_nextSnapshotSequence)
                    .Append(" tracked=").Append(nAll)
                    .Append(" | ");
                int lim = Mathf.Min(entityCount, 12);
                for (int i = 0; i < lim; i++)
                {
                    sb.Append(_buffer[i].EntityName)
                        .Append("(id=").Append(_buffer[i].Index)
                        .Append(" clip=").Append(_buffer[i].Clip ?? "")
                        .Append(" hp=").Append(_buffer[i].HealthPct)
                        .Append("% alive=").Append(_buffer[i].Alive ? 1 : 0)
                        .Append(") ");
                }
                if (entityCount > lim)
                    sb.Append("…+").Append(entityCount - lim);
                return sb.ToString();
            }, 1.5f);

            // Clip / alive transitions get per-id Trace (no per-frame spam).
            for (int i = 0; i < entityCount; i++)
            {
                EntitySnapshotNet snap = _buffer[i];
                if (_prevClip.TryGetValue(snap.Index, out string prevClip)
                    && !string.Equals(prevClip, snap.Clip, StringComparison.Ordinal))
                {
                    EntitySyncLog.Anim(snap.Index.ToString(),
                        "[HostAnim] id=" + snap.Index + " " + snap.EntityName
                        + " clip " + (prevClip ?? "") + " → " + (snap.Clip ?? "")
                        + (snap.PrevClip != null ? " via " + snap.PrevClip : "")
                        + " frame=" + snap.ClipFrame + " anim=" + (snap.Animating ? 1 : 0), 0.4f);
                }
                _prevClip[snap.Index] = snap.Clip ?? "";

                if (_prevAlive.TryGetValue(snap.Index, out bool prevAlive) && prevAlive != snap.Alive)
                {
                    EntitySyncLog.Event(() =>
                        "[HostAlive] id=" + snap.Index + " " + snap.EntityName
                        + " alive " + prevAlive + " → " + snap.Alive
                        + " hp=" + snap.HealthPct);
                }
                _prevAlive[snap.Index] = snap.Alive;
            }
        }

        private static bool TryBuildSnapshot(Character c, short id, Vector3 cPos, bool force, out EntitySnapshotNet snap)
        {
            snap = default;

            // A villager away for the night is off on every peer; never wake or stream it.
            if (NightVillage.IsHidden(c.gameObject))
                return false;

            // Near a remote: WorldGrid edge cases can leave isActive/animator off while the
            // GO is still tracked; otherwise the client gets empty clips and sliding sprites. Wake
            // presentation components so processAnims can own Walk/Idle again.
            float wakeSq = GameplayConstants.EntityActivationRange * GameplayConstants.EntityActivationRange;
            if (c.alive
                && PlayerPositionManager.IsAnyRemoteWithinSq(cPos, wakeSq)
                && (!c.isActive || (c.animator != null && !c.animator.enabled)))
            {
                try
                {
                    if (!c.gameObject.activeSelf)
                        c.gameObject.SetActive(true);
                    c.enableComponents(true);
                    EntitySyncLog.Trace("ent:wake:" + id,
                        () => "[HostWake] id=" + id + " " + (c.name ?? "")
                            + " wasActive=" + c.isActive + " animOn="
                            + (c.animator != null && c.animator.enabled), 2f);
                }
                catch { /* dismantled mid-frame */ }
            }

            // Prefer Character.animator (cached body) over raw GetComponent for presentation.
            tk2dSpriteAnimator anim = null;
            try { anim = c.animator; } catch { /* dismantled */ }
            if (anim == null)
                anim = c.GetComponent<tk2dSpriteAnimator>();

            // clipToPlay is what processAnims decided this frame; CurrentClip can be null
            // after enableComponents / SetActive cycles while clipToPlay is still Walk/Idle.
            string clip = "";
            try
            {
                if (!string.IsNullOrEmpty(c.clipToPlay))
                    clip = c.clipToPlay;
            }
            catch { /* odd prefab */ }
            tk2dSpriteAnimationClip current = anim != null ? anim.CurrentClip : null;
            if (string.IsNullOrEmpty(clip) && current != null)
                clip = current.name;

            // The frame belongs to the clip sent only when the animator is showing it (clipToPlay
            // can be ahead of Play for a frame), as EnemyAttackSender.ReadClip does. A finished
            // once clip reports frames.Length: its last frame is what is on screen.
            short clipFrame = -1;
            bool animating = false;
            if (current != null && current.frames != null && current.frames.Length > 0
                && string.Equals(current.name, clip, StringComparison.Ordinal))
            {
                clipFrame = (short)Mathf.Clamp(anim.CurrentFrame, 0, current.frames.Length - 1);
                // Vanilla processAnims calls Play(clipToPlay) every frame while Character.Update
                // runs, and tk2d restarts a finished once clip on Play: Walk / Run / Idle keep
                // cycling though their wrap mode is Once. A clip that finishes and holds (aim
                // pause, death, a body that stopped updating) is not animating.
                if (anim.enabled && !anim.Paused)
                {
                    animating = anim.Playing
                        || (c.isActive && c.enabled && !c.dummy && c.gameObject.activeInHierarchy);
                }
            }

            Vector3 rot = c.transform.eulerAngles;
            byte healthPct = (byte)Mathf.Clamp((c.Health / Mathf.Max(c.maxHealth, 1f)) * 100f, 0, 100);
            // A still-positive pre-death pool can round to 0%. Keep 1% so the
            // client does not turn a downed enemy into a finished corpse.
            byte flags = 0;
            if (c.sleeping) flags |= EntitySnapshotNet.FlagSleeping;
            if (c.eating) flags |= EntitySnapshotNet.FlagEating;
            bool alive = c.alive;
            // Pre-death is alive=false with health still above zero. That is the
            // downed phase, not the finished kill.
            if (!alive && c.hasPreDeath && c.Health > 0f)
            {
                flags |= EntitySnapshotNet.FlagDowned;
                if (healthPct == 0)
                    healthPct = 1;
            }
            if (c.behaviour == Character.Behaviour.escaping
                || c.aggressiveness == Aggressiveness.flee
                || c.aggressiveness == Aggressiveness.fleeAndDespawn
                || c.wantToDespawn)
                flags |= EntitySnapshotNet.FlagFleeing;
            flags = (byte)((flags & 0x0F) | EntitySnapshotNet.PackBehaviour(c.behaviour));

            byte flags2 = 0;
            if (animating) flags2 |= EntitySnapshotNet.Flag2Animating;
            Flier flier = c.flier;
            if (flier != null)
            {
                // Altitude follows from these on the client (vanilla processAnims constants).
                if (flier.inFlight) flags2 |= EntitySnapshotNet.Flag2InFlight;
                if (flier.diving) flags2 |= EntitySnapshotNet.Flag2Diving;
            }
            byte loop = Audio.EntityLoopSync.HostSlot(c);

            string prevClip = null;
            float prevClipT = 0f;
            if (_clipLogs.TryGetValue(id, out ClipStartLog log))
                log.TryIntermediate(c.GetInstanceID(), clip, out prevClip, out prevClipT);

            // Cheap dirty gate before Unity name / PrefabPathComponent work. The frame alone is
            // no change: the client runs the clip itself once it has it.
            if (!force && prevClip == null && _lastSent.TryGetValue(id, out EntitySnapshotNet last)
                && last.PosX == cPos.x && last.PosY == cPos.y && last.PosZ == cPos.z
                && last.RotY == rot.y
                && last.Alive == alive
                && last.HealthPct == healthPct
                && last.Flags == flags
                && (last.Flags2 & ~EntitySnapshotNet.Flag2PrevClip) == flags2
                && last.Loop == loop
                && string.Equals(last.Clip, clip, StringComparison.Ordinal))
            {
                return false;
            }

            if (!_descriptors.TryGetValue(id, out Descriptor desc) || desc.Owner != c)
            {
                string entityName = c.name ?? "";
                if (entityName.EndsWith("(Clone)", StringComparison.Ordinal))
                    entityName = entityName.Substring(0, entityName.Length - 7);
                string prefabPath = "";
                var ppc = c.GetComponent<PrefabPathComponent>();
                if (ppc != null && ppc.Path != null)
                    prefabPath = ppc.Path;
                // The vanilla save id (Core.addToSaveable / a save load): the same on every peer
                // for a creature of the shared save. A dream copy is not saved (dontSave) and
                // carries its original's id, so it sends none.
                int saveId = 0;
                SaveableObject so = c.saveableObject;
                if (so != null && so.assigned && !so.dontSave && so.uniqueId > 0)
                    saveId = so.uniqueId;
                desc = new Descriptor
                {
                    Owner = c,
                    Name = entityName,
                    PrefabPath = prefabPath,
                    SaveId = saveId,
                    SendsLeft = DescriptorSends
                };
            }
            bool withDescriptor = _fullResyncTick || desc.SendsLeft > 0;
            if (withDescriptor && desc.SendsLeft > 0)
                desc.SendsLeft--;
            _descriptors[id] = desc;

            if (prevClip != null)
                flags2 |= EntitySnapshotNet.Flag2PrevClip;
            snap = new EntitySnapshotNet
            {
                Index = id,
                PosX = cPos.x,
                PosY = cPos.y,
                PosZ = cPos.z,
                RotY = rot.y,
                Clip = clip,
                ClipFrame = clipFrame,
                Alive = alive,
                HealthPct = healthPct,
                HasDescriptor = withDescriptor,
                EntityName = desc.Name,
                PrefabPath = desc.PrefabPath,
                SaveId = desc.SaveId,
                Flags = flags,
                Flags2 = flags2,
                Loop = loop,
                PrevClip = prevClip,
                PrevClipAgeMs = prevClip != null
                    ? (ushort)Mathf.Clamp((_batchHostTime - prevClipT) * 1000f, 0f, ushort.MaxValue)
                    : (ushort)0
            };
            return true;
        }

        /// <summary>Host with peers: body clip starts are logged (<see cref="NoteClipStart"/>).</summary>
        private static bool _clipTrackingOn;
        internal static bool ClipTrackingOn => _clipTrackingOn;
        /// <summary>Animator → the Character it is the body of (null: not a body animator).</summary>
        private static readonly Dictionary<tk2dSpriteAnimator, Character> _clipOwners = new Dictionary<tk2dSpriteAnimator, Character>(128);
        private static readonly Dictionary<short, ClipStartLog> _clipLogs = new Dictionary<short, ClipStartLog>(128);

        /// <summary>
        /// A body animator started a clip other than the one it showed (from the tk2d Play
        /// hook). The snapshot carries the one a client would miss between two sends.
        /// </summary>
        internal static void NoteClipStart(tk2dSpriteAnimator anim, string clipName)
        {
            if (!_clipOwners.TryGetValue(anim, out Character c))
            {
                c = anim.GetComponent<Character>();
                if (c != null && !ReferenceEquals(c.animator, anim))
                    c = null;
                if (_clipOwners.Count >= 4096)
                    _clipOwners.Clear();
                _clipOwners[anim] = c;
            }
            if (c == null)
                return;
            if (!CharacterTracker.TryGetStableId(c, out short id) || id == 0)
                return;
            _clipLogs.TryGetValue(id, out ClipStartLog log);
            log.Note(c.GetInstanceID(), clipName, HostNow);
            _clipLogs[id] = log;
        }

        private static void MarkClipSent(short id, string clip)
        {
            Character c = CharacterTracker.FindByStableId(id);
            if (c == null)
                return;
            _clipLogs.TryGetValue(id, out ClipStartLog log);
            log.MarkSent(c.GetInstanceID(), clip, _batchHostTime);
            _clipLogs[id] = log;
        }

        private static int _sendCount; // process-scoped: monotonic send counter
        private static int _fullResyncCounter;
        private static bool _paused;
        private static readonly Dictionary<short, string> _prevClip = new Dictionary<short, string>(128);
        private static readonly Dictionary<short, bool> _prevAlive = new Dictionary<short, bool>(128);

        /// <summary>Pauses broadcasting (positions frozen on receiver).</summary>
        public static void Pause() => _paused = true;

        /// <summary>Resumes broadcasting.</summary>
        public static void Resume() => _paused = false;

        /// <summary>Stops broadcasting and clears dirty cache.</summary>
        public static void Stop()
        {
            _sendTimer = 0f;
            _epoch = -1;
            _lastTickAt = -1f;
            _inRange.Clear();
            _clipTrackingOn = false;
            _clipOwners.Clear();
            _clipLogs.Clear();
            _nextSnapshotSequence = 0;
            _lastSent.Clear();
            _descriptors.Clear();
            _settle.Clear();
            _settleNext.Clear();
            _fullResyncTick = false;
            _tick = 0;
            _batchHostTime = 0f;
            _prevClip.Clear();
            _prevAlive.Clear();
            _fullResyncCounter = 0;
            _paused = false;
            _scanStart = 0;
        }

        private static bool HasChanged(EntitySnapshotNet last, EntitySnapshotNet current)
        {
            return last.PosX != current.PosX || last.PosY != current.PosY || last.PosZ != current.PosZ
                || last.RotY != current.RotY
                || last.Clip != current.Clip
                || last.Alive != current.Alive || last.HealthPct != current.HealthPct
                || last.EntityName != current.EntityName || last.PrefabPath != current.PrefabPath
                || last.Flags != current.Flags
                || ((last.Flags2 ^ current.Flags2) & ~EntitySnapshotNet.Flag2PrevClip) != 0
                || current.PrevClip != null
                || last.Loop != current.Loop;
            // HasDescriptor is transport only: a re-sent name is not a change. The clip frame
            // is not one either (the client plays the clip on); a pass-through clip is.
        }
    }
}
