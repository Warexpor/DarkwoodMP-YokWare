using UnityEngine;

namespace DWMPHorde.Players
{
    /// <summary>
    /// Drives torso and leg animations for the second player (local co-op or remote proxy) based on network snapshots.
    /// </summary>
    public sealed partial class SecondPlayerAnimController : MonoBehaviour
    {
        /// <summary>
        /// Movement state used to select the correct animation set.
        /// </summary>
        public enum LocomotionState : byte
        {
            /// <summary>Standing still.</summary>
            Idle = 0,
            /// <summary>Walking.</summary>
            Walk = 1,
            /// <summary>Running.</summary>
            Run = 2
        }

        private tk2dSpriteAnimator _torsoAnimator;
        private tk2dSpriteAnimator _legsAnimator;
        private tk2dBaseSprite _torsoSprite;
        private Renderer _legsRenderer;

        // Fallback animation library used when the clone lacks certain torso clips
        private tk2dSpriteAnimation _noneAnimsLib;
        private LocomotionState _state = LocomotionState.Idle;
        private bool _deathClipPlayed;
        private bool _flipX;
        private bool _networkReverseLegs;
        private bool _hasNetworkLegFacing;

        // The legs' world heading is the player's own, in every state. Vanilla turns the legs
        // towards the walking direction on their own, apart from the body: standing still they
        // keep the last direction walked while the body turns with the aim. Each state carries
        // the heading the player's game shows; the copy glides from the one shown to the newest
        // over one send interval, so 30 values a second do not step on a faster screen.
        private float _legFromY;
        private float _legToY;
        private float _legShownY;
        private float _legGlide = 1f;

        // Desired LegsWalk rate for this proxy (vanilla setLegsFPS: 10 drag / 17 walk).
        // Applied via animator.ClipFps so we never mutate shared library assets.
        private bool _legsDragFps;

        // The torch flame particles follow the hand per frame (item emitterPositions); its light
        // stays at the body centre, as vanilla Player does.
        private Transform _particleEmitter;
        private InvItem _emitterItem;

        /// <summary>Current locomotion state.</summary>
        public LocomotionState State => _state;
        /// <summary>Current horizontal flip state.</summary>
        public bool FlipX => _flipX;

        private void Awake()
        {
            _torsoAnimator = GetComponent<tk2dSpriteAnimator>();
            _torsoSprite = GetComponent<tk2dBaseSprite>();
            _noneAnimsLib = Resources.Load("PlayerNoneAnims", typeof(tk2dSpriteAnimation)) as tk2dSpriteAnimation;

            Transform legsTransform = transform.Find("PlayerLegs");
            if (legsTransform != null)
            {
                _legsAnimator = legsTransform.GetComponent<tk2dSpriteAnimator>();
                _legsRenderer = legsTransform.GetComponent<Renderer>();

                if (_legsAnimator != null)
                    _legsAnimator.AnimationEventTriggered += OnLegsAnimationEvent;
            }

            if (_torsoAnimator == null)
                ModRuntime.Log?.LogWarning("SecondPlayerAnimController: no torso tk2dSpriteAnimator on root.");

            if (_legsAnimator == null)
                ModRuntime.Log?.LogWarning("SecondPlayerAnimController: no PlayerLegs / legs animator found.");
            else
                ModRuntime.LegacyInfo("SecondPlayerAnimController: torso + legs animators ready.");
        }

        // Stops legs animation when the feet-neutral event fires during idle
        private void OnLegsAnimationEvent(tk2dSpriteAnimator animator, tk2dSpriteAnimationClip clip, int frameNum)
        {
            if (_state != LocomotionState.Idle)
                return;

            if (clip.GetFrame(frameNum).eventInfo == "FeetNeutral")
                _legsAnimator?.Stop();
        }

        private void LateUpdate()
        {
            TurnLegs();
            UpdateEmitterPosition();
        }

        /// <summary>The newest leg heading from the player; the first one is taken as it is.</summary>
        private void SetLegHeading(short legFacingY)
        {
            if (!_hasNetworkLegFacing)
            {
                _hasNetworkLegFacing = true;
                _legFromY = _legToY = _legShownY = legFacingY;
                _legGlide = 1f;
                return;
            }
            _legFromY = _legShownY;
            _legToY = legFacingY;
            _legGlide = 0f;
        }

        /// <summary>
        /// After the body's turn (Update). Running, vanilla holds the legs on the body's rotation;
        /// otherwise they keep their own world heading while the body turns above them.
        /// </summary>
        private void TurnLegs()
        {
            if (_legsAnimator == null || !_hasNetworkLegFacing)
                return;
            if (_state == LocomotionState.Run)
            {
                _legsAnimator.transform.rotation = transform.rotation;
                _legShownY = transform.eulerAngles.y;
                return;
            }
            _legGlide = Mathf.Min(1f, _legGlide + Time.deltaTime / Networking.LanNetworkManager.SendInterval);
            _legShownY = Mathf.LerpAngle(_legFromY, _legToY, _legGlide);
            _legsAnimator.transform.rotation = Quaternion.Euler(90f, _legShownY, 0f);
        }

        /// <summary>Current torso clip name (for light/torch diagnostics).</summary>
        public string CurrentTorsoClipName => _torsoAnimator != null ? _torsoAnimator.CurrentClip?.name : null;

        /// <summary>
        /// Sets the item whose emitter positions should drive the torch/lantern
        /// light and particle effect positions each frame.
        /// </summary>
        public void SetEmittedItem(InvItem itemDef)
        {
            _emitterItem = itemDef;
            _particleEmitter = transform.Find("ItemParticleEmitter");
            _lastEmitterClipMiss = null;
        }

        /// <summary>
        /// Clears the emitter item reference so emitters snap to (0,0,z).
        /// </summary>
        public void ClearEmittedItem()
        {
            _emitterItem = null;
            _lastEmitterClipMiss = null;
        }

        /// <summary>True when this anim controller is already driving emitters for <paramref name="itemType"/>.</summary>
        public bool HasEmittedItem(string itemType)
        {
            if (_emitterItem == null || string.IsNullOrEmpty(itemType))
                return false;
            if (transform.Find("ItemLightEmitter") == null)
                return false;
            string t = _emitterItem.type ?? _emitterItem.name ?? "";
            return string.Equals(t, itemType, System.StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(t) && t.IndexOf(itemType, System.StringComparison.OrdinalIgnoreCase) >= 0)
                || itemType.IndexOf(t, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Whether emitterPositions has a key for this torso clip (or Idle fallback).</summary>
        public bool EmitterHasClip(string clipName)
        {
            if (_emitterItem == null) return false;
            var ep = _emitterItem.emitterPositions;
            if (ep == null || ep.typesDict == null) return false;
            if (!string.IsNullOrEmpty(clipName) && ep.typesDict.ContainsKey(clipName))
                return true;
            return ep.typesDict.ContainsKey("Idle");
        }

        private string _lastEmitterClipMiss;

        internal void UpdateEmitterPosition()
        {
            if (_particleEmitter == null)
                _particleEmitter = transform.Find("ItemParticleEmitter");

            if (_emitterItem == null || _torsoAnimator == null)
                return;

            var ep = _emitterItem.emitterPositions;
            if (ep == null || ep.typesDict == null || ep.typesDict.Count == 0)
                return;

            string clipName = _torsoAnimator.CurrentClip?.name;
            if (string.IsNullOrEmpty(clipName))
            {
                if (!ep.typesDict.TryGetValue("Idle", out var idleEntry))
                    return;
                int f = _torsoAnimator != null ? _torsoAnimator.CurrentFrame : 0;
                if (idleEntry.positions == null || idleEntry.positions.Count <= f)
                    return;
                Vector2 p = idleEntry.positions[f];
                if (_particleEmitter != null)
                    _particleEmitter.localPosition = new Vector3(p.x, p.y, _particleEmitter.localPosition.z);
                return;
            }

            if (!ep.typesDict.TryGetValue(clipName, out var entry))
            {
                // One log per missing clip name; a wrong torch flame usually indicates this.
                if (!string.Equals(_lastEmitterClipMiss, clipName, System.StringComparison.Ordinal))
                {
                    _lastEmitterClipMiss = clipName;
                    ModRuntime.LegacyInfo($"[Light] emitter clip miss type={(_emitterItem.type ?? "?")} clip={clipName} keys={ep.typesDict.Count}");
                }
                // Fallback Idle so flame still follows something while walking unknown clips.
                if (ep.typesDict.TryGetValue("Idle", out var idleFb)
                    && idleFb.positions != null && idleFb.positions.Count > 0)
                {
                    Vector2 p = idleFb.positions[0];
                    if (_particleEmitter != null)
                        _particleEmitter.localPosition = new Vector3(p.x, p.y, _particleEmitter.localPosition.z);
                }
                return;
            }

            int frame = _torsoAnimator.CurrentFrame;
            if (entry.positions == null || entry.positions.Count <= frame)
                return;

            Vector2 pos = entry.positions[frame];
            if (_particleEmitter != null)
                _particleEmitter.localPosition = new Vector3(pos.x, pos.y, _particleEmitter.localPosition.z);
        }
    }
}
