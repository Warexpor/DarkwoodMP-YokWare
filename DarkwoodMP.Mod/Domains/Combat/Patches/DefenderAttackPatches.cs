using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

// "Defender decides": the host still runs every enemy, but an enemy melee swing or
// projectile is judged on the machine of the player it would hit. The host fans out the
// attack frame (EnemyAttack); each client re-creates it on the enemy as that client sees
// it, and only that client's own player can be hit by the copy. The host original keeps
// hitting the host player, other enemies and the world, and skips remote stand-ins.
namespace DWMPHorde.Patches
{
    /// <summary>
    /// Marks a melee sensor or enemy projectile that follows "defender decides".
    /// Host original: never hits remote stand-ins. Client copy: only hits the local player.
    /// </summary>
    internal sealed class DefenderAttackMarker : MonoBehaviour
    {
        public bool Active;
        public bool ClientCopy;
        public short EntityId;
        /// <summary>Client copy: final host damage (strength modifier applied on the host).</summary>
        public float Damage;
        public byte Kind;

        internal void Arm(bool clientCopy, short entityId, float damage, byte kind)
        {
            Active = true;
            ClientCopy = clientCopy;
            EntityId = entityId;
            Damage = damage;
            Kind = kind;
        }

        internal void Disarm()
        {
            Active = false;
            ClientCopy = false;
            EntityId = 0;
            Damage = 0f;
            Kind = 0;
        }

        // Pool despawn deactivates the object; the next spawn from the pool must start clean.
        private void OnDisable() => Disarm();

        internal static bool TryGetActive(Component c, out DefenderAttackMarker marker)
        {
            marker = c != null ? c.GetComponent<DefenderAttackMarker>() : null;
            return marker != null && marker.Active;
        }

        internal static DefenderAttackMarker Ensure(GameObject go)
        {
            DefenderAttackMarker m = go.GetComponent<DefenderAttackMarker>();
            return m != null ? m : go.AddComponent<DefenderAttackMarker>();
        }
    }

    /// <summary>Call-scoped state shared by the defender patches (each unwound by a Finalizer).</summary>
    internal static class DefenderAttackContext
    {
        /// <summary>Host: inside <c>Character.melee</c> whose attack was sent; its sensor spawns next.</summary>
        internal static bool PendingHostMelee;
        internal static short PendingEntityId;
        /// <summary>Host: inside <c>rangedAttack</c> / <c>spawnProjectile</c>; first spawned object is the projectile.</summary>
        internal static bool CapturingProjectile;
        internal static GameObject CapturedProjectile;
        /// <summary>Inside a defender projectile collision: its impact FX are local on every peer.</summary>
        internal static int InsideProjectileCollide;
        /// <summary>Host: inside a host-original enemy ThrownItem landing.</summary>
        internal static ThrownItem HostThrownLanding;
        /// <summary>Host: nesting depth of <c>Explodes.explode</c> (a landing's blast is not a direct hit).</summary>
        internal static int ExplodeDepth;

        /// <summary>Host: a stand-in getHit right now is the direct hit of a host-original enemy throw.</summary>
        internal static bool IsHostThrownDirectHit => HostThrownLanding != null && ExplodeDepth == 0;

        internal static void NoteSpawned(GameObject go)
        {
            if (CapturingProjectile && CapturedProjectile == null && go != null)
                CapturedProjectile = go;
        }

        internal static void Reset()
        {
            PendingHostMelee = false;
            PendingEntityId = 0;
            CapturingProjectile = false;
            CapturedProjectile = null;
            InsideProjectileCollide = 0;
            HostThrownLanding = null;
            ExplodeDepth = 0;
        }
    }

    /// <summary>Host side: decides which enemy attacks are fanned out and sends them.</summary>
    internal static class EnemyAttackSender
    {
        /// <summary>
        /// An enemy near a remote player, with a host id the clients know. Anything else keeps
        /// the old host-decided path (it cannot reach a remote player anyway).
        /// </summary>
        internal static bool Eligible(Character c, out short id, out LanNetworkManager net)
        {
            id = 0;
            if (!NetGuard.ConnectedHost(out net) || c == null)
                return false;
            if (!PlayerPositionManager.HasRemotePlayer)
                return false;
            if (c.GetComponentInParent<RemotePlayerProxy>() != null)
                return false;
            if (DreamSyncManager.IsDreamActive && DreamSyncManager.IsWorldFrozenForComponent(c))
                return false;
            float r = GameplayConstants.EntityActivationRange;
            if (!PlayerPositionManager.IsAnyRemoteWithinSq(c.transform.position, r * r))
                return false;
            id = CharacterTracker.GetStableId(c);
            return id != 0;
        }

        internal static void ReadClip(Character c, out string clip, out short frame)
        {
            clip = "";
            frame = -1;
            tk2dSpriteAnimator anim = null;
            try { anim = c.animator; } catch { /* dismantled */ }
            try
            {
                if (!string.IsNullOrEmpty(c.clipToPlay))
                    clip = c.clipToPlay;
            }
            catch { /* odd prefab */ }
            if (anim != null && anim.CurrentClip != null)
            {
                if (string.IsNullOrEmpty(clip))
                    clip = anim.CurrentClip.name;
                if (anim.CurrentClip.name == clip)
                    frame = (short)anim.CurrentFrame;
            }
        }

        internal static void Send(LanNetworkManager net, EnemyAttackMessage msg)
        {
            net.SendToAll(NetMessageType.EnemyAttack, w => msg.Serialize(w),
                DeliveryMethod.ReliableOrdered, skipLoadingPeers: true);
            EntitySyncLog.CombatTrace("enemyatk:" + msg.EntityId, () =>
                "[EnemyAttack] send id=" + msg.EntityId + " kind=" + msg.Kind + " name=" + msg.Name
                + " dmg=" + msg.Damage.ToString("F0") + " clip=" + msg.Clip + "@" + msg.ClipFrame, 0.25f);
        }

        /// <summary>Host: fan out a captured enemy projectile and mark the original.</summary>
        internal static void SendProjectile(Character c, GameObject go, byte kind, string name)
        {
            if (go == null || c == null || string.IsNullOrEmpty(name))
                return;
            if (!Eligible(c, out short id, out LanNetworkManager net))
                return;

            Bullet bullet = go.GetComponent<Bullet>();
            ThrownItem thrown = go.GetComponent<ThrownItem>();
            // Anything else cannot hurt a player directly; leave it on the old path.
            if (bullet == null && thrown == null)
                return;

            Vector3 p = go.transform.position;
            ReadClip(c, out string clip, out short frame);
            var msg = new EnemyAttackMessage
            {
                EntityId = id,
                Kind = kind,
                Name = name,
                HostTime = EntityStateBroadcastService.HostNow,
                PosX = p.x, PosY = p.y, PosZ = p.z,
                RotY = go.transform.eulerAngles.y,
                Damage = bullet != null ? bullet.damage : thrown.damage,
                Clip = clip,
                ClipFrame = frame
            };
            if (bullet == null && thrown != null)
            {
                Rigidbody rb = go.GetComponent<Rigidbody>();
                Vector3 v = rb != null ? rb.velocity : Vector3.zero;
                msg.Thrown = true;
                msg.VelX = v.x; msg.VelY = v.y; msg.VelZ = v.z;
                msg.LandX = thrown.landTarget.x;
                msg.LandY = thrown.landTarget.y;
                msg.LandZ = thrown.landTarget.z;
                msg.FlyTime = thrown.flyTime;
                msg.Drag = rb != null ? rb.drag : 0f;
            }

            DefenderAttackMarker.Ensure(go).Arm(false, id, msg.Damage, kind);
            Send(net, msg);
        }

        /// <summary>Removes a projectile the way it came: back to its pool, or destroyed.</summary>
        internal static void Despawn(Transform t)
        {
            if (t == null) return;
            try
            {
                if (PathologicalGames.PoolManager.Pools["FX"].IsSpawned(t)
                    || PathologicalGames.PoolManager.Pools["UI"].IsSpawned(t))
                {
                    Core.RemovePooledPrefab(t);
                    return;
                }
            }
            catch { /* pools gone mid-teardown */ }
            Object.Destroy(t.gameObject);
        }
    }

    /// <summary>Host: fan out an enemy melee swing; its sensor skips remote stand-ins.</summary>
    [HarmonyPatch(typeof(Character), "melee")]
    public static class HostEnemyMeleeSendPatch
    {
        private static void Prefix(Character __instance, Character.SensorType __0)
        {
            DefenderAttackContext.PendingHostMelee = false;
            if (__0 == null || __0.sensor == null)
                return;
            if (!EnemyAttackSender.Eligible(__instance, out short id, out LanNetworkManager net))
                return;

            Vector3 p = __instance.transform.position;
            EnemyAttackSender.ReadClip(__instance, out string clip, out short frame);
            EnemyAttackSender.Send(net, new EnemyAttackMessage
            {
                EntityId = id,
                Kind = EnemyAttackMessage.KindMelee,
                Name = __0.sensor.name,
                HostTime = EntityStateBroadcastService.HostNow,
                PosX = p.x, PosY = p.y, PosZ = p.z,
                RotY = __instance.transform.eulerAngles.y,
                // Vanilla MeleeSensor hits the player for damage * attacker strengthModifier.
                Damage = __0.damage * __instance.strengthModifier,
                Clip = clip,
                ClipFrame = frame
            });
            DefenderAttackContext.PendingEntityId = id;
            DefenderAttackContext.PendingHostMelee = true;
        }

        private static void Finalizer()
        {
            DefenderAttackContext.PendingHostMelee = false;
            DefenderAttackContext.PendingEntityId = 0;
        }
    }

    /// <summary>
    /// Pool spawn callback: the sensor spawned inside a sent <c>melee()</c> is a defender
    /// original; every other spawn from the pool starts unmarked.
    /// </summary>
    [HarmonyPatch(typeof(MeleeSensor), "OnSpawned")]
    public static class DefenderSensorSpawnPatch
    {
        private static void Postfix(MeleeSensor __instance)
        {
            if (__instance == null) return;
            if (DefenderAttackContext.PendingHostMelee)
            {
                DefenderAttackMarker.Ensure(__instance.gameObject)
                    .Arm(false, DefenderAttackContext.PendingEntityId, 0f, EnemyAttackMessage.KindMelee);
                // One sensor per melee() call.
                DefenderAttackContext.PendingHostMelee = false;
                return;
            }
            DefenderAttackMarker m = __instance.GetComponent<DefenderAttackMarker>();
            if (m != null)
                m.Disarm();
        }
    }

    /// <summary>Host: capture and fan out a ranged <c>SensorType</c> shot.</summary>
    [HarmonyPatch(typeof(Character), "rangedAttack")]
    public static class HostEnemyRangedSendPatch
    {
        private static void Prefix(Character __instance, Character.SensorType __0, out bool __state)
        {
            __state = false;
            if (__0 == null || __0.sensor == null || __instance.target == null)
                return;
            if (!EnemyAttackSender.Eligible(__instance, out _, out _))
                return;
            DefenderAttackContext.CapturedProjectile = null;
            DefenderAttackContext.CapturingProjectile = true;
            __state = true;
        }

        private static void Postfix(Character __instance, Character.SensorType __0, bool __state)
        {
            if (!__state) return;
            GameObject go = DefenderAttackContext.CapturedProjectile;
            DefenderAttackContext.CapturingProjectile = false;
            DefenderAttackContext.CapturedProjectile = null;
            EnemyAttackSender.SendProjectile(__instance, go, EnemyAttackMessage.KindRangedSensor, __0.name);
        }

        private static void Finalizer()
        {
            DefenderAttackContext.CapturingProjectile = false;
            DefenderAttackContext.CapturedProjectile = null;
        }
    }

    /// <summary>Host: capture and fan out an activity projectile (<c>spawnProjectile</c>).</summary>
    [HarmonyPatch(typeof(Character), "spawnProjectile")]
    public static class HostEnemyProjectileSendPatch
    {
        private static void Prefix(Character __instance, string __0, Transform __1, out bool __state)
        {
            __state = false;
            if (string.IsNullOrEmpty(__0) || __1 == null)
                return;
            if (!EnemyAttackSender.Eligible(__instance, out _, out _))
                return;
            DefenderAttackContext.CapturedProjectile = null;
            DefenderAttackContext.CapturingProjectile = true;
            __state = true;
        }

        private static void Postfix(Character __instance, string __0, bool __state)
        {
            if (!__state) return;
            GameObject go = DefenderAttackContext.CapturedProjectile;
            DefenderAttackContext.CapturingProjectile = false;
            DefenderAttackContext.CapturedProjectile = null;
            EnemyAttackSender.SendProjectile(__instance, go, EnemyAttackMessage.KindPooledProjectile, __0);
        }

        private static void Finalizer()
        {
            DefenderAttackContext.CapturingProjectile = false;
            DefenderAttackContext.CapturedProjectile = null;
        }
    }

    /// <summary>Capture point for <c>spawnProjectile</c>'s pooled spawn.</summary>
    [HarmonyPatch(typeof(Core), "AddPooledPrefab", typeof(string), typeof(Vector3), typeof(Quaternion))]
    public static class DefenderPooledProjectileCapturePatch
    {
        private static void Postfix(Transform __result)
        {
            if (DefenderAttackContext.CapturingProjectile && __result != null)
                DefenderAttackContext.NoteSpawned(__result.gameObject);
        }
    }

    /// <summary>
    /// Client: an enemy copy playing a host attack clip must not fire its own attack frame
    /// (event 997 → melee / ranged / banshee scream). The host's EnemyAttack re-creates the
    /// attack instead; the local frame used to add a second, unsynced hit (and a scream that
    /// was then fanned back to the host). Frame events that move or weigh the body (Teleport,
    /// Move, Push, PushRelative, Stop, SetMass) are the host's too: its pose arrives in the
    /// snapshots, and on the copy they jumped or shoved it for a frame before the shown pose put
    /// it back (SetMass changed how hard the local player pushes it). Sounds, shadows and the
    /// rotation flag stay.
    /// </summary>
    [HarmonyPatch(typeof(Character), "checkFrameTrigger")]
    public static class ClientEnemyAttackFramePatch
    {
        private static bool Prefix(Character __instance, string eventInfo, int eventInt)
        {
            if (eventInt != 997 && !MovesBody(eventInt, eventInfo))
                return true;
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Client)
                return true;
            return !ClientAIConditionalHelper.ShouldSkipAI(__instance);
        }

        /// <summary>Vanilla's body-moving cases, reached only past the 997-999 sound / attack events.</summary>
        private static bool MovesBody(int eventInt, string eventInfo)
        {
            if (eventInt >= 997 && eventInt <= 999)
                return false;
            switch (eventInfo)
            {
                case "Teleport":
                case "Move":
                case "Push":
                case "PushRelative":
                case "Stop":
                case "SetMass":
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Client: enemy-type melee sensors. A re-created copy (marker) hits only the local
    /// player, with the host's damage; anything else of this type is a presentation copy and
    /// deals nothing (the host decides enemy-vs-enemy, barricades and other players).
    /// </summary>
    [HarmonyPatch(typeof(MeleeSensor), "OnTriggerEnter", new[] { typeof(Collider) })]
    public static class ClientEnemySensorPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(MeleeSensor __instance, Collider __0)
        {
            if (__instance == null || __instance.type != MeleeSensor.MeleeSensorType.character)
                return true;
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Client)
                return true;
            if (!DefenderAttackMarker.TryGetActive(__instance, out DefenderAttackMarker m) || !m.ClientCopy)
                return false;
            TryHitLocalPlayer(__instance, __0, m);
            return false;
        }

        private static void TryHitLocalPlayer(MeleeSensor sensor, Collider col, DefenderAttackMarker m)
        {
            if (col == null || col.transform == null) return;
            Transform attacker = sensor.attackerTransform;
            if (attacker == null) return;
            if (IsIgnored(sensor, col)) return;

            Player local = Player.Instance;
            if (local == null) return;
            // Vanilla's player branch: the collider's own GameObject carries Player.
            Player p = col.gameObject.GetComponent<Player>();
            if (p == null || p != local) return;

            // Vanilla bookkeeping and gates, in vanilla order.
            sensor.collidersToIgnore.Add(col);
            sensor.gameObjectsToIgnore.Add(col.gameObject);
            if (attacker == col.transform || col.transform.IsChildOf(attacker)) return;
            if (!sensor.doesNotNeedLineOfSight && !Core.canSeeAttack(attacker, col.transform)) return;
            if (!local.alive || DeathStateTracker.LocalNightDeath) return;

            if (sensor.onHit != null)
                sensor.onHit();
            int dmg = (int)m.Damage;
            if (sensor.shadowSensor)
                local.getHitByShadow(m.Damage);
            else
                local.getHit(dmg);
            for (int j = 0; j < sensor.effects.Count; j++)
            {
                if (sensor.effects[j] != null)
                    local.effects.activate(sensor.effects[j]);
            }

            Vector3 hit = col.ClosestPoint(attacker.position);
            EnemyAttackNetHandlers.SendHitConfirm(m.EntityId, EnemyAttackMessage.KindMelee, dmg, hit);
            EntitySyncLog.Damage("[EnemyAttack] local hit by melee id=" + m.EntityId + " dmg=" + dmg
                + (sensor.shadowSensor ? " shadow" : ""));
        }

        private static bool IsIgnored(MeleeSensor sensor, Collider col)
        {
            List<Collider> ignored = sensor.collidersToIgnore;
            for (int i = 0; i < ignored.Count; i++)
            {
                if (ignored[i] != null && ignored[i] == col)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Enemy bullets on both roles. Host original: a remote stand-in absorbs the bullet without
    /// damage (that player's client decides). Client copy: only the local player takes damage;
    /// enemies and other stand-ins just stop it.
    /// </summary>
    [HarmonyPatch(typeof(Bullet), "onCollide", typeof(Collider), typeof(Vector3))]
    public static class DefenderBulletCollidePatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Bullet __instance, Collider collider, Vector3 hitPoint, out bool __state)
        {
            __state = false;
            if (collider == null || !DefenderAttackMarker.TryGetActive(__instance, out DefenderAttackMarker m))
                return true;
            DefenderAttackContext.InsideProjectileCollide++;
            __state = true;

            int layer = collider.gameObject.layer;
            if (layer != 11 && layer != 21)
                return true;
            CharBase cb = collider.GetComponent<CharBase>();
            if (cb == null && collider.transform.parent != null)
                cb = collider.transform.parent.GetComponent<CharBase>();
            if (cb == null || !cb.alive)
                return true;

            bool standIn = collider.GetComponentInParent<RemotePlayerProxy>() != null;
            if (!m.ClientCopy)
            {
                if (!standIn)
                    return true;
                EnemyAttackSender.Despawn(__instance.transform);
                return false;
            }

            Player local = Player.Instance;
            float y = __instance.transform.eulerAngles.y;
            if (local != null && cb == local && !DeathStateTracker.LocalNightDeath)
            {
                int dmg = (int)m.Damage;
                local.getHit(dmg, __instance.objectThatSpawnedMe, CanCutInHalf: false, byPlayer: false, canInterrupt: true);
                Core.AddPooledPrefab("FX", "Shotsplat1", hitPoint, Quaternion.Euler(90f, y + Random.Range(-40f, 40f), 0f));
                EnemyAttackNetHandlers.SendHitConfirm(m.EntityId, m.Kind, dmg, hitPoint);
                EntitySyncLog.Damage("[EnemyAttack] local hit by bullet id=" + m.EntityId + " dmg=" + dmg);
            }
            else if (!standIn)
            {
                Core.AddPooledPrefab("FX", "Shotsplat1", hitPoint, Quaternion.Euler(90f, y + Random.Range(-40f, 40f), 0f));
            }
            EnemyAttackSender.Despawn(__instance.transform);
            return false;
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
                DefenderAttackContext.InsideProjectileCollide--;
        }
    }

    /// <summary>
    /// Enemy ThrownItem landing on both roles. Client copy: only a direct hit on the local
    /// player counts, then the copy goes away (the host original owns the landed object, the
    /// blast and any spawn). Host original: vanilla, minus the direct hit on stand-ins
    /// (<see cref="ProxyDamagePatch"/> reads <see cref="DefenderAttackContext.IsHostThrownDirectHit"/>).
    /// </summary>
    [HarmonyPatch(typeof(ThrownItem), "onCollide", typeof(Collider), typeof(Vector3))]
    public static class DefenderThrownLandPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ThrownItem __instance, Collider __0, out bool __state)
        {
            __state = false;
            if (!DefenderAttackMarker.TryGetActive(__instance, out DefenderAttackMarker m))
                return true;
            if (m.ClientCopy)
            {
                ClientLand(__instance, __0, m);
                return false;
            }
            DefenderAttackContext.HostThrownLanding = __instance;
            __state = true;
            return true;
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
                DefenderAttackContext.HostThrownLanding = null;
        }

        private static void ClientLand(ThrownItem ti, Collider col, DefenderAttackMarker m)
        {
            if (!ti.thrown) return;
            ti.thrown = false;
            ti.onGround = true;
            Vector3 pos = ti.transform.position;
            int dmg = (int)m.Damage;

            CharBase hit = null;
            if (col == null)
            {
                if (!ti.stayOnWaterAfterFalling)
                {
                    Ground ground = Ground.getGround(pos);
                    if (ground != null && ground.groundType == GroundType.water)
                    {
                        Core.AddPrefab("FX/WaterRipple_itemThrownIn", pos, Quaternion.Euler(90f, 0f, 0f), null);
                        AudioController.Play("itemThrownInWater", pos);
                        EnemyAttackSender.Despawn(ti.transform);
                        return;
                    }
                }
                CharBase at = Core.GetCharacterAt(pos);
                if (at != null && dmg > 0)
                    hit = at;
            }
            else
            {
                hit = col.gameObject.GetComponent<CharBase>();
            }

            Player local = Player.Instance;
            if (hit != null && local != null && hit == local && local.alive && !DeathStateTracker.LocalNightDeath)
            {
                local.getHit(dmg, ti.objectThatSpawnedMe, CanCutInHalf: false, byPlayer: false, canInterrupt: ti.canInterrupt);
                EnemyAttackNetHandlers.SendHitConfirm(m.EntityId, m.Kind, dmg, pos);
                EntitySyncLog.Damage("[EnemyAttack] local hit by throw id=" + m.EntityId + " dmg=" + dmg);
            }
            if (!string.IsNullOrEmpty(ti.collideSound))
                AudioController.Play(ti.collideSound, pos);
            EnemyAttackSender.Despawn(ti.transform);
        }
    }
}
