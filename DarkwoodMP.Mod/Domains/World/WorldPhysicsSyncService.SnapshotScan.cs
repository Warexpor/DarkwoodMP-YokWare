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
        private static void SyncDoors()
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;

            ListTracker<Door>.Cleanup();

            Player local = Player.Instance;
            if (local == null) return;
            Vector3 center = local.transform.position;

            // Also scan near ALL remote proxies so doors near any client player
            // are detected even if the host player is far away.
            List<Vector3> allProxyPositions = FillProxyScanPositions();

            IList<Door> allDoors = ListTracker<Door>.GetAll();
            for (int i = 0; i < allDoors.Count; i++)
            {
                Door door = allDoors[i];
                if (door == null) continue;

                float distToHost = Vector3.Distance(door.transform.position, center);
                bool nearAnyProxy = false;
                foreach (Vector3 pp in allProxyPositions)
                {
                    if (Vector3.Distance(door.transform.position, pp) <= _scanRadius)
                    {
                        nearAnyProxy = true;
                        break;
                    }
                }
                if (distToHost > _scanRadius && !nearAnyProxy) continue;

                Vector3 dp = door.transform.position;
                Vector3 key = new Vector3((float)Math.Round(dp.x, 1), (float)Math.Round(dp.y, 1), (float)Math.Round(dp.z, 1));

                bool opened = TraverseHack.ReadDoorOpened(door);

                float bodyRotY = 0f;
                Vector3 angVel = Vector3.zero;
                if (door.body != null)
                {
                    bodyRotY = door.body.eulerAngles.y;
                    Rigidbody rb = door.body.GetComponent<Rigidbody>();
                    if (rb != null) angVel = rb.angularVelocity;
                }

                bool stateChanged = !_lastDoorOpen.TryGetValue(key, out bool wasOpened) || wasOpened != opened;
                bool isMoving = opened && angVel.sqrMagnitude > 0.01f;
                NoteStateKey(_doorKeyAge, key, stateChanged || isMoving);

                if (stateChanged || isMoving)
                {
                    _lastDoorOpen[key] = opened;

                    if (_doors.Count < 64)
                    {
                        _doors.Add(new DoorState
                        {
                            PosX = key.x,
                            PosY = key.y,
                            PosZ = key.z,
                            Opened = opened,
                            BodyRotY = bodyRotY,
                            AngVelX = angVel.x,
                            AngVelY = angVel.y,
                            AngVelZ = angVel.z
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Scans previously detected traps and records any triggered state changes.
        /// </summary>
        private static void SyncTraps()
        {
            // Remove dead entries
            _snapStaleIntKeys.Clear();
            foreach (var kv in _knownTraps)
            {
                if (kv.Value == null)
                    _snapStaleIntKeys.Add(kv.Key);
            }
            for (int di = 0; di < _snapStaleIntKeys.Count; di++)
                _knownTraps.Remove(_snapStaleIntKeys[di]);

            foreach (GameObject go in _knownTraps.Values)
            {
                if (go == null) continue;
                Vector3 pos = go.transform.position;
                Vector3 key = new Vector3((float)Math.Round(pos.x, 1), (float)Math.Round(pos.y, 1), (float)Math.Round(pos.z, 1));
                bool triggered = ReadTrapTriggered(go);
                bool changed = !_lastTrapTriggered.TryGetValue(key, out bool was) || was != triggered;
                NoteStateKey(_trapKeyAge, key, changed);
                if (changed)
                {
                    _lastTrapTriggered[key] = triggered;
                    if (_traps.Count < 32)
                    {
                        int trapId = TrapNetworkId.GetOrMintHost(go);
                        short occupant = ResolveTrapOccupant(trapId, key);
                        _traps.Add(new TrapState
                        {
                            PosX = key.x, PosY = key.y, PosZ = key.z,
                            Triggered = triggered,
                            TrapNetId = trapId,
                            OccupantPlayerId = occupant
                        });
                    }
                }
            }
        }

        /// <summary>Who is currently locked to this trap (local + remotes).</summary>
        internal static short ResolveTrapOccupant(int trapNetId, Vector3 trapPos)
        {
            if (trapNetId <= 0) return 0;
            var net = ModRuntime.Network;
            if (net == null) return 0;

            Player local = Player.Instance;
            if (local != null && local.inBearTrap)
            {
                int localTrap = TrapNetworkId.ResolveOccupyingTrapId(local.transform.position,
                    hostMint: net.Role == NetworkRole.Host);
                if (localTrap == trapNetId)
                    return (short)net.LocalPlayerId;
            }

            foreach (var kv in net.EnumerateRemoteTrapOccupancy())
            {
                if (kv.Value == trapNetId)
                    return (short)kv.Key;
            }

            return 0;
        }

        /// <summary>
        /// Scans tracked generators near the host player and records any on/off or fuel changes.
        /// </summary>
        private static void SyncGenerators()
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;

            Player local = Player.Instance;
            if (local == null) return;
            Vector3 center = local.transform.position;

            // Also scan for generators near ALL remote proxies so they are
            // synced even when the host player is far from any of them.
            List<Vector3> allProxyPositions = FillProxyScanPositions();

            IList<Generator> allGens = ListTracker<Generator>.GetAll();
            for (int i = 0; i < allGens.Count; i++)
            {
                Generator gen = allGens[i];
                if (gen == null) continue;

                float distToHost = Vector3.Distance(gen.transform.position, center);
                bool nearAnyProxy = false;
                foreach (Vector3 pp in allProxyPositions)
                {
                    if (Vector3.Distance(gen.transform.position, pp) <= _scanRadius)
                    {
                        nearAnyProxy = true;
                        break;
                    }
                }
                if (distToHost > _scanRadius && !nearAnyProxy) continue;

                Vector3 dp = gen.transform.position;
                Vector3 key = new Vector3((float)Math.Round(dp.x, 1), (float)Math.Round(dp.y, 1), (float)Math.Round(dp.z, 1));
                bool isOn = gen.isOn;
                float fuel = gen.fuel;

                bool onChanged = !_lastGeneratorOn.TryGetValue(key, out bool was) || was != isOn;
                bool fuelChanged = false;
                if (!onChanged)
                {
                    if (!_lastGeneratorFuel.TryGetValue(key, out float lastFuel))
                        fuelChanged = true;
                    else if (Mathf.Abs(fuel - lastFuel) > 10f)
                        fuelChanged = true;
                }

                NoteStateKey(_generatorKeyAge, key, onChanged || fuelChanged);
                if (onChanged || fuelChanged)
                {
                    _lastGeneratorOn[key] = isOn;
                    _lastGeneratorFuel[key] = fuel;

                    if (_generators.Count < 8)
                    {
                        string itemType = "";
                        Item itemComp = gen.GetComponent<Item>();
                        if (itemComp != null && itemComp.invItem != null)
                            itemType = itemComp.invItem.type;

                        _generators.Add(new GeneratorState
                        {
                            PosX = key.x,
                            PosY = key.y,
                            PosZ = key.z,
                            IsOn = isOn,
                            Fuel = fuel,
                            LowPower = gen.lowPower,
                            ItemType = itemType
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Examines a trigger collider to determine if it belongs to a trap
        /// and caches the result so the scan-loop can skip it on subsequent frames.
        /// </summary>
        private static void DetectTrap(Collider col)
        {
            if (col == null) return;
            GameObject root = col.gameObject;
            Rigidbody rb = col.attachedRigidbody;
            if (rb != null) root = rb.gameObject;
            if (root == null) return;

            int id = root.GetInstanceID();
            if (_knownTraps.ContainsKey(id)) return;
            float now = Time.time;

            // Already classified
            if (_trapResultCache.TryGetValue(id, out TrapClassification known))
            {
                known.LastSeen = now;
                _trapResultCache[id] = known;
                if (known.IsTrap && !_knownTraps.ContainsKey(id))
                    _knownTraps[id] = root;
                return;
            }

            if (!TrapNetworkId.IsWorldTrap(root))
            {
                _trapResultCache[id] = new TrapClassification { IsTrap = false, LastSeen = now };
                return;
            }

            // Verify by checking for a "triggered"/"snapped"/"sprung" bool field
            if (HasTrapField(root))
            {
                _trapResultCache[id] = new TrapClassification { IsTrap = true, LastSeen = now };
                _knownTraps[id] = root;
                var net = ModRuntime.Network;
                if (net != null && net.Role == NetworkRole.Host)
                    TrapNetworkId.GetOrMintHost(root);
                else
                    TrapNetworkId.RegisterKnown(root);
            }
            else
            {
                _trapResultCache[id] = new TrapClassification { IsTrap = false, LastSeen = now };
            }
        }

        private static void NoteStateKey(Dictionary<Vector3, StateKeyAge> ages, Vector3 key, bool sent)
        {
            float now = Time.time;
            ages.TryGetValue(key, out StateKeyAge age);
            age.LastSeen = now;
            if (sent || age.LastSent <= 0f)
                age.LastSent = now;
            ages[key] = age;
        }

        /// <summary>
        /// Forget keys no longer scanned (destroyed / permanently out of range) and drop the
        /// "last state" of a few long-unsent keys so the next scan re-sends just those. Replaces a
        /// blanket clear that re-sent every door / trap / generator in range in one burst.
        /// </summary>
        private static void PruneStateKeys(Dictionary<Vector3, StateKeyAge> ages,
            Dictionary<Vector3, bool> lastState, Dictionary<Vector3, float> lastExtra, float now)
        {
            _stateKeyScratch.Clear();
            foreach (var kv in ages)
            {
                if (now - kv.Value.LastSeen > StateKeyForgetSeconds)
                    _stateKeyScratch.Add(kv.Key);
            }
            for (int i = 0; i < _stateKeyScratch.Count; i++)
            {
                Vector3 k = _stateKeyScratch[i];
                ages.Remove(k);
                lastState.Remove(k);
                lastExtra?.Remove(k);
            }

            // State keys that were never noted (older entries) also go.
            _stateKeyScratch.Clear();
            foreach (var kv in lastState)
            {
                if (!ages.ContainsKey(kv.Key))
                    _stateKeyScratch.Add(kv.Key);
            }
            for (int i = 0; i < _stateKeyScratch.Count; i++)
            {
                lastState.Remove(_stateKeyScratch[i]);
                lastExtra?.Remove(_stateKeyScratch[i]);
            }

            _stateKeyScratch.Clear();
            foreach (var kv in ages)
            {
                if (_stateKeyScratch.Count >= StateKeyResyncPerPass) break;
                if (now - kv.Value.LastSent > StateKeyResyncSeconds && lastState.ContainsKey(kv.Key))
                    _stateKeyScratch.Add(kv.Key);
            }
            for (int i = 0; i < _stateKeyScratch.Count; i++)
            {
                Vector3 k = _stateKeyScratch[i];
                lastState.Remove(k);
                lastExtra?.Remove(k);
                // Rotate: this key counts as sent now so the next pass picks other stale keys.
                StateKeyAge age = ages[k];
                age.LastSent = now;
                ages[k] = age;
            }
            _stateKeyScratch.Clear();
        }

        /// <summary>Drop trap classifications for colliders the scan has not touched in a while.</summary>
        private static void PruneTrapResultCache(float now)
        {
            _snapStaleIntKeys.Clear();
            foreach (var kv in _trapResultCache)
            {
                if (now - kv.Value.LastSeen > TrapResultForgetSeconds)
                    _snapStaleIntKeys.Add(kv.Key);
            }
            for (int i = 0; i < _snapStaleIntKeys.Count; i++)
                _trapResultCache.Remove(_snapStaleIntKeys[i]);
            _snapStaleIntKeys.Clear();
        }

        /// <summary>Returns true if the GameObject has a component with a trap-related boolean field.</summary>
        private static readonly List<Component> _trapCompScratch = new List<Component>(8);

        private static bool HasTrapField(GameObject go)
        {
            if (go == null) return false;
            // Fast path: almost all Darkwood traps are Trigger with public triggered.
            if (go.GetComponent<Trigger>() != null)
                return true;

            go.GetComponents(_trapCompScratch);
            for (int i = 0; i < _trapCompScratch.Count; i++)
            {
                Component comp = _trapCompScratch[i];
                if (comp == null) continue;
                Traverse t = Traverse.Create(comp);
                bool val;
                if (TryReadBool(t, "triggered", out val)) return true;
                if (TryReadBool(t, "snapped", out val)) return true;
                if (TryReadBool(t, "sprung", out val)) return true;
                if (TryReadBool(t, "isTriggered", out val)) return true;
            }
            return false;
        }

        /// <summary>Reads the triggered/snapped/sprung/isTriggered field from a trap GameObject.</summary>
        internal static bool ReadTrapTriggered(GameObject go)
        {
            if (go == null) return false;
            Trigger trig = go.GetComponent<Trigger>();
            if (trig != null)
                return trig.triggered;

            go.GetComponents(_trapCompScratch);
            for (int i = 0; i < _trapCompScratch.Count; i++)
            {
                Component comp = _trapCompScratch[i];
                if (comp == null) continue;
                Traverse t = Traverse.Create(comp);
                bool val;
                if (TryReadBool(t, "triggered", out val)) return val;
                if (TryReadBool(t, "snapped", out val)) return val;
                if (TryReadBool(t, "sprung", out val)) return val;
                if (TryReadBool(t, "isTriggered", out val)) return val;
            }
            return false;
        }

        /// <summary>Tries to read a boolean field via Harmony Traverse without throwing.</summary>
        private static bool TryReadBool(Traverse t, string field, out bool val)
        {
            val = false;
            try
            {
                var f = t.Field(field);
                if (f.FieldExists())
                {
                    val = f.GetValue<bool>();
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning($"[TraverseFieldRead] failed to read {field}: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Applies a received <see cref="PhysicsStateMessage"/> to the local world:
        /// positions objects (with interpolation targets), opens/closes doors,
        /// triggers traps, and syncs generators. Skips the local player and remote proxies.
        /// </summary>
        /// <param name="state">The snapshot to apply.</param>
        /// <param name="fromPeer">Identifier of the sender, used for logging.</param>
    }
}
