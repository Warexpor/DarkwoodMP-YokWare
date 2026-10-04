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

        private static void Prefix(Controller __instance)
        {
            Stash.Clear();
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || __instance.logicLights == null)
                return;
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null)
                    continue;
                Transform flashT = proxy.transform.Find("Flashlight");
                Light2D lt = flashT != null ? flashT.GetComponent<Light2D>() : null;
                if (lt == null || !__instance.logicLights.Contains(lt))
                    continue;
                lt.unlightGraphNodes();
                __instance.logicLights.Remove(lt);
                Stash.Add(lt);
            }
        }

        private static void Finalizer(Controller __instance)
        {
            if (Stash.Count == 0)
                return;
            bool lightNow = Player.Instance != null && !Player.Instance.inEpilogue
                && !Singleton<OutsideLocations>.Instance.loading
                && !Singleton<Dreams>.Instance.switchingDream && !Singleton<Dreams>.Instance.dreaming;
            for (int i = 0; i < Stash.Count; i++)
            {
                Light2D lt = Stash[i];
                if (lt == null)
                    continue;
                if (!__instance.logicLights.Contains(lt))
                    __instance.logicLights.Add(lt);
                if (lightNow && lt.gameObject.activeInHierarchy)
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
