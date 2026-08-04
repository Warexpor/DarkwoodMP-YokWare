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
        /// <summary>
        /// BepInEx plugin version (semver). 0.7.x = active Path B line.
        /// Earlier "0.9.x" labels were too ambitious — see CHANGELOG Versioning.
        /// </summary>
        public const string Version = "0.7.33";
        /// <summary>Shown in UI banners and multiplayer menu.</summary>
        public const string DisplayVersion = "0.7.33 Path B (dream karuzela)";
        /// <summary>Horde LAN wire protocol (unchanged from remaster 0.4.15). Optional IDs 112–129.</summary>
        public const int ProtocolVersion = 19;
        public const int DefaultPort = 7788;
        public const string Authors = "Warexpor & Yokyy";
        public const string Description = "Darkwood co-op — Horde host-authoritative sync (Path B)";
    }
}
