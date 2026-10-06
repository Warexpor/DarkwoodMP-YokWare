using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Replaces Sniffer.Update entirely on the host.
    /// Checks BOTH the host player (Player.Instance) and ALL proxies for smell range,
    /// and sniffs the one <see cref="PlayerTargetArbiter.PickSmelled"/> picks (the player it is
    /// already after, else the nearest). When the sniff completes, attacks whichever player
    /// triggered the sniff.
    /// </summary>
    [HarmonyPatch(typeof(Sniffer), "Update")]
    public static class HostSnifferUpdatePatch
    {
        /// <summary>Maps Sniffer → playerId of the target that triggered the current/next sniff.</summary>
        private static readonly Dictionary<Sniffer, int> _sniffTargetPlayerId = new Dictionary<Sniffer, int>();

        /// <summary>Sniffer instance id → owning Character (Update runs every frame).</summary>
        private static readonly Dictionary<int, Character> _characterBySniffer = new Dictionary<int, Character>();

        private static readonly AccessTools.FieldRef<Sniffer, float> TimeStartedSniffing =
            AccessTools.FieldRefAccess<Sniffer, float>("timeStartedSniffing");

        public static void Reset()
        {
            _sniffTargetPlayerId.Clear();
            _characterBySniffer.Clear();
        }

        private static Character CharacterOf(Sniffer sniffer)
        {
            int id = sniffer.GetInstanceID();
            if (!_characterBySniffer.TryGetValue(id, out Character c) || c == null)
            {
                if (_characterBySniffer.Count >= 4096)
                    _characterBySniffer.Clear();
                c = sniffer.GetComponent<Character>();
                _characterBySniffer[id] = c;
            }
            return c;
        }

        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(Sniffer __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (!PlayerPositionManager.HasRemotePlayer)
                return true;
            if (__instance.disabled)
                return false;

            Character charComponent = CharacterOf(__instance);
            if (charComponent == null)
                return false;

            // Let the original Sniffer.Update run when ALL proxies are outside
            // the entity's effective detection range (visual + smell).
            if (ProxyDistanceHelper.ProxyIsFar(charComponent))
                return true;

            var net = ModRuntime.Network;
            if (net == null) return false;

            // --- Sniff lifecycle ---
            float timeStarted = TimeStartedSniffing(__instance);

            if (__instance.sniffing)
            {
                if (Time.time - timeStarted > __instance.sniffTime)
                    StopSniffAndAttack(__instance, charComponent, net);
                return false;
            }

            if (__instance.canSniff)
            {
                // Vanilla startSniffing gates: not while the entity already sees an enemy or is
                // chasing / defending / escaping. (Vanilla only gates the START, so the sniff
                // timer and cooldown above/below keep running while the entity is busy.)
                if (charComponent.enemyInSight
                    || charComponent.behaviour == Character.Behaviour.chasingTarget
                    || charComponent.behaviour == Character.Behaviour.defensive
                    || charComponent.behaviour == Character.Behaviour.escaping)
                    return false;

                // Every player body inside the smell radius counts alike: the one the creature is
                // already after, else the nearest (PlayerTargetArbiter).
                if (!PlayerTargetArbiter.PickSmelled(charComponent, __instance.radius, out int sniffedId))
                    return false;

                _sniffTargetPlayerId[__instance] = sniffedId; // -1 = host

                __instance.sniffing = true;
                TimeStartedSniffing(__instance) = Time.time;
                AudioController.Play(__instance.sniffSound, __instance.transform);
                return false;
            }

            // Use the original cooldownTime from sniff start.
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

                CharBase proxyCB = proxy.CachedCharBase;
                if (proxyCB == null || proxyCB.invisible || proxyCB.ignoreMe)
                    return;

                // Vanilla stopSniffing: the sniffed body must still be within smell range.
                if (Core.trueDistance(__instance.transform.position, proxy.transform.position) >= __instance.radius)
                    return;

                PlayerTargetArbiter.Commit(charComponent, proxy.transform, "sniff");
            }
            else
            {
                // Attack the host
                Player host = Player.Instance;
                if (host == null || host.invisible || host.ignoreMe)
                    return;

                // Vanilla stopSniffing: the sniffed body must still be within smell range.
                if (Core.trueDistance(__instance.transform.position, host._transform.position) >= __instance.radius)
                    return;

                // Attack the sniffed body. attackPlayer() retargets to the nearest
                // peer and would drop the host the sniffer just finished on.
                PlayerTargetArbiter.Commit(charComponent, host.transform, "sniff");
            }
        }
    }

    /// <summary>
    /// Friend / Enemy of the Forest for every player body. Vanilla checks
    /// <c>target == Player.Instance._transform &amp;&amp; faction == animalAggressive &amp;&amp;
    /// Player.Instance.skills.FriendOfTheForest</c> (or EnemyOfTheForest) in two places: the far
    /// sighting in processAnims (a defensive animal turns defensive at a slower count for a Friend,
    /// chases an Enemy) and onSeeEnemyNear (a Friend is chased even when the path cannot search).
    /// A client's stand-in failed the identity test, so its skills never counted. Both
    /// conditions read "the target is a player" and "that player's skill" instead; the rest of
    /// vanilla's logic is untouched.
    /// </summary>
    [HarmonyPatch]
    public static class HostForestSkillTargetPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Character), "processAnims");
            yield return AccessTools.Method(typeof(Character), "onSeeEnemyNear");
        }

        /// <summary>Vanilla's <c>target == Player.Instance._transform</c>, for any player body.</summary>
        public static bool TargetIsPlayer(Transform target)
        {
            Player host = Player.Instance;
            if (host != null && target == host.transform)
                return true;
            return target != null && target.GetComponent<RemotePlayerProxy>() != null;
        }

        /// <summary>The Friend of the Forest skill of the player <paramref name="c"/> targets.</summary>
        public static bool TargetFriendOfTheForest(Character c) => TargetSkill(c, friend: true);

        /// <summary>The Enemy of the Forest skill of the player <paramref name="c"/> targets.</summary>
        public static bool TargetEnemyOfTheForest(Character c) => TargetSkill(c, friend: false);

        private static bool TargetSkill(Character c, bool friend)
        {
            Transform t = c != null ? c.target : null;
            Player host = Player.Instance;
            if (t == null || host != null && t == host.transform)
                return host != null && host.skills != null
                    && (friend ? host.skills.FriendOfTheForest : host.skills.EnemyOfTheForest);
            RemotePlayerProxy proxy = t.GetComponent<RemotePlayerProxy>();
            return proxy != null && (friend ? proxy.RemoteHasFriendOfTheForest : proxy.RemoteHasEnemyOfTheForest);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            MethodInfo getInstance = AccessTools.PropertyGetter(typeof(Player), nameof(Player.Instance));
            FieldInfo transformField = AccessTools.Field(typeof(Player), "_transform");
            FieldInfo targetField = AccessTools.Field(typeof(Character), nameof(Character.target));
            FieldInfo skillsField = AccessTools.Field(typeof(Player), nameof(Player.skills));
            FieldInfo friendField = AccessTools.Field(typeof(PlayerSkills), nameof(PlayerSkills.FriendOfTheForest));
            FieldInfo enemyField = AccessTools.Field(typeof(PlayerSkills), nameof(PlayerSkills.EnemyOfTheForest));
            MethodInfo opEquality = AccessTools.Method(typeof(UnityEngine.Object), "op_Equality");
            MethodInfo isPlayer = AccessTools.Method(typeof(HostForestSkillTargetPatch), nameof(TargetIsPlayer));
            MethodInfo friendOf = AccessTools.Method(typeof(HostForestSkillTargetPatch), nameof(TargetFriendOfTheForest));
            MethodInfo enemyOf = AccessTools.Method(typeof(HostForestSkillTargetPatch), nameof(TargetEnemyOfTheForest));

            var list = new List<CodeInstruction>(instructions);
            if (getInstance == null || transformField == null || targetField == null || skillsField == null
                || friendField == null || enemyField == null || opEquality == null)
            {
                Logging.ModLog.Error(Logging.LogCat.AI, "[ForestSkill] " + original.Name + " hook not applied (missing member)");
                return list;
            }

            int patched = 0;
            for (int i = 2; i < list.Count; i++)
            {
                bool friend = list[i].LoadsField(friendField);
                if (!friend && !list[i].LoadsField(enemyField))
                    continue;
                if (!list[i - 1].LoadsField(skillsField) || !list[i - 2].Calls(getInstance))
                    continue;
                // Player.Instance.skills.X → this.target's player's X (stack: one bool either way).
                // Rewritten in place so branch labels on these instructions stay put.
                list[i - 2].opcode = OpCodes.Ldarg_0;
                list[i - 2].operand = null;
                list[i - 1].opcode = OpCodes.Call;
                list[i - 1].operand = friend ? friendOf : enemyOf;
                list[i].opcode = OpCodes.Nop;
                list[i].operand = null;

                // The same condition's earlier `target == Player.Instance._transform`.
                for (int k = i - 3; k >= 3 && k > i - 24; k--)
                {
                    if (!list[k].Calls(opEquality) || !list[k - 1].LoadsField(transformField)
                        || !list[k - 2].Calls(getInstance) || !list[k - 3].LoadsField(targetField))
                        continue;
                    list[k - 2].opcode = OpCodes.Nop;
                    list[k - 2].operand = null;
                    list[k - 1].opcode = OpCodes.Nop;
                    list[k - 1].operand = null;
                    list[k].operand = isPlayer;
                    patched++;
                    break;
                }
            }

            int expected = original.Name == "processAnims" ? 2 : 1;
            if (patched != expected)
                Logging.ModLog.Error(Logging.LogCat.AI,
                    "[ForestSkill] " + original.Name + " expected " + expected + " forest checks, patched " + patched);
            return list;
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

            // Proxy target; vanilla growl only accepts a Player or Character target. Same body for
            // the stand-in, including vanilla's throttle and its repeat while the chase lasts
            // (dropped before: no re-growl, and every re-acquire growled and alerted at once).
            if (__instance.isRoutineActive("waitToGrowl"))
                return false;
            if (__instance.sounds != null && !__instance.sleeping)
                __instance.sounds.playGrowl();

            AlertInArea?.Invoke(__instance, new object[] { 500f, false });
            __instance.startRoutine(AccessTools.MethodDelegate<System.Action>(WaitToGrowl, __instance), 6f, 30f);
            return false;
        }

        private static readonly System.Reflection.MethodInfo AlertInArea =
            AccessTools.Method(typeof(Character), "alertCharactersInArea", new[] { typeof(float), typeof(bool) });
        private static readonly System.Reflection.MethodInfo WaitToGrowl =
            AccessTools.Method(typeof(Character), "waitToGrowl");
    }

    /// <summary>
    /// Vanilla's closer-enemy check (every 2.5-3.5 s while chasing) attacks the first character in
    /// its sight list that it is hostile to. With several player bodies that first entry is just
    /// whichever the physics overlap listed first. The player bodies count as one entry here:
    /// when vanilla's pick is a player, <see cref="PlayerTargetArbiter"/> says which one (the held
    /// one, or one clearly nearer: this check is the only switch window). Non-player picks stay
    /// vanilla's (it was changed to "the closest of everything" before, also for non-players).
    /// </summary>
    [HarmonyPatch(typeof(Character), "checkForNewEnemyCloserThanTarget")]
    public static class HostCheckForCloserEnemyPatch
    {
        private static bool Prefix(Character __instance)
        {
            if (__instance == null || !HostPlayerIdentity.HostWithRemotes())
                return true;

            List<CharBase> list = __instance.charactersInSight;
            for (int i = 0; i < list.Count; i++)
            {
                CharBase cb = list[i];
                if (cb == null || !__instance.attacksFaction(cb.faction))
                    continue;
                Transform pick = cb.transform;
                PlayerTargetReason reason = PlayerTargetReason.None;
                if (PlayerTargetArbiter.IsPlayerBody(pick))
                {
                    Transform chosen = PlayerTargetArbiter.Choose(__instance, __instance.target,
                        switchWindow: true, out reason, out bool chosenSensed);
                    if (chosen != null)
                    {
                        if (chosen != pick)
                            PlayerTargetArbiter.TraceHold(__instance, chosen, pick, "checkForNewEnemyCloserThanTarget", reason);
                        // A held target out of sight for a moment is not re-committed (that would
                        // hand the creature its real position); vanilla only attacks what it sees.
                        if (!chosenSensed && chosen == __instance.target)
                            return false;
                        pick = chosen;
                    }
                }
                PlayerTargetArbiter.Commit(__instance, pick, "checkForNewEnemyCloserThanTarget", reason);
                return false;
            }
            return false;
        }
    }

    /// <summary>
    /// InSightOfPlayer (PorterSpawner, banshee agitation, sight events) only
    /// tested host FOV. Host+remotes: AnyInSight (Player + proxies).
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
        private static void HitIfSeen(Shooter shooter, Transform body)
        {
            CharBase cb = body != null ? body.GetComponent<CharBase>() : null;
            if (cb == null || !cb.alive || !Core.canSee(shooter.transform, body))
                return;
            cb.getHit(
                shooter.damage / Core.trueDistance(shooter.transform, body),
                null,
                CanCutInHalf: false,
                byPlayer: false,
                canInterrupt: false,
                normalHit: false,
                showRedScreen: true);
        }

        private static bool Prefix(Shooter __instance)
        {
            var net = ModRuntime.Network;
            if (net != null && net.IsConnected && net.Role == NetworkRole.Client)
                return false;
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

            // Vanilla hurts "the player" whenever the shooter can see them, whatever it aims at:
            // every player it can see, as each would be alone.
            HitIfSeen(__instance, Player.Instance != null ? Player.Instance.transform : null);
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy != null && !DeathStateTracker.IsRemoteNightDead(proxy.PlayerId))
                    HitIfSeen(__instance, proxy.transform);
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
            var net = ModRuntime.Network;
            if (net != null)
            {
                foreach (var proxy in net.GetAllProxies())
                {
                    if (proxy == null)
                        continue;
                    CharBase cb = proxy.CachedCharBase;
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
