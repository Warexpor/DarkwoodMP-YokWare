using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;

namespace DWMPHorde
{
    /// <summary>
    /// What the YokWare add-on plugins (the optional manual saves) may use of the co-op mod: the
    /// game's language, the session state, the world-save rules, the crash-safe save-set copy and
    /// the gameplay-input lock. Menu screens come from <see cref="YokWare.VanillaMenu.Vm"/>.
    /// </summary>
    public static class AddOnApi
    {
        /// <summary>The game's language code (EN, RU, …), as the co-op mod's own text uses it.</summary>
        public static string Language => Loc.Language;

        /// <summary>The co-op mod's own Russian for <paramref name="english"/> (English when it has none).</summary>
        public static string Translate(string english) => Loc.T(english);

        /// <summary>In a co-op session (hosting, joined, or connecting).</summary>
        public static bool InSession => ModRuntime.Network != null && ModRuntime.Network.Role != NetworkRole.Offline;

        /// <summary>Joined to someone else's world: the host owns the world save.</summary>
        public static bool IsClient => ModRuntime.Network != null && ModRuntime.Network.Role == NetworkRole.Client
            && ModRuntime.Network.IsConnected;

        /// <summary>Why the world must not be saved right now (night death, dream, prologue, …), or null.</summary>
        public static string WorldSaveBlockReason() => WorldSaveGuards.GetWorldSaveBlockReason();

        /// <summary>The game is quitting: nothing may be saved any more.</summary>
        public static bool Quitting => WorldSaveGuards.IsQuitting;

        /// <summary>Every file of a save folder (sav.dat, savch.dat, …) read in one go.</summary>
        public static List<KeyValuePair<string, byte[]>> ReadSaveSet(string dir) => WorldSaveShareService.ReadSaveSet(dir);

        /// <summary>
        /// Make <paramref name="dir"/> hold exactly <paramref name="files"/> through the crash-safe swap
        /// (all of it, or the folder untouched).
        /// </summary>
        public static bool TryReplaceSaveSet(string dir, List<KeyValuePair<string, byte[]>> files, out string error)
            => WorldSaveShareService.TryReplaceSaveSet(dir, files, out error);

        /// <summary>Write a small text file through a temp file and a rename (never half-written).</summary>
        public static void WriteTextAtomic(string path, string text) => CoopWorldCopyMeta.WriteAllTextAtomic(path, text);

        /// <summary>
        /// Another copy of a profile is about to be loaded offline: it is a different save instance,
        /// so it gets a new campaign id (no co-op backup of another copy may apply to it), and the
        /// mod's world state of the session before is dropped.
        /// </summary>
        public static void BeforeOfflineLoad(int profileId)
        {
            CoopWorldCopyMeta.MintNewCampaignId(profileId);
            WorldPhysicsSyncService.Reset();
            DreamSyncManager.OnDisconnected();
            MultiplayerMapManager.Reset();
        }

        /// <summary>Hold vanilla gameplay input while <paramref name="owner"/>'s screen takes the keyboard.</summary>
        public static void HoldInput(string owner, bool held) => UiInputLock.Set(owner, held);
    }
}
