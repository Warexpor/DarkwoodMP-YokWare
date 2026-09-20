using System;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Apply a backup snapshot onto the local Player (stats, inv, skills, pose).</summary>
    public static partial class ClientStateBackup
    {
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
                WrongSaveWarning.Notify(
                    "character backup belongs to a different co-op campaign — restore refused");
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

        private static void RestorePosition(ClientStateBackupData data)
        {
            Player player = Player.Instance;
            if (player == null || data == null) return;

            Vector3 pos = new Vector3(data.PosX, data.PosY, data.PosZ);
            // Uninitialized or missing trailer; never teleport to world origin by accident.
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
