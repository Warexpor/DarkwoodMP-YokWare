using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Vanilla <c>Controller.updateLogicLights</c> lights the path nodes of any logic light named
    /// "Flashlight" only inside the LOCAL player's <c>flashlightCollider</c> bounds. A peer's
    /// flashlight on a proxy carries that name, so it lit nodes in the local player's beam (or
    /// none at all with the local flashlight off): shadows and shadow armor ignored a peer's beam.
    /// Each proxy flashlight is taken out of vanilla's pass and lit with its own beam box, sized
    /// as vanilla <c>Player.processFlashlight</c> sizes the collider from the cone radius.
    /// </summary>
    [HarmonyPatch(typeof(Controller), nameof(Controller.updateLogicLights))]
    public static class RemoteFlashlightNodesPatch
    {
        private static readonly List<Light2D> Stash = new List<Light2D>(4); // process-scoped: call-scoped, emptied by the Finalizer

        /// <summary>Lights of a dream pad this player is no longer in, held out of the pass.</summary>
        private static readonly List<Light2D> PadStash = new List<Light2D>(8); // process-scoped: call-scoped, emptied by the Finalizer

        private static readonly Dictionary<RemotePlayerProxy, Light2D> FlashOf = new Dictionary<RemotePlayerProxy, Light2D>(); // process-scoped: keys checked for null on read, pruned when it grows

        /// <summary>The proxy's "Flashlight" Light2D, found once (this runs every frame).</summary>
        private static Light2D FlashlightOf(RemotePlayerProxy proxy)
        {
            if (FlashOf.TryGetValue(proxy, out Light2D lt) && lt != null)
                return lt;
            Transform flashT = proxy.transform.Find("Flashlight");
            lt = flashT != null ? flashT.GetComponent<Light2D>() : null;
            if (lt == null)
                return null;
            if (FlashOf.Count >= 16)
                FlashOf.Clear();
            FlashOf[proxy] = lt;
            return lt;
        }

        private static void Prefix(Controller __instance)
        {
            Stash.Clear();
            PadStash.Clear();
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || __instance.logicLights == null)
                return;
            StashLeftDreamPadLights(__instance);
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null)
                    continue;
                Light2D lt = FlashlightOf(proxy);
                if (lt == null || !__instance.logicLights.Remove(lt))
                    continue;
                lt.unlightGraphNodes();
                Stash.Add(lt);
            }
        }

        /// <summary>
        /// Vanilla's dream end removes the dream's path graph at once and destroys the dream pad 4 s
        /// later; a co-op host awake while the others dream keeps the pad longer. Out of the dream,
        /// the pad's logic lights went through the overworld node lookup against graphs that are
        /// gone (<c>AstarPath.GetNodesAround</c>: a NullReferenceException with its stack trace for
        /// every light, every pass, 12 after the home dream). Nothing on the pad is lit for a
        /// player who is not in it, so its lights are held out until the pad is gone or entered.
        /// </summary>
        private static void StashLeftDreamPadLights(Controller ctrl)
        {
            Dreams dreams = Singleton<Dreams>.Instance;
            Location pad = dreams != null ? dreams.dreamLocation : null;
            if (pad == null || dreams.dreaming || dreams.switchingDream)
                return;
            Transform padT = pad.transform;
            List<Light2D> lights = ctrl.logicLights;
            for (int i = lights.Count - 1; i >= 0; i--)
            {
                Light2D lt = lights[i];
                if (lt == null || !lt.transform.IsChildOf(padT))
                    continue;
                lt.unlightGraphNodes();
                lights.RemoveAt(i);
                PadStash.Add(lt);
            }
        }

        private static void Finalizer(Controller __instance)
        {
            for (int i = 0; i < PadStash.Count; i++)
            {
                if (PadStash[i] != null && !__instance.logicLights.Contains(PadStash[i]))
                    __instance.logicLights.Add(PadStash[i]);
            }
            PadStash.Clear();
            if (Stash.Count == 0)
                return;
            bool lightNow = Player.Instance != null && !Player.Instance.inEpilogue
                && !Singleton<OutsideLocations>.Instance.loading
                && !Singleton<Dreams>.Instance.switchingDream && !Singleton<Dreams>.Instance.dreaming;
            // A peer still in a dream this player woke up from: the pad's path graph went with the
            // dream's end here (StashLeftDreamPadLights), so its beam lights nothing; looked up, it
            // threw in AstarPath.GetNodesAround every pass until that peer woke too.
            Transform leftPad = Sync.DreamSyncManager.GetLoadedDreamPad();
            for (int i = 0; i < Stash.Count; i++)
            {
                Light2D lt = Stash[i];
                if (lt == null)
                    continue;
                if (!__instance.logicLights.Contains(lt))
                    __instance.logicLights.Add(lt);
                if (lightNow && lt.gameObject.activeInHierarchy
                    && (leftPad == null || !Sync.DreamSyncManager.IsInPadSlot(leftPad, lt.transform.position)))
                    lt.lightGraphNodes(BeamBounds(lt));
            }
            Stash.Clear();
        }

        /// <summary>
        /// Vanilla's beam box has side s = clamp(cursor distance, 100, 1000) and reaches 2s ahead
        /// (cursor distance = cone radius / 2.5). This box holds it in every facing; the cone mesh
        /// test inside <c>lightGraphNodes</c> keeps only the nodes in front.
        /// </summary>
        private static Bounds BeamBounds(Light2D flash)
        {
            float side = Mathf.Clamp(flash.LightRadius / 2.5f, 100f, 1000f);
            return new Bounds(flash.transform.position, new Vector3(4f * side, 2000f, 4f * side));
        }
    }
}
