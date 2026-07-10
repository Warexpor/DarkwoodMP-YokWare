using System.Reflection;
using UnityEngine;

namespace DWMPHorde.Players
{
    /// <summary>
    /// Reads current animation state from a Player for network replication.
    /// </summary>
    public static class PlayerAnimationSnapshot
    {
        private static readonly FieldInfo WantToReverseLegsField =
            typeof(Player).GetField("wantToReverseLegs", BindingFlags.Instance | BindingFlags.NonPublic);

        // Vanilla default legs threshold (Player.legsAnimatorMagnitude when not dragging).
        private const float LegAnimThresholdNormal = 55f;

        /// <summary>
        /// Matches Player.legsAnimatorMagnitude: 10 while dragging, 55 otherwise.
        /// Drag walk is slower; without this the remote never gets Walk at drag speeds.
        /// </summary>
        public static float GetLegAnimThreshold(Player player)
        {
            if (player != null && player.dragging)
                return 10f;
            return LegAnimThresholdNormal;
        }

        /// <summary>
        /// Determines the locomotion state (Idle/Walk/Run) from the player's rigidbody velocity and running flag.
        /// </summary>
        public static SecondPlayerAnimController.LocomotionState ReadLocomotion(Player player)
        {
            if (player == null)
                return SecondPlayerAnimController.LocomotionState.Idle;

            if (player.immobilised)
                return SecondPlayerAnimController.LocomotionState.Idle;

            if (player.running)
                return SecondPlayerAnimController.LocomotionState.Run;

            Vector3 vel = player.Rigidbody != null ? player.Rigidbody.velocity : Vector3.zero;
            float speed = new Vector2(vel.x, vel.z).magnitude;
            if (speed >= GetLegAnimThreshold(player))
                return SecondPlayerAnimController.LocomotionState.Walk;

            return SecondPlayerAnimController.LocomotionState.Idle;
        }

        /// <summary>
        /// Reads the torso's Y rotation as a short.
        /// </summary>
        public static short ReadTorsoFacingY(Player player)
        {
            if (player == null)
                return 0;

            return (short)Mathf.RoundToInt(player.transform.eulerAngles.y);
        }

        /// <summary>
        /// Reads the legs object's Y rotation (falls back to the root transform if PlayerLegs is missing).
        /// </summary>
        public static short ReadLegFacingY(Player player)
        {
            if (player == null)
                return 0;

            Transform legs = player.transform.Find("PlayerLegs");
            float y = legs != null ? legs.eulerAngles.y : player.transform.eulerAngles.y;
            return (short)Mathf.RoundToInt(y);
        }

        /// <summary>
        /// Reads the private wantToReverseLegs field via reflection.
        /// </summary>
        public static bool ReadReverseLegs(Player player)
        {
            if (player == null || WantToReverseLegsField == null)
                return false;

            object value = WantToReverseLegsField.GetValue(player);
            return value is bool b && b;
        }

        /// <summary>
        /// Reads whether the player's sprite is flipped horizontally.
        /// </summary>
        public static bool ReadFlipX(Player player)
        {
            tk2dSprite sprite = player != null ? player.GetComponentInChildren<tk2dSprite>(true) : null;
            return sprite != null && sprite.FlipX;
        }

        /// <summary>
        /// Reads the current frame index of the torso animation.
        /// Returns -1 if the player or animator is unavailable.
        /// </summary>
        public static short ReadCurrentFrame(Player player)
        {
            if (player == null || player.torsoAnimator == null)
                return -1;
            return (short)player.torsoAnimator.CurrentFrame;
        }

        /// <summary>
        /// Reads the name of the currently playing torso clip. Returns null if the player is idling (no meaningful animation to sync).
        /// </summary>
        public static string ReadTorsoClip(Player player)
        {
            if (player == null || player.torsoAnimator == null)
                return null;
            var clip = player.torsoAnimator.CurrentClip;
            // Suppress Idle clip transmission; remote can derive idle from state
            if (clip == null || clip.name == "Idle")
            {
                Vector3 vel = player.Rigidbody != null ? player.Rigidbody.velocity : Vector3.zero;
                float speed = new Vector2(vel.x, vel.z).magnitude;
                if (speed < GetLegAnimThreshold(player) && !player.running)
                    return null;
            }
            return clip != null ? clip.name : null;
        }

        /// <summary>
        /// Reads the name of the currently playing legs clip. Returns null if the player is stationary (no walk/run animation).
        /// </summary>
        public static string ReadLegsClip(Player player)
        {
            if (player == null)
                return null;

            if (!player.running)
            {
                Vector3 vel = player.Rigidbody != null ? player.Rigidbody.velocity : Vector3.zero;
                float speed = new Vector2(vel.x, vel.z).magnitude;
                if (speed < GetLegAnimThreshold(player))
                    return null;
            }

            Transform legsT = player.transform.Find("PlayerLegs");
            if (legsT == null)
                return null;
            tk2dSpriteAnimator legsAnim = legsT.GetComponent<tk2dSpriteAnimator>();
            if (legsAnim == null)
                return null;
            var clip = legsAnim.CurrentClip;
            return clip != null ? clip.name : null;
        }
    }
}
