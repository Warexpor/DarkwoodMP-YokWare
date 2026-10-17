namespace DWMPHorde
{
    /// <summary>Shared gameplay magic numbers used across patches/networking.</summary>
    public static class GameplayConstants
    {
        /// <summary>Vanilla player hitscan Physics.Raycast layer mask.</summary>
        public const int HitscanLayerMask = 18909185;

        /// <summary>
        /// Host WorldGrid proxy bubble + entity broadcast near-band (world units).
        /// Was 3500 — dual bubble woke 200–340 ents past the 256 snapshot cap → client
        /// phantom/claim/destroy storms when both players entered save-heavy areas.
        /// Aligned with client entity interest (1400).
        /// </summary>
        public const float EntityActivationRange = 1400f;

        /// <summary>
        /// A body already streamed / driven stays so until it is this far (XZ) from every player:
        /// host send range and client interest leave here and enter at
        /// <see cref="EntityActivationRange"/>, so a creature on the edge does not flap.
        /// </summary>
        public const float EntityInterestLeaveRange = 1500f;

        /// <summary>
        /// Max attacker→target distance for client PlayerAttack on host.
        /// Matches entity broadcast range so long guns / open-map fights are not dropped
        /// by the old 350u clamp (felt like "bullets do nothing" far from host).
        /// </summary>
        public const float MaxPlayerAttackRange = 3500f;

        /// <summary>
        /// Host check of a client's reported own position against the host proxy of that
        /// client (interpolation + latency drift at a sprint, with margin).
        /// </summary>
        public const float MaxAttackerPositionDrift = 400f;

        /// <summary>
        /// Max host-proxy → target distance for a client melee hit: weapon reach plus the
        /// lag of both the attacker proxy and the client's view of the target at a sprint.
        /// </summary>
        public const float MaxPlayerMeleeAttackRange = 200f;

        /// <summary>
        /// Max host-proxy → target distance for a client ranged / explosion hit: the client
        /// interest band (it cannot see or aim at entities beyond it).
        /// </summary>
        public const float MaxPlayerRangedAttackRange = 1400f;

        /// <summary>
        /// Per-peer attack token bucket: sustained hits per second. Sized by the fastest vanilla
        /// weapon: the military flamethrower shoots a flame every 0.04 s (25 a second) and each
        /// flame hits every body in the stream once, so 10 bodies in it is 250 hits a second. At
        /// 20 a second one target was the limit and a pack lost most of its hits.
        /// </summary>
        public const float PeerAttackRatePerSec = 250f;

        /// <summary>Per-peer attack token bucket: burst size (two seconds of the above).</summary>
        public const float PeerAttackBurst = 500f;

        /// <summary>Per-peer damage token bucket: sustained damage per second (the hits above at a flame's 18).</summary>
        public const float PeerDamageRatePerSec = 4500f;

        /// <summary>Per-peer damage token bucket: burst damage (two seconds of the above; an explosion over a pack fits).</summary>
        public const float PeerDamageBurst = 9000f;

        /// <summary>
        /// When resolving an unsynced client hit by name, host entity must be this close
        /// to the client's reported target position (avoids hitting a same-named dog map-wide).
        /// </summary>
        public const float PlayerAttackNameMatchRadius = 80f;
    }
}
