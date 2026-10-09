using System;
using System.Collections.Generic;
using System.Reflection;
using DWMPHorde.Logging;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Items
{
    /// <summary>
    /// Injects craftable walkie_talkie (2 scrap + 1 nail, workbench lvl 1) for radio voice; it
    /// runs on a 9V battery (reloaded like the flashlight) and comes off the bench charged.
    /// Ported from friend Melon WalkieItem onto BepInEx Harmony / PatchAll.
    /// </summary>
    public static class WalkieItem
    {
        public const string ItemType = "walkie_talkie";
        private const string DonorType = "junk";
        /// <summary>
        /// The game's own handheld radio in the inventory atlas (<c>InventorySprites</c>), the
        /// icon of the "damaged handheld radio" journal note: same hand, same grey, same diagonal
        /// as every other item.
        /// </summary>
        private const string IconSprite = "radio_small_01";

        private static GameObject _templateGo; // process-scoped: injected item template
        private static InvItem _template; // process-scoped: injected item template
        private static CraftingRecipes _recipes; // process-scoped: injected item template
        private static bool _langDone; // process-scoped: one-time injection state
        private static float _nextAttempt; // process-scoped: one-time injection state
        private static bool _warnedNoDb; // process-scoped: one-time injection state
        /// <summary>True once the item is built and its name is in the language sheet.</summary>
        private static bool _settled; // process-scoped: one-time injection state

        public static void Tick()
        {
            if (_settled)
                return;
            if (Time.unscaledTime < _nextAttempt)
                return;
            _nextAttempt = Time.unscaledTime + 1f;
            // Boot-time injection; later language switches are re-injected by LanguageSwitchPatch.
            try { InjectLocalization(); } catch { /* ignore */ }
            if (_template == null)
            {
                try { EnsureTemplate(Singleton<ItemsDatabase>.Instance); }
                catch { /* ignore */ }
            }
            _settled = _template != null && _langDone;
        }

        /// <summary>
        /// <c>Language.DoSwitch</c> rebuilds every sheet from the language files, dropping the
        /// injected walkie keys. Re-inject right after each switch (the settled <see cref="Tick"/>
        /// path no longer runs).
        /// </summary>
        [HarmonyPatch(typeof(Language), "DoSwitch")]
        private static class LanguageSwitchPatch
        {
            private static void Postfix()
            {
                try { InjectLocalization(); }
                catch (Exception ex)
                {
                    ModLog.Warn(LogCat.Audio, "Walkie localization re-inject: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// The walkie in the database's name list (<c>itemsDict</c>: type to resource path), so
        /// item lists built from it, the vanilla debug Items window and item-giver mods, show it
        /// and can give it. Nothing loads the path: <c>getItem</c> is answered above it
        /// (<see cref="GetItemPatch"/>). Loot comes from loot tables, not this list.
        /// </summary>
        private static void Register(ItemsDatabase db)
        {
            if (db != null && db.itemsDict != null && !db.itemsDict.ContainsKey(ItemType))
                db.itemsDict.Add(ItemType, "YokWare/" + ItemType);
        }

        /// <summary><c>populateDict</c> clears the list and refills it from the asset: add the walkie back.</summary>
        [HarmonyPatch(typeof(ItemsDatabase), nameof(ItemsDatabase.populateDict))]
        private static class PopulateDictPatch
        {
            private static void Postfix(ItemsDatabase __instance)
            {
                if (_template != null)
                    Register(__instance);
            }
        }

        [HarmonyPatch(typeof(ItemsDatabase), nameof(ItemsDatabase.hasItem))]
        private static class HasItemPatch
        {
            private static bool Prefix(ItemsDatabase __instance, string type, ref bool __result)
            {
                if (type != ItemType)
                    return true;
                // Claim the item only when getItem can actually serve it; otherwise fall through
                // to vanilla so a database without the donor items behaves exactly as before.
                try
                {
                    if (!EnsureTemplate(__instance))
                        return true;
                }
                catch
                {
                    return true;
                }
                __result = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(ItemsDatabase), nameof(ItemsDatabase.getItem))]
        private static class GetItemPatch
        {
            private static bool Prefix(ItemsDatabase __instance, string type, bool instantiate, ref InvItem __result)
            {
                if (type != ItemType)
                    return true;
                try
                {
                    if (!EnsureTemplate(__instance))
                        return true;
                    __result = instantiate
                        ? UnityEngine.Object.Instantiate(_templateGo).GetComponent<InvItem>()
                        : _template;
                    return false;
                }
                catch (Exception ex)
                {
                    ModLog.Error(LogCat.Audio, "Walkie getItem: " + ex.Message);
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(Workbench), nameof(Workbench.open))]
        private static class WorkbenchOpenPatch
        {
            private static void Prefix(Workbench __instance)
            {
                try
                {
                    if (_recipes == null && !EnsureTemplate(Singleton<ItemsDatabase>.Instance))
                        return;
                    if (__instance.levels == null)
                        return;
                    foreach (Workbench.Level level in __instance.levels)
                    {
                        if (level != null && level.level == 1 && level.recipes != null
                            && !level.recipes.Contains(_recipes))
                            level.recipes.Add(_recipes);
                    }
                }
                catch (Exception ex)
                {
                    ModLog.Error(LogCat.Audio, "Walkie workbench recipe: " + ex.Message);
                }
            }
        }

        private static bool EnsureTemplate(ItemsDatabase db)
        {
            if (_template != null)
                return true;
            if (db == null)
            {
                if (!_warnedNoDb)
                {
                    _warnedNoDb = true;
                    // Expected once at boot before ItemsDatabase Awake — not a failure.
                    ModLog.Event(LogCat.Audio, "Walkie: ItemsDatabase not ready yet (will retry)");
                }
                return false;
            }

            InvItem donor = db.getItem(DonorType, false);
            InvItem nail = db.getItem("nail", false);
            if (donor == null || nail == null)
            {
                ModLog.Error(LogCat.Audio, "Walkie: donor items missing (junk/nail)");
                return false;
            }

            _templateGo = new GameObject("YokWare_WalkieTalkie");
            _templateGo.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_templateGo);
            InvItem item = _templateGo.AddComponent<InvItem>();
            foreach (FieldInfo field in typeof(InvItem).GetFields(BindingFlags.Instance | BindingFlags.Public))
                field.SetValue(item, field.GetValue(donor));

            item.type = ItemType;
            item.iconType = IconSprite;
            item.categories = new List<InvItem.Category> { (InvItem.Category)700 };
            item.upgrades = new List<ItemUpgrade>();
            item.effects = new List<InvItemEffect>();
            item.itemsAfterUsing = new List<CraftingRequirement>();
            item.useRequirements = new List<EventTriggerRequirement>();
            item.locations = new List<string>();
            item.dupa = new Dictionary<string, int>();
            item.sex = (InvItem.Sex)0;
            item.modifierQuality = (InvItem.ModifierQuality)0;
            item.stackable = false;
            item.stacksDurability = false;
            item.maxAmount = 1;
            item.value = 150;
            item.isExpItem = false;
            item.expValue = 0;
            item.examinable = false;
            item.useable = false;
            item.placeOnUse = false;
            item.canBePlaced = false;
            item.givesSkillSlot = false;
            item.addsHotbarSlot = false;
            item.addsInventorySlot = false;
            item.isAmmo = false;
            item.isArmor = false;
            item.addsPoisonImmunity = false;
            item.isImportantItem = false;
            item.isWorkbenchUpgrade = false;
            item.isMap = false;
            item.showPopup = true;
            item.rottenItem = null;
            item.rotten = false;
            // Runs on a 9V battery like the vanilla flashlight: the durability bar is the charge
            // (drained by VoiceChatService while switched on), and the vanilla Reload key swaps in
            // a battery9v (InvItemClass.reload: no ammo, so durability back to full).
            item.hasDurability = true;
            item.maxDurability = 100f;
            item.durabilityDrain = 0f;
            item.durabilityRegeneration = 0f;
            item.regeneratesWhenInactive = false;
            item.hasAmmo = false;
            item.canBeReloaded = true;
            item.ammoType = "battery9v";
            item.reloadSound = "pistol_reload";
            item.isFirearm = false;
            item.isMelee = false;
            item.canBeAimed = false;
            item.isRepairKit = false;
            item.protectsFromShadows = false;
            item.isFlashlight = false;
            item.nightVision = false;
            item.isNaturalLight = false;
            item.lightEmitter = null;
            item._particleEmitter = null;
            item.emitterPositions = null;
            item.isThrowable = false;
            item.recoverableAfterThrown = false;
            item.item = null;

            _recipes = _templateGo.AddComponent<CraftingRecipes>();
            _recipes.craftTime = 2f;
            _recipes.removeOnCraft = false;
            _recipes.useOnCraft = false;
            _recipes.timesCraftedLimit = 0;
            _recipes.initialized = false;
            _recipes.recipes = new List<CraftingRecipes.Recipe>
            {
                new CraftingRecipes.Recipe
                {
                    produceAmount = 1,
                    requirements = new List<CraftingRequirement>
                    {
                        new CraftingRequirement { item = donor, amount = 2 },
                        new CraftingRequirement { item = nail, amount = 1 }
                    },
                    additionalItemsProduced = new List<CraftingRecipes.Recipe.AdditionalItemProduced>()
                }
            };
            _template = item;
            Register(db);
            ModLog.Event(LogCat.Audio, "Walkie-Talkie item built (2 scrap + 1 nail, WB lvl 1)");
            return true;
        }

        private static void InjectLocalization()
        {
            Dictionary<string, string> sheet = Language.GetAllKeysForSheet("Items");
            if (sheet == null)
                return;
            if (sheet.ContainsKey("walkie_talkie_name"))
            {
                _langDone = true;
                return;
            }
            // DoSwitch runs before the menu's per-frame language refresh: read the setting now.
            Loc.SetLanguage(GameSettings.GetString("LanguageCode"));
            sheet.Add("walkie_talkie_name", Loc.T("Walkie-Talkie"));
            if (!sheet.ContainsKey("walkie_talkie_desc"))
            {
                sheet.Add("walkie_talkie_desc",
                    Loc.T("A crude two-way radio. Carry one each to talk over any distance."));
            }
            if (!_langDone)
            {
                _langDone = true;
                ModLog.Event(LogCat.Audio, "Walkie localization injected");
            }
        }
    }
}
