namespace DWMPHorde
{
    /// <summary>
    /// YokWare Branch product identity. Path B ships the Horde remaster sync.
    /// Internal namespace remains DWMPHorde; BepInEx GUID is the public product id.
    /// </summary>
    public static class PluginInfo
    {
        public const string Guid = "com.yokware.branch";
        public const string Name = "YokWare Branch";
        /// <summary>
        /// BepInEx plugin version and the single source of the product version: the csproj reads it
        /// from this line and AssemblyInfo derives from it. The supported product line is 0.8.x.
        /// </summary>
        public const string Version = "0.8.188";
        /// <summary>Full product label shown in UI banners, the multiplayer menu and log banners (already includes the product name).</summary>
        public const string DisplayVersion = Name + " " + Version + " / Path B";
        /// <summary>Horde wire protocol. Bumped whenever a wire format changes or a message id is added.</summary>
        public const int ProtocolVersion = 51;
        public const int DefaultPort = 7788;
        public const string Authors = "Warexpor & Yokyy";
        public const string Description = "Darkwood co-op — Horde host-authoritative sync (Path B)";
    }
}
