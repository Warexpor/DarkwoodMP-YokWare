using UnityEngine;

namespace DWMPHorde.Players
{
    /// <summary>
    /// Physics / immobilisation helper for a co-op player clone body (proxy or local clone).
    /// </summary>
    public sealed class LocalPlayerBodyController : MonoBehaviour
    {
        private Transform _transform;
        private Rigidbody _rigidbody;
        private tk2dSpriteAnimator _torsoAnimator;
        private tk2dSpriteAnimator _legsAnimator;
        private Player _player;
        private bool _wasImmobilised;

        /// <summary>
        /// Copies rigidbody properties from the main player and caches component references.
        /// </summary>
        public void SetupFromMain(Player main)
        {
            _transform = transform;
            _rigidbody = GetComponent<Rigidbody>();
            _torsoAnimator = GetComponent<tk2dSpriteAnimator>();
            _player = GetComponent<Player>();

            Transform legs = _transform.Find("PlayerLegs");
            if (legs != null)
            {
                _legsAnimator = legs.GetComponent<tk2dSpriteAnimator>();
                Renderer legsRenderer = legs.GetComponent<Renderer>();
                if (legsRenderer != null)
                    legsRenderer.enabled = true;
            }

            if (main != null && _rigidbody != null && main.Rigidbody != null)
            {
                _rigidbody.mass = main.Rigidbody.mass;
                _rigidbody.drag = main.Rigidbody.drag;
                _rigidbody.angularDrag = main.Rigidbody.angularDrag;
                _rigidbody.useGravity = main.Rigidbody.useGravity;
                _rigidbody.constraints = main.Rigidbody.constraints;
                _rigidbody.interpolation = main.Rigidbody.interpolation;
            }
        }

        private void Update()
        {
            if (_player == null || _rigidbody == null)
                return;

            bool immobilised = _player.immobilised;

            if (immobilised && !_wasImmobilised)
                OnImmobilised();

            if (!immobilised && _wasImmobilised)
                OnUnImmobilised();

            _wasImmobilised = immobilised;

            ClampHeight();
        }

        private void OnImmobilised()
        {
            _rigidbody.velocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;

            if (_legsAnimator != null)
                _legsAnimator.Stop();
        }

        private void OnUnImmobilised()
        {
            if (_torsoAnimator != null)
                _torsoAnimator.Play("Idle");
        }

        // Keeps the clone at the correct Y for Darkwood's 2.5D perspective
        private void ClampHeight()
        {
            float yPos = Core.getYpos(PosType.player, randomize: false);
            Vector3 pos = _transform.position;
            _transform.position = new Vector3(pos.x, yPos, pos.z);
        }
    }
}
