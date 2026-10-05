using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Vanilla checks a craft's, repair's or upgrade's ingredients when the progress bar starts and
    /// takes them when it fills (<c>Player.progressBarCompleted</c>), without a second look. With
    /// the workbench pile shared, a teammate could take the planks during those seconds: the
    /// action still finished. If the pile then lacked them, the host refused the take and the
    /// refund only took that ingredient back from the crafter's own bag, so a crafter without it
    /// kept the product for free.
    /// Now the ingredients are checked again when the bar fills, and a completion the host refuses
    /// (both reached for the same pile item at once) is undone: the product taken back, the repair
    /// or the upgrade reverted.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.progressBarCompleted))]
    public static class WorkbenchCompletionPatch
    {
        private static readonly AccessTools.FieldRef<Player, CraftingRecipes> CraftedRecipes =
            AccessTools.FieldRefAccess<Player, CraftingRecipes>("currentlyCraftedRecipes");
        private static readonly AccessTools.FieldRef<Player, CraftingRecipes.Recipe> CraftedRecipe =
            AccessTools.FieldRefAccess<Player, CraftingRecipes.Recipe>("currentlyCraftedRecipe");

        internal struct State
        {
            public bool Active;
            public Inventory Pile;
            public string Product;
            public int ProductBefore;
            public InvItemClass Repaired;
            public float DurabilityBefore;
            public InvItemClass Upgraded;
            public ItemUpgrade Upgrade;
        }

        private static void Prefix(Player __instance, out State __state)
        {
            __state = default;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected || __instance != Player.Instance)
                return;
            Inventory pile = __instance.openedItemInventory != null && __instance.openedItemInventory.isWorkbench
                ? __instance.openedItemInventory2
                : null;
            if (pile == null)
                return; // nothing shared was drawn on

            if (__instance.crafting)
            {
                CraftingRecipes.Recipe recipe = CraftedRecipe(__instance);
                CraftingRecipes recipes = CraftedRecipes(__instance);
                if (recipe != null)
                {
                    recipe.refresh();
                    if (!recipe.requirementsMet())
                    {
                        __instance.crafting = false;
                        ModRuntime.LegacyInfo("[Workbench] craft dropped — its ingredients left the pile meanwhile");
                    }
                    else if (recipes != null)
                    {
                        InvItem inv = recipes.GetComponent<InvItem>();
                        __state.Product = inv != null ? inv.type : null;
                        __state.ProductBefore = CountWithPile(__state.Product);
                    }
                }
            }
            if (__instance.repairingItem && !InvItemClass.isNull(__instance.itemBeingRepaired))
            {
                RepairRequirements rr = __instance.itemBeingRepaired.baseClass != null
                    ? __instance.itemBeingRepaired.baseClass.GetComponent<RepairRequirements>()
                    : null;
                if (rr != null)
                {
                    rr.refresh();
                    if (!rr.requirementsMet())
                    {
                        __instance.repairingItem = false;
                        ModRuntime.LegacyInfo("[Workbench] repair dropped — its materials left the pile meanwhile");
                    }
                    else
                    {
                        __state.Repaired = __instance.itemBeingRepaired;
                        __state.DurabilityBefore = __instance.itemBeingRepaired.durability;
                    }
                }
            }
            if (__instance.upgradingItem && __instance.itemUpgradeBeingAdded != null && !InvItemClass.isNull(__instance.itemBeingUpgraded))
            {
                __instance.itemUpgradeBeingAdded.refresh();
                if (!__instance.itemUpgradeBeingAdded.requirementsMet())
                {
                    __instance.upgradingItem = false;
                    ModRuntime.LegacyInfo("[Workbench] upgrade dropped — its materials left the pile meanwhile");
                }
                else
                {
                    __state.Upgraded = __instance.itemBeingUpgraded;
                    __state.Upgrade = __instance.itemUpgradeBeingAdded;
                }
            }
            __state.Pile = pile;
            __state.Active = __state.Product != null || __state.Repaired != null || __state.Upgraded != null;
        }

        private static void Postfix(State __state)
        {
            if (!__state.Active || __state.Pile == null)
                return;
            int produced = __state.Product != null ? CountWithPile(__state.Product) - __state.ProductBefore : 0;
            WorkbenchUndo.Note(__state.Pile.transform.position, __state.Product, produced,
                __state.Repaired, __state.DurabilityBefore, __state.Upgraded, __state.Upgrade);
        }

        internal static int CountWithPile(string type)
        {
            if (string.IsNullOrEmpty(type) || Player.Instance == null)
                return 0;
            int n = 0;
            foreach (InvItemClass it in Player.Instance.Inventory.getAllItemsInPlayer(type, includeAdditionalInventory: true))
            {
                if (!InvItemClass.isNull(it))
                    n += it.baseClass != null && it.baseClass.stackable ? it.amount : 1;
            }
            return n;
        }
    }

    /// <summary>The last workbench completion that drew on the shared pile, kept briefly for a host refusal.</summary>
    internal static class WorkbenchUndo
    {
        private const float WindowSec = 3f;

        private static bool _armed;          // reset-in: Reset
        private static Vector3 _pilePos;     // reset-in: Reset
        private static float _at;            // reset-in: Reset
        private static string _product;      // reset-in: Reset
        private static int _produced;        // reset-in: Reset
        private static InvItemClass _repaired; // reset-in: Reset
        private static float _durabilityBefore; // reset-in: Reset
        private static InvItemClass _upgraded; // reset-in: Reset
        private static ItemUpgrade _upgrade;   // reset-in: Reset

        internal static void Reset()
        {
            _armed = false;
            _pilePos = Vector3.zero;
            _at = 0f;
            _produced = 0;
            _durabilityBefore = 0f;
            _product = null;
            _repaired = null;
            _upgraded = null;
            _upgrade = null;
        }

        internal static void Note(Vector3 pilePos, string product, int produced, InvItemClass repaired, float durabilityBefore,
            InvItemClass upgraded, ItemUpgrade upgrade)
        {
            _armed = true;
            _pilePos = pilePos;
            _at = Time.unscaledTime;
            _product = product;
            _produced = produced;
            _repaired = repaired;
            _durabilityBefore = durabilityBefore;
            _upgraded = upgraded;
            _upgrade = upgrade;
        }

        /// <summary>
        /// The host refused a removal from this pile that was not a take: the last completion drew
        /// on materials that were already gone. Undo it once. False when no completion matches.
        /// </summary>
        internal static bool TryUndo(Vector3 containerPos)
        {
            if (!_armed || Time.unscaledTime - _at > WindowSec || (containerPos - _pilePos).sqrMagnitude > 1f)
                return false;
            _armed = false;
            Player p = Player.Instance;
            if (p == null)
                return true;
            if (!string.IsNullOrEmpty(_product) && _produced > 0)
            {
                p.Inventory.removeItemAmountFromPlayer(_product, _produced, includeAdditionalInventory: true);
                ModRuntime.LegacyInfo($"[Workbench] host refused the materials — took back {_product} x{_produced}");
            }
            if (!InvItemClass.isNull(_repaired))
            {
                _repaired.durability = _durabilityBefore;
                _repaired.refresh();
                ModRuntime.LegacyInfo("[Workbench] host refused the materials — repair undone");
            }
            if (!InvItemClass.isNull(_upgraded) && _upgrade != null && _upgraded.upgrades.Remove(_upgrade))
            {
                _upgraded.refresh();
                p.refreshRecipes();
                ModRuntime.LegacyInfo("[Workbench] host refused the materials — upgrade undone");
            }
            Reset();
            return true;
        }
    }
}
