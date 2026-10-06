using System;
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

        /// <summary>
        /// Applies a network state snapshot to the proxy (position, animation, vault state).
        /// </summary>
        public void ApplyNetworkState(PlayerStateNet state)
        {
            _targetRotationY = state.TorsoFacingY;
            _hasState = true;

            if (!_freezePosition)
            {
                _targetPosition = state.Position;

                // Snap on first state or large teleports (outside location load: world → bunker/village).
                // Pure lerp left proxies kilometers off-map for seconds after loading screens.
                if (_rb != null)
                {
                    if (_firstState)
                    {
                        _firstState = false;
                        _rb.position = state.Position;
                        _rb.velocity = Vector3.zero;
                    }
                    else
                    {
                        Vector3 flat = _rb.position - state.Position;
                        flat.y = 0f;
                        bool farXz = flat.sqrMagnitude > 150f * 150f;
                        bool farY = Mathf.Abs(_rb.position.y - state.Position.y) > 40f;
                        if (farXz || farY)
                        {
                            _rb.position = state.Position;
                            _rb.velocity = Vector3.zero;
                        }
                    }
                }
                else if (_firstState)
                {
                    _firstState = false;
                }
            }

            _anim?.ApplyNetworkSnapshot(
                state.Locomotion, state.FlipX, state.LegFacingY,
                state.ReverseLegs, state.TorsoFacingY, state.TorsoClip, state.LegsClip, state.CurrentFrame);

            bool nowVaulting = state.TorsoClip == "JumpWindow";
            if (nowVaulting != _isVaulting)
            {
                _isVaulting = nowVaulting;

                // Disable colliders during vault so proxy can pass through window;
                // re-enable when vault ends.
                foreach (var col in _cachedColliders)
                    col.enabled = !nowVaulting;

                if (!nowVaulting)
                {
                    // Vault just ended — teleport to exact final position so we don't
                    // accumulate lag from the lerp.
                    _rb.position = _targetPosition;
                    _pushOffset = Vector3.zero;
                }
            }
        }

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _cachedColliders = GetComponentsInChildren<Collider>(true);
            _cachedCharBase = GetComponent<CharBase>();
        }

        private void Start()
        {
            foreach (tk2dSpriteAnimator anim in GetComponentsInChildren<tk2dSpriteAnimator>(true))
            {
                if (anim.name.IndexOf("leg", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    anim.AnimationEventTriggered += OnLegAnimationEvent;
                    break;
                }
            }
        }

        private void OnLegAnimationEvent(tk2dSpriteAnimator animator, tk2dSpriteAnimationClip clip, int frameNum)
        {
            string eventInfo = clip.GetFrame(frameNum).eventInfo;
            if (eventInfo == "FootHitGround")
                OnFootstep?.Invoke(PlayerId, false);
            else if (eventInfo == "FootHitGroundRun")
                OnFootstep?.Invoke(PlayerId, true);
        }

        private void OnDestroy()
        {
            DWMPHorde.Patches.EventTriggersProxyOccupancy.ForgetPlayer(PlayerId);
            foreach (tk2dSpriteAnimator anim in GetComponentsInChildren<tk2dSpriteAnimator>(true))
            {
                if (anim.name.IndexOf("leg", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    anim.AnimationEventTriggered -= OnLegAnimationEvent;
                    break;
                }
            }
        }

        // Safety-net: detect Bullet component collisions in case FastProjectile
        // raycast misses the proxy (short-distance edge cases).
        private void OnCollisionEnter(Collision collision)
        {
            if (collision.rigidbody == null) return;

            // Same item-velocity suppression as OnCollisionStay — prevents the
            // initial impact from pushing the object and triggering ItemSounds.
            if (collision.rigidbody.GetComponent<Item>() != null)
            {
                collision.rigidbody.velocity = Vector3.zero;
                collision.rigidbody.angularVelocity = Vector3.zero;
                StopNativeItemSound(collision.rigidbody);
                return;
            }

            var bullet = collision.gameObject.GetComponent<Bullet>();
            if (bullet == null) return;

            var net = ModRuntime.Network as Networking.LanNetworkManager;
            if (net == null || net.Role == Networking.NetworkRole.Offline) return;
            if (bullet.objectThatSpawnedMe != null) return; // Skip enemy bullets
            if (!SessionSettings.FriendlyFireEnabled) return; // FF disabled

            // Prefer live weapon modded damage when local player owns the shot;
            // bullet.damage is set at spawn and may omit upgrade modifiers.
            int dmg = Mathf.Max(1, bullet.damage);
            Player local = Player.Instance;
            if (local != null && !InvItemClass.isNull(local.currentItem) && local.currentItem.baseClass != null
                && local.currentItem.baseClass.isFirearm)
            {
                dmg = Mathf.Max(1, local.currentItem.getModdedDamage(local.currentItem.baseClass.damage));
            }
            if (!ProxyCombatRelay.TryConsumeSafetyNet(net.LocalPlayerId, PlayerId))
                return;
            Vector3 pos = transform.position;

            if (net.Role == Networking.NetworkRole.Host)
            {
                // Host: send damage to the client that owns this proxy (never broadcast).
                net.SendToPlayer(PlayerId, Networking.NetMessageType.DamagePlayer, w =>
                {
                    new Networking.DamagePlayerMessage
                    {
                        Damage = dmg,
                        AttackerPosX = pos.x,
                        AttackerPosY = pos.y,
                        AttackerPosZ = pos.z,
                        ShowRedScreen = true,
                        NormalHit = true,
                        CanInterrupt = true
                    }.Serialize(w);
                }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            else
            {
                // Client: send friendly fire to host (who will relay to the correct player)
                net.Send(Networking.NetMessageType.FriendlyFire, w =>
                {
                    new Networking.FriendlyFireMessage
                    {
                        Damage = dmg,
                        AttackerPosX = pos.x,
                        AttackerPosY = pos.y,
                        AttackerPosZ = pos.z,
                        AttackerPlayerId = net.LocalPlayerId,
                        VictimPlayerId = PlayerId
                    }.Serialize(w);
                }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }

            ModRuntime.LegacyInfo($"[ProxyCollisionEnter] bullet hit proxy, relayed {dmg} damage");

            // Physically destroy the bullet so it doesn't persist
            if (collision.gameObject != null)
                UnityEngine.Object.Destroy(collision.gameObject);
        }

        // Throttled log counter to avoid spamming the log file
        private static int _pushCollideCount; // process-scoped: log throttle

        private void OnCollisionStay(Collision collision)
        {
            if (collision.rigidbody == null)
                return;

            // Prevent proxy from pushing items — the host's own physics handles
            // all object movement.  Without this, the client's native ItemSounds
            // plays the moving sound when the proxy collides with furniture, and
            // the sound persists because the host stops pushing but the client's
            // proxy keeps colliding with (or resting on) the object locally.
            if (collision.rigidbody.GetComponent<Item>() != null)
            {
                collision.rigidbody.velocity = Vector3.zero;
                collision.rigidbody.angularVelocity = Vector3.zero;
                StopNativeItemSound(collision.rigidbody);
                return;
            }

            if (collision.rigidbody == Player.Instance?.Rigidbody)
            {
                if (++_pushCollideCount % 60 == 0 && ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[ProxyCollide] pushed by local player");

                Vector3 pushDir = transform.position - collision.transform.position;
                pushDir.y = 0f;
                if (pushDir.magnitude > 0.01f)
                    pushDir.Normalize();
                float speed = Mathf.Clamp(collision.relativeVelocity.magnitude * 0.5f, 0f, 4f);
                _pushOffset = pushDir * speed;
            }
        }

        /// <summary>Stop the native ItemSounds moving sound the same way the original
        /// game does: movingSoundAO.Stop(0.5f).  Also put the Rigidbody to sleep so
        /// ItemSounds.Update() returns early (the IsSleeping() check at line 144).</summary>
        private static void StopNativeItemSound(Rigidbody rb)
        {
            var sounds = rb.GetComponent<ItemSounds>();
            if (sounds == null) return;

            // Primary: access private movingSoundAO field via Traverse
            var ao = Traverse.Create(sounds).Field("movingSoundAO").GetValue<AudioObject>();
            if (ao != null)
            {
                rb.Sleep();
                ao.Stop(0.5f);
                Traverse.Create(sounds).Field("movingSoundAO").SetValue(null);
                return;
            }

            // Fallback: use the game's own API to find and stop all playing AudioObjects
            string soundId = sounds.movingSound;
            if (!string.IsNullOrEmpty(soundId))
            {
                var playing = AudioController.GetPlayingAudioObjects(soundId);
                if (playing != null)
                {
                    foreach (var playingAo in playing)
                    {
                        if (playingAo != null)
                            playingAo.Stop(0.5f);
                    }
                }
            }
        }

        private void FixedUpdate()
        {
            if (!_hasState || _rb == null)
                return;

            if (_isVaulting)
            {
                // During vault: teleport directly to target position.
                // Colliders are disabled so we pass through the window.
                _rb.position = _targetPosition;
                _rb.velocity = Vector3.zero;
                _pushOffset = Vector3.zero;
                return;
            }

            Vector3 target = _targetPosition + _pushOffset;

            // Clamp horizontal drift so proxy can't be launched away by chain-reaction pushes.
            // Keep network Y — old code locked target.y to rb.y while FreezePositionY was set,
            // so a bad first place (Y=-12k under bunker) made the host permanently invisible.
            Vector3 drift = target - _targetPosition;
            drift.y = 0f;
            float maxDrift = 50f;
            if (drift.magnitude > maxDrift)
                target = new Vector3(
                    _targetPosition.x + drift.normalized.x * maxDrift,
                    _targetPosition.y,
                    _targetPosition.z + drift.normalized.z * maxDrift);

            float t = 18f * Time.fixedDeltaTime;
            Vector3 next = Vector3.Lerp(_rb.position, target, t);
            // Snap Y when network height diverges (teleport / dream pad / bunker floor).
            if (Mathf.Abs(_rb.position.y - _targetPosition.y) > 40f)
                next.y = _targetPosition.y;

            Vector3 delta = next - _rb.position;
            // Y via position (constraint FreezePositionY otherwise ignores velocity.y).
            if (Mathf.Abs(delta.y) > 0.001f)
            {
                Vector3 p = _rb.position;
                p.y = next.y;
                _rb.position = p;
                delta.y = 0f;
            }

            // Use velocity so Unity physics naturally handles entity pushing (XZ only)
            _rb.velocity = delta / Time.fixedDeltaTime;

            // Decay push force gradually
            _pushOffset = Vector3.Lerp(_pushOffset, Vector3.zero, Time.fixedDeltaTime * 10f);

            // Smoothly interpolate Y rotation to avoid cone/view jitter from low-rate network updates
            float rotT = 18f * Time.fixedDeltaTime;
            Vector3 euler = transform.eulerAngles;
            euler.y = Mathf.LerpAngle(euler.y, _targetRotationY, rotT);
            transform.eulerAngles = euler;
        }

        // Forces shadow to always point straight down for correct 2.5D appearance
        private void LateUpdate()
        {
            if (_shadow != null)
                _shadow.eulerAngles = new Vector3(90f, 0f, 0f);
        }
    }

    /// <summary>
    /// Snapshot of a remote player's position and animation state sent over the network.
    /// </summary>
    public struct PlayerStateNet
    {
        /// <summary>World position.</summary>
        public Vector3 Position;
        /// <summary>Locomotion state (Idle/Walk/Run).</summary>
        public SecondPlayerAnimController.LocomotionState Locomotion;
        /// <summary>Whether the sprite is flipped horizontally.</summary>
        public bool FlipX;
        /// <summary>Legs object Y rotation (quantised to short).</summary>
        public short LegFacingY;
        /// <summary>Whether legs are reversed (walking backwards).</summary>
        public bool ReverseLegs;
        /// <summary>Torso object Y rotation (quantised to short).</summary>
        public short TorsoFacingY;
        /// <summary>Name of the currently playing torso clip, or null/empty for idle.</summary>
        public string TorsoClip;
        /// <summary>Name of the currently playing legs clip, or null/empty for idle.</summary>
        public string LegsClip;
        /// <summary>Current frame index of the torso animation, or -1 if unknown.</summary>
        public short CurrentFrame;
    }
}
