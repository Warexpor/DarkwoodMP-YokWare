using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using LiteNetLib;
using PathologicalGames;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// "Defender decides" receive side. Client: re-create a host enemy attack on the local
    /// copy of the enemy, armed to hit only this client's own player. Host: show a confirmed
    /// hit (sound, blood) on the victim's stand-in for everyone else.
    /// </summary>
    internal static class EnemyAttackNetHandlers
    {
        /// <summary>A strike older than this (reliable resend after loss, long stall) is dropped, not landed late.</summary>
        private const float MaxAttackAgeSec = 0.6f;
        /// <summary>Host: cosmetic confirms per victim, at most one per window.</summary>
        private const float ConfirmDebounceSec = 0.1f;

        private static readonly Dictionary<int, float> _lastConfirm = new Dictionary<int, float>();

        internal static void Reset() => _lastConfirm.Clear();

        internal static void HandleEnemyAttack(EnemyAttackMessage m)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Client)
                return;
            if (Player.Instance == null)
                return;

            if (ClientEntityInterpolationService.TryGetHostAge(m.HostTime, out float age) && age > MaxAttackAgeSec)
            {
                EntitySyncLog.CombatTrace("enemyatk:late", () =>
                    "[EnemyAttack] drop late id=" + m.EntityId + " age=" + age.ToString("F2") + "s", 1f);
                return;
            }

            Vector3 hostPos = new Vector3(m.PosX, m.PosY, m.PosZ);
            // Out of interest: the attack cannot reach this player.
            if (!ClientEntityInterpolationService.IsInClientInterest(hostPos))
                return;

            Character c = CharacterTracker.FindByStableId(m.EntityId);
            if (c != null && (!ClientEntityInterpolationService.IsHostSynced(m.EntityId) || c.gameObject == null
                || !c.gameObject.activeInHierarchy))
                c = null;
            if (c != null)
                ClientEntityInterpolationService.PresentAttackClip(c, m.EntityId, m.Clip, m.ClipFrame);

            try
            {
                if (m.Kind == EnemyAttackMessage.KindMelee)
                    SpawnMelee(c, m, hostPos);
                else
                    SpawnProjectile(c, m, hostPos);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[EnemyAttack] re-create failed id=" + m.EntityId + " kind=" + m.Kind
                    + " name=" + m.Name + ": " + ex.Message);
            }
        }

        private static void SpawnMelee(Character c, EnemyAttackMessage m, Vector3 hostPos)
        {
            SpawnPool pool = PoolManager.Pools["Sensors"];
            if (pool == null || string.IsNullOrEmpty(m.Name) || !pool.prefabs.ContainsKey(m.Name))
            {
                EntitySyncLog.CombatTrace("enemyatk:noprefab:" + m.Name, () =>
                    "[EnemyAttack] no sensor prefab '" + m.Name + "'", 5f);
                return;
            }

            // On the enemy as this client sees it; the host pose only when the local copy is
            // not matched yet (a temporary anchor stands in for the attacker transform).
            Transform anchor;
            if (c != null)
            {
                anchor = c.transform;
            }
            else
            {
                var go = new GameObject("DWMP_EnemyAttackAnchor");
                go.transform.SetPositionAndRotation(hostPos, Quaternion.Euler(90f, m.RotY, 0f));
                Object.Destroy(go, 2f);
                anchor = go.transform;
            }

            // Vanilla Character.melee placement.
            Vector3 pos = anchor.position + anchor.up * 50f;
            Quaternion rot = Quaternion.Euler(90f, anchor.eulerAngles.y, 0f);
            Transform t = pool.Spawn(pool.prefabs[m.Name], pos, rot);
            if (t == null) return;
            MeleeSensor sensor = t.GetComponent<MeleeSensor>();
            if (sensor == null)
            {
                pool.Despawn(t);
                return;
            }
            sensor.attackerTransform = anchor;
            sensor.type = MeleeSensor.MeleeSensorType.character;
            sensor.damage = (int)m.Damage;
            sensor.barricadeDamage = 0;
            if (sensor.stickToAttacker)
                t.parent = anchor;
            DefenderAttackMarker.Ensure(t.gameObject).Arm(true, m.EntityId, m.Damage, m.Kind);

            EntitySyncLog.CombatTrace("enemyatk:rx:" + m.EntityId, () =>
                "[EnemyAttack] melee id=" + m.EntityId + " sensor=" + m.Name + " dmg=" + m.Damage.ToString("F0")
                + (c == null ? " (host pose)" : ""), 0.25f);
        }

        private static void SpawnProjectile(Character c, EnemyAttackMessage m, Vector3 hostSpawn)
        {
            // The shooter's own prefab data (SensorType, collider) lives on the local copy.
            if (c == null)
            {
                EntitySyncLog.CombatTrace("enemyatk:noshooter:" + m.EntityId, () =>
                    "[EnemyAttack] projectile id=" + m.EntityId + " shooter not matched; skipped", 2f);
                return;
            }

            // Fired from where this client sees the shooter; the whole flight is shifted by the
            // same offset, so it heads where the host's shot heads.
            Vector3 spawn = c.transform.position + new Vector3(0f, 2f, 0f);
            Vector3 delta = spawn - hostSpawn;
            Quaternion rot = Quaternion.Euler(90f, m.RotY, 0f);

            GameObject go = null;
            if (m.Kind == EnemyAttackMessage.KindRangedSensor)
            {
                Character.SensorType st = null;
                for (int i = 0; i < c.sensorTypes.Count; i++)
                {
                    if (c.sensorTypes[i] != null && c.sensorTypes[i].name == m.Name)
                    {
                        st = c.sensorTypes[i];
                        break;
                    }
                }
                if (st == null || st.sensor == null)
                {
                    EntitySyncLog.CombatTrace("enemyatk:nosensor:" + m.Name, () =>
                        "[EnemyAttack] no ranged sensor '" + m.Name + "' on " + c.name, 5f);
                    return;
                }
                go = Core.AddPrefab(st.sensor, spawn, rot, null, worldSpace: true);
            }
            else
            {
                Transform pooled = Core.AddPooledPrefab(m.Name, spawn, rot);
                go = pooled != null ? pooled.gameObject : null;
            }
            if (go == null) return;

            Collider own = go.GetComponent<Collider>();
            if (own != null && c.collider != null)
                Physics.IgnoreCollision(own, c.collider);

            Bullet bullet = go.GetComponent<Bullet>();
            if (bullet != null)
            {
                bullet.objectThatSpawnedMe = c.transform;
                bullet.damage = (int)m.Damage;
            }
            ThrownItem thrown = go.GetComponent<ThrownItem>();
            if (thrown != null && m.Thrown)
            {
                // Fly-only copy: no pickup, land spawn or blast side effects on this client.
                WorldPhysicsSyncService.MuteThrownCombat(go);
                Rigidbody rb = go.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.drag = m.Drag;
                    rb.velocity = new Vector3(m.VelX, m.VelY, m.VelZ);
                    rb.AddTorque(0f, 225000f, 0f);
                }
                thrown.thrown = true;
                thrown.landTarget = new Vector3(m.LandX, m.LandY, m.LandZ) + delta;
                thrown.flyTime = m.FlyTime;
                thrown.objectThatSpawnedMe = c.transform;
            }
            Explodes explodes = go.GetComponent<Explodes>();
            if (explodes != null)
                explodes.objectThatSpawnedMe = c.transform;
            FastProjectile fast = go.GetComponent<FastProjectile>();
            if (fast != null)
                fast.ignoreCollisionsWith = c.collider;

            DefenderAttackMarker.Ensure(go).Arm(true, m.EntityId, m.Damage, m.Kind);
            EntitySyncLog.CombatTrace("enemyatk:rx:" + m.EntityId, () =>
                "[EnemyAttack] projectile id=" + m.EntityId + " kind=" + m.Kind + " name=" + m.Name
                + " dmg=" + m.Damage.ToString("F0"), 0.25f);
        }

        /// <summary>Client: tell the host a re-created attack hit this player (damage is already applied).</summary>
        internal static void SendHitConfirm(short entityId, byte kind, int damage, Vector3 hit)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Client || !net.IsConnected)
                return;
            var msg = new EnemyHitConfirmMessage
            {
                EntityId = entityId,
                Kind = kind,
                Damage = damage,
                HitX = hit.x, HitY = hit.y, HitZ = hit.z
            };
            net.Send(NetMessageType.EnemyHitConfirm, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Host: show a client-confirmed enemy hit on that player's stand-in for the other peers.</summary>
        internal static void HandleEnemyHitConfirm(EnemyHitConfirmMessage m)
        {
            if (!NetGuard.Host(out var net)) return;
            int pid = net.CurrentReceivePlayerId;
            if (pid <= 0) return;
            if (!CombatAuthorityPolicy.IsFinitePosition(m.HitX, m.HitY, m.HitZ)) return;
            RemotePlayerProxy proxy = net.GetProxy(pid);
            if (proxy == null) return;

            float now = Time.time;
            if (_lastConfirm.TryGetValue(pid, out float last) && now - last < ConfirmDebounceSec)
                return;
            _lastConfirm[pid] = now;

            Vector3 proxyPos = proxy.transform.position;
            Vector3 hit = new Vector3(m.HitX, m.HitY, m.HitZ);
            // A report far from the stand-in is not this body's hit; show it on the body.
            if ((hit - proxyPos).sqrMagnitude > 150f * 150f)
                hit = proxyPos;

            if (m.Kind == EnemyAttackMessage.KindMelee)
                AudioController.Play("player_melee_hit", proxyPos);

            CharBase cb = proxy.CachedCharBase;
            bool inWater = cb != null && cb.inWater;
            string blood = inWater ? "FX/Bloodsplats/Shotsplat" : "FX/Bloodsplats/Shotsplat_stay";
            float rotY = Random.Range(0f, 360f);
            bool prevHack = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try { Core.AddPrefab(blood, hit, Quaternion.Euler(90f, rotY, 0f), null); }
            finally { TraverseHack.SetExplicitFlag(prevHack); }

            // The victim already sees its own hit.
            net.SendToAllExcept(pid, NetMessageType.BulletImpact, w => new BulletImpactMessage
            {
                PrefabName = blood,
                PoolName = "",
                PosX = hit.x,
                PosY = hit.y,
                PosZ = hit.z,
                RotX = 90f,
                RotY = rotY,
                RotZ = 0f
            }.Serialize(w), DeliveryMethod.ReliableOrdered);

            EntitySyncLog.Damage("[EnemyAttack] p" + pid + " confirmed hit by id=" + m.EntityId
                + " kind=" + m.Kind + " dmg=" + m.Damage);
        }
    }
}
