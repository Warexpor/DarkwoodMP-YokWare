using System;
using System.Collections.Generic;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// ClientStateBackup restore helpers for recipes, hotbar selection,
    /// CharacterEffects, personal map markers, and craftedItems.
    /// </summary>
    public static partial class ClientStateBackup
    {
        /// <summary>
        /// Mark the backup hotbar slot as selected without InvSlot.select()
        /// (that forces shouldBeActive=true). Older JSON defaults to 0.
        /// </summary>
        private static void ApplyHotbarSelectedSlot(Player player, int selectedSlot)
        {
            if (player?.Hotbar?.slots == null || player.Hotbar.slots.Count == 0)
                return;
            // −1 / out-of-range: older backup or corrupt — keep host-loaded flags.
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
                // Null ActiveEffects = older backup (skip). Empty list = clear host effects.
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
        /// Null CraftedItems = older backup (leave host-loaded counts).
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
        /// Backups from before the party map board kept this player's pins here. They go to the host
        /// once as ordinary pins (the host ignores one already on the board at that spot); new
        /// backups no longer carry pins, the host's world keeps the board.
        /// </summary>
        private static void RestoreLocalMapMarkers(ClientStateBackupData data)
        {
            if (data?.LocalMapMarkers == null || data.LocalMapMarkers.Count == 0)
                return;
            int chapter = data.Chapter > 0 ? data.Chapter : Sync.MapPinBoard.CurrentChapter();
            int sent = 0;
            for (int i = 0; i < data.LocalMapMarkers.Count; i++)
            {
                MarkerEntry entry = data.LocalMapMarkers[i];
                if (entry == null) continue;
                Sync.MapPinBoard.RequestPut(Sync.MapPinKind.Mark, chapter, entry.X, entry.Z);
                sent++;
            }
            data.LocalMapMarkers = null;
            if (sent > 0)
                ModRuntime.LegacyInfo($"[ClientBackup] legacy map pins sent to the party board: {sent}");
        }

    }
}
