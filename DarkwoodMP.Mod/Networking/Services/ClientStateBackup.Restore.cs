using System;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Apply a backup snapshot onto the local Player (stats, inv, skills, pose).</summary>
    public static partial class ClientStateBackup
    {
        /// <returns>True when inv/skills/pose were applied; false on refuse/no-op.</returns>
        public static bool RestoreFromBackup(ClientStateBackupData data)
        {
            Player player = Player.Instance;
            if (player == null) return false;
            if (data == null) return false;

            // On the dream pad the live inventory is the dream's (the overworld one waits in
            // Dreams' copy for the exit): a reconnect mid-dream (host migration) keeps the body.
            if (Dreams.Instance != null && Dreams.Instance.dreaming)
            {
                ModRuntime.LegacyInfo("[ClientBackup] in a dream — restore skipped, live state kept");
                return false;
            }

            if (!MatchesCurrentCampaign(data))
            {
                ModRuntime.Log?.LogWarning(
                    "[ClientBackup] refused restore — campaign mismatch (backup="
                    + (data.CampaignId ?? "(none)")
                    + " current="
                    + (CoopWorldCopyMeta.TryGetCurrentCampaignId() ?? "(none)") + ")");
                WrongSaveWarning.Notify(
                    "character backup belongs to a different co-op campaign — restore refused");
                return false;
            }

            if (!HasMeaningfulProgress(data))
            {
                ModRuntime.Log?.LogWarning(
                    "[ClientBackup] refused restore — empty backup would wipe character");
                return false;
            }

            // Full reject (inv/hotbar/skills/pose) — position far-from-live alone is
            // void-only and must not leave a Jul hotbar applied after a stale refuse.
            if (LooksLikeStaleBackupOnFreshWorld(data))
            {
                ModRuntime.Log?.LogWarning(
                    "[ClientBackup] refused restore — stale/legacy-poison backup");
                return false;
            }

            player.experience = data.Experience;
            player.currentLevel = data.CurrentLevel;
            // Level-dream slots are this player's own, not the host's from the loaded world.
            Sync.DreamSession.SetLocalLvlFlags(data.DreamLvlFlags >= 0
                ? (byte)data.DreamLvlFlags
                : Sync.DreamSession.LvlFlagsPassedAt(data.CurrentLevel));
            // Permanent pools: vanilla SaveState.loadValues loops upgradeHealth/Stamina
            // (maxHealth/maxStamina += 25 each). Offline WorldSaveShare loads the HOST
            // character first — assigning upgrade counts alone left the host max pool.
            ReconcileVitalUpgradePools(player, data.HealthUpgrades, data.StaminaUpgrades);
            // Slot counts must match upgrade deltas (vanilla loadValues loops
            // upgradeHotbar/upgradeInventory). Assigning counts alone left a
            // client with more upgrades than the host-loaded body short on
            // slots; RestoreItems used to addSlot() per item and grew forever.
            ReconcileInventoryUpgradeSlots(player, data.HotbarUpgrades, data.InventoryUpgrades);
            player.lifes = data.Lives;
            player.saturation = data.Saturation;
            player.fedToday = data.FedToday;
            player.lastTimeAte = data.LastTimeAte;

            if (data.Health > 0f) player.health = data.Health;
            if (data.Stamina > 0f) player.stamina = data.Stamina;

            // Per-player skills (chosen + uses + unspent points). Never net-synced.
            // RestoreSkills unsets host LessHealth/MoreHealth before client init;
            // those setters mutate maxHealth — clamp after.
            RestoreSkills(data);
            // Vanilla PlayerSkills.SaveState.loadValues sets canActivateSkill AFTER initialize.
            if (player.skills != null)
                player.skills.canActivateSkill = data.CanActivateSkill;
            if (player.health > player.maxHealth) player.health = player.maxHealth;
            if (player.stamina > player.maxStamina) player.stamina = player.maxStamina;

            // Restore inventory / hotbar at recorded Slot indices (vanilla
            // Inventory.SaveState.loadValues uses slotId). Packing into
            // getNextFreeSlot scrambled hotbar keys when gaps existed.
            RestoreItems(player.Inventory, data.InventoryItems);
            RestoreItems(player.Hotbar, data.HotbarItems);
            // Select recorded hotbar slot BEFORE rebind — host-loaded body may leave
            // a different InvSlot.selected; flip flags only (InvSlot.select forces
            // shouldBeActive=true and would undo flashlight-off restore).
            ApplyHotbarSelectedSlot(player, data.HotbarSelectedSlot);
            RebindCurrentItemFromSelectedHotbar(player);

            RestoreRecipes(data);
            RestoreCraftedItems(data);
            RestoreActiveEffects(data);
            RestoreLocalMapMarkers(data);
            RestoreNightTraderReputations(data);
            RestorePlayerFlags(data);
            RestoreHomeOven(data);

            // Position was always collected on Save; apply on restore so rejoin returns to exit spot.
            RestorePosition(data);

            // RestoreItems uses InvSlot.createItem — never hits addItemType* Harmony that
            // drives PeerItemPresence. Soft reconnect also ClearPlayer'd us on disconnect.
            // Republish inv+hotbar so host EventTrigger haveItem (keys/tanks) works again.
            try { Sync.PeerItemPresence.SendFullLocalInventory(); }
            catch { /* offline / mid-teardown */ }

            ModRuntime.LegacyInfo(
                $"[ClientBackup] restored from backup — level={data.CurrentLevel} exp={data.Experience} skills={(data.Skills?.Count ?? 0)} pts={data.SkillPoints} inv={(data.InventoryItems?.Count ?? 0)} items pos=({data.PosX.ToString("F0")},{data.PosZ.ToString("F0")})");
            return true;
        }

        /// <summary>
        /// Grow Hotbar/Inventory slot lists to match backup upgrade counts.
        /// Never shrinks (extra empty host slots are harmless). Does not call
        /// Player.upgradeHotbar/Inventory — those also bump the counters.
        /// </summary>
        private static void ReconcileInventoryUpgradeSlots(Player player, int wantHotbarUpgrades, int wantInventoryUpgrades)
        {
            if (player == null) return;
            if (wantHotbarUpgrades < 0) wantHotbarUpgrades = 0;
            if (wantInventoryUpgrades < 0) wantInventoryUpgrades = 0;

            int curH = player.hotbarUpgrades;
            int deltaH = wantHotbarUpgrades - curH;
            if (deltaH > 0 && player.Hotbar != null)
            {
                for (int i = 0; i < deltaH; i++)
                    player.Hotbar.addSlot();
            }
            player.hotbarUpgrades = wantHotbarUpgrades;

            int curI = player.inventoryUpgrades;
            int deltaI = wantInventoryUpgrades - curI;
            if (deltaI > 0 && player.Inventory != null)
            {
                // Vanilla upgradeInventory adds two slots per upgrade.
                for (int i = 0; i < deltaI; i++)
                {
                    player.Inventory.addSlot();
                    player.Inventory.addSlot();
                }
            }
            player.inventoryUpgrades = wantInventoryUpgrades;
        }

        private static void RestoreItems(Inventory inv, System.Collections.Generic.List<ItemEntry> entries)
        {
            if (inv == null || entries == null) return;

            inv.clear();
            inv.initSlots();

            for (int i = 0; i < entries.Count; i++)
            {
                ItemEntry entry = entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.Type))
                    continue;

                InvSlot slot = null;
                if (entry.Slot >= 0 && entry.Slot < inv.slots.Count)
                    slot = inv.slots[entry.Slot];
                if (slot == null || !InvItemClass.isNull(slot.invItem))
                    slot = inv.getNextFreeSlot();
                if (slot == null)
                    continue;

                // Vanilla recipes: prefer createItem(recipeFor, …, isRecipe:true).
                // Legacy backups may store Type="recipe" + RecipeFor.
                bool isRecipe = entry.IsRecipe;
                string createType = entry.Type;
                if (isRecipe && !string.IsNullOrEmpty(entry.RecipeFor))
                    createType = entry.RecipeFor;

                InvItemClass item = isRecipe
                    ? slot.createItem(createType, entry.Amount, 1f,
                        InvItem.ModifierQuality.none, isRecipe: true)
                    : slot.createItem(createType, entry.Amount);
                if (item == null)
                    continue;

                item.durability = entry.Durability;

                // Flashlight on/off (+ similar toggle state). Flag alone is enough
                // for onDoneSwitchingItem when the slot is later selected.
                item.shouldBeActive = entry.ShouldBeActive;
                // Regen-when-inactive uses timeDeactivated vs Time.time. A wall-clock
                // from a prior session would block regen; clamp to "ready now".
                float td = entry.TimeDeactivated;
                if (td > Time.time)
                    td = 0f;
                item.timeDeactivated = td;

                ApplyItemUpgrades(item, entry.Upgrades);
            }

            // Lantern / gasmask / armor hotbar auto-actives (vanilla init path).
            try { inv.checkForActiveSwitches(force: true); }
            catch { /* non-fatal */ }
        }

        /// <summary>
        /// After clear+recreate, Player.currentItem still pointed at the wiped
        /// InvItemClass. Rebind from the selected hotbar slot and re-apply
        /// flashlight/lightEmitter activation when shouldBeActive was restored.
        /// </summary>
        private static void RebindCurrentItemFromSelectedHotbar(Player player)
        {
            if (player?.Hotbar?.slots == null) return;
            for (int i = 0; i < player.Hotbar.slots.Count; i++)
            {
                InvSlot slot = player.Hotbar.slots[i];
                if (slot == null || !slot.selected || InvItemClass.isNull(slot.invItem))
                    continue;

                InvItemClass item = slot.invItem;
                player.currentItem = item;
                if (item.shouldBeActive && item.baseClass != null && item.durability > 0f
                    && (item.baseClass.isFlashlight || item.baseClass.lightEmitter != null))
                {
                    try { item.switchActive(destActive: true); }
                    catch { /* presentation only */ }
                }
                return;
            }
        }

        private static void ApplyItemUpgrades(InvItemClass item, System.Collections.Generic.List<string> names)
        {
            if (item == null || names == null || names.Count == 0)
                return;
            ItemsDatabase db = Singleton<ItemsDatabase>.Instance;
            if (db == null) return;

            if (item.upgrades == null)
                item.upgrades = new System.Collections.Generic.List<ItemUpgrade>();
            else
                item.upgrades.Clear();

            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                if (string.IsNullOrEmpty(name)) continue;
                ItemUpgrade upgrade = db.getUpgrade(name);
                if (upgrade != null)
                    item.upgrades.Add(upgrade);
            }
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

        /// <summary>
        /// The loaded world's per-player flags are the host's (its popups, its oven, its night):
        /// put back this player's own, so a rejoin does not show the help popups again or greet
        /// it by the host's night.
        /// </summary>
        private static void RestorePlayerFlags(ClientStateBackupData data)
        {
            if (data?.PlayerFlags == null)
                return;
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;
            int n = 0;
            for (int i = 0; i < data.PlayerFlags.Count; i++)
            {
                FlagEntry e = data.PlayerFlags[i];
                if (e == null || !PerPlayerFlagPolicy.IsExperience(e.Name))
                    continue;
                SetPlayerFlag(flags, e.Name, e.IsTrue, e.Amount);
                n++;
            }
            ApplyChapterPlayerFlags(flags);
            ModRuntime.LegacyInfo($"[ClientBackup] restored {n} per-player flag(s)");
        }

        /// <summary>
        /// A character new to the world (no snapshot of its own): vanilla's new-game values
        /// (WorldFlagsDatabase: all off), not the host's from the loaded save.
        /// </summary>
        internal static void ResetPlayerFlagsForNewCharacter()
        {
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;
            string[] names = PerPlayerFlagPolicy.PersistedFlags;
            for (int i = 0; i < names.Length; i++)
                SetPlayerFlag(flags, names[i], false, 0);
            ApplyChapterPlayerFlags(flags);
            ModRuntime.LegacyInfo("[ClientBackup] fresh character — per-player flags at new-game values");
        }

        /// <summary>Vanilla <c>Flags.setCh2flags</c> turns these on for every player entering chapter 2.</summary>
        private static void ApplyChapterPlayerFlags(Flags flags)
        {
            WorldGenerator wg = Singleton<WorldGenerator>.Instance;
            if (wg == null || wg.chapterID < 2)
                return;
            string[] ch2 = PerPlayerFlagPolicy.Chapter2Flags;
            for (int i = 0; i < ch2.Length; i++)
                flags.setFlag(ch2[i], activeModifier: true);
        }

        private static void SetPlayerFlag(Flags flags, string name, bool isTrue, int amount)
        {
            if (flags.flagsDict.TryGetValue(name, out Flags.Flag f) && f != null)
            {
                f.isTrue = isTrue;
                f.amount = amount;
                return;
            }
            // Unset and staying at the default: nothing to add.
            if (!isTrue && amount == 0)
                return;
            flags.setFlag(name, isTrue);
            flags.setFlag(name, amount);
        }

        /// <summary>
        /// A client loads the host's world, whose save holds the host's home oven: every rejoin
        /// moved the client's home (and respawn point) to the host's hideout. Put back its own,
        /// lit, as vanilla keeps a home oven.
        /// </summary>
        private static void RestoreHomeOven(ClientStateBackupData data)
        {
            Player player = Player.Instance;
            if (player == null || data == null || !data.HasHomeOven)
                return;
            int chapterNow = Singleton<WorldGenerator>.Instance != null ? Singleton<WorldGenerator>.Instance.chapterID : 0;
            if (data.Chapter > 0 && chapterNow > 0 && data.Chapter != chapterNow)
                return;
            Vector3 pos = new Vector3(data.HomeOvenX, data.HomeOvenY, data.HomeOvenZ);
            ExperienceMachine home = null;
            foreach (ExperienceMachine em in WorldQueryHelper.GetCachedSceneComponents<ExperienceMachine>())
            {
                if (em != null && (em.transform.position - pos).sqrMagnitude < 1.5f * 1.5f)
                {
                    home = em;
                    break;
                }
            }
            if (home == null)
                return;
            player.experienceMachine = home;
            if (!home.isOn)
                Patches.OvenHomes.RelightOwnHomeNextFrame();
            ModRuntime.LegacyInfo("[ClientBackup] restored home oven at " + pos);
        }

        private static void RestorePosition(ClientStateBackupData data)
        {
            Player player = Player.Instance;
            if (player == null || data == null) return;

            Vector3 pos = new Vector3(data.PosX, data.PosY, data.PosZ);
            // Uninitialized or missing trailer; never teleport to world origin by accident.
            if (pos.sqrMagnitude < 0.01f)
                return;

            // A snapshot from the previous chapter's map: the new chapter places the player itself.
            int chapterNow = Singleton<WorldGenerator>.Instance != null ? Singleton<WorldGenerator>.Instance.chapterID : 0;
            if (data.Chapter > 0 && chapterNow > 0 && data.Chapter != chapterNow)
            {
                ModRuntime.LegacyInfo($"[ClientBackup] skip position restore — taken in chapter {data.Chapter}, now {chapterNow}");
                return;
            }

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

            // Offline load already placed the body from sav.dat. A stale backup with
            // wrong XZ (or Y=16 vs live Y≈-1984) is the dual-box "nowhere / void" bug.
            Vector3 live = player.transform.position;
            float xz = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(live.x, live.z));
            if (xz > 5000f || Mathf.Abs(pos.y - live.y) > 500f)
            {
                ModRuntime.Log?.LogWarning(
                    "[ClientBackup] skip position restore — backup far from loaded pose backup="
                    + pos + " live=" + live + " xz=" + xz.ToString("F0"));
                return;
            }

            try
            {
                player.teleportTo(pos, Quaternion.Euler(90f, 0f, 0f));
                if (Singleton<WorldGrid>.Instance != null)
                    Singleton<WorldGrid>.Instance.refreshPosition(pos, instant: true, force: true);

                var net = ModRuntime.Network;
                if (net != null && net.IsConnected)
                    net.TeleportRemoteProxyTo(pos, 0f);

                ModRuntime.LegacyInfo(
                    $"[ClientBackup] restored position {pos}");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] position restore failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Align maxHealth/maxStamina with backup upgrade counts after host-sav load.
        /// Delta-adjusts pools (does not call upgradeHealth — that also setHealth(max)).
        /// </summary>
        private static void ReconcileVitalUpgradePools(Player player, int wantHealthUpgrades, int wantStaminaUpgrades)
        {
            if (player == null) return;
            if (wantHealthUpgrades < 0) wantHealthUpgrades = 0;
            if (wantStaminaUpgrades < 0) wantStaminaUpgrades = 0;

            int curH = player.healthUpgrades;
            int deltaH = wantHealthUpgrades - curH;
            if (deltaH != 0)
            {
                player.maxHealth += 25f * deltaH;
                if (player.maxHealth < 1f)
                    player.maxHealth = 1f;
                player.healthUpgrades = wantHealthUpgrades;
            }
            else
            {
                player.healthUpgrades = wantHealthUpgrades;
            }

            int curS = player.staminaUpgrades;
            int deltaS = wantStaminaUpgrades - curS;
            if (deltaS != 0)
            {
                player.maxStamina += 25f * deltaS;
                if (player.maxStamina < 1f)
                    player.maxStamina = 1f;
                player.staminaUpgrades = wantStaminaUpgrades;
            }
            else
            {
                player.staminaUpgrades = wantStaminaUpgrades;
            }
        }

        /// <summary>
        /// Re-apply chosen progression skills from backup (mirrors vanilla
        /// PlayerSkills.SaveState.loadValues without touching host peers).
        /// Unsets host LessHealth1/MoreHealth1 first — co-op restores onto the
        /// host-loaded Player, unlike vanilla load onto a fresh instance.
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
                // Host-sav left LessHealth1/MoreHealth1 true (setters: maxHealth −50 / +25).
                // Vanilla loadValues only clears chosen — fine on a fresh Player; co-op
                // restores onto the host-loaded Player. Unset before client initialize so
                // we neither keep the host trait nor double-apply when chosen was cleared
                // then initialize(true) sets the property again. Runs after
                // ReconcileVitalUpgradePools (upgrade deltas are independent additives).
                if (ps.LessHealth1)
                    ps.LessHealth1 = false;
                if (ps.MoreHealth1)
                    ps.MoreHealth1 = false;

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
