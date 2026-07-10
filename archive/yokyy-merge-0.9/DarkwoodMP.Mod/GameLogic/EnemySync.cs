using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.Patches;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Host-authoritative enemy synchronization (v0.7 - replaces the old distributed
/// ownership model, which could not keep enemies in sync). Ported/adapted from
/// the BepInEx mod's EntityStateBroadcastService + ClientEntityInterpolationService.
///
/// AUTHORITY (host, or the elected time-authority on a dedicated server) runs ALL
/// enemy AI and broadcasts a snapshot of every Character near any player at 10Hz
/// (EntityStatePacket, unreliable). It also points enemies at the nearest player
/// so clients get attacked too, and applies damage forwarded by clients.
///
/// NON-AUTHORITY machines disable enemy AI (ClientAIDisable_Patch) and simply
/// MIRROR the authority's snapshots: match each snapshot to the local Character by
/// stable id (or name+position, since world-download makes worlds identical),
/// interpolate its transform, drive its animation, and kill it when the authority
/// says it died. Their own attacks are forwarded to the authority to apply.
/// </summary>
public class EnemySync
{
    private readonly NetworkLayer _network;

    // ---- authority broadcast ----
    private const float SendInterval = 0.1f;      // 10Hz
    private const float BroadcastRangeSq = 3500f * 3500f;
    private const int MaxEntities = 200;
    private const float NudgeInterval = 1.5f;
    private const float NudgeAwareness = 20f;
    private float _lastSend;
    private float _lastNudge;

    // ---- client interpolation ----
    private const float SnapshotInterval = 0.1f;
    private const float MaxInterpDelay = 0.4f;
    private const float MatchRadius = 15f;
    private const float PendingTimeout = 0.25f;
    private const float PhantomCleanup = 5f;

    private class InterpState
    {
        public Vector3 PrevPos, TargetPos;
        public float PrevRotY, TargetRotY;
        public float ArrivalTime;
        public bool HasTarget;
        public bool First = true;
        public float StaleSince;
        public Rigidbody Body;
        // Native isKinematic value captured before the mirror forces it true, so
        // it can be restored if this machine is promoted to authority (dedicated
        // re-election) and has to simulate these enemies itself.
        public bool OrigKinematic;
        public bool KinematicCaptured;
    }

    private struct Pending
    {
        public short Id;
        public string Name, PrefabPath;
        public Vector3 Pos;
        public float RotY;
        public string Clip;
        public short ClipFrame;
        public bool Alive;
        public float Added;
    }

    private readonly Dictionary<short, InterpState> _states = new();
    private readonly Dictionary<short, Vector3> _displayPos = new();
    private readonly Dictionary<short, float> _displayRot = new();
    private readonly HashSet<short> _synced = new();
    private readonly HashSet<short> _phantoms = new();
    private readonly HashSet<short> _soundStopped = new();
    private readonly HashSet<short> _deathPlayed = new();
    private readonly List<Pending> _pending = new();
    private static readonly List<short> _keyBuf = new();
    private static readonly List<short> _staleBuf = new();

    private Type _characterType;
    private FieldInfo _idleAniField;
    private FieldInfo _isActiveField;
    private float _lastRegistryScan = float.MinValue;
    private const float RegistryScanInterval = 5f;   // until first population
    private const float SettledScanInterval = 30f;   // safety net afterwards

    // GetComponentInChildren per enemy per 10Hz tick is a hierarchy walk -
    // cache the animator per Character instance (re-resolved when destroyed)
    private readonly Dictionary<int, tk2dSpriteAnimator> _animCache = new();

    private tk2dSpriteAnimator AnimatorOf(Character c)
    {
        var key = c.GetInstanceID();
        if (_animCache.TryGetValue(key, out var anim) && anim != null) return anim;
        anim = c.GetComponentInChildren<tk2dSpriteAnimator>();
        if (anim != null) _animCache[key] = anim;
        else _animCache.Remove(key);
        if (_animCache.Count > 2048) _animCache.Clear(); // bound (ids recycle)
        return anim;
    }
    private float _lastDiagLog;
    private bool _wasAuthority;

    public EnemySync(NetworkLayer network)
    {
        _network = network;
    }

    private bool ResolveApi()
    {
        if (_characterType != null) return true;
        _characterType = GameTypes.GetType("Character");
        if (_characterType == null) return false;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (var t = _characterType; t != null; t = t.BaseType)
        {
            _idleAniField ??= t.GetField("idleAni", flags | BindingFlags.DeclaredOnly);
            _isActiveField ??= t.GetField("IsActive", flags | BindingFlags.DeclaredOnly);
        }
        return true;
    }

    private static bool IsAuthority => NetworkManager.Instance != null && NetworkManager.Instance.IsTimeAuthority;

    /// <summary>
    /// True when THIS machine mirrors (does not simulate) this character.
    /// Accepts any Component on the entity (e.g. CharacterEffect, Burn) — resolves Character.
    /// </summary>
    public bool IsClientMirroring(Component anyOnEntity)
    {
        if (IsAuthority || anyOnEntity == null) return false;
        var c = anyOnEntity as Character
            ?? anyOnEntity.GetComponent<Character>()
            ?? anyOnEntity.GetComponentInParent<Character>();
        if (c == null) return false;
        return CharacterTracker.TryGetStableId(c, out var id) && _synced.Contains(id);
    }

    // Legacy call sites (PvpHit etc.) - locally simulated == authority side.
    public bool IsLocallySimulated(Component character) => IsAuthority;

    // ==================================================================
    // Authority: broadcast + AI targeting  (OnOwnerUpdate, called every frame)
    // ==================================================================

    /// <summary>
    /// Populate the registry with characters that already existed before the mod
    /// patches applied (the world-download loads the world, and its enemies run
    /// Character.Start BEFORE we connect + patch). Without this the authority's
    /// registry is empty, it broadcasts nothing, and clients (AI disabled) just
    /// freeze. Throttled scan - much rarer than the old 4-8Hz per-system scans.
    /// </summary>
    private void RefreshRegistry()
    {
        // Once the registry is populated, new spawns arrive via the
        // Character.Start patch (CharacterRegistry_Patch) - the scan is only
        // a safety net for stragglers, so back way off: a whole-scene
        // FindObjectsOfType every 5s was a visible periodic hitch.
        var interval = CharacterTracker.Count > 0 ? SettledScanInterval : RegistryScanInterval;
        if (Time.time - _lastRegistryScan < interval) return;
        _lastRegistryScan = Time.time;
        if (!ResolveApi()) return;
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(_characterType))
            if (obj is Character c) CharacterTracker.Add(c);
    }

    public void OnOwnerUpdate()
    {
        RefreshRegistry();

        // Authority promotion (dedicated re-election: the lowest-id client left).
        // While mirroring, this machine forced enemy Rigidbodies kinematic, muted
        // their sound components and froze their AI. Now that we must SIMULATE
        // them, restore that state or they stay frozen/silent statues.
        var auth = IsAuthority;
        if (auth && !_wasAuthority) RestoreMirroredEnemies();
        _wasAuthority = auth;

        if (!auth) return;
        if (Time.time - _lastSend < SendInterval) return;
        _lastSend = Time.time;
        if (!ResolveApi() || !_network.IsConnected) return;

        var manager = NetworkManager.Instance;
        var localT = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<PlayerSync>()?.LocalPlayerTransform;
        var localPos = localT != null ? localT.position : Vector3.zero;
        var clones = CollectClones(manager);

        var localPlayer = Player.Instance;
        var all = CharacterTracker.GetAll();
        var list = new List<EntitySnapshot>(Math.Min(all.Length, MaxEntities));

        foreach (var c in all)
        {
            if (c == null) continue;
            if (c.transform.root.name.StartsWith("RemotePlayer_")) continue;
            if (localPlayer != null && c.gameObject == localPlayer.gameObject) continue; // never broadcast our own player
            var pos = c.transform.position;
            if ((pos - localPos).sqrMagnitude > BroadcastRangeSq && !AnyCloneWithin(clones, pos, 3500f))
                continue;

            var anim = AnimatorOf(c);
            var clip = anim != null && anim.CurrentClip != null ? anim.CurrentClip.name : "";
            var frame = anim != null && anim.CurrentClip != null ? (short)anim.CurrentFrame : (short)-1;
            var name = c.gameObject.name;
            if (name.EndsWith("(Clone)")) name = name.Substring(0, name.Length - 7);

            list.Add(new EntitySnapshot
            {
                Id = CharacterTracker.GetStableId(c),
                X = pos.x, Y = pos.y, Z = pos.z,
                RotY = c.transform.eulerAngles.y,
                Clip = clip,
                ClipFrame = frame,
                Alive = c.alive,
                HealthPct = (byte)Mathf.Clamp(c.Health / Mathf.Max(c.maxHealth, 1f) * 100f, 0, 100),
                Name = name,
                PrefabPath = "Characters/" + name
            });
            if (list.Count >= MaxEntities) break;
        }

        if (NetworkLayer.VerboseLogging && Time.time - _lastDiagLog > 1f)
        {
            _lastDiagLog = Time.time;
            ModLogger.Msg($"[EnemySync] AUTHORITY broadcasting {list.Count}/{all.Length} entities (localPos={localPos})");
        }

        // Send in small chunks so each datagram stays under the ~1400-byte MTU.
        // One big datagram gets IP-fragmented and a single lost fragment drops the
        // whole snapshot - which showed up as very stuttery mirrored enemies.
        const int PerPacket = 20;
        for (var off = 0; off < list.Count; off += PerPacket)
        {
            var n = Math.Min(PerPacket, list.Count - off);
            var chunk = new EntitySnapshot[n];
            list.CopyTo(off, chunk, 0, n);
            _network.Send(new EntityStatePacket { Entities = chunk });
        }

        if (Time.time - _lastNudge > NudgeInterval)
        {
            _lastNudge = Time.time;
            NudgeTargets(manager, localPos, clones, all);
        }
    }

    private void NudgeTargets(NetworkManager manager, Vector3 localPos, List<(int id, Vector3 pos)> clones, Character[] all)
    {
        if (clones.Count == 0) return;
        var nudged = 0;
        foreach (var c in all)
        {
            if (nudged >= 12) break;
            if (c == null || !c.alive) continue;
            if (c.transform.root.name.StartsWith("RemotePlayer_")) continue;
            var pos = c.transform.position;
            var localD = (pos - localPos).sqrMagnitude;
            GameObject bestClone = null;
            var bestD = Mathf.Min(localD, NudgeAwareness * NudgeAwareness);
            foreach (var (id, clonePos) in clones)
            {
                var d = (clonePos - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; bestClone = manager.GetRemotePlayer(id); }
            }
            if (bestClone == null) continue;
            try { c.attack(bestClone); nudged++; } catch { }
        }
    }

    /// <summary>Authority: apply damage a client forwarded (client hit an enemy).</summary>
    public void ApplyRemoteAttack(short id, float damage, bool canCut, Vector3 attackerPos)
    {
        if (!IsAuthority || !ResolveApi()) return;
        damage = DamageSync.SanitizePeerDamage(damage);
        if (damage <= 0f) return;
        var c = CharacterTracker.FindByStableId(id);
        if (c == null || !c.alive) return;
        try
        {
            RemoteApply.Active = true;
            // We are the authority applying the attacker's forwarded hit: the
            // blood this getHit spawns is first-hand FX and must broadcast
            // (the attacker's machine skipped its own getHit entirely).
            RemoteApply.BroadcastFxFromApply = true;
            try { c.getHit(damage, null, canCut, true, true, true, true, false, false); }
            finally
            {
                RemoteApply.Active = false;
                RemoteApply.BroadcastFxFromApply = false;
            }
        }
        catch (Exception ex) { ModLogger.Error($"[EnemySync] ApplyRemoteAttack: {ex.Message}"); }
    }

    // ==================================================================
    // Non-authority: receive + interpolate
    // ==================================================================

    public void OnEntityState(EntityStatePacket packet)
    {
        if (IsAuthority || !ResolveApi()) return;

        if (NetworkLayer.VerboseLogging && Time.time - _lastDiagLog > 1f)
        {
            _lastDiagLog = Time.time;
            ModLogger.Msg($"[EnemySync] CLIENT recv {packet.Entities.Length} snapshots -> synced={_synced.Count} states={_states.Count} pending={_pending.Count} registry={CharacterTracker.Count}");
        }

        foreach (var e in packet.Entities)
        {
            var target = new Vector3(e.X, e.Y, e.Z);
            var c = CharacterTracker.FindByStableId(e.Id);
            if (c != null)
            {
                var cn = Strip(c.gameObject.name);
                if (string.Equals(cn, e.Name, StringComparison.OrdinalIgnoreCase))
                {
                    _synced.Add(e.Id);
                    ApplyEntity(c, e, target);
                    continue;
                }
                // stable id hit the wrong local entity - unbind and re-match
                CharacterTracker.AssignId(c, 0);
            }

            c = CharacterTracker.FindByPositionAndName(target, e.Name, MatchRadius, _synced)
                // Fallback: the local enemy may have drifted from the authority's
                // position while its AI was frozen, so bind the nearest same-named
                // local enemy anywhere rather than spawn a duplicate phantom.
                ?? CharacterTracker.FindClosestByName(e.Name, target, _synced);
            if (c != null)
            {
                CharacterTracker.AssignId(c, e.Id);
                _synced.Add(e.Id);
                EnsureAwake(c);
                ApplyEntity(c, e, target);
                continue;
            }

            _pending.Add(new Pending
            {
                Id = e.Id, Name = e.Name, PrefabPath = e.PrefabPath,
                Pos = target, RotY = e.RotY, Clip = e.Clip, ClipFrame = e.ClipFrame,
                Alive = e.Alive, Added = Time.time
            });
        }
    }

    private void ApplyEntity(Character c, EntitySnapshot e, Vector3 target)
    {
        EnsureAwake(c);

        // Host drives everything - silence the local AI sound component once so
        // it can't loop stale sounds (client AI is disabled anyway).
        if (_soundStopped.Add(e.Id) && c.sounds != null)
            c.sounds.enabled = false;

        if (!_states.TryGetValue(e.Id, out var s))
        {
            s = new InterpState();
            _states[e.Id] = s;
        }
        s.StaleSince = 0f;
        if (s.First)
        {
            _displayPos[e.Id] = c.transform.position;
            _displayRot[e.Id] = c.transform.eulerAngles.y;
            s.First = false;
        }
        s.PrevPos = _displayPos[e.Id];
        s.PrevRotY = _displayRot[e.Id];
        s.TargetPos = target;
        s.TargetRotY = e.RotY;
        s.ArrivalTime = Time.time;
        s.HasTarget = true;

        if (!e.Alive && c.alive)
        {
            try { c.die(); } catch { }
        }

        DriveAnimation(c, e);
    }

    private void DriveAnimation(Character c, EntitySnapshot e)
    {
        var anim = AnimatorOf(c);
        if (anim == null) return;
        try
        {
            if (!string.IsNullOrEmpty(e.Clip))
            {
                var isDead = !c.alive;
                if (!(isDead && _deathPlayed.Contains(e.Id)))
                {
                    if ((anim.CurrentClip == null || anim.CurrentClip.name != e.Clip) && anim.GetClipByName(e.Clip) != null)
                    {
                        anim.Play(e.Clip);
                        if (isDead) _deathPlayed.Add(e.Id);
                    }
                }
                // Deliberately NOT forcing anim.SetFrame every snapshot: snapping
                // the frame 10x/sec fought the local playback and looked choppy.
                // Syncing the clip name is enough - it plays smoothly locally.
            }
            else if (c.alive && !anim.Playing)
            {
                var idle = _idleAniField?.GetValue(c) as string;
                if (!string.IsNullOrEmpty(idle) && anim.GetClipByName(idle) != null)
                    anim.Play(idle);
            }
        }
        catch { }
    }

    /// <summary>Called every frame on non-authority machines: interpolate + pending + cleanup.</summary>
    public void OnMirrorUpdate()
    {
        RefreshRegistry();
        if (IsAuthority) return;
        if (!ResolveApi()) return;
        var now = Time.time;

        // pending matches
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            var c = CharacterTracker.FindByPositionAndName(p.Pos, p.Name, MatchRadius, _synced);
            if (c != null)
            {
                CharacterTracker.AssignId(c, p.Id);
                _synced.Add(p.Id);
                EnsureAwake(c);
                SeedState(p.Id, c, p);
                _pending.RemoveAt(i);
                continue;
            }
            if (now - p.Added > PendingTimeout)
            {
                if (!_synced.Contains(p.Id))
                {
                    var spawned = SpawnPhantom(p);
                    if (spawned != null)
                    {
                        CharacterTracker.AssignId(spawned, p.Id);
                        _synced.Add(p.Id);
                        _phantoms.Add(p.Id);
                        EnsureAwake(spawned);
                        spawned.transform.position = p.Pos;
                        SeedState(p.Id, spawned, p);
                    }
                }
                _pending.RemoveAt(i);
            }
        }

        // interpolation
        _keyBuf.Clear();
        _keyBuf.AddRange(_states.Keys);
        _staleBuf.Clear();

        foreach (var id in _keyBuf)
        {
            var s = _states[id];
            var c = CharacterTracker.FindByStableId(id);
            if (c == null) { _staleBuf.Add(id); continue; }

            if (!s.HasTarget)
            {
                if (s.StaleSince > 0f && now - s.StaleSince > PhantomCleanup)
                {
                    // Authority stopped broadcasting this enemy (dead / gone / far).
                    // Its mirror is frozen (client AI is disabled), so don't leave a
                    // stuck statue: destroy phantoms (client-created), and hide real
                    // save enemies (SetActive false) so they can be re-bound + shown
                    // again if the authority resumes broadcasting them.
                    if (_phantoms.Contains(id))
                    {
                        UnityEngine.Object.Destroy(c.gameObject);
                        _phantoms.Remove(id);
                    }
                    else
                    {
                        c.gameObject.SetActive(false);
                        CharacterTracker.AssignId(c, 0); // unbind so it can rematch cleanly
                    }
                    _staleBuf.Add(id);
                    _synced.Remove(id);
                    _displayPos.Remove(id);
                    _displayRot.Remove(id);
                    // Prune per-id bookkeeping too: the authority reuses short ids
                    // (NextFreeId hands a freed id to a new Character), so a stale
                    // entry left here would make the NEXT enemy with this id skip
                    // its death animation / keep its muted sounds. Also bounds growth.
                    _soundStopped.Remove(id);
                    _deathPlayed.Remove(id);
                }
                continue;
            }

            var elapsed = now - s.ArrivalTime;
            if (elapsed > MaxInterpDelay)
            {
                _displayPos[id] = s.TargetPos;
                _displayRot[id] = s.TargetRotY;
                s.HasTarget = false;
                s.StaleSince = now;
            }
            else if (elapsed > SnapshotInterval)
            {
                var extrap = elapsed - SnapshotInterval;
                var vel = (s.TargetPos - s.PrevPos) / SnapshotInterval;
                _displayPos[id] = s.TargetPos + vel * extrap;
                _displayRot[id] = s.TargetRotY;
            }
            else
            {
                var t = elapsed / SnapshotInterval;
                var smooth = t * t * (3f - 2f * t);
                _displayPos[id] = Vector3.Lerp(s.PrevPos, s.TargetPos, smooth);
                _displayRot[id] = Mathf.LerpAngle(s.PrevRotY, s.TargetRotY, smooth);
            }

            // Drive the mirror directly. Make its Rigidbody kinematic so the
            // (disabled-AI) enemy's physics can't fight the snapshot, and set the
            // transform straight - MovePosition only reliably applies in
            // FixedUpdate, which is why the mirrors were sitting still.
            if (s.Body == null || s.Body.gameObject != c.gameObject)
                s.Body = c.GetComponent<Rigidbody>();
            if (s.Body != null)
            {
                if (!s.KinematicCaptured) { s.OrigKinematic = s.Body.isKinematic; s.KinematicCaptured = true; }
                if (!s.Body.isKinematic) s.Body.isKinematic = true;
            }
            c.transform.position = _displayPos[id];
            var rot = c.transform.eulerAngles;
            rot.y = _displayRot[id];
            c.transform.eulerAngles = rot;
        }

        foreach (var id in _staleBuf) _states.Remove(id);
    }

    private void SeedState(short id, Character c, Pending p)
    {
        if (!_states.TryGetValue(id, out var s)) { s = new InterpState(); _states[id] = s; }
        s.StaleSince = 0f;
        s.First = false;
        _displayPos[id] = c.transform.position;
        _displayRot[id] = c.transform.eulerAngles.y;
        s.PrevPos = c.transform.position;
        s.PrevRotY = c.transform.eulerAngles.y;
        s.TargetPos = p.Pos;
        s.TargetRotY = p.RotY;
        s.ArrivalTime = Time.time;
        s.HasTarget = true;
        if (!p.Alive && c.alive) { try { c.die(); } catch { } }
    }

    private Character SpawnPhantom(Pending p)
    {
        var path = !string.IsNullOrEmpty(p.PrefabPath) ? p.PrefabPath : "Characters/" + Strip(p.Name);
        GameObject go = null;
        RemoteApply.Active = true;
        try
        {
            go = Core.AddPrefab(path, p.Pos, Quaternion.Euler(90f, p.RotY, 0f), null, false);
            if (go != null) Core.addToSaveable(go, true, true);
        }
        catch { }
        finally { RemoteApply.Active = false; }
        if (go == null) return null;
        var c = go.GetComponentInChildren<Character>();
        if (c == null) { UnityEngine.Object.Destroy(go); return null; }
        return c;
    }

    /// <summary>
    /// Reliable one-shot dynamic spawn (EntitySpawnPacket). Prefer over delayed phantom.
    /// EntityState continues to drive motion after bind.
    /// </summary>
    public void OnRemoteEntitySpawn(short entityId, string entityType, string prefabPath, Vector3 pos, float rotY)
    {
        if (IsAuthority || entityId == 0) return;
        try
        {
            if (_synced.Contains(entityId))
            {
                var existing = CharacterTracker.FindByStableId(entityId);
                if (existing != null)
                {
                    existing.transform.position = pos;
                    return;
                }
            }

            var match = CharacterTracker.FindByPositionAndName(pos, entityType ?? "", MatchRadius, _synced)
                ?? CharacterTracker.FindClosestByName(entityType ?? "", pos, _synced);
            if (match != null)
            {
                CharacterTracker.AssignId(match, entityId);
                _synced.Add(entityId);
                EnsureAwake(match);
                SeedState(entityId, match, new Pending
                {
                    Id = entityId, Name = entityType ?? "", PrefabPath = prefabPath,
                    Pos = pos, RotY = rotY, Alive = true, Added = Time.time
                });
                return;
            }

            var phantom = SpawnPhantom(new Pending
            {
                Id = entityId,
                Name = entityType ?? "",
                PrefabPath = string.IsNullOrEmpty(prefabPath) ? "Characters/" + (entityType ?? "") : prefabPath,
                Pos = pos, RotY = rotY, Alive = true, Added = Time.time
            });
            if (phantom == null) return;
            CharacterTracker.AssignId(phantom, entityId);
            _synced.Add(entityId);
            _phantoms.Add(entityId);
            EnsureAwake(phantom);
            SeedState(entityId, phantom, new Pending
            {
                Id = entityId, Name = entityType ?? "", PrefabPath = prefabPath,
                Pos = pos, RotY = rotY, Alive = true, Added = Time.time
            });
            ModLogger.Msg($"[EnemySync] Reliable remote spawn id={entityId} '{entityType}'");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[EnemySync] OnRemoteEntitySpawn: {ex.Message}");
        }
    }

    private void EnsureAwake(Character c)
    {
        if (c == null) return;
        var go = c.gameObject;
        if (!go.activeSelf) go.SetActive(true);
        if (!c.enabled) c.enabled = true;
        try { _isActiveField?.SetValue(c, true); } catch { }
        var anim = c.GetComponentInChildren<tk2dSpriteAnimator>();
        if (anim != null && !anim.enabled) anim.enabled = true;
    }

    /// <summary>
    /// Multi-center positions for all remotes: live proxy transform preferred,
    /// else <see cref="PlayerPositionRegistry"/> (packet-fresh even mid-upgrade).
    /// </summary>
    private static List<(int, Vector3)> CollectClones(NetworkManager manager)
    {
        var r = new List<(int, Vector3)>();
        var seen = new HashSet<int>();
        if (manager != null)
        {
            foreach (var kvp in manager.RemotePlayers)
            {
                if (kvp.Value == null) continue;
                r.Add((kvp.Key, kvp.Value.transform.position));
                seen.Add(kvp.Key);
            }
        }
        // Registry may know a peer before the clone upgrades from capsule
        var fromReg = new List<(int id, Vector3 pos)>();
        PlayerPositionRegistry.CollectRemotePositions(fromReg);
        foreach (var (id, pos) in fromReg)
        {
            if (seen.Contains(id)) continue;
            r.Add((id, pos));
            seen.Add(id);
        }
        return r;
    }

    private static bool AnyCloneWithin(List<(int id, Vector3 pos)> clones, Vector3 pos, float radius)
    {
        var rSq = radius * radius;
        foreach (var (_, cp) in clones)
            if ((cp - pos).sqrMagnitude <= rSq) return true;
        return false;
    }

    private static string Strip(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return name.EndsWith("(Clone)") ? name.Substring(0, name.Length - 7) : name;
    }

    /// <summary>
    /// Called once when this machine is promoted from mirror to authority. Undoes
    /// every mirror-side mutation (kinematic Rigidbody, muted sounds, frozen AI)
    /// so the now-authoritative AI can drive these enemies, then drops the mirror
    /// bookkeeping. Phantoms (client-spawned copies) simply become authoritative
    /// enemies from here.
    /// </summary>
    private void RestoreMirroredEnemies()
    {
        try
        {
            // Only touch enemies this machine actually mirrored (tracked ids) - a
            // blanket pass over the registry could wake characters the game left
            // deliberately inactive. On a fresh authority start these sets are
            // empty, so this is a no-op (correct).
            var ids = new HashSet<short>(_states.Keys);
            ids.UnionWith(_soundStopped);
            foreach (var id in ids)
            {
                var c = CharacterTracker.FindByStableId(id);
                if (c == null) continue;
                if (_states.TryGetValue(id, out var s) && s.Body != null && s.KinematicCaptured)
                    s.Body.isKinematic = s.OrigKinematic;
                if (_soundStopped.Contains(id))
                {
                    try { if (c.sounds != null) c.sounds.enabled = true; } catch { }
                }
                EnsureAwake(c);
            }
        }
        catch (Exception ex) { ModLogger.Error($"[EnemySync] RestoreMirroredEnemies: {ex.Message}"); }
        _states.Clear();
        _displayPos.Clear();
        _displayRot.Clear();
        _synced.Clear();
        _phantoms.Clear();
        _soundStopped.Clear();
        _deathPlayed.Clear();
        _pending.Clear();
    }

    public void Reset()
    {
        _states.Clear();
        _displayPos.Clear();
        _displayRot.Clear();
        _synced.Clear();
        _phantoms.Clear();
        _soundStopped.Clear();
        _deathPlayed.Clear();
        _pending.Clear();
        _animCache.Clear();
        _wasAuthority = false;
    }
}
