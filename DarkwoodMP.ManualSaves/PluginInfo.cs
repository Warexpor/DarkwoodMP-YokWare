namespace YokWare.ManualSaves
{
    public static class PluginInfo
    {
        public const string Guid = "com.yokware.manualsaves";
        public const string Name = "YokWare Manual Saves";
        public const string Version = "1.1.0";
        /// <summary>The co-op mod this add-on runs on (BepInEx GUID of YokWare Branch).</summary>
        public const string CoopGuid = "com.yokware.branch";
        /// <summary>The oldest co-op mod with the API this add-on calls (slots keep the characters: 0.8.296).</summary>
        public const string MinCoopVersion = "0.8.296";
    }
}
