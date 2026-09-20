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
                foreach (WorldObjectState obj in state.Objects)
                {
                    if (obj.Name == "Player" || obj.Name == "PlayerLegs" || obj.Name == "RemotePlayer" || obj.Name.Contains("DoorSensor"))
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

                        string ck = obj.Name + "_" + go.GetInstanceID();
                        _lastClientUpdateTime[ck] = Time.time;

                        Rigidbody rb = go.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            int goId = go.GetInstanceID();
                            rb.isKinematic = true;

                            // Baseline: last interp target, else current host pose.
                            // A missing baseline produces no position delta and
                            // must not start body-push scrape.
                            Vector3 baseline = go.transform.position;
                            if (_objectInterp.TryGetValue(goId, out var existingInterp))
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
                            Rigidbody localRb = go.GetComponent<Rigidbody>();
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
                            var __ic = go.GetComponent<Item>();
                            if (__ic != null)
                            {
                                int __gid = go.GetInstanceID();
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
                    foreach (DoorState ds in state.Doors)
                    {
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
                    foreach (TrapState ts in state.Traps)
                    {
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
                    foreach (GeneratorState gs in state.Generators)
                    {
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

        /// <summary>Removes an object from the interpolation dictionary so it stops being smoothed.</summary>
        /// <param name="go">The GameObject to remove.</param>
        public static void RemoveObjectFromInterpolation(GameObject go)
        {
            if (go == null) return;
            _objectInterp.Remove(go.GetInstanceID());
        }

        /// <summary>
        /// After remote E-drag ends (or local drag ends on host): drop client-push
        /// kinematic hold + interp by object name so the free body is draggable again.
        /// Without this, host kept isKinematic / claim side-effects and could not re-grab.
        /// </summary>
        public static void ReleaseClientPushHoldByName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return;

            List<int> dropIds = null;
            foreach (var kv in _clientKinematic)
            {
                if (string.Equals(kv.Value.objName, objectName, StringComparison.OrdinalIgnoreCase))
                {
                    if (dropIds == null) dropIds = new List<int>();
                    dropIds.Add(kv.Key);
                }
            }

            if (dropIds != null)
            {
                for (int i = 0; i < dropIds.Count; i++)
                {
                    int id = dropIds[i];
                    if (_clientKinematic.TryGetValue(id, out var kin) && kin.rb != null && kin.rb.isKinematic)
                        kin.rb.isKinematic = false;
                    _clientKinematic.Remove(id);
                    _clientKinematicGate[id] = Time.time;
                    _objectInterp.Remove(id);
                    _bodyPushSoundActive.Remove(objectName);
                    _bodyPushSoundTimer.Remove(id);
                    if (_pushGidToName.TryGetValue(id, out string n) && string.Equals(n, objectName, StringComparison.OrdinalIgnoreCase))
                        _pushGidToName.Remove(id);
                    _pushNameToGid.Remove(objectName);
                }
            }

            // Name-only cleanup when gid maps already gone.
            _bodyPushSoundActive.Remove(objectName);
            _pushNameToGid.Remove(objectName);
        }

        /// <summary>
        /// Finds a world object (mushroom, exp item, shiny stone, etc.) by position and destroys it.
        /// Used when the remote peer reports that they harvested/picked up the object.
        /// </summary>
        public static void DestroyObjectByPos(Vector3 pos, string objectName)
        {
            // AudioObject removal requests are ephemeral sound effects, not actual traps
            if (!string.IsNullOrEmpty(objectName) && objectName.ToLowerInvariant().Contains("audioobject"))
                return;

            // Debounce before scene queries to avoid duplicate removal work.
            // from disarm was repeatedly scanning and then throwing on DestroyImmediate+name.
            int posKey = MakePosNameKey(pos.x, pos.y, pos.z, objectName);
            float now = Time.time;
            if (_destroyDebounce.TryGetValue(posKey, out float lastDestroy)
                && (now - lastDestroy) < DestroyDebounceTime)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[ObjectDestroy] debounced duplicate at " + pos);
                return;
            }

            string needle = string.IsNullOrEmpty(objectName) ? null : objectName.ToLowerInvariant();
            GameObject best = null;
            float bestDistSq = float.MaxValue;
            const float overlapR = 8f;
            const float scanR = 12f;
            float overlapSq = overlapR * overlapR;
            float scanSq = scanR * scanR;

            // 1) OverlapSphere: any nearby root matching name/type OR harvest keywords.
            Collider[] nearby = Physics.OverlapSphere(pos, overlapR);
            for (int i = 0; i < nearby.Length; i++)
            {
                Collider col = nearby[i];
                if (col == null) continue;
                GameObject root = col.gameObject;
                if (root == null) continue;
                Rigidbody rb = col.attachedRigidbody;
                if (rb != null && rb.gameObject != null) root = rb.gameObject;

                // Prefer Item / itemInv Inventory roots for world pickups (shiny stone, etc.).
                Item item = col.GetComponentInParent<Item>();
                if (item != null && item.gameObject != null) root = item.gameObject;
                else
                {
                    Inventory inv = col.GetComponentInParent<Inventory>();
                    if (inv != null && inv.invType == Inventory.InvType.itemInv && inv.gameObject != null)
                        root = inv.gameObject;
                }

                if (root == null) continue;
                if (!ShouldDestroyWorldPickup(root, needle))
                    continue;

                float dSq = XzDistSq(root.transform.position, pos);
                if (dSq < bestDistSq && dSq <= overlapSq)
                {
                    bestDistSq = dSq;
                    best = root;
                }
            }

            // 2) Scene scan by display name / invItem.type near pos (no collider items).
            // Skip the scene-wide search for known trap names after the overlap
            // query. A missing trap has already been removed.
            bool trapNeedle = needle != null
                && (needle.Contains("trap") || needle.Contains("bear") || needle.Contains("snap"));
            if (best == null && needle != null && !trapNeedle)
            {
                Item[] items = UnityEngine.Object.FindObjectsOfType<Item>();
                for (int i = 0; i < items.Length; i++)
                {
                    Item it = items[i];
                    if (it == null) continue;
                    GameObject go = it.gameObject;
                    if (go == null || !go.scene.IsValid()) continue;
                    string itemType = it.invItem != null ? it.invItem.type : null;
                    if (!NameOrItemTypeMatches(go, itemType, needle)) continue;
                    float dSq = XzDistSq(go.transform.position, pos);
                    if (dSq > scanSq) continue;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        best = go;
                    }
                }

                if (best == null)
                {
                    Inventory[] invs = UnityEngine.Object.FindObjectsOfType<Inventory>();
                    for (int i = 0; i < invs.Length; i++)
                    {
                        Inventory inv = invs[i];
                        if (inv == null || inv.invType != Inventory.InvType.itemInv) continue;
                        GameObject go = inv.gameObject;
                        if (go == null || !go.scene.IsValid()) continue;
                        string slotType = FirstSlotType(inv);
                        bool nameOk = NameOrItemTypeMatches(go, slotType, needle);
                        float dSq = XzDistSq(go.transform.position, pos);
                        if (!nameOk && dSq > 4f * 4f) continue;
                        if (!nameOk && string.IsNullOrEmpty(slotType) && dSq <= 4f * 4f)
                        { /* empty itemInv near pickup pos */ }
                        else if (!nameOk) continue;
                        if (dSq > scanSq) continue;
                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            best = go;
                        }
                    }
                }
            }

            // Last resort: GameObject.Find, used only when the name is unique.
            if (best == null && !string.IsNullOrEmpty(objectName))
            {
                GameObject named = GameObject.Find(objectName);
                if (named != null && XzDistSq(named.transform.position, pos) < 12f * 12f)
                {
                    best = named;
                    bestDistSq = XzDistSq(named.transform.position, pos);
                }
            }

            if (best == null)
            {
                // Still claim debounce so follow-up removes of an already-gone trap skip the scan.
                _destroyDebounce[posKey] = now;
                ModRuntime.LegacyInfo("[ObjectDestroy] miss name=\"" + (objectName ?? "") + "\" at " + pos);
                return;
            }

            _destroyDebounce[posKey] = now;

            string destroyedName = objectName;
            try
            {
                if (best != null)
                    destroyedName = best.name;
            }
            catch { /* destroyed Unity object */ }

            RemoveObjectFromInterpolation(best);
            try
            {
                if (best.transform != null)
                    Core.RemovePooledPrefab(best.transform);
            }
            catch { /* ignore */ }
            try
            {
                TraverseHack.ApplyingFromNetwork = true;
                UnityEngine.Object.DestroyImmediate(best);
            }
            finally { TraverseHack.ApplyingFromNetwork = false; }
            ModRuntime.LegacyInfo("[ObjectDestroy] destroyed \"" + (destroyedName ?? "") + "\" at " + pos
                + " d=" + Mathf.Sqrt(bestDistSq).ToString("F1"));
        }

        private static float XzDistSq(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        private static string FirstSlotType(Inventory inv)
        {
            if (inv?.slots == null || inv.slots.Count == 0) return null;
            InvItemClass c = inv.slots[0].invItem;
            return InvItemClass.isNull(c) ? null : c.type;
        }

        private static bool NameOrItemTypeMatches(GameObject go, string itemType, string needleLower)
        {
            if (go == null || string.IsNullOrEmpty(needleLower)) return false;
            string n;
            try { n = go.name.ToLowerInvariant(); }
            catch { return false; }
            string bare = n.Replace("(clone)", "").Trim();
            if (n == needleLower || n.Contains(needleLower) || needleLower.Contains(bare))
                return true;
            if (!string.IsNullOrEmpty(itemType)
                && itemType.Equals(needleLower, System.StringComparison.OrdinalIgnoreCase))
                return true;
            // Display name "Scrap metal" vs type scrap_metal / scrapMetal
            if (!string.IsNullOrEmpty(itemType))
            {
                string t = itemType.ToLowerInvariant();
                string tSpaced = t.Replace('_', ' ');
                string needleSpaced = needleLower.Replace('_', ' ');
                if (n.Contains(tSpaced) || needleSpaced.Contains(tSpaced) || tSpaced.Contains(needleSpaced))
                    return true;
                // Localized display: Language.Get(type + "_name") == "Scrap metal"
                try
                {
                    string display = Language.Get(itemType + "_name", "Items");
                    if (!string.IsNullOrEmpty(display)
                        && display.Equals(needleLower, System.StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (!string.IsNullOrEmpty(display)
                        && display.ToLowerInvariant() == needleSpaced)
                        return true;
                }
                catch { /* Language table may not be ready */ }
            }
            return false;
        }

        private static bool ShouldDestroyWorldPickup(GameObject root, string needleLower)
        {
            if (root == null) return false;
            string rootName;
            try { rootName = root.name.ToLowerInvariant(); }
            catch { return false; }
            if (rootName.Contains("mushroom") || rootName.Contains("exp") || rootName.Contains("bio")
                || rootName.Contains("trap") || rootName.Contains("bear") || rootName.Contains("snap") || rootName.Contains("animal")
                || rootName.Contains("barrel") || rootName.Contains("tank") || rootName.Contains("glass") || rootName.Contains("chain")
                || rootName.Contains("infect"))
                return true;

            if (needleLower == null) return false;

            Item item = root.GetComponent<Item>() ?? root.GetComponentInParent<Item>();
            if (item != null)
            {
                string t = item.invItem != null ? item.invItem.type : null;
                if (NameOrItemTypeMatches(item.gameObject, t, needleLower))
                    return true;
            }

            Inventory inv = root.GetComponent<Inventory>() ?? root.GetComponentInParent<Inventory>();
            if (inv != null && inv.invType == Inventory.InvType.itemInv
                && NameOrItemTypeMatches(inv.gameObject, FirstSlotType(inv), needleLower))
                return true;

            return NameOrItemTypeMatches(root, null, needleLower);
        }

        /// <summary>
        /// After a remote peer emptied an itemInv <b>world pickup</b> (shiny stone etc.),
        /// destroy the visual GO if slots are empty.
        /// Furniture containers also use <c>itemInv</c>; destroy only dropped pickups.
        /// Vanilla only auto-destroys emptied <see cref="Item.isDroppedItem"/> pickups.
        /// </summary>
        public static void DestroyEmptyItemInvAt(Vector3 pos)
        {
            Inventory inv = WorldQueryHelper.FindInventoryByPos(pos, 3f);
            if (inv == null || inv.invType != Inventory.InvType.itemInv) return;

            // Wardrobes / chests / desks share itemInv with ground pickups. Only
            // destroy emptied dropped-item pickups (getDroppedItem parity).
            Item item = inv.GetComponent<Item>() ?? inv.GetComponentInParent<Item>();
            if (item == null || !item.isDroppedItem)
                return;

            if (inv.slots != null)
            {
                for (int i = 0; i < inv.slots.Count; i++)
                {
                    if (inv.slots[i] != null && !InvItemClass.isNull(inv.slots[i].invItem))
                        return; // still has loot
                }
            }
            DestroyObjectByPos(inv.transform.position, inv.name);
        }

        /// <summary>
        /// Sets a new position/rotation target for an object and resets the interpolation
        /// state so it smoothly moves from its current position to the target over <paramref name="durationSec"/>.
        /// </summary>
        private static void SetObjectTarget(GameObject go, Vector3 targetPos, Vector3 targetRot, float durationSec = -1f)
        {
            int id = go.GetInstanceID();
            float now = Time.time;
            float duration = durationSec > 0.001f ? durationSec : InterpFixedDuration;

            if (_objectInterp.TryGetValue(id, out var state))
            {
                float dur = state.TargetTime - state.PrevTime;
                if (dur > 0.001f)
                {
                    // Catch up the previous state to the current render-time lerp position
                    float t = Mathf.Clamp01((now - state.PrevTime) / dur);
                    state.PrevPos = Vector3.Lerp(state.PrevPos, state.TargetPos, t);
                    Quaternion prevRotQ = Quaternion.Euler(state.PrevRot);
                    Quaternion targetRotQ = Quaternion.Euler(state.TargetRot);
                    state.PrevRot = Quaternion.Slerp(prevRotQ, targetRotQ, t).eulerAngles;
                }
                else
                {
                    state.PrevPos = state.TargetPos;
                    state.PrevRot = state.TargetRot;
                }
            }
            else
            {
                state.Target = go;
                // Prefer live rigidbody pose (kinematic drive) over transform if available.
                Rigidbody rb0 = go.GetComponent<Rigidbody>();
                if (rb0 != null)
                {
                    state.PrevPos = rb0.position;
                    state.PrevRot = rb0.rotation.eulerAngles;
                }
                else
                {
                    state.PrevPos = go.transform.position;
                    state.PrevRot = go.transform.eulerAngles;
                }
            }

            state.Target = go;
            state.TargetPos = targetPos;
            state.TargetRot = targetRot;
            state.PrevTime = now;
            state.TargetTime = now + duration;
            _objectInterp[id] = state;

            if (IsSceneFixedLightItem(go))
                return;

            // Lock to the host position during active sync to prevent proxy collisions.
            // on the client from pushing the object away from the host's position.
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                // Host must stay kinematic too while interpolating client-pushed free-bodies.
                rb.isKinematic = true;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>
        /// Wall and fixed lamps use LightState for on/off; PhysicsState must not
        /// kinematic-lock (client walk-blocker).
        /// Floor / pushable lamps (<c>draggable</c> or ItemSounds moving scrape) are
        /// free bodies: stream pose so observers hear body-push MOS (DragSync already
        /// covered E-drag). Blanket <c>isLight</c> skip made Lamp_old_yellow_01 silent
        /// on push while chairs/wardrobe scraped normally.
        /// </summary>
        private static bool IsSceneFixedLightItem(GameObject go)
        {
            if (go == null) return false;
            Item item = go.GetComponent<Item>();
            // Movable lamps: same free-body path as stools / wardrobes.
            if (item != null && item.draggable)
                return false;
            ItemSounds sounds = go.GetComponent<ItemSounds>();
            if (sounds != null
                && (!string.IsNullOrEmpty(sounds.movingSound)
                    || !string.IsNullOrEmpty(sounds.movingSound_grass)))
                return false;

            if (item != null && item.isLight)
                return true;
            if (go.GetComponent<ItemLight>() != null)
                return true;
            string n = go.name ?? "";
            if (n.IndexOf("Lamp", StringComparison.OrdinalIgnoreCase) >= 0
                && (n.IndexOf("dream", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("switch", StringComparison.OrdinalIgnoreCase) >= 0
                    || (item != null && item.isLight)))
                return true;
            return false;
        }

        /// <summary>
        /// Drop kinematic/interp hold. Collider isTrigger is owned by host
        /// <c>DreamPropCollider</c> parity (GE isColliderTrigger), not guessed here.
        /// </summary>
        private static void RepairSceneFixedLightPhysics(GameObject go)
        {
            if (go == null) return;
            RemoveObjectFromInterpolation(go);
            Rigidbody lampRb = go.GetComponent<Rigidbody>();
            if (lampRb != null && lampRb.isKinematic)
                lampRb.isKinematic = false;
        }

        /// <summary>
        /// Look up a GameObject by name, then by proximity, then by spawning it
        /// from the <see cref="ItemsDatabase"/> if the <see cref="WorldObjectState.ItemType"/>
        /// is known.  Returns null only when every strategy fails.
        /// </summary>
        private static GameObject FindOrSpawnObject(WorldObjectState obj)
        {
            Vector3 targetPos = new Vector3(obj.PosX, obj.PosY, obj.PosZ);

            // Strategy 1: overlap sphere near the reported position (avoids teleporting
            // objects with non-unique names because GameObject.Find can match any instance)
            Collider[] nearby = Physics.OverlapSphere(targetPos, 1.5f);
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null || nearby[i].attachedRigidbody == null) continue;
                GameObject candidate = nearby[i].attachedRigidbody.gameObject;
                if (candidate.name != obj.Name) continue;
                if (candidate.GetComponent<Character>() != null) continue;
                // Skip objects handled by dedicated sync systems
                if (candidate.GetComponent<DroppedItemIdentifier>() != null) continue;
                if (candidate.GetComponent<DeathDrop>() != null) continue;
                return candidate;
            }

            // Strategy 1b: wider sphere before full-scene scan (client stutter when host
            // pushes objects away and can miss a small OverlapSphere query.
            {
                Collider[] wide = Physics.OverlapSphere(targetPos, 15f);
                GameObject bestWide = null;
                float bestWideDist = float.MaxValue;
                for (int i = 0; i < wide.Length; i++)
                {
                    if (wide[i] == null || wide[i].attachedRigidbody == null) continue;
                    GameObject candidate = wide[i].attachedRigidbody.gameObject;
                    if (candidate.name != obj.Name) continue;
                    if (candidate.GetComponent<Character>() != null) continue;
                    if (candidate.GetComponent<DroppedItemIdentifier>() != null) continue;
                    if (candidate.GetComponent<DeathDrop>() != null) continue;
                    float d = Vector3.Distance(candidate.transform.position, targetPos);
                    if (d < bestWideDist)
                    {
                        bestWideDist = d;
                        bestWide = candidate;
                    }
                }
                if (bestWide != null)
                    return bestWide;
            }

            // Strategy 2: rate-limited full Rigidbody scan (scene-wide FindObjectsOfType
            // every PhysicsState packet was a dual-box hitch source).
            float nowScan = Time.time;
            if (nowScan - _lastFullRbScanTime >= FullRbScanMinInterval)
            {
                _lastFullRbScanTime = nowScan;
                DWMPHorde.Logging.ClientPerfProbe.NoteFullRbScan();
                var footSw = System.Diagnostics.Stopwatch.StartNew();
                Rigidbody[] allRbs = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
                footSw.Stop();
                DWMPHorde.Logging.ClientPerfProbe.NoteFindObjectsOfType("Rigidbody", footSw.Elapsed.TotalMilliseconds);
                GameObject best = null;
                float bestDist = float.MaxValue;
                for (int i = 0; i < allRbs.Length; i++)
                {
                    Rigidbody rb = allRbs[i];
                    if (rb == null) continue;
                    if (rb.name != obj.Name) continue;
                    if (rb.GetComponent<Character>() != null) continue;
                    if (rb.GetComponent<DroppedItemIdentifier>() != null) continue;
                    if (rb.GetComponent<DeathDrop>() != null) continue;
                    float d = Vector3.Distance(rb.transform.position, targetPos);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = rb.gameObject;
                    }
                }
                if (best != null)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo("[ObjectApply] found \"" + best.name + "\" via full scan (" + bestDist.ToString("F1") + " u from target)");
                    return best;
                }
            }

            // Strategy 3: spawn from ItemsDatabase (cross-world-chunk support)
            // Do not spawn a duplicate inside the active dream pad.
            // (client solid lamp / ghost bell) while the real prop already exists.
            if (DreamSyncManager.IsDreamActive
                || (Dreams.Instance != null && Dreams.Instance.dreaming))
                return null;

            string nameLower = obj.Name != null ? obj.Name.ToLowerInvariant() : "";
            if (nameLower.Contains("droppeditem") || nameLower.Contains("deathdrop"))
                return null;

            if (string.IsNullOrEmpty(obj.ItemType))
                return null;

            if (Singleton<ItemsDatabase>.Instance == null || !Singleton<ItemsDatabase>.Instance.hasItem(obj.ItemType))
                return null;

            InvItem itemDef = Singleton<ItemsDatabase>.Instance.getItem(obj.ItemType, instantiate: false);
            if (itemDef == null || itemDef.item == null)
                return null;

            GameObject prefab = itemDef.item as GameObject;
            if (prefab == null)
                return null;

            Quaternion rot = Quaternion.Euler(obj.RotX, obj.RotY, obj.RotZ);
            GameObject spawned;
            try
            {
                TraverseHack.ApplyingFromNetwork = true;
                spawned = Core.AddPrefab(prefab, targetPos, rot, null);
                if (spawned == null)
                    spawned = UnityEngine.Object.Instantiate(prefab, targetPos, rot);
            }
            finally
            {
                TraverseHack.ApplyingFromNetwork = false;
            }

            if (spawned != null)
            {
                Rigidbody rb = spawned.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.position = targetPos;
                    rb.rotation = rot;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                ModRuntime.LegacyInfo("[ObjectApply] spawned \"" + obj.Name + "\" type=" + obj.ItemType + " at " + targetPos);
            }

            return spawned;
        }

        /// <summary>
        /// Finds a trap by position using the local overlap query. This avoids
        /// scene-wide searches on the packet path.
        /// </summary>
        internal static GameObject FindTrapByPos(Vector3 pos, string objectName = null)
        {
            GameObject hit = FindTrapInSphere(pos, 1.5f);
            if (hit != null) return hit;
            hit = FindTrapInSphere(pos, 8f);
            if (hit != null) return hit;
            hit = FindTrapInSphere(pos, 20f);
            if (hit != null) return hit;

            // Optional name, used only when an instance is already active.
            if (!string.IsNullOrEmpty(objectName))
            {
                GameObject named = GameObject.Find(objectName);
                if (named != null && HasTrapField(named))
                    return named;
            }

            return null;
        }

        private static GameObject FindTrapInSphere(Vector3 pos, float radius)
        {
            Collider[] nearby = Physics.OverlapSphere(pos, radius);
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null) continue;
                GameObject root = nearby[i].gameObject;
                Rigidbody rb = nearby[i].attachedRigidbody;
                if (rb != null) root = rb.gameObject;
                if (root == null) continue;
                if (HasTrapField(root))
                    return root;
            }
            return null;
        }

        /// <summary>
        /// Applies the triggered/untriggered state to a trap GameObject.
        /// When triggering, plays the sound, spawns the visual prefab, alerts nearby characters,
        /// switches the sprite, and cleans up the Item/Inventory components.
        /// Skips re-triggering if the trap is already in the target state.
        /// </summary>
        /// <param name="go">The trap GameObject.</param>
        /// <param name="triggered">Whether the trap should be set to triggered.</param>
        /// <param name="silentDisarm">
        /// True = successful harvest/disarm: mirror vanilla <c>switchToTriggered</c> only
        /// (sprite/name, keep the GameObject). No explosion prefab or sound;
        /// the normal triggered path remains separate.
        /// </param>
        internal static void ApplyTrapState(GameObject go, bool triggered, bool silentDisarm = false)
        {
            if (go == null) return;

            // Silent disarm first: even if ReadTrapTriggered already true, peer may still
            // have active=true (catchable). Never early-out before forcing the belt.
            if (triggered && silentDisarm)
            {
                Trigger tSilent = go.GetComponent<Trigger>();
                if (tSilent != null)
                {
                    if (!tSilent.triggered)
                    {
                        bool prev = TraverseHack.ApplyingFromNetwork;
                        TraverseHack.ApplyingFromNetwork = true;
                        try { tSilent.switchToTriggered(); }
                        finally { TraverseHack.ApplyingFromNetwork = prev; }
                    }

                    // Belt: switchToTriggered should clear these; force so peers cannot re-catch.
                    tSilent.triggered = true;
                    tSilent.active = false;
                    tSilent.canDisarm = false;
                }
                else
                {
                    Component[] comps = go.GetComponents<Component>();
                    foreach (Component comp in comps)
                    {
                        if (comp == null) continue;
                        Traverse tr = Traverse.Create(comp);
                        if (TryWriteBool(tr, "triggered", true)) break;
                    }
                }
                // Disarm while occupied: vanilla interrupt clears inBearTrap (BeartrapStop).
                ReleaseLocalBearTrapIfNear(go.transform.position);
                ModRuntime.LegacyInfo("[TrapApply] silent disarm (no FX) " + go.name);
                return;
            }

            bool current = ReadTrapTriggered(go);
            if (current == triggered) return;

            Component[] allComponents = go.GetComponents<Component>();
            foreach (Component comp in allComponents)
            {
                if (comp == null) continue;
                Traverse t = Traverse.Create(comp);
                if (TryWriteBool(t, "triggered", triggered)) break;
                if (TryWriteBool(t, "snapped", triggered)) break;
                if (TryWriteBool(t, "sprung", triggered)) break;
                if (TryWriteBool(t, "isTriggered", triggered)) break;
            }

            if (triggered)
            {
                bool isHarvestable = go.name.ToLowerInvariant().Contains("mushroom");
                Trigger trig = go.GetComponent<Trigger>();
                Explodes expl = go.GetComponent<Explodes>();

                // Diagnostic: confirm what actually owns this mushroom's boom. World
                // Some mushrooms have a Trigger but no Explodes component,
                // their blast is the Trigger's prefabToSpawn, which the old isHarvestable
                // skip was hiding.
                if (isHarvestable && ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[TrapApply] mushroom comps name=" + go.name
                        + " hasExplodes=" + (expl != null)
                        + " prefabToSpawn=" + (trig != null && trig.prefabToSpawn != null ? trig.prefabToSpawn.name : "null")
                        + " activateSound=" + (trig != null ? trig.activateSound : ""));

                if (expl == null || !isHarvestable)
                {
                    // Generic trap VFX: activateSound + prefabToSpawn. Runs for every
                    // non-mushroom trap (unchanged) AND for mushrooms without an Explodes
                    // component. World mushrooms such as expObj_mushroom_interior_01
                    // boom is the Trigger's prefabToSpawn. The old `!isHarvestable`-only
                    // gate skipped the latter on the false assumption they used Explodes,
                    // which caused the silent snap.
                    if (trig != null && !string.IsNullOrEmpty(trig.activateSound))
                        AudioController.Play(trig.activateSound, go.transform);

                    // Spawn explosion visual prefab (blood splatter, etc.) at ground level.
                    // Dropped items have Y=-10.4 (below terrain), so raycast to find ground.
                    if (trig != null && trig.prefabToSpawn != null)
                    {
                        Vector3 spawnPos = go.transform.position + new Vector3(0f, 1f, 0f);
                        // Bump underground traps up to ground level
                        if (spawnPos.y < 0f)
                        {
                            RaycastHit hit;
                            if (Physics.Raycast(spawnPos + Vector3.up * 50f, Vector3.down, out hit, 100f))
                                spawnPos.y = hit.point.y + 0.5f;
                        }
                        try
                        {
                            Core.AddPrefab(trig.prefabToSpawn, spawnPos, Quaternion.Euler(90f, 0f, 0f), null);
                        }
                        catch (Exception ex)
                        {
                            if (ModRuntime.VerboseLogging)
                                ModRuntime.Log?.LogWarning($"[PhysicsSpawn] AddPrefab failed for {trig?.prefabToSpawn}: {ex.Message}");
                            UnityEngine.Object.Instantiate(trig.prefabToSpawn, spawnPos, Quaternion.Euler(90f, 0f, 0f));
                        }
                    }
                }
                else if (isHarvestable)
                {
                    // Explodes-based mushroom (rare): render the blast via the explosion
                    // visual path and own the end state (destroyOnExplode).
                    string prefabName = expl.explosionPrefab != null ? expl.explosionPrefab.name : "";
                    string soundId = ResolveExplosionSoundId(expl.explodeSound ?? "", go.name, expl) ?? "";
                    ModRuntime.LegacyInfo("[TrapApply] mushroom blast VFX (Explodes) " + go.name
                        + " prefab=" + prefabName + " sound=" + soundId + " at " + go.transform.position);
                    SpawnExplosionVisual(go.transform.position, go.name, prefabName, soundId);

                    if (trig != null && trig.alertRadius > 0f)
                        Character.alertInArea(go.transform.position, trig.alertRadius, dangerousSound: false, 1f);
                    return;
                }

                // Alert characters in radius
                if (trig != null && trig.alertRadius > 0f)
                    Character.alertInArea(go.transform.position, trig.alertRadius, dangerousSound: false, 1f);

                // Visual sprite + name change (matches original game's OnAfterTrigger call via waitFramesAndRun)
                if (trig != null)
                    trig.switchToTriggered();

                // Cancel disarm in progress
                Item item = go.GetComponent<Item>();
                if (item != null)
                {
                    try { item.onTriggerFire(); }
                    catch (System.Exception ex)
                    {
                        ModRuntime.Log?.LogWarning(
                            "[TrapApply] onTriggerFire failed on " + go.name + ": " + ex.Message);
                    }
                }

                // Destroy Item only if the prefab is configured to remove it
                // (if dontDestroyItemAfterTriggering is true, Item stays for hover/name display)
                if (trig == null || !trig.dontDestroyItemAfterTriggering)
                {
                    if (item != null)
                        UnityEngine.Object.Destroy(item);
                }

                // Destroy Inventory only if configured to remove it
                if (trig == null || !trig.dontRemoveInventoryAfterTriggering)
                {
                    Inventory inv = go.GetComponent<Inventory>();
                    if (inv != null)
                        UnityEngine.Object.Destroy(inv);
                    if (item != null)
                        item.invItem = null;
                }
            }
        }

        /// <summary>
        /// After silent disarm: free local player still flagged inBearTrap on this trap.
        /// </summary>
        private static void ReleaseLocalBearTrapIfNear(Vector3 trapPos)
        {
            Player local = Player.Instance;
            if (local == null || !local.inBearTrap) return;
            float dx = local.transform.position.x - trapPos.x;
            float dz = local.transform.position.z - trapPos.z;
            if (dx * dx + dz * dz > 100f * 100f) return;
            try
            {
                local.interruptAllActions(doDropItem: false, stopBeartrap: true);
                ModRuntime.LegacyInfo("[TrapApply] released local inBearTrap after silent disarm");
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[TrapApply] interruptAllActions failed: " + ex.Message);
                local.inBearTrap = false;
            }
        }

        /// <summary>Tries to write a boolean field via Harmony Traverse without throwing.</summary>
        private static bool TryWriteBool(Traverse t, string field, bool val)
        {
            try
            {
                var f = t.Field(field);
                if (f.FieldExists())
                {
                    f.SetValue(val);
                    return true;
                }
            }
            catch { if (ModRuntime.VerboseLogging) ModRuntime.Log?.LogWarning("[WPSS] caught exception"); }
            return false;
        }

        /// <summary>Finds a Door by position using the tracker and a scene-wide fallback.</summary>
        private static Door FindDoorByPos(Vector3 pos)
        {
            Door door = ListTracker<Door>.FindByPosition(pos);
            if (door != null)
                return door;

            // Fallback: search all Door instances to catch doors that were
            // spawned dynamically after the tracker's Awake patch ran, or
            // doors from world-grid chunks the host has loaded.
            Door[] all = UnityEngine.Object.FindObjectsOfType<Door>();
            for (int i = 0; i < all.Length && i < 128; i++)
            {
                Door d = all[i];
                if (d == null) continue;
                if (Vector3.Distance(d.transform.position, pos) < 2f)
                {
                    ListTracker<Door>.Add(d);
                    return d;
                }
            }
            return null;
        }

        /// <summary>
        /// Spawns a generator on-demand from <see cref="GeneratorState.ItemType"/>
        /// when it doesn't exist locally (e.g. remote player turned on a generator
        /// in an unloaded world chunk).
        /// </summary>
        private static Generator SpawnGenerator(GeneratorState gs)
        {
            if (string.IsNullOrEmpty(gs.ItemType))
                return null;

            if (Singleton<ItemsDatabase>.Instance == null || !Singleton<ItemsDatabase>.Instance.hasItem(gs.ItemType))
                return null;

            InvItem itemDef = Singleton<ItemsDatabase>.Instance.getItem(gs.ItemType, instantiate: false);
            if (itemDef == null || itemDef.item == null)
                return null;

            GameObject prefab = itemDef.item as GameObject;
            if (prefab == null)
                return null;

            Vector3 pos = new Vector3(gs.PosX, gs.PosY, gs.PosZ);
            Quaternion rot = Quaternion.identity;
            GameObject go = Core.AddPrefab(prefab, pos, rot, null);
            if (go == null)
                go = UnityEngine.Object.Instantiate(prefab, pos, rot);

            if (go == null) return null;

            Generator gen = go.GetComponent<Generator>();
            if (gen != null)
                ListTracker<Generator>.Add(gen);

            ModRuntime.LegacyInfo("[GeneratorSync] spawned type=" + gs.ItemType + " at " + pos);
            return gen;
        }

        /// <summary>Finds a Generator by position via the tracker.</summary>
        private static Generator FindGeneratorByPos(Vector3 pos)
        {
            Generator gen = ListTracker<Generator>.FindByPosition(pos);
            if (gen != null)
                return gen;

            // Fallback: search all loaded Generator instances to catch generators
            // that were spawned dynamically after the tracker's Start patch ran.
            Generator[] all = UnityEngine.Object.FindObjectsOfType<Generator>();
            for (int i = 0; i < all.Length && i < 32; i++)
            {
                Generator g = all[i];
                if (g == null) continue;
                if (Vector3.Distance(g.transform.position, pos) < 2f)
                {
                    ListTracker<Generator>.Add(g);
                    return g;
                }
            }
            return null;
        }

        /// <summary>
        /// Per-frame interpolation driver. Moves each tracked physics object from its previous
        /// position toward its target over a fixed 0.25 s window. Skips objects being dragged
        /// locally and removes stale entries that haven't received a new snapshot recently.
        /// On the host, resets velocity/angular velocity to prevent physics fighting the interpolation.
        /// </summary>
    }
}
