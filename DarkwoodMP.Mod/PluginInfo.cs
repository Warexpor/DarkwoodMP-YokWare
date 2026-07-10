namespace DWMPHorde
{
    /// <summary>
    /// YokWare Branch product identity — Path B ships Horde remaster sync as the load path.
    /// Internal namespace remains DWMPHorde; BepInEx GUID is the public product id.
    /// </summary>
    public static class PluginInfo
    {
        public const string Guid = "com.yokware.branch";
        public const string Name = "YokWare Branch";
        /// <summary>BepInEx plugin version (semver).</summary>
        public const string Version = "0.9.1";
        /// <summary>Shown in UI banners and multiplayer menu.</summary>
        public const string DisplayVersion = "0.9.1 Path B (Horde base)";
        /// <summary>Horde LAN wire protocol (unchanged from remaster 0.4.15).</summary>
        public const int ProtocolVersion = 19;
        public const int DefaultPort = 7788;
        public const string Authors = "Warexpor & Yokyy";
        public const string Description = "Darkwood co-op — Horde host-authoritative sync (Path B)";
    }
}
