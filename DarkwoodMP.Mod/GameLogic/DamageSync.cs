using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.Patches;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Applies remote damage:
///  - environmental damageable objects (DamageUpdatePacket, legacy path)
///  - friendly fire: another player hit OUR clone on their machine, so the
///    damage is applied to the real local player here.
/// </summary>
public class DamageSync
{
    private readonly NetworkLayer _network;
    private readonly Dictionary<string, GameObjectState> _trackedObjects = new();

    /// <summary>Horde-style peer damage cap (grief / bad packets).</summary>
    public const float MaxPeerDamage = 200f;
    private const float FriendlyFireDebounceSec = 0.08f;
    private readonly Dictionary<int, float> _ffDebounce = new Dictionary<int, float>();

    private Type _playerType;
    private MethodInfo _getHitBig; // getHit(float, Transform, bool x7)
    private MethodInfo _getHitSmall; // getHit(float)

    private struct GameObjectState
    {
        public string ObjectId;
        public float Health;
        public float MaxHealth;
        public bool IsDestroyed;
        public Material[] OriginalMaterials;
        public MeshRenderer Renderer;
    }

    public DamageSync(NetworkLayer network)
    {
        _network = network;
    }

    // ------------------------------------------------------------------
    // Friendly fire (receive side)
    // ------------------------------------------------------------------

    /// <summary>Clamp untrusted peer-reported damage (Horde SanitizePeerDamage).</summary>
    public static float SanitizePeerDamage(float damage)
    {
        if (float.IsNaN(damage) || float.IsInfinity(damage) || damage <= 0f) return 0f;
        return Mathf.Min(damage, MaxPeerDamage);
    }

    /// <summary>Another player hit us - apply the damage to the real local player.</summary>
    public void ApplyRemotePlayerHit(int attackerId, float damage)
    {
        damage = SanitizePeerDamage(damage);
        if (damage <= 0f) return;
        if (!ResolvePlayerApi()) return;

        // 80ms debounce per attacker (shotgun multi-collider / packet spam)
        var now = Time.time;
        if (_ffDebounce.TryGetValue(attackerId, out var last) && now - last < FriendlyFireDebounceSec)
            return;
        _ffDebounce[attackerId] = now;
        if (_ffDebounce.Count > 64) _ffDebounce.Clear();

        var playerTransform = DarkwoodMP.DependencyInjection.ServiceLocator
            .Resolve<PlayerSync>()?.LocalPlayerTransform;
        var player = playerTransform != null ? playerTransform.GetComponent(_playerType) : null;
        if (player == null) return;

        // Attacker transform decides knockback/red-screen direction
        var attackerObj = NetworkManager.Instance?.GetRemotePlayer(attackerId);
        var attacker = attackerObj != null ? attackerObj.transform : playerTransform;

        RemoteApply.Active = true;
        try
        {
            if (_getHitBig != null)
            {
                // getHit(damage, attackerTransform, CanCutInHalf, byPlayer,
                //        canInterrupt, normalHit, showRedScreen, force, dontShowHealthBar)
                _getHitBig.Invoke(player, new object[] { damage, attacker, false, true, true, true, true, false, false });
            }
            else
            {
                _getHitSmall?.Invoke(player, new object[] { damage });
            }
            ModLogger.Msg($"[DamageSync] Hit by player {attackerId} for {damage:F0}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[DamageSync] Failed to apply pvp hit: {ex.Message}");
        }
        finally
        {
            RemoteApply.Active = false;
        }
    }

    public void Reset()
    {
        _ffDebounce.Clear();
        _trackedObjects.Clear();
    }

    /// <summary>
    /// A melee hit on our clone carried a status effect (enemy poison bite,
    /// player weapon effect) - the raw damage came via ApplyRemotePlayerHit,
    /// this applies the effect itself with its duration/strength (v0.4).
    /// </summary>
    public void ApplyRemoteEffect(int effectType, float duration, float modifier, float interval)
    {
        try
        {
            var playerTransform = DarkwoodMP.DependencyInjection.ServiceLocator
                .Resolve<PlayerSync>()?.LocalPlayerTransform;
            if (playerTransform == null) return;
            var effects = playerTransform.GetComponent<CharacterEffects>();
            if (effects == null) return;

            // Clamp against garbage packets - effects run on the real player
            duration = Mathf.Clamp(duration, 0f, 600f);

            RemoteApply.Active = true;
            try
            {
                effects.activate((CharacterEffectType)effectType, duration, modifier, interval, 0f);
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[DamageSync] Remote effect {(CharacterEffectType)effectType} ({duration:F0}s)");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[DamageSync] Failed to apply remote effect: {ex.Message}");
        }
    }

    private bool ResolvePlayerApi()
    {
        if (_playerType != null) return _getHitBig != null || _getHitSmall != null;

        _playerType = GameTypes.GetType("Player");
        if (_playerType == null) return false;

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var m in _playerType.GetMethods(flags))
        {
            if (m.Name != "getHit") continue;
            var ps = m.GetParameters();
            if (ps.Length == 9) _getHitBig = m;
            else if (ps.Length == 1) _getHitSmall = m;
        }
        return _getHitBig != null || _getHitSmall != null;
    }

    // ------------------------------------------------------------------
    // Environmental object damage (legacy path)
    // ------------------------------------------------------------------

    public void OnDamageUpdate(DamageUpdatePacket packet)
    {
        var state = new GameObjectState
        {
            ObjectId = packet.ObjectId,
            Health = packet.Health,
            MaxHealth = packet.MaxHealth,
            IsDestroyed = packet.IsDestroyed
        };

        var obj = GameObject.Find(packet.ObjectId);
        if (obj != null)
        {
            state.Renderer = obj.GetComponentInChildren<MeshRenderer>();
            if (state.Renderer != null)
                state.OriginalMaterials = state.Renderer.materials;
        }

        _trackedObjects[packet.ObjectId] = state;

        if (obj != null)
            ApplyVisualDamage(obj, state);

        if (packet.IsDestroyed)
            obj?.SetActive(false);

        ModLogger.Msg($"[DamageSync] Object '{packet.ObjectId}' health={packet.Health:F0}/{packet.MaxHealth:F0} destroyed={packet.IsDestroyed}");
    }

    private void ApplyVisualDamage(GameObject obj, GameObjectState state)
    {
        var renderer = state.Renderer;
        if (renderer == null || state.OriginalMaterials == null) return;

        float ratio = state.MaxHealth > 0f ? Mathf.Clamp01(state.Health / state.MaxHealth) : 0f;

        for (int i = 0; i < renderer.materials.Length; i++)
        {
            var mat = renderer.materials[i];
            if (mat == null) continue;

            // Darken material based on damage
            var color = mat.color;
            var darkened = Color.Lerp(color, Color.black, 1f - ratio);
            darkened.a = color.a;
            mat.color = darkened;

            if (state.IsDestroyed)
            {
                mat.color = new Color(color.r, color.g, color.b, 0.3f);
                obj.SetActive(false);
            }
        }
    }
}
