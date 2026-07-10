using System;
using System.Collections.Generic;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.Patches;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Night-shadow (ShadowCreature) visual sync for the distributed model.
///
/// Shadows are spawned by each player's own NightShadows skill
/// (Player.tryToSpawnShadow -> "characters/fakechars/shadow"). They are a
/// ShadowCreature, NOT a Character, so EnemySync never saw them. Each player's
/// vanilla AI already spawns and fights ITS OWN shadows correctly (the AI
/// targets the local Player.Instance) - the only gap is that the partner never
/// SAW them. This module closes that gap only: every machine broadcasts its
/// live shadows and mirrors the partner's as frozen, network-driven visuals.
///
/// Unlike the BepInEx mod this needs no Harmony patch and no host authority:
/// shadows are discovered by scanning, mirrored copies have their ShadowCreature
/// behaviour disabled so they neither chase the observer nor spawn attack
/// sensors, and their transform is driven purely by the owner's updates.
/// </summary>
public class ShadowSync
{
    private readonly NetworkLayer _network;

    // 2Hz: the scan behind this is a whole-scene FindObjectsOfType every
    // interval, all night long - at the old ~8Hz it was the single biggest
    // recurring hitch. Mirrors interpolate between updates, and shadow
    // creatures are jittery ghost visuals anyway, so 2Hz reads fine.
    private const float SendInterval = 0.5f;
    private const float PosSharpness = 12f;
    private const float MirrorTimeout = 4f;      // owner went silent -> drop mirror

    private Type _shadowType;
    private float _lastScan;

    // Owner side: instance ids we broadcast last scan (to emit "gone" when they vanish)
    private readonly HashSet<string> _sentThisScan = new();
    private readonly HashSet<string> _sentLastScan = new();
    // Deaths already announced once (corpse lingers a couple seconds - don't respam)
    private readonly HashSet<string> _deadSent = new();

    // Mirror side: shadows owned by a partner, keyed "ownerId:instanceId"
    private readonly Dictionary<string, MirrorShadow> _mirrors = new();
    private static readonly List<string> _staleBuffer = new();

    public ShadowSync(NetworkLayer network)
    {
        _network = network;
    }

    private bool ResolveApi()
    {
        if (_shadowType != null) return true;
        _shadowType = GameTypes.GetType("ShadowCreature");
        return _shadowType != null;
    }

    // ------------------------------------------------------------------
    // Owner side
    // ------------------------------------------------------------------

    public void OnUpdate()
    {
        InterpolateMirrors();

        if (Time.time - _lastScan < SendInterval) return;
        _lastScan = Time.time;
        if (!_network.IsConnected || !ResolveApi()) return;

        // Shadows only exist at night. FindObjectsOfType is a whole-scene scan
        // (a real cost in a large explored world), so skip it entirely by day -
        // any leftover mirrors are cleaned up by the interpolation timeout.
        bool day;
        try { day = Core.isDay(); } catch { day = false; }
        if (day)
        {
            _sentThisScan.Clear();
            _sentLastScan.Clear();
            return;
        }

        var myId = Math.Max(_network.LocalClientId, 0);
        _sentThisScan.Clear();

        foreach (var obj in UnityEngine.Object.FindObjectsOfType(_shadowType))
        {
            if (obj is not Component sc) continue;
            // Never re-broadcast a shadow we are mirroring for someone else
            if (sc.GetComponent<MirrorShadowTag>() != null) continue;

            var instanceId = sc.GetInstanceID().ToString();
            var pos = sc.transform.position;
            var ry = sc.transform.eulerAngles.y;
            var type = sc.gameObject.name.IndexOf("immortal", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;

            if (IsDead(sc))
            {
                // Announce a death exactly once (corpse lingers before despawn)
                if (_deadSent.Add(instanceId))
                    Send(instanceId, type, pos, ry, dead: true, reliable: true);
                continue; // no longer a live shadow to track
            }

            _sentThisScan.Add(instanceId);
            Send(instanceId, type, pos, ry, dead: false, reliable: false);
        }

        // Shadows we reported alive last scan that are gone now (destroyed without a
        // die() we caught) -> tell partners to drop them
        foreach (var id in _sentLastScan)
        {
            if (_sentThisScan.Contains(id) || _deadSent.Contains(id)) continue;
            _deadSent.Add(id);
            _network.SendReliable(new ShadowStatePacket
            {
                PlayerId = myId,
                InstanceId = id,
                ShadowType = 0,
                X = 0, Y = 0, Z = 0, Ry = 0,
                Dead = true
            });
        }
        if (_deadSent.Count > 256) _deadSent.Clear();

        _sentLastScan.Clear();
        foreach (var id in _sentThisScan) _sentLastScan.Add(id);
    }

    private void Send(string instanceId, int type, Vector3 pos, float ry, bool dead, bool reliable)
    {
        var packet = new ShadowStatePacket
        {
            PlayerId = Math.Max(_network.LocalClientId, 0),
            InstanceId = instanceId,
            ShadowType = type,
            X = pos.x, Y = pos.y, Z = pos.z, Ry = ry,
            Dead = dead
        };
        if (reliable) _network.SendReliable(packet);
        else _network.Send(packet);
    }

    /// <summary>Join bulk: live local shadows for a late joiner (Horde SendShadowsTo).</summary>
    public void CollectSnapshot(List<Packets.Packet> into)
    {
        if (into == null || !ResolveApi()) return;
        try
        {
            if (Core.isDay()) return;
        }
        catch { return; }

        var myId = Math.Max(_network.LocalClientId, 0);
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(_shadowType))
        {
            if (obj is not Component sc) continue;
            if (sc.GetComponent<MirrorShadowTag>() != null) continue;
            if (IsDead(sc)) continue;
            var pos = sc.transform.position;
            var ry = sc.transform.eulerAngles.y;
            var type = sc.gameObject.name.IndexOf("immortal", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;
            into.Add(new ShadowStatePacket
            {
                PlayerId = myId,
                InstanceId = sc.GetInstanceID().ToString(),
                ShadowType = type,
                X = pos.x, Y = pos.y, Z = pos.z, Ry = ry,
                Dead = false
            });
        }
    }

    private bool IsDead(Component sc)
    {
        try
        {
            var f = _shadowType.GetField("dead",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return f?.GetValue(sc) is bool b && b;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------
    // Mirror side
    // ------------------------------------------------------------------

    public void OnRemoteShadow(int playerId, string instanceId, int type, Vector3 pos, float ry, bool dead)
    {
        if (!ResolveApi()) return;
        var key = playerId + ":" + instanceId;

        if (!_mirrors.TryGetValue(key, out var mirror))
        {
            if (dead) return; // never saw it alive, nothing to remove
            mirror = SpawnMirror(type, pos, ry);
            if (mirror == null) return;
            _mirrors[key] = mirror;
        }

        mirror.TargetPos = pos;
        mirror.TargetRot = Quaternion.Euler(90f, ry, 0f);
        mirror.LastUpdate = Time.time;

        if (dead)
        {
            KillMirror(mirror);
            _mirrors.Remove(key);
        }
    }

    private MirrorShadow SpawnMirror(int type, Vector3 pos, float ry)
    {
        GameObject go = null;
        RemoteApply.Active = true;
        try
        {
            var prefab = type == 1 ? "characters/fakechars/shadow_immortal" : "characters/fakechars/shadow";
            go = Core.AddPrefab(prefab, pos, Quaternion.Euler(90f, ry, 0f), null);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ShadowSync] mirror spawn failed: {ex.Message}");
        }
        finally
        {
            RemoteApply.Active = false;
        }
        if (go == null) return null;

        go.AddComponent<MirrorShadowTag>();

        // Disable the shadow's own AI so it neither chases the observer nor
        // spawns attack sensors; drive it purely from network updates.
        var sc = go.GetComponent(_shadowType);
        if (sc is Behaviour behaviour) behaviour.enabled = false;

        var anim = go.GetComponentInChildren<tk2dSpriteAnimator>();
        if (anim != null && anim.GetClipByName("Float") != null)
            anim.Play("Float");

        return new MirrorShadow
        {
            Object = go,
            Animator = anim,
            TargetPos = pos,
            TargetRot = Quaternion.Euler(90f, ry, 0f),
            LastUpdate = Time.time
        };
    }

    private void InterpolateMirrors()
    {
        if (_mirrors.Count == 0) return;
        var now = Time.time;
        var t = 1f - Mathf.Exp(-PosSharpness * Time.deltaTime);
        _staleBuffer.Clear();

        foreach (var kvp in _mirrors)
        {
            var m = kvp.Value;
            if (m.Object == null)
            {
                _staleBuffer.Add(kvp.Key);
                continue;
            }
            if (now - m.LastUpdate > MirrorTimeout)
            {
                KillMirror(m);
                _staleBuffer.Add(kvp.Key);
                continue;
            }

            var tr = m.Object.transform;
            if ((tr.position - m.TargetPos).sqrMagnitude > 400f)
            {
                tr.position = m.TargetPos;
                tr.rotation = m.TargetRot;
            }
            else
            {
                tr.position = Vector3.Lerp(tr.position, m.TargetPos, t);
                tr.rotation = Quaternion.Slerp(tr.rotation, m.TargetRot, t);
            }
        }

        foreach (var id in _staleBuffer)
            _mirrors.Remove(id);
    }

    private void KillMirror(MirrorShadow m)
    {
        if (m?.Object == null) return;
        try
        {
            if (m.Animator != null && m.Animator.GetClipByName("Death1") != null)
                m.Animator.Play("Death1");
            UnityEngine.Object.Destroy(m.Object, 2f);
        }
        catch
        {
            UnityEngine.Object.Destroy(m.Object);
        }
    }

    public void Reset()
    {
        foreach (var m in _mirrors.Values)
            if (m?.Object != null) UnityEngine.Object.Destroy(m.Object);
        _mirrors.Clear();
        _sentThisScan.Clear();
        _sentLastScan.Clear();
        _deadSent.Clear();
    }

    private class MirrorShadow
    {
        public GameObject Object;
        public tk2dSpriteAnimator Animator;
        public Vector3 TargetPos;
        public Quaternion TargetRot = Quaternion.identity;
        public float LastUpdate;
    }
}

/// <summary>Marks a locally-spawned mirror of a partner's shadow so the owner scan skips it.</summary>
public class MirrorShadowTag : MonoBehaviour { }
