using HarmonyLib;

namespace DWMPHorde.Patches
{
    // The exclusive workbench lock is currently disabled so both players may
    // use the bench. The types and handlers remain as no-ops because the
    // message ID is part of the wire contract.

    /// <summary>Disabled exclusive workbench lock; opening always succeeds.</summary>
    [HarmonyPatch(typeof(Workbench), "open")]
    public static class WorkbenchLockOpenPatch
    {
        private static bool Prefix(Workbench __instance, ref bool __state)
        {
            // Feature disabled; never block open or claim.
            __state = false;
            return true;
        }

        private static void Postfix(Workbench __instance, bool __state)
        {
            // Feature disabled; no claim or release on open.
        }
    }

    /// <summary>PARKED: was release-on-Inventory.hide for workbench lock.</summary>
    [HarmonyPatch(typeof(Inventory), "hide")]
    public static class WorkbenchLockInventoryHidePatch
    {
        private static void Prefix(Inventory __instance)
        {
            // Feature disabled.
        }

        internal static Workbench ResolveWorkbench(Inventory inv)
        {
            if (inv == null) return null;
            if (inv.workbench != null)
                return inv.workbench;
            if (!inv.isWorkbench) return null;
            Workbench wb = inv.GetComponent<Workbench>();
            if (wb != null) return wb;
            if (inv.transform.parent != null)
            {
                wb = inv.transform.parent.GetComponent<Workbench>();
                if (wb != null) return wb;
                wb = inv.transform.parent.GetComponentInParent<Workbench>();
            }
            return wb;
        }
    }

    [HarmonyPatch(typeof(Player), "closeInventory")]
    public static class WorkbenchLockPlayerCloseInventoryPatch
    {
        private static void Prefix(Player __instance)
        {
            // Feature disabled.
        }
    }

    [HarmonyPatch(typeof(Workbench), "close")]
    public static class WorkbenchLockClosePatch
    {
        private static void Prefix(Workbench __instance)
        {
            // Feature disabled.
        }
    }

    internal static class WorkbenchLockHelpers
    {
        internal static void ReleaseLocal(Workbench wb)
        {
            // Feature disabled; no-op.
        }
    }
}
