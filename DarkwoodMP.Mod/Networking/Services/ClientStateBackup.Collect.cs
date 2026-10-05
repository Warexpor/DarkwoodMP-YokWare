using System;
using System.Collections.Generic;
using Steamworks;
using DWMPHorde.Networking.Steam;
using DWMPHorde.Sync;
using Newtonsoft.Json;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Collect live player state and JSON (de)serialize helpers.</summary>
    public static partial class ClientStateBackup
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

            // SteamID64 is the preferred stable host disk key across PlayerId reshuffles.
            data.SteamId = TryResolveLocalSteamIdString();
            // LAN / non-Steam: install-scoped key (Steam+SecondDarkwood dual-box path).
            data.StableClientKey = GetOrCreateLanClientKey();

            data.Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            data.Chapter = Singleton<WorldGenerator>.Instance != null ? Singleton<WorldGenerator>.Instance.chapterID : 0;
            data.CampaignId = CoopWorldCopyMeta.GetOrCreateCampaignIdForCurrentProfile();
            data.ContentFingerprint = CoopWorldCopyMeta.TryGetCurrentContentFingerprint();

            // Never persist dream-pad coordinates as the rejoin spawn; that throws the
            // client into empty -50k/-75k space after the pad is torn down.
            Vector3 pos = ResolveOverworldBackupPosition(player);
            data.PosX = pos.x; data.PosY = pos.y; data.PosZ = pos.z;

            // On the dream pad the live bag, health, effects and clock are the dream's. Vanilla
            // keeps the real ones aside and puts them back at wake-up (Dreams.*Copy, then a full
            // heal in Player.endDreaming): a quit or drop mid-dream saved the dream kit as the
            // player's own, and the next join restored it.
            Dreams dreams = Dreams.Instance;
            bool inDream = dreams != null && dreams.dreaming && !player.firstPlay;

            data.Health = inDream ? player.maxHealth : player.health;
            if (player.experienceMachine != null)
            {
                Vector3 home = player.experienceMachine.transform.position;
                data.HasHomeOven = true;
                data.HomeOvenX = home.x;
                data.HomeOvenY = home.y;
                data.HomeOvenZ = home.z;
            }
            data.Stamina = player.stamina;
            data.Experience = player.experience;
            data.CurrentLevel = player.currentLevel;
            data.DreamLvlFlags = Sync.DreamSession.ReadLocalLvlFlags();
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
                data.CanActivateSkill = player.skills.canActivateSkill;
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

            List<InvSlot> invSlots = inDream ? dreams.inventorySlotsCopy : player.Inventory?.slots;
            if (invSlots != null)
            {
                data.InventoryItems = new List<ItemEntry>();
                for (int i = 0; i < invSlots.Count; i++)
                {
                    var slot = invSlots[i];
                    if (slot != null && !InvItemClass.isNull(slot.invItem))
                        data.InventoryItems.Add(MakeItemEntry(slot.invItem, i));
                }
            }

            List<InvSlot> hotSlots = inDream ? dreams.hotbarSlotsCopy : player.Hotbar?.slots;
            if (hotSlots != null)
            {
                data.HotbarItems = new List<ItemEntry>();
                for (int i = 0; i < hotSlots.Count; i++)
                {
                    var slot = hotSlots[i];
                    if (slot != null && !InvItemClass.isNull(slot.invItem))
                        data.HotbarItems.Add(MakeItemEntry(slot.invItem, i));
                }
                // Prefer live selected flag; getSelectedSlotId returns 0 when none.
                // Vanilla selects slot 0 at wake-up.
                data.HotbarSelectedSlot = inDream ? 0 : player.Hotbar.getSelectedSlotId();
            }

            data.ActiveEffects = inDream ? CollectSavedEffects(dreams.effectsCopy) : CollectActiveEffects(player);
            data.LocalMapMarkers = CollectLocalMapMarkers();

            var controller = Singleton<Controller>.Instance;
            if (controller != null)
            {
                data.Day = controller.day;
                data.GameTimeMinutes = inDream ? (int)dreams.timeCopy : controller.CurrentTime;
            }

            // Persist morning-trader reputation per player rather than in host-shared bulk.
            data.NightTraderReputations = CollectNightTraderReputations();
            data.CraftedItems = CollectCraftedItems(player);

            return data;
        }

        private static List<CraftedEntry> CollectCraftedItems(Player player)
        {
            var list = new List<CraftedEntry>();
            if (player?.craftedItems == null) return list;
            for (int i = 0; i < player.craftedItems.Count; i++)
            {
                StringAndInt entry = player.craftedItems[i];
                if (entry == null || string.IsNullOrEmpty(entry._string)) continue;
                // Skip zero counts (getCraftedItem may insert zeros).
                if (entry._int <= 0) continue;
                list.Add(new CraftedEntry { Type = entry._string, Count = entry._int });
            }
            return list;
        }

        private static List<EffectEntry> CollectSavedEffects(CharacterEffects.SaveState saved)
        {
            var list = new List<EffectEntry>();
            if (saved?.effects == null) return list;
            for (int i = 0; i < saved.effects.Count; i++)
            {
                CharacterEffects.SaveState.SavedEffect fx = saved.effects[i];
                if (fx == null) continue;
                if (fx.type == CharacterEffectType.damage || fx.type == CharacterEffectType.timeFreeze)
                    continue;
                list.Add(new EffectEntry
                {
                    Type = (int)fx.type,
                    Duration = fx.duration,
                    Modifier = fx.modifier,
                    Interval = fx.interval,
                    TimeElapsed = fx.timeElapsed
                });
            }
            return list;
        }

        private static List<EffectEntry> CollectActiveEffects(Player player)
        {
            var list = new List<EffectEntry>();
            if (player?.effects?.activeEffects == null) return list;
            for (int i = 0; i < player.effects.activeEffects.Count; i++)
            {
                CharacterEffect fx = player.effects.activeEffects[i];
                if (fx == null) continue;
                // Instant damage pulse — re-activate would getHit again on restore.
                if (fx.type == CharacterEffectType.damage)
                    continue;
                // timeFreeze toggles Controller.DoUpdateTime globally — host TimeSync owns the clock.
                if (fx.type == CharacterEffectType.timeFreeze)
                    continue;
                list.Add(new EffectEntry
                {
                    Type = (int)fx.type,
                    Duration = fx.duration,
                    Modifier = fx.modifier,
                    Interval = fx.interval,
                    TimeElapsed = fx.timeElapsed
                });
            }
            return list;
        }

        private static List<MarkerEntry> CollectLocalMapMarkers()
        {
            var list = new List<MarkerEntry>();
            var markers = Sync.MultiplayerMapManager.LocalMarkers;
            if (markers == null || markers.Count == 0) return list;
            for (int i = 0; i < markers.Count; i++)
            {
                Vector3 p = markers[i];
                list.Add(new MarkerEntry { X = p.x, Y = p.y, Z = p.z });
            }
            return list;
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


        /// <summary>SteamID64 string for this box when Steamworks is ready; else null.</summary>
        internal static string TryResolveLocalSteamIdString()
        {
            try
            {
                var sid = SteamCoopTransport.LocalSteamId();
                if (sid.IsValid() && sid.m_SteamID != 0)
                    return sid.m_SteamID.ToString();
            }
            catch { /* Steam not ready / non-Steam box */ }
            return null;
        }

        private static ItemEntry MakeItemEntry(InvItemClass item, int slot)
        {
            // Mirror vanilla InvItemClass.SaveState (see decompile InvItemClass.SaveState
            // ctor). Firearm magazine lives in amount when hasAmmo; createItem maps
            // Amount → ammo. Also persist shouldBeActive / timeDeactivated / upgrades
            // — durability alone is not enough for flashlight on/off or workbench
            // ItemUpgrade damage/durability modifiers (melee/armor).
            bool hasAmmo = item.baseClass != null && item.baseClass.hasAmmo;
            List<string> upgrades = null;
            if (item.upgrades != null && item.upgrades.Count > 0)
            {
                upgrades = new List<string>(item.upgrades.Count);
                for (int u = 0; u < item.upgrades.Count; u++)
                {
                    ItemUpgrade up = item.upgrades[u];
                    if (up != null && !string.IsNullOrEmpty(up.name))
                        upgrades.Add(up.name);
                }
                if (upgrades.Count == 0)
                    upgrades = null;
            }
            return new ItemEntry
            {
                Slot = slot,
                Type = item.type,
                Durability = item.durability,
                Amount = hasAmmo ? item.ammo : item.amount,
                IsRecipe = item.isRecipe,
                RecipeFor = item.recipeFor,
                ShouldBeActive = item.shouldBeActive,
                Upgrades = upgrades,
                TimeDeactivated = item.timeDeactivated
            };
        }

        public static string SerializeToJson(ClientStateBackupData data)
        {
            // Compact: this string is also the wire payload, and indentation roughly doubled it.
            return JsonConvert.SerializeObject(data, Formatting.None);
        }

        public static ClientStateBackupData DeserializeFromJson(string json)
        {
            return JsonConvert.DeserializeObject<ClientStateBackupData>(json);
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
        /// <see cref="Dreams.positionCopy"/>; use that for backups.
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

            // Inside a cellar / bunker / house pad (every pad slot is past the pad bound): the world
            // point vanilla keeps for the return trip, so a rejoin lands where the player went in.
            var ol = Singleton<OutsideLocations>.Instance;
            if (ol != null && ol.playerInOutsideLocation
                && ol.positionCopy.sqrMagnitude > 0.01f && !IsDreamPadCoordinate(ol.positionCopy))
                return ol.positionCopy;

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
    }
}
