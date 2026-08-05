using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Sync;
using Newtonsoft.Json;
using UnityEngine;

namespace DWMPHorde.Networking
{
    [Serializable]
    public class ClientStateBackupData
    {
        /// <summary>Network player id of the client this snapshot belongs to (0 = unknown/local-only).</summary>
        public int PlayerId;
        /// <summary>
        /// Stable co-op campaign id from <see cref="CoopWorldCopyMeta.CampaignId"/>.
        /// Backups are save/campaign-scoped — restore refused on mismatch.
        /// </summary>
        public string CampaignId;
        /// <summary>
        /// Host world package fingerprint at collect time. Refuses restore when the
        /// loaded save was rewound / swapped within the same CampaignId.
        /// </summary>
        public string ContentFingerprint;
        public string Timestamp;
        public int Day;
        public int GameTimeMinutes;
        public float PosX, PosY, PosZ;
        public float Health, Stamina;
        public int Experience, CurrentLevel;
        public int HealthUpgrades, StaminaUpgrades, HotbarUpgrades, InventoryUpgrades;
        public int Lives;
        public float Saturation;
        public bool FedToday;
        public int LastTimeAte;
        /// <summary>Unspent skill points (per-player).</summary>
        public int SkillPoints;
        public List<string> Recipes;
        public List<SkillEntry> Skills;
        public List<string> AvailableSkillNames;
        public List<ItemEntry> InventoryItems;
        public List<ItemEntry> HotbarItems;
        /// <summary>
        /// Per-player morning trader standing (NightTrader / The Three). Model C —
        /// not overwritten by host ReputationBulkSync.
        /// </summary>
        public List<NpcRepEntry> NightTraderReputations;
    }

    [Serializable]
    public class NpcRepEntry
    {
        public string Name;
        public int Reputation;
    }

    [Serializable]
    public class ItemEntry
    {
        public int Slot;
        public string Type;
        public float Durability;
        public int Amount;
        public bool IsRecipe;
        public string RecipeFor;
    }

    [Serializable]
    public class SkillEntry
    {
        public string Name;
        public int TimesUsed;
    }

    public static class ClientStateBackup
    {
        public static ClientStateBackupData CollectBackupData()
        {
            var data = new ClientStateBackupData();
            Player player = Player.Instance;
            if (player == null) return data;

            // Tag with local network id when connected (multi-client host storage key).
            if (ModRuntime.Network != null && ModRuntime.Network.IsConnected)
                data.PlayerId = ModRuntime.Network.LocalPlayerId;
            else
                data.PlayerId = 0;

            data.Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            data.CampaignId = CoopWorldCopyMeta.GetOrCreateCampaignIdForCurrentProfile();
            data.ContentFingerprint = CoopWorldCopyMeta.TryGetCurrentContentFingerprint();

            // Never persist dream-pad coords as the rejoin spawn — that throws the
            // client into empty -50k/-75k space after the pad is torn down.
            Vector3 pos = ResolveOverworldBackupPosition(player);
            data.PosX = pos.x; data.PosY = pos.y; data.PosZ = pos.z;

            data.Health = player.health;
            data.Stamina = player.stamina;
            data.Experience = player.experience;
            data.CurrentLevel = player.currentLevel;
            data.HealthUpgrades = player.healthUpgrades;
            data.StaminaUpgrades = player.staminaUpgrades;
            data.HotbarUpgrades = player.hotbarUpgrades;
            data.InventoryUpgrades = player.inventoryUpgrades;
            data.Lives = player.lifes;
            data.Saturation = player.saturation;
            data.FedToday = player.fedToday;
            data.LastTimeAte = player.lastTimeAte;

            if (player.recipes != null)
            {
                data.Recipes = new List<string>();
                for (int i = 0; i < player.recipes.Count; i++)
                {
                    if (player.recipes[i] != null)
                    {
                        InvItem comp = player.recipes[i].GetComponent<InvItem>();
                        if (comp != null && !string.IsNullOrEmpty(comp.type) && !data.Recipes.Contains(comp.type))
                            data.Recipes.Add(comp.type);
                    }
                }
            }

            if (player.skills != null)
            {
                data.SkillPoints = player.skills.SkillPoints;
                if (player.skills.skills != null)
                {
                    data.Skills = new List<SkillEntry>();
                    for (int i = 0; i < player.skills.skills.Count; i++)
                    {
                        var sk = player.skills.skills[i];
                        if (sk == null) continue;
                        // Vanilla save uses gameObject.name as skill type key.
                        string name = sk.gameObject != null ? sk.gameObject.name : sk.name;
                        if (string.IsNullOrEmpty(name)) continue;
                        data.Skills.Add(new SkillEntry { Name = name, TimesUsed = sk.timesUsed });
                    }
                }
                if (player.skills.availableSkills != null)
                {
                    data.AvailableSkillNames = new List<string>();
                    for (int i = 0; i < player.skills.availableSkills.Count; i++)
                    {
                        var sk = player.skills.availableSkills[i];
                        if (sk == null) continue;
                        string name = sk.gameObject != null ? sk.gameObject.name : sk.name;
                        if (!string.IsNullOrEmpty(name))
                            data.AvailableSkillNames.Add(name);
                    }
                }
            }

            if (player.Inventory?.slots != null)
            {
                data.InventoryItems = new List<ItemEntry>();
                for (int i = 0; i < player.Inventory.slots.Count; i++)
                {
                    var slot = player.Inventory.slots[i];
                    if (slot != null && !InvItemClass.isNull(slot.invItem))
                        data.InventoryItems.Add(MakeItemEntry(slot.invItem, i));
                }
            }

            if (player.Hotbar?.slots != null)
            {
                data.HotbarItems = new List<ItemEntry>();
                for (int i = 0; i < player.Hotbar.slots.Count; i++)
                {
                    var slot = player.Hotbar.slots[i];
                    if (slot != null && !InvItemClass.isNull(slot.invItem))
                        data.HotbarItems.Add(MakeItemEntry(slot.invItem, i));
                }
            }

            var controller = Singleton<Controller>.Instance;
            if (controller != null)
            {
                data.Day = controller.day;
                data.GameTimeMinutes = controller.CurrentTime;
            }

            // Model C: persist morning-trader rep per player (not host-shared bulk).
            data.NightTraderReputations = CollectNightTraderReputations();

            return data;
        }

        private static List<NpcRepEntry> CollectNightTraderReputations()
        {
            var list = new List<NpcRepEntry>();
            var flags = Singleton<Flags>.Instance;
            if (flags?.npcStates == null) return list;

            for (int i = 0; i < flags.npcStates.Count; i++)
            {
                var st = flags.npcStates[i];
                if (st == null || string.IsNullOrEmpty(st.name)) continue;
                if (!Patches.ReputationSyncUtil.IsPerPlayerReputationNpcName(st.name))
                    continue;
                list.Add(new NpcRepEntry { Name = st.name, Reputation = st.reputation });
            }
            return list;
        }

        private static void RestoreNightTraderReputations(ClientStateBackupData data)
        {
            if (data?.NightTraderReputations == null || data.NightTraderReputations.Count == 0)
                return;

            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;

            for (int i = 0; i < data.NightTraderReputations.Count; i++)
            {
                var entry = data.NightTraderReputations[i];
                if (entry == null || string.IsNullOrEmpty(entry.Name)) continue;
                if (!Patches.ReputationSyncUtil.IsPerPlayerReputationNpcName(entry.Name))
                    continue;

                var state = flags.getNPCState(entry.Name);
                if (state != null)
                {
                    state.reputation = entry.Reputation;
                }
                else
                {
                    flags.npcStates.Add(new Flags.NPCState
                    {
                        name = entry.Name,
                        reputation = entry.Reputation,
                        wantsToTalk = true
                    });
                }
            }
            ModRuntime.LegacyInfo(
                $"[ClientBackup] restored {data.NightTraderReputations.Count} night-trader reputation(s)");
        }

        private static ItemEntry MakeItemEntry(InvItemClass item, int slot)
        {
            return new ItemEntry
            {
                Slot = slot,
                Type = item.type,
                Durability = item.durability,
                Amount = item.amount,
                IsRecipe = item.isRecipe,
                RecipeFor = item.recipeFor
            };
        }

        public static string SerializeToJson(ClientStateBackupData data)
        {
            return JsonConvert.SerializeObject(data, Formatting.Indented);
        }

        public static ClientStateBackupData DeserializeFromJson(string json)
        {
            return JsonConvert.DeserializeObject<ClientStateBackupData>(json);
        }

        /// <summary>Profile save directory for the active Darkwood profile (creates if needed).</summary>
        public static string GetProfileBackupDirectory()
        {
            string saveDir = Application.persistentDataPath + "/1_4Save";
            string profileName = "prof" + (Core.currentProfile?.id ?? 1);
            string dir = saveDir + "/" + profileName;
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning($"[ClientStateBackup] mkdir failed: {ex.Message}");
            }
            return dir;
        }

        private static string SanitizeCampaignIdForPath(string campaignId)
        {
            if (string.IsNullOrEmpty(campaignId)) return null;
            // GUID "N" is hex-only; strip anything else for path safety.
            var sb = new System.Text.StringBuilder(campaignId.Length);
            for (int i = 0; i < campaignId.Length; i++)
            {
                char c = campaignId[i];
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>
        /// Host-side path for a remote client's backup, keyed by network PlayerId + campaign.
        /// </summary>
        public static string GetBackupFilePathForPlayer(int playerId)
        {
            if (playerId <= 0)
                return GetLocalSelfBackupPath();
            string campaign = SanitizeCampaignIdForPath(
                CoopWorldCopyMeta.GetOrCreateCampaignIdForCurrentProfile());
            if (string.IsNullOrEmpty(campaign))
                return GetProfileBackupDirectory() + "/client_backup_p" + playerId + ".json";
            return GetProfileBackupDirectory() + "/client_backup_p" + playerId + "_" + campaign + ".json";
        }

        /// <summary>
        /// Local-only path for this machine's snapshot, keyed by current campaign.
        /// </summary>
        public static string GetLocalSelfBackupPath()
        {
            string campaign = SanitizeCampaignIdForPath(
                CoopWorldCopyMeta.GetOrCreateCampaignIdForCurrentProfile());
            if (string.IsNullOrEmpty(campaign))
                return GetProfileBackupDirectory() + "/client_backup_self.json";
            return GetProfileBackupDirectory() + "/client_backup_self_" + campaign + ".json";
        }

        /// <summary>Legacy single-file path (pre multi-client / pre-campaign). Load fallback only.</summary>
        public static string GetLegacyBackupFilePath()
        {
            return GetProfileBackupDirectory() + "/client_backup.json";
        }

        private static string GetLegacyPlayerBackupPath(int playerId) =>
            GetProfileBackupDirectory() + "/client_backup_p" + playerId + ".json";

        private static string GetLegacySelfBackupPath() =>
            GetProfileBackupDirectory() + "/client_backup_self.json";

        /// <summary>
        /// True when backup looks like a prior playthrough applied onto a fresh day-1 world
        /// (CampaignId reused because mint was skipped). Used to refuse host push / restore.
        /// </summary>
        public static bool LooksLikeStaleBackupOnFreshWorld(ClientStateBackupData data)
        {
            if (data == null || !HasMeaningfulProgress(data)) return false;
            var ctrl = Singleton<Controller>.Instance;
            int day = ctrl != null ? ctrl.day : (Core.currentProfile != null ? Core.currentProfile.day : 0);
            if (day > 1) return false;
            // Day-1 world with a progressed character (lvl/skills/inv) from another fingerprint.
            string curFp = CoopWorldCopyMeta.TryGetCurrentContentFingerprint();
            if (string.IsNullOrEmpty(curFp) || string.IsNullOrEmpty(data.ContentFingerprint))
                return data.CurrentLevel >= 2 || (data.Skills != null && data.Skills.Count >= 2);
            if (string.Equals(curFp, data.ContentFingerprint, StringComparison.OrdinalIgnoreCase))
                return false;
            return data.CurrentLevel >= 1
                || (data.Skills != null && data.Skills.Count > 0)
                || (data.InventoryItems != null && data.InventoryItems.Count > 0);
        }

        /// <summary>True when backup JSON belongs to the active campaign (or both unscoped legacy).
        /// Fingerprint matching was removed — host/client package hashes diverge after share.
        /// </summary>
        public static bool MatchesCurrentCampaign(ClientStateBackupData data)
        {
            if (data == null) return false;
            string current = CoopWorldCopyMeta.TryGetCurrentCampaignId();
            if (string.IsNullOrEmpty(current) && string.IsNullOrEmpty(data.CampaignId))
                return true; // legacy both sides
            if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(data.CampaignId))
                return false;
            return string.Equals(current, data.CampaignId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when backup has meaningful progression (not an empty wipe snapshot).</summary>
        public static bool HasMeaningfulProgress(ClientStateBackupData data)
        {
            if (data == null) return false;
            if (data.CurrentLevel > 0 || data.Experience > 0) return true;
            if (data.SkillPoints > 0) return true;
            if (data.Skills != null && data.Skills.Count > 0) return true;
            if (data.InventoryItems != null && data.InventoryItems.Count > 0) return true;
            if (data.HotbarItems != null && data.HotbarItems.Count > 0) return true;
            return false;
        }

        /// <summary>Rough richness score for choosing between two backups.</summary>
        public static int ProgressScore(ClientStateBackupData data)
        {
            if (data == null) return 0;
            int score = data.CurrentLevel * 1000 + data.Experience
                + data.SkillPoints * 50
                + (data.Skills?.Count ?? 0) * 100
                + (data.InventoryItems?.Count ?? 0) * 10
                + (data.HotbarItems?.Count ?? 0) * 10;
            return score;
        }

        /// <summary>Parse backup Timestamp for freshness compares (0 on failure).</summary>
        public static DateTime TryParseBackupTimestamp(ClientStateBackupData data)
        {
            if (data == null || string.IsNullOrEmpty(data.Timestamp))
                return DateTime.MinValue;
            if (DateTime.TryParse(data.Timestamp, out DateTime dt))
                return dt;
            return DateTime.MinValue;
        }

        /// <summary>
        /// Pre-0.7.20 backups omit CampaignId. Stamp current campaign and re-save
        /// so host stop rejecting with "file=(none)".
        /// </summary>
        private static ClientStateBackupData MigrateLegacyCampaignIfNeeded(
            ClientStateBackupData data, int playerIdForPath)
        {
            if (data == null) return null;
            if (MatchesCurrentCampaign(data))
                return data;

            string current = CoopWorldCopyMeta.TryGetCurrentCampaignId();
            if (string.IsNullOrEmpty(current) || !string.IsNullOrEmpty(data.CampaignId))
                return null; // mismatched non-empty id, or no campaign to adopt

            data.CampaignId = current;
            try
            {
                string json = SerializeToJson(data);
                if (playerIdForPath > 0)
                    SaveBackupFile(json, playerIdForPath);
                else
                    SaveLocalSelfBackupFile(json);
                ModRuntime.LegacyInfo(
                    "[ClientBackup] migrated legacy backup → campaign " + current);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning(
                    "[ClientBackup] legacy campaign migrate failed: " + ex.Message);
            }
            return data;
        }

        /// <summary>Save a remote client's backup on the host (or any peer-keyed store).</summary>
        public static void SaveBackupFile(string json, int playerId)
        {
            try
            {
                // Ensure JSON CampaignId matches disk key when host stamps current campaign.
                try
                {
                    var parsed = DeserializeFromJson(json);
                    if (parsed != null)
                    {
                        string cur = CoopWorldCopyMeta.GetOrCreateCampaignIdForCurrentProfile();
                        if (!string.IsNullOrEmpty(cur)
                            && !string.Equals(parsed.CampaignId, cur, StringComparison.OrdinalIgnoreCase))
                        {
                            // Prefer payload's campaign if set (client's view); else stamp host.
                            if (string.IsNullOrEmpty(parsed.CampaignId))
                            {
                                parsed.CampaignId = cur;
                                json = SerializeToJson(parsed);
                            }
                        }
                    }
                }
                catch { /* keep raw json */ }

                string path = GetBackupFilePathForPlayer(playerId);
                // If JSON has its own CampaignId, write under that key (host world may differ
                // only if misconfigured — prefer embedded id for file name).
                try
                {
                    var parsed = DeserializeFromJson(json);
                    if (parsed != null && !string.IsNullOrEmpty(parsed.CampaignId) && playerId > 0)
                    {
                        string c = SanitizeCampaignIdForPath(parsed.CampaignId);
                        if (!string.IsNullOrEmpty(c))
                            path = GetProfileBackupDirectory() + "/client_backup_p" + playerId + "_" + c + ".json";
                    }
                }
                catch { /* use path from GetBackupFilePathForPlayer */ }

                File.WriteAllText(path, json);
                ModRuntime.LegacyInfo("[ClientBackup] saved player " + playerId + " → " + path);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ClientBackup] failed to save player " + playerId + ": " + ex);
            }
        }

        /// <summary>Save this machine's local self backup (ManualSave / pre-load / exit).</summary>
        public static void SaveLocalSelfBackupFile(string json)
        {
            try
            {
                // Never clobber a good self file with an empty collect (title/load race).
                try
                {
                    var incoming = DeserializeFromJson(json);
                    if (incoming != null && !HasMeaningfulProgress(incoming))
                    {
                        var existing = TryReadBackup(GetLocalSelfBackupPath())
                            ?? TryReadBackup(GetLegacySelfBackupPath());
                        if (existing != null && HasMeaningfulProgress(existing)
                            && MatchesCurrentCampaign(existing))
                        {
                            ModRuntime.LegacyInfo(
                                "[ClientBackup] refuse overwrite local self with empty snapshot");
                            return;
                        }
                    }
                }
                catch { /* write anyway */ }

                string path = GetLocalSelfBackupPath();
                try
                {
                    var parsed = DeserializeFromJson(json);
                    if (parsed != null && !string.IsNullOrEmpty(parsed.CampaignId))
                    {
                        string c = SanitizeCampaignIdForPath(parsed.CampaignId);
                        if (!string.IsNullOrEmpty(c))
                            path = GetProfileBackupDirectory() + "/client_backup_self_" + c + ".json";
                    }
                }
                catch { /* default path */ }

                File.WriteAllText(path, json);
                ModRuntime.LegacyInfo("[ClientBackup] saved local self → " + path);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ClientBackup] failed to save local self: " + ex);
            }
        }

        /// <summary>Load host-stored backup for a specific network player id (current campaign only).</summary>
        public static ClientStateBackupData LoadBackupFileForPlayer(int playerId)
        {
            try
            {
                string path = GetBackupFilePathForPlayer(playerId);
                ClientStateBackupData data = TryReadBackup(path);
                if (data == null && playerId > 0)
                    data = TryReadBackup(GetLegacyPlayerBackupPath(playerId));
                if (data == null)
                {
                    string legacy = GetLegacyBackupFilePath();
                    if (playerId > 0)
                        data = TryReadBackup(legacy);
                }
                if (data == null) return null;
                data = MigrateLegacyCampaignIfNeeded(data, playerId);
                if (data == null)
                {
                    ModRuntime.LegacyInfo(
                        "[ClientBackup] skip p" + playerId
                        + " backup — campaign mismatch (file=(none/mismatched) current="
                        + (CoopWorldCopyMeta.TryGetCurrentCampaignId() ?? "(none)") + ")");
                    return null;
                }
                if (!HasMeaningfulProgress(data))
                {
                    ModRuntime.LegacyInfo(
                        "[ClientBackup] skip p" + playerId + " backup — empty/no progress");
                    return null;
                }
                return data;
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ClientBackup] failed to load player " + playerId + ": " + ex);
                return null;
            }
        }

        /// <summary>Load local self backup for the current campaign; legacy fallback if unscoped.</summary>
        public static ClientStateBackupData LoadLocalSelfBackupFile()
        {
            try
            {
                ClientStateBackupData data = TryReadBackup(GetLocalSelfBackupPath());
                if (data == null)
                    data = TryReadBackup(GetLegacySelfBackupPath());
                if (data == null)
                    data = TryReadBackup(GetLegacyBackupFilePath());
                if (data == null) return null;
                data = MigrateLegacyCampaignIfNeeded(data, 0);
                if (data == null)
                {
                    ModRuntime.LegacyInfo(
                        "[ClientBackup] skip local self — campaign mismatch (file=(none/mismatched) current="
                        + (CoopWorldCopyMeta.TryGetCurrentCampaignId() ?? "(none)") + ")");
                    return null;
                }
                if (!HasMeaningfulProgress(data))
                {
                    ModRuntime.LegacyInfo("[ClientBackup] skip local self — empty/no progress");
                    return null;
                }
                return data;
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ClientBackup] failed to load local self: " + ex);
                return null;
            }
        }

        private static ClientStateBackupData TryReadBackup(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            string json = File.ReadAllText(path);
            return DeserializeFromJson(json);
        }

        public static void RestoreFromBackup(ClientStateBackupData data)
        {
            Player player = Player.Instance;
            if (player == null) return;
            if (data == null) return;

            if (!MatchesCurrentCampaign(data))
            {
                ModRuntime.Log?.LogWarning(
                    "[ClientBackup] refused restore — campaign mismatch (backup="
                    + (data.CampaignId ?? "(none)")
                    + " current="
                    + (CoopWorldCopyMeta.TryGetCurrentCampaignId() ?? "(none)") + ")");
                return;
            }

            if (!HasMeaningfulProgress(data))
            {
                ModRuntime.Log?.LogWarning(
                    "[ClientBackup] refused restore — empty backup would wipe character");
                return;
            }

            player.experience = data.Experience;
            player.currentLevel = data.CurrentLevel;
            player.healthUpgrades = data.HealthUpgrades;
            player.staminaUpgrades = data.StaminaUpgrades;
            player.hotbarUpgrades = data.HotbarUpgrades;
            player.inventoryUpgrades = data.InventoryUpgrades;
            player.lifes = data.Lives;
            player.saturation = data.Saturation;
            player.fedToday = data.FedToday;
            player.lastTimeAte = data.LastTimeAte;

            if (data.Health > 0f) player.health = data.Health;
            if (data.Stamina > 0f) player.stamina = data.Stamina;

            // Per-player skills (chosen + uses + unspent points). Never net-synced.
            RestoreSkills(data);

            // Restore inventory items
            if (data.InventoryItems != null && player.Inventory != null)
            {
                player.Inventory.clear();
                player.Inventory.initSlots();
                for (int i = 0; i < data.InventoryItems.Count; i++)
                {
                    var entry = data.InventoryItems[i];
                    if (!string.IsNullOrEmpty(entry.Type))
                    {
                        player.Inventory.addSlot();
                        var slot = player.Inventory.getNextFreeSlot();
                        if (slot != null)
                        {
                            var item = slot.createItem(entry.Type, entry.Amount);
                            if (item != null)
                            {
                                item.durability = entry.Durability;
                                if (entry.IsRecipe) item.isRecipe = true;
                            }
                        }
                    }
                }
            }

            // Restore hotbar items
            if (data.HotbarItems != null && player.Hotbar != null)
            {
                player.Hotbar.clear();
                player.Hotbar.initSlots();
                for (int i = 0; i < data.HotbarItems.Count; i++)
                {
                    var entry = data.HotbarItems[i];
                    if (!string.IsNullOrEmpty(entry.Type))
                    {
                        player.Hotbar.addSlot();
                        var slot = player.Hotbar.getNextFreeSlot();
                        if (slot != null)
                        {
                            var item = slot.createItem(entry.Type, entry.Amount);
                            if (item != null) item.durability = entry.Durability;
                        }
                    }
                }
            }

            RestoreNightTraderReputations(data);

            // Position was always collected on Save; apply on restore so rejoin returns to exit spot.
            RestorePosition(data);

            ModRuntime.LegacyInfo(
                "[ClientBackup] restored from backup — level=" + data.CurrentLevel +
                " exp=" + data.Experience +
                " skills=" + (data.Skills?.Count ?? 0) +
                " pts=" + data.SkillPoints +
                " inv=" + (data.InventoryItems?.Count ?? 0) + " items" +
                " pos=(" + data.PosX.ToString("F0") + "," + data.PosZ.ToString("F0") + ")");
        }

        private static void RestorePosition(ClientStateBackupData data)
        {
            Player player = Player.Instance;
            if (player == null || data == null) return;

            Vector3 pos = new Vector3(data.PosX, data.PosY, data.PosZ);
            // Uninitialized / missing trailer — never teleport to world origin by accident.
            if (pos.sqrMagnitude < 0.01f)
                return;

            // Stale backups taken mid-dream used pad coords; applying them in the
            // overworld is the "abyss" teleport. Keep inv/skills; skip pose.
            bool dreamingNow = DreamSyncManager.IsDreamActive
                || (Dreams.Instance != null && Dreams.Instance.dreaming);
            if (!dreamingNow && IsDreamPadCoordinate(pos))
            {
                ModRuntime.Log?.LogWarning(
                    "[ClientBackup] skip position restore — dream-pad coords while overworld "
                    + pos);
                return;
            }

            try
            {
                player.teleportTo(pos, Quaternion.Euler(90f, 0f, 0f));
                if (Singleton<WorldGrid>.Instance != null)
                    Singleton<WorldGrid>.Instance.refreshPosition(pos, instant: true, force: true);

                var net = ModRuntime.Network as LanNetworkManager;
                if (net != null && net.IsConnected)
                    net.TeleportRemoteProxyTo(pos, 0f);

                ModRuntime.LegacyInfo(
                    "[ClientBackup] restored position " + pos);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] position restore failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Dream pads live around −50k/−75k. Overworld playtest coords are far smaller.
        /// </summary>
        internal static bool IsDreamPadCoordinate(Vector3 pos)
        {
            const float padAbs = 40000f;
            return Mathf.Abs(pos.x) >= padAbs || Mathf.Abs(pos.z) >= padAbs;
        }

        /// <summary>
        /// While dreaming, vanilla keeps the pre-dream overworld pose in
        /// <see cref="Dreams.positionCopy"/> — use that for backups.
        /// Also refuse live pad coords after dream flags clear (endDreaming window /
        /// corrupted positionCopy) so quit snapshots never reintroduce the abyss.
        /// </summary>
        private static Vector3 ResolveOverworldBackupPosition(Player player)
        {
            Vector3 live = player.transform.position;
            bool dreaming = DreamSyncManager.IsDreamActive
                || (Dreams.Instance != null && Dreams.Instance.dreaming);

            if (dreaming)
            {
                if (Dreams.Instance != null)
                {
                    Vector3 copy = Dreams.Instance.positionCopy;
                    if (copy.sqrMagnitude > 0.01f && !IsDreamPadCoordinate(copy))
                        return copy;
                }
                if (DreamSyncManager.TryGetPreDreamOverworldPosition(out Vector3 pre))
                    return pre;
                ModRuntime.Log?.LogWarning(
                    "[ClientBackup] mid-dream snapshot — omitting pad position " + live);
                return Vector3.zero;
            }

            if (!IsDreamPadCoordinate(live))
                return live;

            // Overworld flags but body still on pad (corrupted positionCopy / mid-end).
            if (Dreams.Instance != null)
            {
                Vector3 copy = Dreams.Instance.positionCopy;
                if (copy.sqrMagnitude > 0.01f && !IsDreamPadCoordinate(copy))
                    return copy;
            }
            if (DreamSyncManager.TryGetPreDreamOverworldPosition(out Vector3 pre2))
                return pre2;

            ModRuntime.Log?.LogWarning(
                "[ClientBackup] refusing pad coords while overworld — omitting " + live);
            return Vector3.zero;
        }

        /// <summary>
        /// Re-apply chosen progression skills from backup (mirrors vanilla
        /// PlayerSkills.SaveState.loadValues without touching host peers).
        /// </summary>
        private static void RestoreSkills(ClientStateBackupData data)
        {
            if (data == null) return;
            Player player = Player.Instance;
            if (player?.skills == null) return;

            // Nothing to restore (legacy backups without skill lists).
            if (data.Skills == null && data.AvailableSkillNames == null && data.SkillPoints == 0)
                return;

            PlayerSkills ps = player.skills;

            try
            {
                // Clear chosen flags on all progression skills (vanilla loadValues).
                if (ps.progressionSkills != null)
                {
                    for (int i = 0; i < ps.progressionSkills.Count; i++)
                    {
                        PlayerSkill sk = ps.progressionSkills[i];
                        if (sk != null)
                            sk.chosen = false;
                    }
                }

                ps.skills.Clear();
                if (data.Skills != null)
                {
                    for (int i = 0; i < data.Skills.Count; i++)
                    {
                        SkillEntry entry = data.Skills[i];
                        if (entry == null || string.IsNullOrEmpty(entry.Name)) continue;
                        PlayerSkill match = FindProgressionSkill(ps, entry.Name);
                        if (match == null) continue;
                        match.timesUsed = entry.TimesUsed;
                        ps.skills.Add(match);
                    }
                }

                ps.availableSkills.Clear();
                if (data.AvailableSkillNames != null)
                {
                    for (int i = 0; i < data.AvailableSkillNames.Count; i++)
                    {
                        string name = data.AvailableSkillNames[i];
                        if (string.IsNullOrEmpty(name)) continue;
                        PlayerSkill match = FindProgressionSkill(ps, name);
                        if (match != null && !ps.availableSkills.Contains(match))
                            ps.availableSkills.Add(match);
                    }
                }

                ps.SkillPoints = data.SkillPoints;
                ps.initialized = false;
                ps.initialize(resetTimesUsed: false);

                ModRuntime.LegacyInfo(
                    $"[ClientBackup] restored skills count={ps.skills.Count} available={ps.availableSkills.Count} pts={ps.SkillPoints}");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] skill restore failed: " + ex.Message);
            }
        }

        private static PlayerSkill FindProgressionSkill(PlayerSkills ps, string name)
        {
            if (ps?.progressionSkills == null || string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < ps.progressionSkills.Count; i++)
            {
                PlayerSkill sk = ps.progressionSkills[i];
                if (sk == null) continue;
                if (sk.gameObject != null && sk.gameObject.name == name)
                    return sk;
                if (sk.name == name)
                    return sk;
            }
            return null;
        }
    }
}
