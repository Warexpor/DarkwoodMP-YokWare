using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Replaces Sniffer.Update entirely on the host.
    /// Checks BOTH the host player (Player.Instance) and ALL proxies for smell range,
    /// and sniffs the closest one. When the sniff completes, attacks whichever player
    /// triggered the sniff.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Sniffer), "Update")]
    public static class HostSnifferUpdatePatch
    {
        /// <summary>Maps Sniffer → playerId of the target that triggered the current/next sniff.</summary>
        private static readonly Dictionary<Sniffer, int> _sniffTargetPlayerId = new Dictionary<Sniffer, int>();

        public static void Reset()
        {
            _sniffTargetPlayerId.Clear();
        }

        private static bool Prefix(Sniffer __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (!PlayerPositionManager.HasRemotePlayer)
                return true;
            if (__instance.disabled)
                return false;

            Character charComponent = __instance.GetComponent<Character>();
            if (charComponent == null)
                return false;

            // Let the original Sniffer.Update run when ALL proxies are outside
            // the entity's effective detection range (visual + smell).
            if (ProxyDistanceHelper.ProxyIsFar(charComponent))
                return true;

            // Entity is busy — skip sniff logic
            if (charComponent.behaviour == Character.Behaviour.chasingTarget ||
                charComponent.behaviour == Character.Behaviour.defensive ||
                charComponent.behaviour == Character.Behaviour.escaping)
                return false;

            var net = LanNetworkManager.Instance;
            if (net == null) return false;

            // --- Sniff lifecycle ---
            var tSniff = Traverse.Create(__instance);
            float timeStarted = tSniff.Field("timeStartedSniffing").GetValue<float>();

            if (__instance.sniffing)
            {
                if (Time.time - timeStarted > __instance.sniffTime)
                    StopSniffAndAttack(__instance, charComponent, net);
                return false;
            }

            if (__instance.canSniff)
            {
                // Check BOTH host and all proxies for proximity
                bool hostInRange = Player.Instance != null &&
                    Core.trueDistance(__instance.transform.position, Player.Instance._transform.position) < __instance.radius;

                // Find the closest in-range proxy
                int closestProxyId = -1;
                float closestProxyDist = float.MaxValue;
                foreach (var proxy in net.GetAllProxies())
                {
                    if (proxy == null) continue;
                    float d = Core.trueDistance(__instance.transform.position, proxy.transform.position);
                    if (d < __instance.radius && d < closestProxyDist)
                    {
                        closestProxyDist = d;
                        closestProxyId = proxy.PlayerId;
                    }
                }

                if (!hostInRange && closestProxyId < 0)
                    return false; // neither host nor any proxy in range

                float distToHost = hostInRange
                    ? Core.trueDistance(__instance.transform.position, Player.Instance._transform.position)
                    : float.MaxValue;

                // Sniff the closer player (or host if equal)
                bool sniffProxy = closestProxyId >= 0 && (!hostInRange || closestProxyDist < distToHost);

                _sniffTargetPlayerId[__instance] = sniffProxy ? closestProxyId : -1; // -1 = host

                __instance.sniffing = true;
                tSniff.Field("timeStartedSniffing").SetValue(Time.time);
                AudioController.Play(__instance.sniffSound, __instance.transform);
                return false;
            }

            // Cooldown — same as original (cooldownTime from sniff start)
            if (Time.time - timeStarted > __instance.cooldownTime)
                __instance.canSniff = true;
            return false;
        }

        private static void StopSniffAndAttack(Sniffer __instance, Character charComponent, LanNetworkManager net)
        {
            __instance.sniffing = false;
            __instance.canSniff = false;

            if (charComponent == null || !charComponent.alive)
                return;

            if (charComponent.behaviour == Character.Behaviour.escaping)
                return;

            if (charComponent.sleeping && !charComponent.wakeUpOnlyManually)
            {
                charComponent.wakeup();
                charComponent.sleeping = false;
            }

            int targetPlayerId;
            if (!_sniffTargetPlayerId.TryGetValue(__instance, out targetPlayerId))
                return; // no target recorded — should not happen
            _sniffTargetPlayerId.Remove(__instance);

            if (targetPlayerId >= 0)
            {
                // Attack a specific proxy
                RemotePlayerProxy proxy = net.GetProxy(targetPlayerId);
                if (proxy == null) return;

                CharBase proxyCB = proxy.GetComponent<CharBase>();
                if (proxyCB == null || proxyCB.invisible || proxyCB.ignoreMe)
                    return;

                charComponent.attackCharacter(proxy.transform);
            }
            else
            {
                // Attack the host
                Player host = Player.Instance;
                if (host == null || host.invisible || host.ignoreMe)
                    return;

                charComponent.attackPlayer();
            }
        }
    }

    /// <summary>
    /// Applies EnemyOfTheForest / FriendOfTheForest effects when a
    /// remote proxy is seen near an animalAggressive entity, overriding
    /// its default behaviour.
    /// </summary>
    [HarmonyPatch(typeof(Character), "onSeeEnemyNear")]
    public static class HostOnSeeEnemyNearPatch
    {
        private static void Postfix(Character __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (!PlayerPositionManager.HasRemotePlayer)
                return;
            if (__instance.target == null)
                return;
            if (__instance.faction != Faction.animalAggressive)
                return;

            RemotePlayerProxy proxy = __instance.target.GetComponent<RemotePlayerProxy>();
            if (proxy == null)
                return;

            if (proxy.RemoteHasEnemyOfTheForest)
            {
                if (__instance.behaviour != Character.Behaviour.chasingTarget)
                    __instance.setBehaviour(Character.Behaviour.chasingTarget);
            }
            else if (proxy.RemoteHasFriendOfTheForest)
            {
                __instance.setBehaviour(Character.Behaviour.defensive);
            }
        }
    }

    /// <summary>
    /// Ensures growl audio + alertCharactersInArea fire even when the
    /// target is a remote proxy. Vanilla growl is skipped for non-Player targets.
    /// </summary>
    [HarmonyPatch(typeof(Character), "growl")]
    public static class HostGrowlPatch
    {
        private static bool Prefix(Character __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (!PlayerPositionManager.HasRemotePlayer)
                return true;
            if (__instance.target == null)
                return true;
            if (__instance.target.GetComponent<RemotePlayerProxy>() == null)
                return true;

            // Proxy target — skip vanilla growl (only works for Player.Instance)
            // and play the growl + area-alert ourselves to avoid double-fire.
            if (__instance.sounds != null && !__instance.sleeping)
                __instance.sounds.playGrowl();

            var alertMethod = AccessTools.Method(typeof(Character), "alertCharactersInArea", new[] { typeof(float), typeof(bool) });
            alertMethod?.Invoke(__instance, new object[] { 500f, false });

            return false;
        }
    }

    /// <summary>
    /// The original checkForNewEnemyCloserThanTarget just picks the first valid
    /// entry in charactersInSight regardless of distance. This Prefix replaces
    /// it with a version that actually finds the CLOSEST enemy, so entities
    /// switch between host and proxy based on proximity.
    /// </summary>
    [HarmonyPatch(typeof(Character), "checkForNewEnemyCloserThanTarget")]
    public static class HostCheckForCloserEnemyPatch
    {
        private static bool Prefix(Character __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (!PlayerPositionManager.HasRemotePlayer)
                return true;

            if (ProxyDistanceHelper.ProxyIsFar(__instance))
                return true;

            if (__instance.charactersInSight.Count == 0)
                return false;

            Transform currentTarget = __instance.target;
            float currentDist = currentTarget != null
                ? Core.trueDistance(__instance.transform.position, currentTarget.position)
                : float.MaxValue;
            float closestDist = currentDist;
            Transform closestTransform = null;

            for (int i = 0; i < __instance.charactersInSight.Count; i++)
            {
                CharBase cb = __instance.charactersInSight[i];
                if (cb == null || !cb.alive) continue;
                if (!__instance.attacksFaction(cb.faction)) continue;
                if (cb.transform == currentTarget) continue;

                float d = Core.trueDistance(__instance.transform.position, cb.transform.position);
                if (d < closestDist)
                {
                    closestDist = d;
                    closestTransform = cb.transform;
                }
            }

            if (closestTransform != null)
                __instance.attackCharacter(closestTransform);

            return false;
        }
    }

    /// <summary>
    /// InSightOfPlayer (banshee agitation, sight events) only tested host FOV.
    /// </summary>
    [HarmonyPatch(typeof(InSightOfPlayer), "checkSight")]
    public static class HostInSightOfPlayerCheckSightPatch
    {
        private static bool Prefix(InSightOfPlayer __instance, bool force)
        {
            if (!HostPlayerIdentity.HostWithRemotes())
                return true;
            if (__instance == null)
                return true;

            bool seen = HostPlayerIdentity.AnyInSight(__instance.transform, __instance.playerCanBeFarAway);
            var t = Traverse.Create(__instance);
            if (seen)
            {
                if (!__instance.inSightOfPlayer || force)
                {
                    __instance.inSightOfPlayer = true;
                    t.Field("timeInSightOfPlayer").SetValue(Time.time);
                }
                if (!__instance.firedInSight)
                    t.Method("doInSight").GetValue();
            }
            else
            {
                if (__instance.inSightOfPlayer || force)
                {
                    __instance.inSightOfPlayer = false;
                    t.Field("timeOutOfSightOfPlayer").SetValue(Time.time);
                }
                if (!__instance.firedOutOfSight)
                    t.Method("doOutOfSight").GetValue();
            }
            return false;
        }
    }

    /// <summary>
    /// Shooter.Start binds playerIsTarget to the host; shoot() damages Player.Instance
    /// even when the ray is aimed at someone else.
    /// </summary>
    [HarmonyPatch(typeof(Shooter), "Start")]
    public static class HostShooterStartPatch
    {
        private static void Postfix(Shooter __instance)
        {
            if (__instance == null || !__instance.playerIsTarget)
                return;
            if (!HostPlayerIdentity.HostWithRemotes())
                return;
            GameObject go = HostPlayerIdentity.NearestLivingGo(__instance.transform.position);
            if (go != null)
                __instance.target = go;
        }
    }

    [HarmonyPatch(typeof(Shooter), "shoot")]
    public static class HostShooterShootPatch
    {
        private static bool Prefix(Shooter __instance)
        {
            if (!HostPlayerIdentity.HostWithRemotes())
                return true;
            if (__instance == null || __instance.target == null)
                return true;

            GameObject nearest = HostPlayerIdentity.NearestLivingGo(__instance.transform.position);
            if (nearest != null)
                __instance.target = nearest;

            Transform targetT = __instance.target.transform;
            Vector3 vector = new Vector3(__instance.transform.position.x, 0f, __instance.transform.position.z);
            Vector3 vector2 = new Vector3(targetT.position.x, 0f, targetT.position.z);
            Traverse.Create(__instance).Field("lastTimeShot").SetValue(Time.time);
            if (!string.IsNullOrEmpty(__instance.shootSound))
                AudioController.Play(__instance.shootSound, __instance.transform);

            float num = UnityEngine.Random.Range(0f - __instance.accuracy, __instance.accuracy);
            if (!Core.canSee(__instance.transform, targetT))
                num *= 1.5f;
            Vector3 vector3 = Quaternion.Euler(0f, num, 0f) * (vector2 - vector).normalized;
            float num2 = Core.trueDistance(vector, vector2)
                + UnityEngine.Random.Range(0f - __instance.radius, __instance.radius);

            CharBase cb = targetT.GetComponent<CharBase>();
            if (cb != null && cb.alive && Core.canSee(__instance.transform, targetT))
            {
                cb.getHit(
                    __instance.damage / Core.trueDistance(__instance.transform, targetT),
                    null,
                    CanCutInHalf: false,
                    byPlayer: false,
                    canInterrupt: false,
                    normalHit: false,
                    showRedScreen: true);
            }

            if (Physics.Raycast(vector, vector3, out var hitInfo, num2, 16809985))
            {
                Core.AddPrefab(
                    __instance.wallHitPrefab,
                    hitInfo.point,
                    Quaternion.Euler(90f, Helpers.angleBetween(vector, hitInfo.point, __instance.transform.forward), 0f),
                    null);
            }
            else
            {
                Core.AddPrefab(
                    __instance.hitPrefab,
                    vector + vector3 * num2,
                    Quaternion.Euler(90f, 0f, 0f),
                    null);
            }
            return false;
        }
    }

    /// <summary>
    /// The Three mercy attacks keyed off host HP only.
    /// </summary>
    [HarmonyPatch(typeof(Character), "checkBrotherWeakAttack")]
    public static class HostBrotherWeakAttackPatch
    {
        private static bool Prefix(Character __instance)
        {
            if (!HostPlayerIdentity.HostWithRemotes())
                return true;
            if (__instance == null || __instance.attacks == null || __instance.attacks.Count < 3)
                return true;

            float minHp = Player.Instance != null ? Player.Instance.health : float.MaxValue;
            var net = LanNetworkManager.Instance;
            if (net != null)
            {
                foreach (var proxy in net.GetAllProxies())
                {
                    if (proxy == null)
                        continue;
                    CharBase cb = proxy.GetComponent<CharBase>();
                    if (cb != null && cb.alive && cb.Health < minHp)
                        minHp = cb.Health;
                }
            }

            if (minHp >= 20f)
                return true;

            __instance.attacks[0].disabled = true;
            __instance.attacks[1].disabled = true;
            __instance.attacks[2].disabled = false;
            return false;
        }
    }
}
