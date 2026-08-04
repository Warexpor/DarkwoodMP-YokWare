namespace DWMPHorde
{
    /// <summary>Shared gameplay magic numbers used across patches/networking.</summary>
    public static class GameplayConstants
    {
        /// <summary>Vanilla player hitscan Physics.Raycast layer mask.</summary>
        public const int HitscanLayerMask = 18909185;

        /// <summary>Default occlusion mask for scrape / moving-object audio.</summary>
        public const int DefaultOcclusionLayerMask = 32769;

        /// <summary>
        /// Host WorldGrid proxy bubble + entity broadcast near-band (world units).
        /// Was 3500 — dual bubble woke 200–340 ents past the 256 snapshot cap → client
        /// phantom/claim/destroy storms when both players entered save-heavy areas.
        /// Aligned with client entity interest (1400).
        /// </summary>
        public const float EntityActivationRange = 1400f;

        /// <summary>
        /// Max attacker→target distance for client PlayerAttack on host.
        /// Matches entity broadcast range so long guns / open-map fights are not dropped
        /// by the old 350u clamp (felt like "bullets do nothing" far from host).
        /// </summary>
        public const float MaxPlayerAttackRange = 3500f;

        /// <summary>
        /// When resolving an unsynced client hit by name, host entity must be this close
        /// to the client's reported target position (avoids hitting a same-named dog map-wide).
        /// </summary>
        public const float PlayerAttackNameMatchRadius = 80f;

        /// <summary>Night spawn "far proxy" minimum distance from host.</summary>
        public const float FarProxyMinDistance = 1000f;
    }
}
