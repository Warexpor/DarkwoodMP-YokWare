using System;
using BepInEx.Logging;
using DWMPHorde.Config;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Players
{
    /// <summary>
    /// Network-driven proxy that mimics a remote player's position, animation, and collision.
    /// </summary>
    public sealed partial class RemotePlayerProxy : MonoBehaviour
    {
        private SecondPlayerAnimController _anim;
        private Transform _shadow;
        private Vector3 _targetPosition;
        private float _targetRotationY;
        private Vector3 _pushOffset;
        private bool _hasState;
        private bool _firstState = true;
        private Rigidbody _rb;
        private bool _isVaulting;
        private bool _freezePosition;
        private Collider[] _cachedColliders;
        private CharBase _cachedCharBase;

        /// <summary>Proxy body colliders (cached at Awake for vault/ignore paths).</summary>
        internal Collider[] CachedColliders => _cachedColliders;

        /// <summary>Proxy CharBase (cached at Awake for host AI / combat hot paths).</summary>
        internal CharBase CachedCharBase => _cachedCharBase;

        /// <summary>
        /// When true, ApplyNetworkState skips position updates so the proxy stays
        /// at the last teleported position. Set by DreamSyncManager during dream
        /// transitions to prevent the proxy from drifting while the remote player
        /// loads the dream scene. Cleared when the remote confirms dream entry.
        /// </summary>
        public bool FreezePosition { get => _freezePosition; set => _freezePosition = value; }

        /// <summary>The network PlayerId this proxy represents.</summary>
        public int PlayerId { get; set; } = -1;

        /// <summary>Whether the remote player has the Shadow Ward skill active.</summary>
        public bool RemoteHasShadowWard { get; set; }
        /// <summary>Whether the remote player has the Forest Spirit Ward skill active.</summary>
        public bool RemoteHasForestSpiritWard { get; set; }
        /// <summary>Whether the remote player has the Friend of the Forest skill active.</summary>
        public bool RemoteHasFriendOfTheForest { get; set; }
        /// <summary>Whether the remote player has the Enemy of the Forest skill active.</summary>
        public bool RemoteHasEnemyOfTheForest { get; set; }
        /// <summary>Whether the remote player is poisoned (visual/AI flag; DoT is local).</summary>
        public bool RemotePoisoned { get; set; }
        /// <summary>Whether the remote player is bleeding (visual/AI flag; DoT is local).</summary>
        public bool RemoteBleeding { get; set; }
        /// <summary>Whether the remote player is currently running.</summary>
        public bool RemoteRunning { get; set; }
        /// <summary>The last received locomotion state for the remote player.</summary>
        public SecondPlayerAnimController.LocomotionState RemoteLocomotion { get; set; }

        /// <summary>Fires when a footstep animation event occurs. Parameters: playerId, isRunning.</summary>
        public event Action<int, bool> OnFootstep;

        /// <summary>
        /// Creates the remote player GameObject, wires components, and returns the proxy component.
        /// </summary>
        public static bool Spawn(ManualLogSource log, out RemotePlayerProxy proxy)
        {
            proxy = null;
            Player source = PlayerControlRouter.MainPlayer ?? Player.Instance;
            if (source == null || source.gameObject == null || !source.gameObject.activeInHierarchy)
            {
                // Caller should gate; silent fail avoids log spam during LoadScene.
                return false;
            }

            // Far below world until first PlayerState — Vector3.zero offset parks the clone
            // on the local player's feet (body-stack on phase-3 join).
            GameObject clone = PlayerProxyBuilder.CreatePlayerClone(
                source,
                "RemotePlayer",
                new Vector3(0f, -2000f, 0f),
                PlayerCloneKind.Remote,
                log);
            if (clone == null)
                return false;

            EnableCollision(clone, log);
            EnableGroundLight(clone.transform, log);
            AddCharBase(clone, log);

            proxy = clone.AddComponent<RemotePlayerProxy>();
            proxy._anim = clone.GetComponent<SecondPlayerAnimController>();
            proxy._shadow = clone.transform.Find("Shadow");

            // Destroy all AudioSources on the proxy so no ambient/status-effect sound plays.
            // Proxy is a visual-only representation; all audio is forwarded via PlayerAudioMessage
            // (HandlePlayerAudio) or PlayProxyFootstepSound (AudioController.Play, not proxy AudioSource).
            foreach (var src in clone.GetComponentsInChildren<AudioSource>(true))
                UnityEngine.Object.Destroy(src);

            return true;
        }

        private static void AddCharBase(GameObject go, ManualLogSource log)
        {
            CharBase cb = go.AddComponent<CharBase>();
            cb.alive = true;
            cb.isActive = true;
            cb.faction = Faction.player;

            Player hostPlayer = Player.Instance;
            if (hostPlayer != null)
            {
                cb.maxHealth = hostPlayer.maxHealth;
                cb.Health = hostPlayer.health;
                log?.LogInfo($"RemoteProxy: HP pool = {cb.Health}/{cb.maxHealth} (matching host)");
            }
            else
            {
                cb.Health = 100f;
                cb.maxHealth = 100f;
                log?.LogInfo("RemoteProxy: HP pool = 100/100 (fallback, no host)");
            }
            log?.LogInfo("RemoteProxy: added standalone CharBase with Faction.player.");
        }

        private static void EnableGroundLight(Transform root, ManualLogSource log)
        {
            Transform shadow = root.Find("Shadow");
            if (shadow != null)
            {
                shadow.gameObject.SetActive(true);
                var sprite = shadow.GetComponent<tk2dBaseSprite>();
                if (sprite != null)
                {
                    Color c = sprite.color;
                    c.a = 1f;
                    sprite.color = c;
                }
                log?.LogInfo("RemoteProxy: enabled Shadow.");
            }
        }

        private static void EnableCollision(GameObject clone, ManualLogSource log)
        {
            Rigidbody existing = clone.GetComponent<Rigidbody>();
            if (existing != null)
                UnityEngine.Object.DestroyImmediate(existing);

            Rigidbody rb = clone.AddComponent<Rigidbody>();
            rb.isKinematic = false;
            rb.useGravity = false;
            rb.mass = 2.5f;
            rb.drag = 0f;
            rb.angularDrag = 10f;
            // Do not FreezePositionY — network teleports / dream pads need full Y authority.
            rb.constraints = RigidbodyConstraints.FreezeRotation;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            // Bullet.onCollide only damages layers Characters (11) and CharactersWater (21).
            const int layerCharacters = 11;
            int enabledCount = 0;
            int wasTrigger = 0;
            foreach (Collider col in clone.GetComponentsInChildren<Collider>(true))
            {
                if (col.isTrigger)
                    wasTrigger++;
                col.enabled = true;
                col.isTrigger = false;
                // Ensure projectile FastProjectile/Bullet raycasts can register hits.
                if (col.gameObject.layer != layerCharacters && col.gameObject.layer != 21)
                    col.gameObject.layer = layerCharacters;
                enabledCount++;
            }
            if (clone.layer != layerCharacters && clone.layer != 21)
                clone.layer = layerCharacters;
            log?.LogInfo($"RemoteProxy: enabled {enabledCount} colliders ({wasTrigger} were triggers, set to non-trigger), layer=Characters");
        }
    }
}
