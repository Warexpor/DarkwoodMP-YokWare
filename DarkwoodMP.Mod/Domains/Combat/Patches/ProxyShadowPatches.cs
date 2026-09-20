using System.Collections.Generic;
using DWMPHorde.Networking;
using HarmonyLib;
using PathologicalGames;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Attached to shadow GameObjects on the host that should target the
    /// remote proxy instead of Player.Instance. Drives the shadow's
    /// orbital movement, attack trigger, and death lifecycle independently
    /// of the vanilla ShadowCreature AI (which always targets the host).
    /// </summary>
    public class ProxyShadowController : MonoBehaviour
    {
        /// <summary>Set before Start() by the spawning code.</summary>
        public Transform TargetProxy { get; set; }

        private Transform _proxyT;
        private ShadowCreature _shadow;
        private tk2dSpriteAnimator _anim;
        private float _speed;
        private float _aggroTimer;
        private float _curAngle;
        private bool _hasAttacked;
        private bool _isDying;

        private void Start()
        {
            _proxyT = TargetProxy;

            _shadow = GetComponent<ShadowCreature>();
            _anim = GetComponent<tk2dSpriteAnimator>();
            _curAngle = Random.Range(0f, 360f);

            if (_shadow != null)
            {
                _shadow.dead = false;
                _speed = _shadow.speed;
            }

            if (_anim != null && _anim.GetClipByName("Float") != null)
                _anim.Play("Float");
        }

        private void Update()
        {
            if (_proxyT == null || _shadow == null || _shadow.dead || _isDying)
                return;

            if (Core.isDay())
            {
                StartDying();
                return;
            }

            _shadow.distanceToPlayer -= _speed * Time.deltaTime;
            _shadow.distanceToPlayer = Mathf.Clamp(_shadow.distanceToPlayer, 0f, 1500f);

            _aggroTimer += Time.deltaTime;
            if (_aggroTimer >= _shadow.timeToSwitchToAggressive)
                _speed = _shadow.speedAggressive;

            _curAngle += 30f * Time.deltaTime;
            if (_curAngle > 360f) _curAngle -= 360f;

            float rad = _curAngle * Mathf.Deg2Rad;
            Vector3 offset = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad)) * _shadow.distanceToPlayer;
            transform.position = _proxyT.position + offset;

            Vector3 dir = (_proxyT.position - transform.position).normalized;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Euler(90f, Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, 0f);

            if (_shadow.distanceToPlayer <= 80f && !_hasAttacked)
                SpawnAttackSensor();
        }

        private void SpawnAttackSensor()
        {
            if (_proxyT == null) return;

            if (Core.isInLight(_proxyT.position, mustBeWalkable: true))
                return;

            if (!PoolManager.Pools.TryGetValue("Sensors", out var pool)) return;
            if (!pool.prefabs.TryGetValue("MeleeSensor_shadow", out var prefab)) return;

            var sensorGO = pool.Spawn(prefab, transform.position,
                Quaternion.Euler(90f, transform.eulerAngles.y, 0f));
            if (sensorGO == null) return;

            var sensor = sensorGO.GetComponent<MeleeSensor>();
            if (sensor == null) return;

            sensor.attackerTransform = transform;
            sensor.type = MeleeSensor.MeleeSensorType.character;
            sensor.damage = _shadow != null ? _shadow.damage : 10;

            _hasAttacked = true;

            // No need to initialize hit tracking — MeleeSensorDeduplicatePatch
            // now uses time-based debounce (nameHash + Time.time) instead of
            // per-sensor-instance state.

            Invoke(nameof(StartDying), 1.5f);
        }

        private void StartDying()
        {
            if (_isDying) return;
            _isDying = true;

            if (_shadow != null)
                _shadow.dead = true;

            if (_anim != null && _anim.GetClipByName("Death1") != null)
                _anim.Play("Death1");

            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null && net.Role == NetworkRole.Host)
            {
                var info = GetComponent<ShadowSyncInfo>();
                if (info != null)
                    net.UnregisterShadow(info.ShadowId);
            }

            Destroy(this, 2f);
            Destroy(gameObject, 3f);
        }
    }

    /// <summary>
    /// Prevents ProxyShadowController shadows from calling ShadowCreature.appear(),
    /// which would teleport them to the host player's position (uses Player.Instance).
    /// The controller handles its own positioning and appearance.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(ShadowCreature), "appear")]
    public static class ProxyShadowAppearBlock
    {
        private static bool Prefix(ShadowCreature __instance)
        {
            if (__instance.GetComponent<ProxyShadowController>() != null)
                return false;
            return true;
        }
    }
}
