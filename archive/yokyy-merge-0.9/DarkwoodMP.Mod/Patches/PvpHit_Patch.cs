using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Friendly-fire hit DETECTION. Verified in IL why the getHit route alone
/// never fired: both MeleeSensor.OnTriggerEnter and Player.spawnBullet resolve
/// targets via GetComponent&lt;Character&gt;() - a remote-player clone (Player
/// component) is never a valid target for the game's own combat code, so
/// Player.getHit was never called on clones.
///
/// Melee: the sensor's trigger collider still physically touches the clone,
/// so a postfix on OnTriggerEnter sees the contact and forwards the damage.
/// Bullets: a postfix on spawnBullet re-casts along the player's aim
/// (transform.up, same convention as throwItem) and forwards a hit if the
/// first thing in the line of fire is a clone.
/// </summary>
public sealed class PvpHit_Patch : IPatch
{
    // A melee sensor overlaps several of the clone's colliders (body, legs)
    // within one swing, so hits are deduped - but only for a short WINDOW, not
    // forever: the MeleeSensor is a persistent component reused every swing, so
    // a permanent (sensor^target) dedupe let the FIRST swing land and then
    // blocked every later swing on the same target ("could hit once, then never
    // again"). A time window collapses the multi-collider double-hit of one
    // swing while still allowing the next swing.
    private static readonly Dictionary<long, float> _meleeHitTime = new();
    private const float MeleeRehitWindow = 0.25f;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        _meleeHitTime.Clear();
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var name = target.DeclaringType!.Name == "MeleeSensor" ? nameof(MeleePostfix) : nameof(BulletPostfix);
        baseHarmony.Patch(target, postfix: new HarmonyMethod(typeof(PvpHit_Patch).GetMethod(name, statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("MeleeSensor", "OnTriggerEnter");
        yield return ("Player", "spawnBullet");
    }

    // Parameter name matches MeleeSensor.OnTriggerEnter(Collider _collider)
    public static void MeleePostfix(object __instance, Collider _collider)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not MeleeSensor sensor || _collider == null) return;

            var rootName = _collider.transform.root.name;
            if (!rootName.StartsWith("RemotePlayer_")) return;

            // Forward swings that the target's machine does not simulate:
            //  - the LOCAL player's own attacks (friendly fire)
            //  - attacks of enemies THIS machine owns (an enemy swinging at a
            //    clone - its frozen twin on the victim's machine never attacks)
            var localPlayer = Player.Instance;
            var isOurSwing = localPlayer != null && sensor.attackerTransform == localPlayer.transform;
            if (!isOurSwing)
            {
                var attackerEnemy = sensor.attackerTransform != null
                    ? sensor.attackerTransform.GetComponent<Character>()
                    : null;
                if (attackerEnemy == null) return;
                var enemySync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<GameLogic.EnemySync>();
                if (enemySync == null || !enemySync.IsLocallySimulated(attackerEnemy)) return;
            }

            if (!TryParseCloneId(rootName, out var targetId)) return;

            var key = ((long)sensor.GetInstanceID() << 32) ^ (uint)targetId;
            var now = Time.time;
            if (_meleeHitTime.TryGetValue(key, out var last) && now - last < MeleeRehitWindow) return;
            _meleeHitTime[key] = now;
            if (_meleeHitTime.Count > 256) _meleeHitTime.Clear(); // bound the map

            SendPvp(targetId, sensor.damage);

            // Forward the sensor's status effects too (enemy poison bite,
            // weapon effects) - damage alone would silently drop them (v0.4)
            if (sensor.effects != null)
            {
                foreach (var fx in sensor.effects)
                {
                    if (fx == null) continue;
                    SendEffect(targetId, (int)fx.type, fx.duration, fx.modifier, fx.interval);
                }
            }

            ModLogger.Msg($"[PvpHit] {(isOurSwing ? "Melee" : "Enemy melee")} hit on player {targetId} ({sensor.damage} dmg)");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[PvpHit_Patch] {ex.Message}");
        }
    }

    private static int _lastBulletFrame = -1;

    public static void BulletPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            // Shotguns call spawnBullet once per pellet in the same frame; the
            // recast can't tell pellets apart, so count the shot once
            if (_lastBulletFrame == Time.frameCount) return;
            _lastBulletFrame = Time.frameCount;
            if (__instance is not Player player) return;
            if (player.transform.root.name.StartsWith("RemotePlayer_")) return;
            if (InvItemClass.isNull(player.currentItem) || player.currentItem.baseClass == null) return;
            if (!player.currentItem.baseClass.isFirearm) return;

            // Recast along the aim direction. Verified against Player.spawnBullet
            // IL: the game fires along `_transform.up` (rotated by the random
            // spread) from `position + up` with range 1000 - match both, or
            // long shots silently miss.
            // QueryTriggerInteraction.Collide is REQUIRED: the clone's body
            // collider is a trigger, and if the game runs with
            // Physics.queriesHitTriggers=false a default raycast can never see
            // it - shots pass straight through the partner.
            var origin = player.transform.position;
            var direction = player.transform.up;
            var hits = Physics.RaycastAll(origin + direction, direction, 1000f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);

            // Find the nearest clone in the line of fire AND the nearest SOLID
            // obstacle. The clone counts only if nothing solid is closer (a wall
            // stops the bullet). Crucially we do NOT skip trigger colliders when
            // looking for the clone: the remote-player clone's body collider is a
            // trigger, which is exactly why the old "skip triggers" recast never
            // registered a hit and ranged PvP did no damage.
            var cloneDist = float.MaxValue;
            string cloneRoot = null;
            var blockerDist = float.MaxValue;
            foreach (var hit in hits)
            {
                if (hit.collider == null) continue;
                var root = hit.collider.transform.root;
                if (root == player.transform.root) continue; // our own colliders
                if (root.name.StartsWith("RemotePlayer_"))
                {
                    if (hit.distance < cloneDist) { cloneDist = hit.distance; cloneRoot = root.name; }
                }
                else if (!hit.collider.isTrigger && hit.distance < blockerDist)
                {
                    blockerDist = hit.distance; // a wall / solid object blocks the shot
                }
            }
            if (cloneRoot == null || cloneDist > blockerDist) return; // missed or blocked
            if (!TryParseCloneId(cloneRoot, out var targetId)) return;

            var damage = player.currentItem.getModdedDamage(player.currentItem.baseClass.damage);
            SendPvp(targetId, damage);
            ModLogger.Msg($"[PvpHit] Shot player {targetId} ({damage} dmg)");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[PvpHit_Patch] {ex.Message}");
        }
    }

    private static bool TryParseCloneId(string rootName, out int targetId)
    {
        // "RemotePlayer_{id}_{name}"
        targetId = -1;
        var parts = rootName.Split('_');
        return parts.Length >= 2 && int.TryParse(parts[1], out targetId);
    }

    private static void SendEffect(int targetId, int effectType, float duration, float modifier, float interval)
    {
        if (duration <= 0f) return;
        var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
        if (network == null || !network.IsConnected) return;

        network.SendReliable(new PvpFxPacket
        {
            PlayerId = Math.Max(network.LocalClientId, 0),
            TargetPlayerId = targetId,
            EffectType = effectType,
            Duration = duration,
            Modifier = modifier,
            Interval = interval
        });
    }

    private static void SendPvp(int targetId, float damage)
    {
        if (damage <= 0f) return;
        var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
        if (network == null || !network.IsConnected) return;

        network.SendReliable(new PvpDamagePacket
        {
            PlayerId = Math.Max(network.LocalClientId, 0),
            TargetPlayerId = targetId,
            Damage = damage
        });
    }
}
