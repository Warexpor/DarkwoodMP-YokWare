using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    public static partial class WorldPhysicsSyncService
    {
        public static void ApplySnapshot(PhysicsStateMessage state, string fromPeer = "host")
        {
            int objApplied = 0, objSkipped = 0, objFailed = 0;
            bool clientRecv = fromPeer != null
                && fromPeer.Equals("host", System.StringComparison.OrdinalIgnoreCase)
                && ModRuntime.Network != null
                && ModRuntime.Network.Role == Networking.NetworkRole.Client;

            if (state.Objects != null)
            {
                int oc = state.EffectiveObjectCount;
                for (int oi = 0; oi < oc; oi++)
                {
                    WorldObjectState obj = state.Objects[oi];
                    if (string.IsNullOrEmpty(obj.Name)
                        || obj.Name == "Player"
                        || obj.Name == "PlayerLegs"
                        || obj.Name == "RemotePlayer"
                        || obj.Name.IndexOf("DoorSensor", StringComparison.Ordinal) >= 0)
                    {
                        objSkipped++;
                        continue;
                    }

                    // Client: skip far free-bodies (FindOrSpawn / full RB scan was dual-box thrash).
                    // Dream pads sit far from the overworld and remain in interest
                    // while dreaming (door-room props
                    // are often > ClientInterestDistance from spawn).
                    if (clientRecv)
                    {
                        Vector3 opos = new Vector3(obj.PosX, obj.PosY, obj.PosZ);
                        bool dreamPad = Dreams.Instance != null && Dreams.Instance.dreaming;
                        if (!dreamPad
                            && !Networking.ClientEntityInterpolationService.IsInClientInterest(opos))
                        {
                            objSkipped++;
                            continue;
                        }
                    }

                    GameObject go = FindOrSpawnObject(obj);

                    if (go == null)
                    {
                        objFailed++;
                        continue;
                    }

                    // Do not kinematic-lock or interpolate an in-flight
                    // match, flare, or molotov.
                    // Peer ThrowableSpawn already set landTarget + setFallSpeed + velocity.
                    if (IsInFlightThrownItem(go))
                    {
                        RemoveObjectFromInterpolation(go);
                        Rigidbody flyRb = go.GetComponent<Rigidbody>();
                        if (flyRb != null && flyRb.isKinematic)
                            flyRb.isKinematic = false;
                        int flyId = go.GetInstanceID();
                        _clientKinematic.Remove(flyId);
                        objSkipped++;
                        continue;
                    }

                    Vector3 pos = new Vector3(obj.PosX, obj.PosY, obj.PosZ);
                    Vector3 rot = new Vector3(obj.RotX, obj.RotY, obj.RotZ);

                    // When a client sends PhysicsState, the host applies it.
                    // For Rigidbody objects: use the existing interpolation system
                    // (smooth between 10 Hz updates) + isKinematic (prevents host
                    // physics from fighting).  The interpolation loop on the host
                    // never releases kinematic, so _clientKinematic handles that.
                    bool fromClient = fromPeer.Equals("client", StringComparison.OrdinalIgnoreCase);
                    if (fromClient)
                    {
                        Vector3 objPos = new Vector3(obj.PosX, obj.PosY, obj.PosZ);
                        Vector3 rotVec = new Vector3(obj.RotX, obj.RotY, obj.RotZ);

                        // E-drag owns this object via DragSync (30 Hz). PhysicsState must not
                        // also drive kinematic/interp or start body-push scrape,
                        // which fought
                        // DragSync and spammed NotifyBodyPushStarted (logs: 53 starts, thrash).
                        var netMgr = ModRuntime.Network as LanNetworkManager;
                        if (netMgr != null && !string.IsNullOrEmpty(obj.Name)
                            && (netMgr._dragClaims.ContainsKey(obj.Name)
                                || netMgr._remoteDragItemNames.Contains(obj.Name)))
                        {
                            objSkipped++;
                            continue;
                        }

                        _lastClientUpdateTime[go.GetInstanceID()] = Time.time;

                        int goId = go.GetInstanceID();
                        bool haveInterp = _objectInterp.TryGetValue(goId, out var existingInterp);
                        Rigidbody rb = null;
                        Item cachedItem = null;
                        if (haveInterp && existingInterp.CachedComps)
                        {
                            rb = existingInterp.CachedRb;
                            cachedItem = existingInterp.CachedItem;
                        }
                        if (rb == null)
                            rb = go.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            rb.isKinematic = true;

                            // Baseline: last interp target, else current host pose.
                            // A missing baseline produces no position delta and
                            // must not start body-push scrape.
                            Vector3 baseline = go.transform.position;
                            if (haveInterp)
                                baseline = existingInterp.TargetPos;
                            float posDelta = Vector3.Distance(baseline, objPos);
                            // After DragSync, first PhysicsState can report multi-meter jumps
                            // (interp target versus live pose), causing MOS start/stop
                            // thrash. Snap without sound.
                            const float BodyPushMaxArmDelta = 1.25f;
                            // Use a meaningful movement threshold so micro-jitter does not
                            // repeatedly start and stop MOS.
                            bool posChanged = posDelta >= 0.1f;
                            bool armScrape = posChanged && posDelta <= BodyPushMaxArmDelta;

                            // Always refresh kinematic hold while client reports the object.
                            // Gate only blocks brand-new sessions right after a clean stop.
                            bool gated = _clientKinematicGate.ContainsKey(goId);
                            if (!gated || posChanged)
                            {
                                if (gated && posChanged)
                                    _clientKinematicGate.Remove(goId);
                                _clientKinematic[goId] = (rb, Time.time + 0.5f, obj.Name);
                            }

                            // Scrape: arm on sane motion only; first quiet packet → stop decision *now*.
                            if (armScrape && !gated)
                            {
                                if (_bodyPushSoundActive.Add(obj.Name))
                                {
                                    ModRuntime.LegacyInfo("[SND] body-push start " + obj.Name + " d=" + posDelta.ToString("F3"));
                                    LanNetworkManager.NotifyBodyPushStarted(go);
                                }
                                else
                                {
                                    // Keep MOS alive mid-push (NoteMoving is idempotent / cancels fade).
                                    LanNetworkManager.NotifyBodyPushStarted(go);
                                }
                                _bodyPushSoundTimer[goId] = Time.time + BodyPushSoundHold;
                                _pushNameToGid[obj.Name] = goId;
                                _pushGidToName[goId] = obj.Name;
                                _pushStationaryCount[goId] = 0;
                            }
                            else if (posChanged && posDelta > BodyPushMaxArmDelta)
                            {
                                // Retarget after a hard drag jump without starting scrape.
                                if (_bodyPushSoundActive.Remove(obj.Name))
                                {
                                    ModRuntime.LegacyInfo("[SND] body-push skip jump d=" + posDelta.ToString("F3") + " " + obj.Name);
                                    LanNetworkManager.NotifyBodyPushStopped(obj.Name);
                                }
                                _pushStationaryCount[goId] = 0;
                            }
                            else if (!gated && _bodyPushSoundActive.Contains(obj.Name))
                            {
                                // Require two quiet ticks before stopping to avoid rapid rearming.
                                if (!_pushStationaryCount.TryGetValue(goId, out int quietN))
                                    quietN = 0;
                                quietN++;
                                _pushStationaryCount[goId] = quietN;
                                if (quietN >= 2 && _bodyPushSoundActive.Remove(obj.Name))
                                {
                                    ModRuntime.LegacyInfo("[SND] body-push stop (quiet) " + obj.Name);
                                    LanNetworkManager.NotifyBodyPushStopped(obj.Name);
                                    _bodyPushSoundTimer.Remove(goId);
                                    _pushNameToGid.Remove(obj.Name);
                                    _pushGidToName.Remove(goId);
                                    _pushStationaryCount.Remove(goId);
                                }
                            }

                            // Snap only on a hard desync. Routine pushes must use
                            // interpolation to avoid visibly snappy host motion.
                            if (posDelta >= ClientPushSnapDistance)
                            {
                                rb.position = objPos;
                                rb.rotation = Quaternion.Euler(rotVec);
                                go.transform.position = objPos;
                                go.transform.rotation = Quaternion.Euler(rotVec);
                                _objectInterp.Remove(goId);
                            }
                            SetObjectTarget(go, objPos, rotVec, ClientPushInterpDuration);
                        }
                        else
                        {
                            go.transform.position = objPos;
                            go.transform.rotation = Quaternion.Euler(rotVec);
                            _objectInterp.Remove(go.GetInstanceID());
                        }
                    }
                    else
                    {
                        // Local pusher/dragger owns this free-body. Host snapshot echo must not:
                        // - SetObjectTarget, which can fight local physics while the object is held.
                        // - NoteMoving / ForceStop (double scrape / kill native mid-push)
                        var echoNet = ModRuntime.Network as LanNetworkManager;
                        bool localDragClaim = echoNet != null && !string.IsNullOrEmpty(obj.Name)
                            && echoNet._dragClaims.TryGetValue(obj.Name, out int claimPid)
                            && claimPid == echoNet.LocalPlayerId;
                        bool localDraggingItem = false;
                        try
                        {
                            Player lp = Player.Instance;
                            localDraggingItem = lp != null && lp.dragging && lp.itemBeingDragged != null
                                && string.Equals(lp.itemBeingDragged.gameObject.name, obj.Name,
                                    StringComparison.Ordinal);
                        }
                        catch { /* dismantled */ }
                        // The local pusher owns native scrape audio, so do not
                        // start MOS from the host echo.
                        // Do NOT gate on "RB non-kinematic" alone: host-pushed lamps stay
                        // non-kinematic on the client and that silenced observer scrape.
                        bool clientLocalFreeBody = echoNet != null
                            && echoNet.Role == NetworkRole.Client
                            && !string.IsNullOrEmpty(obj.Name)
                            && (ItemMovingSoundHelper.IsLocalPushOrDragOwner(obj.Name)
                                || ItemMovingSoundHelper.HasRecentPushAuthority(obj.Name)
                                || ItemMovingSoundHelper.HasRecentClientPhysicsSent(obj.Name));
                        if (localDragClaim
                            || localDraggingItem
                            || clientLocalFreeBody
                            || (!string.IsNullOrEmpty(obj.Name)
                                && ItemMovingSoundHelper.IsLocalPushOrDragOwner(obj.Name)))
                        {
                            RemoveObjectFromInterpolation(go);
                            int localId = go.GetInstanceID();
                            Rigidbody localRb = null;
                            if (_objectInterp.TryGetValue(localId, out var localInterp) && localInterp.CachedComps)
                                localRb = localInterp.CachedRb;
                            if (localRb == null)
                                localRb = go.GetComponent<Rigidbody>();
                            if (localRb != null && localRb.isKinematic)
                                localRb.isKinematic = false;
                            if (ItemMovingSoundHelper.IsRemoteScrape(obj.Name)
                                || MovingObjectSoundService.IsPlaying(obj.Name)
                                || MovingObjectSoundService.IsFading(obj.Name))
                                MovingObjectSoundService.StopImmediate(obj.Name);
                            objSkipped++;
                            continue;
                        }

                        // Body-push scrape: shared loop service (also used by E-drag).
                        if (!string.IsNullOrEmpty(obj.Name))
                        {
                            int __gid = go.GetInstanceID();
                            Item __ic = null;
                            if (_objectInterp.TryGetValue(__gid, out var __eiCached) && __eiCached.CachedComps)
                                __ic = __eiCached.CachedItem;
                            if (__ic == null)
                                __ic = go.GetComponent<Item>();
                            if (__ic != null)
                            {
                                bool __hi = _objectInterp.TryGetValue(__gid, out var __ei);
                                float __pd = __hi
                                    ? Vector3.Distance(__ei.TargetPos, pos)
                                    : Vector3.Distance(go.transform.position, pos);

                                var __isnd = __ic.GetComponent<ItemSounds>();
                                if (__isnd != null)
                                {
                                    if (__pd >= 0.1f)
                                    {
                                        // Remote host→client motion: MOS only (MarkRemoteScrape inside NoteMoving).
                                        MovingObjectSoundService.NoteMoving(__ic.gameObject, obj.Name, __isnd);
                                        _pushNameToGid[obj.Name] = __gid;
                                        _pushGidToName[__gid] = obj.Name;
                                        _lastPushSoundTime[__gid] = Time.time;
                                        _pushStationaryCount[__gid] = 0;
                                    }
                                    else if (_lastPushSoundTime.ContainsKey(__gid)
                                        || ItemMovingSoundHelper.IsRemoteScrape(obj.Name))
                                    {
                                        // Need two quiet ticks before soft-stop (avoids scrape chatter).
                                        if (!_pushStationaryCount.TryGetValue(__gid, out int quietN))
                                            quietN = 0;
                                        quietN++;
                                        _pushStationaryCount[__gid] = quietN;
                                        if (quietN >= 2)
                                        {
                                            ItemMovingSoundHelper.SoftStopNetwork(obj.Name);
                                            _lastPushSoundTime.Remove(__gid);
                                            _pushStationaryCount.Remove(__gid);
                                            _pushNameToGid.Remove(obj.Name);
                                            _pushGidToName.Remove(__gid);
                                        }
                                    }
                                }
                            }
                        }
                        // Fixed world lamps must not be kinematic-locked because
                        // that blocks client movement.
                        if (IsSceneFixedLightItem(go))
                        {
                            RepairSceneFixedLightPhysics(go);
                            objSkipped++;
                            continue;
                        }
                        SetObjectTarget(go, pos, rot);
                    }
                    objApplied++;
                }
            }

            // Log summary every 15 applies to avoid spamming
            if ((objApplied > 0 || objFailed > 0) && ++_objApplyLogCounter % 15 == 0 && ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[ObjectApply] applied=" + objApplied + " skipped=" + objSkipped + " failed=" + objFailed + " from " + fromPeer);

            int doorApplied = 0, doorFailed = 0, doorSkipped = 0;
            if (state.Doors != null)
            {
                try
                {
                    TraverseHack.ApplyingFromNetwork = true;
                    int dc = state.EffectiveDoorCount;
                    for (int di = 0; di < dc; di++)
                    {
                        DoorState ds = state.Doors[di];
                        Vector3 doorPos = new Vector3(ds.PosX, ds.PosY, ds.PosZ);
                        Door door = FindDoorByPos(doorPos);
                        if (door == null)
                        {
                            doorFailed++;
                            continue;
                        }

                        bool currentOpened = TraverseHack.ReadDoorOpened(door);
                        if (currentOpened == ds.Opened)
                        {
                            doorSkipped++;
                            continue;
                        }

                        if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo("[DoorApply] " + door.name + " " + (ds.Opened ? "OPEN" : "CLOSE") + " from " + fromPeer);
                        TraverseHack.SetDoorOpened(door, ds.Opened, new Vector3(ds.OpenerPosX, ds.OpenerPosY, ds.OpenerPosZ), ds.OpenForce, ds.BodyRotY, ds.AngVelX, ds.AngVelY, ds.AngVelZ);
                        doorApplied++;
                    }
                }
                finally
                {
                    TraverseHack.ApplyingFromNetwork = false;
                }
                if (doorApplied > 0 || doorFailed > 0)
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo("[DoorRecv] applied=" + doorApplied + " failed=" + doorFailed + " skipped=" + doorSkipped + " from " + fromPeer);
            }

            int trapApplied = 0, trapSkipped = 0;
            if (state.Traps != null)
            {
                try
                {
                    TraverseHack.ApplyingFromNetwork = true;
                    int tc = state.EffectiveTrapCount;
                    for (int ti = 0; ti < tc; ti++)
                    {
                        TrapState ts = state.Traps[ti];
                        Vector3 tPos = new Vector3(ts.PosX, ts.PosY, ts.PosZ);
                        GameObject go = ts.TrapNetId > 0
                            ? TrapNetworkId.FindById(ts.TrapNetId)
                            : null;
                        if (go == null)
                            go = FindTrapByPos(tPos);
                        if (go == null)
                        {
                            TrapNetworkId.QueuePending(
                                ts.TrapNetId, tPos, ts.Triggered,
                                silentDisarm: ts.OccupantPlayerId == TrapState.OccupantSilentDisarm);
                            trapSkipped++;
                            continue;
                        }

                        if (ts.TrapNetId > 0)
                            TrapNetworkId.Ensure(go, ts.TrapNetId);
                        else if (ModRuntime.Network is LanNetworkManager n
                                 && n.Role == NetworkRole.Host)
                        {
                            TrapNetworkId.GetOrMintHost(go);
                        }

                        bool silent = ts.OccupantPlayerId == TrapState.OccupantSilentDisarm;
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo("[TrapApply] " + go.name + " id=" + ts.TrapNetId
                                + " at " + tPos + " triggered=" + ts.Triggered + " silent=" + silent);
                        ApplyTrapState(go, ts.Triggered, silentDisarm: silent);
                        trapApplied++;
                    }
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogError("[TrapApply] Exception: " + ex);
                }
                finally
                {
                    TraverseHack.ApplyingFromNetwork = false;
                }
                if (trapApplied > 0 || trapSkipped > 0)
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo("[TrapRecv] applied=" + trapApplied + " skipped=" + trapSkipped + " from " + fromPeer);
            }

            if (state.Generators != null)
            {
                try
                {
                    // Guard turnOn/turnOff/setLowPower patches from re-sending
                    TraverseHack.ApplyingFromNetwork = true;
                    int gc = state.EffectiveGeneratorCount;
                    for (int gi = 0; gi < gc; gi++)
                    {
                        GeneratorState gs = state.Generators[gi];
                        Vector3 gPos = new Vector3(gs.PosX, gs.PosY, gs.PosZ);
                        Generator gen = FindGeneratorByPos(gPos);
                        if (gen == null)
                            gen = SpawnGenerator(gs);
                        if (gen == null) continue;

                        ApplyGeneratorState(gen, gs.IsOn, gs.Fuel, gs.LowPower);
                    }
                }
                finally
                {
                    TraverseHack.ApplyingFromNetwork = false;
                }
            }
        }
    }
}
