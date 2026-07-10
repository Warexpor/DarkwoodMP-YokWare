using DWMPHorde.Config;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// After any explosion runs on the host, damage remote proxies in the blast
    /// via targeted <see cref="NetMessageType.DamagePlayer"/> (never broadcast).
    ///
    /// Environmental blasts (barrels, gas) always hurt all players.
    /// Player-thrown explosives respect FriendlyFireEnabled for teammates, but
    /// the thrower always takes their own blast (vanilla self-damage).
    /// Host Player.Instance is already damaged by vanilla explode().
    /// </summary>
    [HarmonyPatch(typeof(Explodes), "explode")]
    public static class ExplosionFriendlyFirePatch
    {
        private static void Postfix(Explodes __instance)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Host) return;
            if (!net.IsConnected) return;
            if (__instance == null) return;
            if (!__instance.affectsPlayer) return;
            if (__instance.radius <= 0f || __instance.damage <= 0f) return;

            try
            {
                if (__instance.transform == null) return;
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ExplosionFF] transform check failed: " + ex.Message);
                return;
            }

            Transform source = ResolveSpawnSource(__instance);
            bool playerSourced = IsPlayerSourced(source, net);
            int throwerPlayerId = playerSourced ? ResolveSourcePlayerId(source, net) : 0;
            bool ffEnabled = ModConfig.FriendlyFireEnabled == null || ModConfig.FriendlyFireEnabled.Value;

            Vector3 blastPos = __instance.transform.position;
            float radius = __instance.radius;
            float baseDamage = __instance.damage;

            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null) continue;

                CharBase proxyCb = proxy.GetComponent<CharBase>();
                if (proxyCb != null && !proxyCb.alive)
                    continue;

                // Player-thrown: when FF is off, only the thrower takes proxy damage
                // (teammates are spared). Environmental blasts hit everyone.
                if (playerSourced && !ffEnabled && throwerPlayerId > 0 && proxy.PlayerId != throwerPlayerId)
                    continue;

                Transform proxyT = proxy.transform;
                float dist = Vector3.Distance(blastPos, proxyT.position);
                if (dist > radius) continue;

                if (!Core.canSee(__instance.transform, proxyT)) continue;

                float falloff = (radius - dist) / radius;
                if (falloff <= 0f) continue;

                int damage = Mathf.Max(1, Mathf.RoundToInt(baseDamage * falloff));
                // Match peer attack clamp so griefy/exploding stacks stay bounded.
                int max = ModConfig.MaxPeerDamage != null ? ModConfig.MaxPeerDamage.Value : 200;
                if (max < 1) max = 1;
                if (damage > max) damage = max;

                Vector3 pos = proxyT.position;
                net.SendDamagePlayer(proxy.PlayerId, new DamagePlayerMessage
                {
                    Damage = damage,
                    AttackerPosX = pos.x,
                    AttackerPosY = pos.y,
                    AttackerPosZ = pos.z,
                    ShowRedScreen = true
                });
                ModRuntime.LegacyInfo(
                    $"[ExplosionFF] blast at {blastPos} → player {proxy.PlayerId} dmg={damage} " +
                    $"(playerSourced={playerSourced} ff={ffEnabled} thrower={throwerPlayerId})");
            }
        }

        private static Transform ResolveSpawnSource(Explodes expl)
        {
            if (expl.objectThatSpawnedMe != null)
                return expl.objectThatSpawnedMe;
            ThrownItem ti = expl.GetComponent<ThrownItem>();
            if (ti != null && ti.objectThatSpawnedMe != null)
                return ti.objectThatSpawnedMe;
            return null;
        }

        private static bool IsPlayerSourced(Transform source, LanNetworkManager net)
        {
            if (source == null) return false;
            if (Player.Instance != null)
            {
                Transform pt = Player.Instance.transform;
                if (source == pt || source.IsChildOf(pt))
                    return true;
            }
            return source.GetComponentInParent<RemotePlayerProxy>() != null;
        }

        private static int ResolveSourcePlayerId(Transform source, LanNetworkManager net)
        {
            if (source == null || net == null) return 0;
            if (Player.Instance != null)
            {
                Transform pt = Player.Instance.transform;
                if (source == pt || source.IsChildOf(pt))
                    return net.LocalPlayerId;
            }
            RemotePlayerProxy proxy = source.GetComponentInParent<RemotePlayerProxy>();
            return proxy != null ? proxy.PlayerId : 0;
        }
    }
}
