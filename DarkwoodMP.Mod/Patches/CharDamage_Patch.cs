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
/// Routes combat damage across the network. getHit is virtual on CharBase with
/// overrides on Player and Character (verified), so patching those two types
/// catches every damage source (MeleeSensor, Bullet, Burn, Explodes, ...).
///
/// Friendly fire: melee swings and bullets DO hit the remote-player clones
/// (they keep their colliders and CharBase component). Instead of damaging the
/// lifeless clone, the hit is forwarded to the player it represents
/// ("pvp:&lt;targetId&gt;:&lt;damage&gt;") and applied to their real Player there.
///
/// Enemy authority: on a client, damage to a HOST-CLAIMED enemy is suppressed
/// locally (the host owns that enemy) and - if it came from the local player's
/// own attack - forwarded as "enemyhit:&lt;hostId&gt;:&lt;damage&gt;". Environmental
/// damage (fire, explosions) is NOT forwarded: the host simulates its own copy
/// of those, forwarding it too would double the damage.
/// </summary>
public sealed class CharDamage_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        // Patch BOTH getHit overloads of the declaring type (the registry only
        // hands us the first match)
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var isPlayer = target.DeclaringType!.Name == "Player";
        foreach (var m in target.DeclaringType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (m.Name != "getHit") continue;
            var ps = m.GetParameters();
            string prefixName;
            if (ps.Length == 1) prefixName = isPlayer ? nameof(PlayerSmallPrefix) : nameof(CharacterSmallPrefix);
            else if (ps.Length == 9) prefixName = isPlayer ? nameof(PlayerBigPrefix) : nameof(CharacterBigPrefix);
            else continue;

            baseHarmony.Patch(m, prefix: new HarmonyMethod(typeof(CharDamage_Patch).GetMethod(prefixName, statics)!));
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Player", "getHit");
        yield return ("Character", "getHit");
    }

    // ------------------------------------------------------------------
    // Player (friendly fire on remote-player clones)
    // ------------------------------------------------------------------

    public static bool PlayerSmallPrefix(object __instance, float damage)
        => HandlePlayerHit(__instance, damage, byPlayer: false, attackerTransform: null);

    public static bool PlayerBigPrefix(object __instance, float damage, Transform attackerTransform, bool byPlayer)
        => HandlePlayerHit(__instance, damage, byPlayer, attackerTransform);

    /// <summary>Returns false (skip original) when the hit target is a clone.</summary>
    private static bool HandlePlayerHit(object instance, float damage, bool byPlayer, Transform attackerTransform)
    {
        try
        {
            if (RemoteApply.Active) return true;
            if (instance is not Component c) return true;

            var rootName = c.transform.root.name;
            if (!rootName.StartsWith("RemotePlayer_")) return true; // the real local player

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return false; // never simulate on clones

            // Forward only damage the victim's machine does NOT simulate itself:
            //  - byPlayer: our melee swings / bullets (attacker-side only)
            //  - enemy attacks (Character attacker): enemies never target the
            //    victim's copy on their machine
            // Environmental damage (explosions, fire, thrown-item landings) is
            // replicated natively on the victim's side - forwarding it too
            // would double it (e.g. a synced molotov).
            var enemyAttacker = attackerTransform != null && attackerTransform.GetComponent<Character>() != null;
            if (!byPlayer && !enemyAttacker) return false;

            // "RemotePlayer_{id}_{name}"
            var parts = rootName.Split('_');
            if (parts.Length < 2 || !int.TryParse(parts[1], out var targetId)) return false;

            network.SendReliable(new PvpDamagePacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                TargetPlayerId = targetId,
                Damage = damage
            });
            return false;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[CharDamage_Patch] {ex.Message}");
            return true;
        }
    }

    // ------------------------------------------------------------------
    // Character (client -> host damage forwarding for claimed enemies)
    // ------------------------------------------------------------------

    // Harmony binds prefix parameters to the target's parameters BY NAME, and
    // Character.getHit names its float "Damage" (capital D - Player uses
    // lowercase "damage"). A lowercase prefix param made BOTH Character
    // overload patches throw "Parameter not found" at apply time, so client
    // hits were never forwarded at all.
    public static bool CharacterSmallPrefix(object __instance, float Damage)
        => HandleCharacterHit(__instance, Damage, byPlayer: false, attackerTransform: null);

    public static bool CharacterBigPrefix(object __instance, float Damage, Transform attackerTransform, bool byPlayer)
        => HandleCharacterHit(__instance, Damage, byPlayer, attackerTransform);

    private static bool HandleCharacterHit(object instance, float damage, bool byPlayer, Transform attackerTransform)
    {
        try
        {
            if (RemoteApply.Active) return true;

            var manager = NetworkManager.Instance;
            if (manager == null || !manager.IsConnected) return true;
            if (instance is not Character ch) return true;

            // Host-authority: the authority simulates every enemy, so it applies
            // damage normally.
            if (manager.IsTimeAuthority) return true;

            // Non-authority: forward the local player's OWN attack to the authority
            // by stable id; never simulate enemy damage locally. Environmental
            // damage (fire/explosion) is simulated by the authority itself, so
            // only byPlayer hits are forwarded.
            if (byPlayer && CharacterTracker.TryGetStableId(ch, out var id))
            {
                var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
                if (network != null && network.IsConnected)
                {
                    network.SendReliable(new EntityAttackPacket
                    {
                        PlayerId = Math.Max(network.LocalClientId, 0),
                        EntityId = id.ToString(),
                        Damage = damage
                    });
                }
            }
            return false; // client never simulates enemy damage
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[CharDamage_Patch] {ex.Message}");
            return true;
        }
    }
}
