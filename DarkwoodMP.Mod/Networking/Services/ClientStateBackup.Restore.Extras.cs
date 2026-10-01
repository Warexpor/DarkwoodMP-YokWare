using System;
using System.Collections.Generic;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// ClientStateBackup restore helpers for recipes, hotbar selection,
    /// CharacterEffects, personal map markers, and craftedItems (0.8.60+).
    /// </summary>
    public static partial class ClientStateBackup
    {
        /// <summary>
        /// Mark the backup hotbar slot as selected without InvSlot.select()
        /// (that forces shouldBeActive=true). Pre-0.8.60 JSON defaults to 0.
        /// </summary>
        private static void ApplyHotbarSelectedSlot(Player player, int selectedSlot)
        {
            if (player?.Hotbar?.slots == null || player.Hotbar.slots.Count == 0)
                return;
            // −1 / out-of-range: pre-0.8.60 backup or corrupt — keep host-loaded flags.
            if (selectedSlot < 0)
                return;
            int count = player.Hotbar.slots.Count;
            int sel = selectedSlot;
            if (sel >= count)
                sel = 0;
            for (int i = 0; i < count; i++)
            {
                InvSlot slot = player.Hotbar.slots[i];
                if (slot != null)
                    slot.selected = (i == sel);
            }
        }

        /// <summary>
        /// Mirror vanilla Player.SaveState.loadValues recipe loop. WorldSaveShare
        /// loads the HOST character first — without this, client-learned recipes
        /// stay collected-but-never-applied across cold rejoin / migration.
        /// </summary>
        private static void RestoreRecipes(ClientStateBackupData data)
        {
            if (data?.Recipes == null)
                return;
            Player player = Player.Instance;
            if (player == null) return;

            try
            {
                player.recipes.Clear();
                for (int i = 0; i < data.Recipes.Count; i++)
                {
                    string type = data.Recipes[i];
                    if (string.IsNullOrEmpty(type)) continue;
                    if (player.hasRecipe(type)) continue;
                    ItemsDatabase db = Singleton<ItemsDatabase>.Instance;
                    if (db == null) continue;
                    CraftingRecipes recipes = db.getRecipes(type, instantiate: false);
                    if (recipes != null)
                        player.recipes.Add(recipes);
                }
                try { player.refreshRecipes(); }
                catch { /* Crafting UI may be null mid-load */ }
                ModRuntime.LegacyInfo(
                    $"[ClientBackup] restored recipes count={player.recipes.Count}");
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] recipe restore failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Mirror vanilla CharacterEffects.SaveState.loadValues. Clears host-loaded
        /// effects first so peer DoTs/buffs (bleed, poison, hunger, wards, …) win.
        /// Skips CharacterEffectType.damage (instant getHit — already filtered at Collect).
        /// </summary>
        private static void RestoreActiveEffects(ClientStateBackupData data)
        {
            if (data?.ActiveEffects == null)
                return;
            Player player = Player.Instance;
            if (player?.effects?.activeEffects == null) return;

            try
            {
                // Null ActiveEffects = pre-0.8.60 backup (skip). Empty list = clear host effects.
                player.effects.removeAllEffects();
                int applied = 0;
                for (int i = 0; i < data.ActiveEffects.Count; i++)
                {
                    EffectEntry entry = data.ActiveEffects[i];
                    if (entry == null) continue;
                    if (!System.Enum.IsDefined(typeof(CharacterEffectType), entry.Type))
                        continue;
                    var type = (CharacterEffectType)entry.Type;
                    if (type == CharacterEffectType.damage
                        || type == CharacterEffectType.timeFreeze)
                        continue;
                    player.effects.activate(
                        type, entry.Duration, entry.Modifier, entry.Interval, entry.TimeElapsed);
                    applied++;
                }
                ModRuntime.LegacyInfo(
                    $"[ClientBackup] restored active effects count={applied}");
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] effect restore failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Mirror vanilla Player.SaveState.loadValues craftedItems assign.
        /// Null CraftedItems = pre-0.8.61 backup (leave host-loaded counts).
        /// Empty list = clear host counts (client never limited-crafted).
        /// </summary>
        private static void RestoreCraftedItems(ClientStateBackupData data)
        {
            if (data?.CraftedItems == null)
                return;
            Player player = Player.Instance;
            if (player == null) return;

            try
            {
                if (player.craftedItems == null)
                    player.craftedItems = new System.Collections.Generic.List<StringAndInt>();
                else
                    player.craftedItems.Clear();

                for (int i = 0; i < data.CraftedItems.Count; i++)
                {
                    CraftedEntry entry = data.CraftedItems[i];
                    if (entry == null || string.IsNullOrEmpty(entry.Type)) continue;
                    int count = entry.Count;
                    if (count < 0) count = 0;
                    player.craftedItems.Add(new StringAndInt(entry.Type, count));
                }
                ModRuntime.LegacyInfo(
                    $"[ClientBackup] restored craftedItems count={player.craftedItems.Count}");
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] craftedItems restore failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Repopulate personal blue map pins after NetworkReset cleared LocalMarkers.
        /// Dedupes + re-broadcasts via MultiplayerMapManager so peers see them again.
        /// </summary>
        private static void RestoreLocalMapMarkers(ClientStateBackupData data)
        {
            if (data?.LocalMapMarkers == null || data.LocalMapMarkers.Count == 0)
                return;
            var positions = new System.Collections.Generic.List<UnityEngine.Vector3>(data.LocalMapMarkers.Count);
            for (int i = 0; i < data.LocalMapMarkers.Count; i++)
            {
                MarkerEntry entry = data.LocalMapMarkers[i];
                if (entry == null) continue;
                positions.Add(new UnityEngine.Vector3(entry.X, entry.Y, entry.Z));
            }
            int added = Sync.MultiplayerMapManager.RestoreLocalMarkersFromBackup(positions);
            if (added > 0)
                ModRuntime.LegacyInfo(
                    $"[ClientBackup] restored local map markers +{added}");
        }

    }
}
