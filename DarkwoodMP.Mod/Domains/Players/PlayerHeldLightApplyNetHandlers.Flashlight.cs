using System;
using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// RX: apply continuous held lights from PlayerState (flare/match/flashlight).
    /// </summary>
    internal sealed partial class PlayerHeldLightApplyNetHandlers
    {

        internal void HandleRemoteFlashlightStream(PlayerStateMessage state, int playerId)
        {
            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            if (proxy == null) return;

            Transform flashT = proxy.transform.Find("Flashlight");
            if (flashT == null)
            {
                // Guarantee cone child so stream can apply (proxy may strip lights).
                var flashGo = new GameObject("Flashlight");
                flashGo.transform.SetParent(proxy.transform, false);
                flashGo.transform.localPosition = Vector3.zero;
                var created = flashGo.AddComponent<Light2D>();
                if (created.LightMaterial == null)
                    created.LightMaterial = Resources.Load("RadialLight") as Material;
                created.LightRadius = 400f;
                created.LightIntensity = 1f;
                created.LightColor = new Color(0.3f, 0.3f, 0.3f, 0f);
                flashGo.SetActive(false);
                flashT = flashGo.transform;
            }

            if (state.FlashlightActive)
            {
                bool wasOff = !flashT.gameObject.activeSelf;
                flashT.gameObject.SetActive(true);
                if (wasOff && Config.ModConfig.IsVerboseLightSync)
                    ModRuntime.LegacyInfo($"[LightSync] remote flashlight ON p{playerId}");

                // Aim: streamed Flashlight yaw (SP cone direction).
                if ((state.LightFlags & PlayerStateMessage.LightFlagFlashAim) != 0)
                    flashT.rotation = Quaternion.Euler(90f, state.FlashAimY, 0f);

                Light2D lt = flashT.GetComponent<Light2D>();
                if (lt != null)
                {
                    if (state.FlashHasParams)
                    {
                        if (state.FlashRadius > 0f)
                            lt.LightRadius = state.FlashRadius;
                        if (state.FlashIntensity > 0f)
                            lt.LightIntensity = state.FlashIntensity;
                        lt.LightColor = new Color(state.FlashColorR, state.FlashColorG, state.FlashColorB, 0f);
                    }
                    lt.lightsPlayer = true;
                    lt.updateGraph = true;
                    var ctrl = Singleton<Controller>.Instance;
                    if (ctrl != null && !ctrl.logicLights.Contains(lt))
                        ctrl.logicLights.Add(lt);
                }
            }
            else
            {
                if (flashT.gameObject.activeSelf)
                {
                    if (Config.ModConfig.IsVerboseLightSync)
                        ModRuntime.LegacyInfo($"[LightSync] remote flashlight OFF p{playerId}");
                    Light2D lt = flashT.GetComponent<Light2D>();
                    if (lt != null)
                    {
                        lt.unlightGraphNodes();
                        var ctrl = Singleton<Controller>.Instance;
                        if (ctrl != null)
                            ctrl.logicLights.Remove(lt);
                    }
                    flashT.gameObject.SetActive(false);
                }
            }
        }

        /// <summary>Light2D template from flare/match item prefab (not PlayerLightDot).</summary>
        private static Light2D TryResolveFlareLightTemplate(string itemType, bool match)
        {
            var db = Singleton<ItemsDatabase>.Instance;
            if (db == null) return null;

            InvItem itemDef = null;
            if (!string.IsNullOrEmpty(itemType) && db.hasItem(itemType))
                itemDef = db.getItem(itemType, instantiate: false);
            if (itemDef == null)
            {
                string[] candidates = match
                    ? new[] { "match", "matchstick", "Match", "Matchstick" }
                    : new[] { "flare", "Flare", "flare_red", "redFlare" };
                for (int i = 0; i < candidates.Length && itemDef == null; i++)
                {
                    if (db.hasItem(candidates[i]))
                        itemDef = db.getItem(candidates[i], instantiate: false);
                }
            }
            GameObject prefab = itemDef != null ? itemDef.item as GameObject : null;
            if (prefab == null) return null;

            Flare fl = prefab.GetComponent<Flare>() ?? prefab.GetComponentInChildren<Flare>(true);
            if (fl != null && fl.light2D != null)
                return fl.light2D;
            return prefab.GetComponentInChildren<Light2D>(true);
        }

        internal void DestroyRemoteFlareLight(int playerId)
        {
            if (!_net.RemotePlayers.TryGetValue(playerId, out var state))
                return;
            var flareLight = state.FlareLight;
            if (flareLight != null)
            {
                foreach (var fl in flareLight.GetComponentsInChildren<Light2D>(true))
                {
                    fl.unlightGraphNodes();
                    var ctrl = Singleton<Controller>.Instance;
                    if (ctrl != null)
                        ctrl.logicLights.Remove(fl);
                }
                UnityEngine.Object.DestroyImmediate(flareLight);
                state.FlareLight = null;
            }
            if (state.FlareFx != null)
            {
                UnityEngine.Object.DestroyImmediate(state.FlareFx);
                state.FlareFx = null;
            }
            state.FlareItemType = null;
        }

        internal void DestroyRemoteItemLight(int playerId)
        {
            if (!_net.RemotePlayers.TryGetValue(playerId, out var state))
                return;
            var lightGo = state.ItemLight;
            if (lightGo == null) return;
            Light2D lt = lightGo.GetComponent<Light2D>();
            if (lt != null)
            {
                lt.unlightGraphNodes();
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null)
                    ctrl.logicLights.Remove(lt);
            }
            UnityEngine.Object.DestroyImmediate(lightGo);
            state.ItemLight = null;
        }
    }
}
