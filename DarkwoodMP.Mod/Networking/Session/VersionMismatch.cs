namespace DWMPHorde.Networking
{
    /// <summary>
    /// The words for two installs on different YokWare versions (different wire protocols). The
    /// protocol number means nothing to a player; the mod versions (from the handshake and the
    /// Steam lobby) say who has which, and which side is behind when only the protocol is known
    /// (a build from before the version was sent).
    /// </summary>
    internal static class VersionMismatch
    {
        internal const string Prefix = "Different YokWare versions";
        private const string Same = " Everyone needs the same version.";

        /// <summary>The joiner's line: what the host runs against this install.</summary>
        internal static string ForJoiner(int hostProtocol, string hostVersion)
        {
            string mine = PluginInfo.Version;
            if (!string.IsNullOrEmpty(hostVersion) && hostVersion != mine)
                return Prefix + ": the host has " + hostVersion + ", you have " + mine + "." + Same;
            return Prefix + ": the host's is " + (hostProtocol > PluginInfo.ProtocolVersion ? "newer" : "older")
                + " than yours (" + mine + ")." + Same;
        }

        /// <summary>The host's line: a joiner on another version was turned away.</summary>
        internal static string ForHost(int joinerProtocol, string joinerVersion)
        {
            string mine = PluginInfo.Version;
            if (!string.IsNullOrEmpty(joinerVersion) && joinerVersion != mine)
                return "A player could not join: they have YokWare " + joinerVersion + ", you have " + mine + "." + Same;
            return "A player could not join: their YokWare is " + (joinerProtocol > PluginInfo.ProtocolVersion ? "newer" : "older")
                + " than yours (" + mine + ")." + Same;
        }
    }
}
