using System;
using System.Collections.Generic;
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

            data.Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            data.CampaignId = CoopWorldCopyMeta.GetOrCreateCampaignIdForCurrentProfile();
            data.ContentFingerprint = CoopWorldCopyMeta.TryGetCurrentContentFingerprint();

            // Never persist dream-pad coordinates as the rejoin spawn; that throws the
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

        // Persist morning-trader reputation per player rather than in host-shared bulk.
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
