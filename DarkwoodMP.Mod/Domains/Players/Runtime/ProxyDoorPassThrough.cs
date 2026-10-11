using UnityEngine;

namespace DWMPHorde.Players
{
    /// <summary>
    /// Another player's stand-in does not push doors here. That player's own game swings the door
    /// with its real body and sends the swing (DoorState); the stand-in pushed this game's copy as
    /// well, so two pushes fought over one door. Near the hinge a door barely turns, the dynamic
    /// stand-in dug into it and was thrown back, and the door shook between the two games' angles
    /// with its creak restarting. On its first touch of a door the stand-in stops colliding with it.
    /// </summary>
    internal sealed class ProxyDoorPassThrough : MonoBehaviour
    {
        private Collider[] _own;

        private void Awake()
        {
            _own = GetComponentsInChildren<Collider>(true);
        }

        private void OnCollisionEnter(Collision collision)
        {
            Collider other = collision != null ? collision.collider : null;
            if (other == null || other.GetComponentInParent<DoorBody>() == null)
                return;
            if (_own == null)
                return;
            for (int i = 0; i < _own.Length; i++)
            {
                if (_own[i] != null)
                    Physics.IgnoreCollision(_own[i], other, true);
            }
        }
    }
}
