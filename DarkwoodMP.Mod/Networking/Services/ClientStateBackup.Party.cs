using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Logging;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Every copy of a co-op world keeps every player's character (the host's own included), in the
    /// same per-player files the host keeps (<c>client_backup_s…/k…_{campaign}.json</c>). Any player
    /// can then host their copy and hand everyone, the old host too, their character back.
    /// The host sends them (<see cref="NetMessageType.PartyCharacter"/>); this stores and collects them.
    /// </summary>
    public static partial class ClientStateBackup
    {
        /// <summary>A joiner's own character as the host's copy stores it, held from the download to its load.</summary>
        private static ClientStateBackupData _heldOwnFromHost; // process-scoped: one join's download, consumed at its load (AdoptHostHeldSelf)

        /// <summary>This install's own character: its Steam id or its LAN key.</summary>
        internal static bool IsOwnCharacter(ClientStateBackupData data)
        {
            if (data == null)
                return false;
            string key = SanitizeStableClientKey(data.StableClientKey);
            if (key != null && string.Equals(key, GetOrCreateLanClientKey(), StringComparison.OrdinalIgnoreCase))
                return true;
            ulong steam = TryParseSteamId(data.SteamId);
            return steam != 0 && steam == TryParseSteamId(TryResolveLocalSteamIdString());
        }

        /// <summary>The newest stored copy of a player's character in this profile (Steam or LAN file).</summary>
        private static ClientStateBackupData ReadStoredCharacter(ulong steamId, string stableKey)
        {
            ClientStateBackupData data = null;
            if (steamId != 0)
                data = TryReadBackup(GetBackupFilePathForSteam(steamId)) ?? TryReadBackup(GetLegacySteamBackupPath(steamId));
            if (!string.IsNullOrEmpty(stableKey))
            {
                var byKey = TryReadBackup(GetBackupFilePathForStableKey(stableKey)) ?? TryReadBackup(GetLegacyStableBackupPath(stableKey));
                // The same player may have both (a Steam session, then a LAN one): the later save wins.
                if (byKey != null && (data == null || TryParseBackupTimestamp(byKey) > TryParseBackupTimestamp(data)))
                    data = byKey;
            }
            return data;
        }

        /// <summary>
        /// Client: another player's character sent by the host. Written into this copy of the world
        /// when <see cref="PartyCharacterPolicy.ShouldStore"/> agrees.
        /// </summary>
        internal static bool StorePartyCharacter(ClientStateBackupData data, PartyCharacterKind kind, float secondsSinceLocalWorldSave)
        {
            if (data == null)
                return false;
            ulong steam = TryParseSteamId(data.SteamId);
            string key = SanitizeStableClientKey(data.StableClientKey);
            // No identity: there is no file to put it in (the bare path is this machine's own).
            if (steam == 0 && key == null)
                return false;
            string tag = (steam != 0 ? "s" + steam : "") + (key != null ? " k" + key.Substring(0, Math.Min(8, key.Length)) : "");
            ClientStateBackupData existing = null;
            try { existing = ReadStoredCharacter(steam, key); }
            catch { /* unreadable: replaced */ }
            if (!PartyCharacterPolicy.ShouldStore((int)kind, MatchesCurrentCampaign(data), IsOwnCharacter(data),
                    HasMeaningfulProgress(data), TryParseBackupTimestamp(data), TryParseBackupTimestamp(existing),
                    existing != null, secondsSinceLocalWorldSave))
            {
                ModLog.Event(LogCat.Save, "[PartyCharacter] not stored " + tag + " (" + kind
                    + (existing != null ? ", have " + existing.Timestamp : "") + ", got " + data.Timestamp + ")");
                return false;
            }
            data.PlayerId = 0;
            SaveBackupFile(SerializeToJson(data), 0, steam, key);
            ModLog.Event(LogCat.Save, "[PartyCharacter] stored " + tag + " (" + kind + ", level " + data.CurrentLevel
                + ", " + ((data.InventoryItems?.Count ?? 0) + (data.HotbarItems?.Count ?? 0)) + " items, " + data.Timestamp + ")");
            return true;
        }

        /// <summary>
        /// Host: every character this copy of the world stores for the current campaign (one per
        /// player, the newest of its files) and the host's own, identity stamped.
        /// </summary>
        internal static List<ClientStateBackupData> CollectStoredPartyCharacters()
        {
            var result = new List<ClientStateBackupData>();
            string campaign = SanitizeCampaignIdForPath(CoopWorldCopyMeta.TryGetCampaignIdForCurrentProfile());
            if (string.IsNullOrEmpty(campaign))
                return result;
            try
            {
                string dir = GetProfileBackupDirectory();
                foreach (string f in Directory.GetFiles(dir, "client_backup_*_" + campaign + ".json"))
                {
                    string name = Path.GetFileName(f);
                    if (name.StartsWith("client_backup_self", StringComparison.OrdinalIgnoreCase))
                        continue;
                    bool bySteam = name.StartsWith("client_backup_s", StringComparison.OrdinalIgnoreCase);
                    if (!bySteam && !name.StartsWith("client_backup_k", StringComparison.OrdinalIgnoreCase))
                        continue; // p-keyed: one session's player id, not an identity
                    string id = name.Substring("client_backup_s".Length);
                    id = id.Substring(0, id.Length - ("_" + campaign + ".json").Length);
                    ClientStateBackupData data;
                    try { data = TryReadBackup(f); }
                    catch { continue; }
                    if (data == null)
                        continue;
                    // The file name is the identity the host stored it under.
                    if (bySteam)
                    {
                        if (!ulong.TryParse(id, out ulong sid) || sid == 0)
                            continue;
                        data.SteamId = sid.ToString();
                    }
                    else
                    {
                        string k = SanitizeStableClientKey(id);
                        if (k == null)
                            continue;
                        data.StableClientKey = k;
                    }
                    AddNewestPerPlayer(result, data);
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[PartyCharacter] listing stored characters failed: " + ex.Message);
            }
            try
            {
                var self = LoadLocalSelfBackupFile();
                if (self != null)
                {
                    if (SanitizeStableClientKey(self.StableClientKey) == null)
                        self.StableClientKey = GetOrCreateLanClientKey();
                    if (TryParseSteamId(self.SteamId) == 0)
                        self.SteamId = TryResolveLocalSteamIdString();
                    AddNewestPerPlayer(result, self);
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[PartyCharacter] own character: " + ex.Message);
            }
            result.RemoveAll(d => !MatchesCurrentCampaign(d) || !HasMeaningfulProgress(d) || LooksLikeStaleBackupOnFreshWorld(d));
            return result;
        }

        internal static bool IsSamePlayer(ClientStateBackupData a, ClientStateBackupData b)
        {
            ulong sa = TryParseSteamId(a.SteamId), sb = TryParseSteamId(b.SteamId);
            if (sa != 0 && sa == sb)
                return true;
            string ka = SanitizeStableClientKey(a.StableClientKey), kb = SanitizeStableClientKey(b.StableClientKey);
            return ka != null && string.Equals(ka, kb, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddNewestPerPlayer(List<ClientStateBackupData> list, ClientStateBackupData data)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var have = list[i];
                if (!IsSamePlayer(have, data))
                    continue;
                var keep = TryParseBackupTimestamp(data) > TryParseBackupTimestamp(have) ? data : have;
                var other = ReferenceEquals(keep, data) ? have : data;
                // One player, two files: the kept copy carries both identities.
                if (TryParseSteamId(keep.SteamId) == 0)
                    keep.SteamId = other.SteamId;
                if (SanitizeStableClientKey(keep.StableClientKey) == null)
                    keep.StableClientKey = other.StableClientKey;
                list[i] = keep;
                return;
            }
            list.Add(data);
        }

        /// <summary>Client, before the download: its own character as the host's copy of the world has it.</summary>
        internal static void HoldOwnFromHost(ClientStateBackupData data)
        {
            _heldOwnFromHost = data;
            ModLog.Event(LogCat.Save, "[PartyCharacter] the host holds this player's character (level "
                + (data?.CurrentLevel ?? 0) + ", " + data?.Timestamp + ") — kept for the load");
        }

        /// <summary>
        /// Join load, before it decides whether this player is new to the world: the character the
        /// host held for this player becomes its own file when this machine has none or an older
        /// one (a player on another machine, or one whose files were lost, is not made new).
        /// </summary>
        internal static void AdoptHostHeldSelf()
        {
            var held = _heldOwnFromHost;
            _heldOwnFromHost = null;
            if (held == null)
                return;
            try
            {
                var local = TryReadBackup(GetLocalSelfBackupPath());
                if (!PartyCharacterPolicy.ShouldAdoptHeldOwn(MatchesCurrentCampaign(held), HasMeaningfulProgress(held),
                        TryParseBackupTimestamp(held), TryParseBackupTimestamp(local), local != null))
                    return;
                held.PlayerId = 0;
                SaveLocalSelfBackupFile(SerializeToJson(held));
                ModLog.Event(LogCat.Save, "[PartyCharacter] own character taken from the host's copy ("
                    + held.Timestamp + (local != null ? ", newer than " + local.Timestamp : ", none here") + ")");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[PartyCharacter] taking the host's copy of this character failed: " + ex.Message);
            }
        }
    }
}
