namespace DWMPHorde
{
    /// <summary>Shared gameplay magic numbers used across patches/networking.</summary>
    public static class GameplayConstants
    {
        /// <summary>Vanilla player hitscan Physics.Raycast layer mask.</summary>
        public const int HitscanLayerMask = 18909185;

        /// <summary>Default occlusion mask for scrape / moving-object audio.</summary>
        public const int DefaultOcclusionLayerMask = 32769;

        /// <summary>WorldGrid / AI activation range (world units).</summary>
        public const float EntityActivationRange = 3500f;

        /// <summary>Night spawn "far proxy" minimum distance from host.</summary>
        public const float FarProxyMinDistance = 1000f;
    }
}
