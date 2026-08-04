using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DWMPHorde.Logging;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Items
{
    /// <summary>
    /// Injects craftable walkie_talkie (2 scrap + 1 nail, workbench lvl 1) for radio voice.
    /// Ported from friend Melon WalkieItem onto BepInEx Harmony / PatchAll.
    /// </summary>
    public static class WalkieItem
    {
        public const string ItemType = "walkie_talkie";
        private const string DonorType = "junk";
        private const string EmbeddedResourceName = "DWMPHorde.Resources.walkie_talkie.png";

        private static GameObject _templateGo;
        private static InvItem _template;
        private static CraftingRecipes _recipes;
        private static string _donorIconName;
        private static Texture2D _iconTexture;
        private static bool _iconTextureFailed;
        private static bool _langDone;
        private static float _nextAttempt;
        private static bool _warnedNoDb;
        /// <summary>True after icon sprite is in a collection (or texture load failed permanently).</summary>
        private static bool _iconSettled;

        public static void Tick()
        {
            // Settled: never call InjectIcon again — FindObjectsOfTypeAll was the ~50ms/5s hitch.
            if (_iconSettled)
                return;
            if (Time.unscaledTime < _nextAttempt)
                return;
            _nextAttempt = Time.unscaledTime + 1f;
            try { InjectLocalization(); } catch { /* ignore */ }
            if (_template == null)
            {
                try { EnsureTemplate(Singleton<ItemsDatabase>.Instance); }
                catch { /* ignore */ }
            }
            if (_iconTextureFailed)
            {
                _iconSettled = true;
                return;
            }
            if (Player.Instance == null || _template == null)
                return;
            try { InjectIcon(); }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Audio, "Walkie icon: " + ex.Message);
            }
            if (_template != null && _template.iconType == ItemType)
                _iconSettled = true;
        }

        [HarmonyPatch(typeof(ItemsDatabase), nameof(ItemsDatabase.hasItem))]
        private static class HasItemPatch
        {
            private static bool Prefix(string type, ref bool __result)
            {
                if (type != ItemType)
                    return true;
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
                    foreach (Workbench.Level level in __instance.levels)
                    {
                        if (level != null && level.level == 1 && !level.recipes.Contains(_recipes))
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
                    ModLog.Warn(LogCat.Audio, "Walkie: ItemsDatabase not ready yet");
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

            _donorIconName = donor.iconType;
            _templateGo = new GameObject("YokWare_WalkieTalkie");
            _templateGo.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_templateGo);
            InvItem item = _templateGo.AddComponent<InvItem>();
            foreach (FieldInfo field in typeof(InvItem).GetFields(BindingFlags.Instance | BindingFlags.Public))
                field.SetValue(item, field.GetValue(donor));

            item.type = ItemType;
            item.iconType = _donorIconName;
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
            item.hasDurability = false;
            item.hasAmmo = false;
            item.canBeReloaded = false;
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
            ModLog.Event(LogCat.Audio, "Walkie-Talkie item built (2 scrap + 1 nail, WB lvl 1)");
            return true;
        }

        private static void InjectLocalization()
        {
            Dictionary<string, string> sheet = Language.GetAllKeysForSheet("Items");
            if (sheet == null || sheet.ContainsKey("walkie_talkie_name"))
                return;
            sheet.Add("walkie_talkie_name", "Walkie-Talkie");
            if (!sheet.ContainsKey("walkie_talkie_desc"))
            {
                sheet.Add("walkie_talkie_desc",
                    "A crude two-way radio. Carry one each to talk over any distance.");
            }
            if (!_langDone)
            {
                _langDone = true;
                ModLog.Event(LogCat.Audio, "Walkie localization injected");
            }
        }

        private static void InjectIcon()
        {
            if (string.IsNullOrEmpty(_donorIconName))
                return;
            Texture2D tex = LoadIconTexture();
            if (tex == null)
                return;

            bool injected = false;
            tk2dSpriteCollectionData[] cols = Resources.FindObjectsOfTypeAll<tk2dSpriteCollectionData>();
            foreach (tk2dSpriteCollectionData col in cols)
            {
                tk2dSpriteCollectionData inst = col.inst != null ? col.inst : col;
                if (inst.GetSpriteIdByName(_donorIconName, -1) < 0)
                    continue;
                if (inst.GetSpriteIdByName(ItemType, -1) >= 0)
                {
                    injected = true;
                    continue;
                }
                AppendDefinition(inst, tex);
                injected = true;
                ModLog.Event(LogCat.Audio, "Walkie sprite injected into '" + inst.name + "'");
            }
            if (injected)
            {
                if (_template.iconType != ItemType)
                    _template.iconType = ItemType;
                _iconSettled = true;
            }
        }

        private static void AppendDefinition(tk2dSpriteCollectionData col, Texture2D tex)
        {
            tk2dSpriteDefinition donor = col.spriteDefinitions[col.GetSpriteIdByName(_donorIconName)];
            Material mat = new Material(donor.material.shader)
            {
                mainTexture = tex,
                name = "YokWare_WalkieIcon"
            };
            Vector3[] positions = (Vector3[])donor.positions.Clone();
            var def = new tk2dSpriteDefinition
            {
                name = ItemType,
                material = mat,
                materialInst = mat,
                materialId = col.materials != null ? col.materials.Length : 0,
                positions = positions,
                uvs = BuildUvsFromPositions(positions),
                normals = donor.normals != null ? (Vector3[])donor.normals.Clone() : new Vector3[0],
                tangents = donor.tangents != null ? (Vector4[])donor.tangents.Clone() : new Vector4[0],
                indices = (int[])donor.indices.Clone(),
                boundsData = (Vector3[])donor.boundsData.Clone(),
                untrimmedBoundsData = (Vector3[])donor.untrimmedBoundsData.Clone(),
                texelSize = donor.texelSize,
                flipped = tk2dSpriteDefinition.FlipMode.None,
                complexGeometry = false
            };

            tk2dSpriteDefinition[] defs = col.spriteDefinitions;
            Array.Resize(ref defs, defs.Length + 1);
            defs[defs.Length - 1] = def;
            col.spriteDefinitions = defs;

            Material[] mats = col.materials ?? new Material[0];
            Array.Resize(ref mats, mats.Length + 1);
            mats[mats.Length - 1] = mat;
            col.materials = mats;
            if (col.materialInsts != null)
            {
                Material[] insts = col.materialInsts;
                Array.Resize(ref insts, insts.Length + 1);
                insts[insts.Length - 1] = mat;
                col.materialInsts = insts;
            }
            col.materialIdsValid = false;
            typeof(tk2dSpriteCollectionData)
                .GetField("spriteNameLookupDict", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(col, null);
        }

        private static Vector2[] BuildUvsFromPositions(Vector3[] positions)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (Vector3 p in positions)
            {
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }
            float midX = (minX + maxX) * 0.5f;
            float midY = (minY + maxY) * 0.5f;
            var uvs = new Vector2[positions.Length];
            for (int i = 0; i < positions.Length; i++)
                uvs[i] = new Vector2(positions[i].x > midX ? 1f : 0f, positions[i].y > midY ? 1f : 0f);
            return uvs;
        }

        private static Texture2D LoadIconTexture()
        {
            if (_iconTexture != null)
                return _iconTexture;
            if (_iconTextureFailed)
                return null;
            try
            {
                byte[] bytes = null;
                string dir = Path.GetDirectoryName(typeof(WalkieItem).Assembly.Location) ?? ".";
                string beside = Path.Combine(dir, "walkie_talkie.png");
                if (File.Exists(beside))
                    bytes = File.ReadAllBytes(beside);
                if (bytes == null)
                {
                    using (Stream stream = typeof(WalkieItem).Assembly
                        .GetManifestResourceStream(EmbeddedResourceName))
                    {
                        if (stream != null)
                        {
                            bytes = new byte[stream.Length];
                            int read = 0;
                            while (read < bytes.Length)
                            {
                                int n = stream.Read(bytes, read, bytes.Length - read);
                                if (n <= 0) break;
                                read += n;
                            }
                        }
                    }
                }
                if (bytes == null)
                {
                    // Fallback logical name from extract
                    using (Stream stream = typeof(WalkieItem).Assembly
                        .GetManifestResourceStream("DarkwoodMP.walkie_talkie.png"))
                    {
                        if (stream != null)
                        {
                            bytes = new byte[stream.Length];
                            int read = 0;
                            while (read < bytes.Length)
                            {
                                int n = stream.Read(bytes, read, bytes.Length - read);
                                if (n <= 0) break;
                                read += n;
                            }
                        }
                    }
                }
                if (bytes == null)
                {
                    _iconTextureFailed = true;
                    ModLog.Warn(LogCat.Audio, "Walkie sprite missing — keeping scrap icon");
                    return null;
                }

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, bytes))
                {
                    _iconTextureFailed = true;
                    ModLog.Warn(LogCat.Audio, "Walkie sprite decode failed");
                    return null;
                }
                tex.name = "YokWare_WalkieIconTex";
                tex.wrapMode = TextureWrapMode.Clamp;
                _iconTexture = tex;
                return tex;
            }
            catch (Exception ex)
            {
                _iconTextureFailed = true;
                ModLog.Warn(LogCat.Audio, "Walkie icon load: " + ex.Message);
                return null;
            }
        }
    }
}
