using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace DWMPHorde.Networking
{
    [Serializable]
    public class ClientStateBackupData
    {
        /// <summary>Network player id of the client this snapshot belongs to (0 = unknown/local-only).</summary>
        public int PlayerId;
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

            Vector3 pos = player.transform.position;
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

        /// <summary>
        /// Host-side path for a remote client's backup, keyed by network PlayerId.
        /// Multi-client: player 2 and 3 must not share a single file.
        /// </summary>
        public static string GetBackupFilePathForPlayer(int playerId)
        {
            if (playerId <= 0)
                return GetLocalSelfBackupPath();
            return GetProfileBackupDirectory() + "/client_backup_p" + playerId + ".json";
        }

        /// <summary>
        /// Local-only path used when this machine snapshots its own inventory
        /// (e.g. ManualSave before loading a host world). Never collides with host-received peer files.
        /// </summary>
        public static string GetLocalSelfBackupPath()
        {
            return GetProfileBackupDirectory() + "/client_backup_self.json";
        }

        /// <summary>Legacy single-file path (pre multi-client). Kept for load fallback only.</summary>
        public static string GetLegacyBackupFilePath()
        {
            return GetProfileBackupDirectory() + "/client_backup.json";
        }

        /// <summary>Save a remote client's backup on the host (or any peer-keyed store).</summary>
        public static void SaveBackupFile(string json, int playerId)
        {
            try
            {
                string path = GetBackupFilePathForPlayer(playerId);
                File.WriteAllText(path, json);
                ModRuntime.LegacyInfo("[ClientBackup] saved player " + playerId + " → " + path);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ClientBackup] failed to save player " + playerId + ": " + ex);
            }
        }

        /// <summary>Save this machine's local self backup (ManualSave / pre-load).</summary>
        public static void SaveLocalSelfBackupFile(string json)
        {
            try
            {
                string path = GetLocalSelfBackupPath();
                File.WriteAllText(path, json);
                ModRuntime.LegacyInfo("[ClientBackup] saved local self → " + path);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ClientBackup] failed to save local self: " + ex);
            }
        }

        /// <summary>Load host-stored backup for a specific network player id.</summary>
        public static ClientStateBackupData LoadBackupFileForPlayer(int playerId)
        {
            try
            {
                string path = GetBackupFilePathForPlayer(playerId);
                if (!File.Exists(path))
                {
                    // One-shot migration: old single-file layout
                    string legacy = GetLegacyBackupFilePath();
                    if (playerId > 0 && File.Exists(legacy))
                        path = legacy;
                    else
                        return null;
                }
                string json = File.ReadAllText(path);
                return DeserializeFromJson(json);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ClientBackup] failed to load player " + playerId + ": " + ex);
                return null;
            }
        }

        /// <summary>Load local self backup; falls back to legacy <c>client_backup.json</c>.</summary>
        public static ClientStateBackupData LoadLocalSelfBackupFile()
        {
            try
            {
                string path = GetLocalSelfBackupPath();
                if (!File.Exists(path))
                {
                    path = GetLegacyBackupFilePath();
                    if (!File.Exists(path))
                        return null;
                }
                string json = File.ReadAllText(path);
                return DeserializeFromJson(json);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ClientBackup] failed to load local self: " + ex);
                return null;
            }
        }

        // --- Back-compat wrappers (single-arg / no-arg used by older call sites) ---

        /// <summary>Deprecated name: saves as local self backup.</summary>
        public static void SaveBackupFile(string json) => SaveLocalSelfBackupFile(json);

        /// <summary>Deprecated name: loads local self backup.</summary>
        public static ClientStateBackupData LoadBackupFile() => LoadLocalSelfBackupFile();

        public static void RestoreFromBackup(ClientStateBackupData data)
        {
            Player player = Player.Instance;
            if (player == null) return;
            if (data == null) return;

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

            ModRuntime.LegacyInfo(
                "[ClientBackup] restored from backup — level=" + data.CurrentLevel +
                " exp=" + data.Experience +
                " skills=" + (data.Skills?.Count ?? 0) +
                " pts=" + data.SkillPoints +
                " inv=" + (data.InventoryItems?.Count ?? 0) + " items");
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
