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
    internal sealed class PlayerHeldLightApplyNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal PlayerHeldLightApplyNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
        }

        /// <summary>
        /// Continuous held lights from PlayerState (~30 Hz): flare B+ + flashlight stream.
        /// Flare is parented to the proxy with a hand local offset (not world body center).
        /// Sole owner for held flare. Removes event-path ItemLight components to
        /// prevent duplicate lights.
        /// </summary>
        internal void HandleRemoteContinuousLights(PlayerStateMessage state, int playerId = -1)
        {
            if (playerId < 0) playerId = _net.CurrentReceivePlayerId;
            if (playerId < 0)
            {
                foreach (int id in _net.EnumeratePeerIds())
                {
                    playerId = id;
                    break;
                }
                if (playerId < 0)
                    playerId = 1;
            }

            HandleRemoteFlareLight(state, playerId);
            HandleRemoteFlashlightStream(state, playerId);
        }

        internal void HandleRemoteFlareLight(PlayerStateMessage state, int playerId)
        {
            // Flare or match continuous held burn light (LightFlagFlare).
            bool flagOn = (state.LightFlags & PlayerStateMessage.LightFlagFlare) != 0
                || state.FlareActive || state.MatchActive;
            bool hasRemain = (state.LightFlags & PlayerStateMessage.LightFlagRemain) != 0;
            byte remain01 = state.HeldLightRemain01;

            // V5: remain 0 → soft 2s fade (not hard cut). While fading, ignore re-ON spam.
            if (_net.RemotePlayers.TryGetValue(playerId, out var fadeCheck)
                && fadeCheck.FlareLight != null
                && Sync.WorldPhysicsSyncService.IsThrownLightFading(fadeCheck.FlareLight))
                return;

            // Soft extinguish when remain hits 0 while still flagged, or flag drops after burn.
            bool wantSoftOff = hasRemain && remain01 == 0 && flagOn;
            bool heldOn = flagOn && !(hasRemain && remain01 == 0);

            if (heldOn)
            {
                // Continuous flare or match state owns the light. Remove the
                // event-path item light.
                DestroyRemoteItemLight(playerId);

                RemotePlayerProxy proxy = _net.GetProxy(playerId);
                var remoteState = _net.GetOrCreateState(playerId);
                Vector3 localOff = new Vector3(state.FlareLocalX, state.FlareLocalY, state.FlareLocalZ);
                bool rising = remoteState.FlareLight == null;

                if (state.FlareHasItemType && !string.IsNullOrEmpty(state.FlareItemType))
                    remoteState.FlareItemType = state.FlareItemType;

                // V5: last ~2s of ~80s life ≈ remain01 < 6 (2/80*255). Scale intensity.
                const byte remainFadeThreshold = 6;
                float remainScale = 1f;
                if (hasRemain && remain01 > 0 && remain01 < remainFadeThreshold)
                    remainScale = remain01 / (float)remainFadeThreshold;

                if (rising)
                {
                    string kind = state.MatchActive ? "match" : "flare";
                    ModLog.Event(LogCat.World,
                        $"[LightSync] remote {kind} ON p{playerId} type={remoteState.FlareItemType ?? "?"} remain01={remain01} localOff=({localOff.x:F1},{localOff.y:F1},{localOff.z:F1})");

                    // Single GO: full flare prefab (stick + lightFlare + Light2D + particles).
                    // Old path spawned Light2D clone AND full FX with lightFlare → double glares.
                    // Matches stay light-only (no stick prefab).
                    if (state.MatchActive)
                        SpawnRemoteMatchLight(playerId, remoteState, proxy, localOff, state, remainScale);
                    else
                        SpawnRemoteHeldFlare(playerId, remoteState, proxy, localOff, state, remainScale);
                }
                else if (remoteState.FlareLight != null)
                {
                    if (proxy != null && remoteState.FlareLight.transform.parent != proxy.transform)
                    {
                        remoteState.FlareLight.transform.SetParent(proxy.transform, false);
                        remoteState.FlareLight.transform.localRotation = Quaternion.identity;
                    }
                    remoteState.FlareLight.transform.localPosition = localOff;
                    // FlareFx is unused for unified held flare (same GO); keep in sync if legacy.
                    if (remoteState.FlareFx != null && remoteState.FlareFx != remoteState.FlareLight)
                        remoteState.FlareFx.transform.localPosition = localOff;

                    // Match: position only on stream ticks. Re-applying intensity every packet
                    // recreated SP flicker on the peer. Flare still needs live radius/intensity.
                    if (state.MatchActive)
                    {
                        if (remainScale < 1f)
                            ApplyRemoteHeldLightParams(remoteState.FlareLight, state, remainScale);
                    }
                    else
                    {
                        ApplyRemoteHeldLightParams(remoteState.FlareLight, state, remainScale);
                    }
                }
            }
            else if (_net.RemotePlayers.TryGetValue(playerId, out var existingState)
                     && (existingState.FlareLight != null || existingState.FlareFx != null))
            {
                // V5: soft fade only on hold-to-burnout (remain hit 0). Throw/switch = hard clear
                // (V1 throw mutex also hard-clears before projectile spawn).
                if (wantSoftOff)
                {
                    StartRemoteHeldFlareFade(playerId, existingState);
                }
                else
                {
                    ModLog.Event(LogCat.World, $"[LightSync] remote held burn light OFF p{playerId}");
                    DestroyRemoteFlareLight(playerId);
                }
            }
        }

        /// <summary>V5: 2s fade on held remote flare light+FX then destroy.</summary>
        internal void StartRemoteHeldFlareFade(int playerId, RemotePlayerState state)
        {
            if (state == null) return;
            GameObject lightGo = state.FlareLight;
            GameObject fxGo = state.FlareFx;
            if (lightGo == null && fxGo == null) return;
            if (lightGo != null && Sync.WorldPhysicsSyncService.IsThrownLightFading(lightGo))
                return;

            ModLog.Event(LogCat.World, $"[LightSync] remote held burn soft-fade p{playerId}");
            // Detach ownership so continuous path won't keep updating; fade owns GOs.
            state.FlareLight = null;
            state.FlareFx = null;
            state.FlareItemType = null;

            if (lightGo != null)
                Sync.WorldPhysicsSyncService.BeginThrownLightFade(
                    lightGo, Sync.WorldPhysicsSyncService.FlareBurnoutFadeSec, fxGo);
            else if (fxGo != null)
                Sync.WorldPhysicsSyncService.BeginThrownLightFade(
                    fxGo, Sync.WorldPhysicsSyncService.FlareBurnoutFadeSec);
        }

        /// <summary>
        /// Held flare: one prefab under proxy (sprite + particles + Light2D + Flare flicker).
        /// Network owns die; no second light/FX clone (client-only glares were double lightFlare).
        /// </summary>
        internal void SpawnRemoteHeldFlare(int playerId, RemotePlayerState remoteState, RemotePlayerProxy proxy,
            Vector3 localOff, PlayerStateMessage state, float remainScale)
        {
            if (proxy == null) return;
            // Clear any legacy dual GOs
            if (remoteState.FlareFx != null)
            {
                UnityEngine.Object.DestroyImmediate(remoteState.FlareFx);
                remoteState.FlareFx = null;
            }
            if (remoteState.FlareLight != null)
            {
                UnityEngine.Object.DestroyImmediate(remoteState.FlareLight);
                remoteState.FlareLight = null;
            }

            GameObject prefab = ResolveFlareItemPrefab(remoteState.FlareItemType, match: false);
            if (prefab == null)
            {
                ModLog.Warn(LogCat.World, $"[LightSync] held flare prefab missing p{playerId} type={remoteState.FlareItemType}");
                SpawnRemoteMatchLight(playerId, remoteState, proxy, localOff, state, remainScale);
                return;
            }

            GameObject go = UnityEngine.Object.Instantiate(prefab);
            go.name = $"RemoteHeldFlare_P{playerId}";
            go.transform.SetParent(proxy.transform, false);
            go.transform.localPosition = localOff;
            go.transform.localRotation = Quaternion.identity;

            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
                UnityEngine.Object.Destroy(rb);
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                col.enabled = false;
            foreach (var ti in go.GetComponentsInChildren<ThrownItem>(true))
                UnityEngine.Object.Destroy(ti);
            foreach (var ex in go.GetComponentsInChildren<Explodes>(true))
                UnityEngine.Object.Destroy(ex);
            foreach (var ad in go.GetComponentsInChildren<AutoDestroyParticles>(true))
                UnityEngine.Object.Destroy(ad);

            // Keep Flare for flicker/lightFlare rotation; network owns burnout.
            Sync.WorldPhysicsSyncService.ClaimFlareLifetime(go);

            // Exactly one active Light2D (prefab may nest extras).
            Light2D primary = null;
            Flare flComp = go.GetComponent<Flare>() ?? go.GetComponentInChildren<Flare>(true);
            if (flComp != null && flComp.light2D != null)
                primary = flComp.light2D;
            foreach (var lt in go.GetComponentsInChildren<Light2D>(true))
            {
                if (primary == null)
                    primary = lt;
                else if (lt != primary)
                {
                    lt.lightsPlayer = false;
                    lt.updateGraph = false;
                    lt.gameObject.SetActive(false);
                }
            }
            if (primary != null)
            {
                if (!primary.gameObject.activeSelf)
                    primary.gameObject.SetActive(true);
                primary.lightsPlayer = true;
                primary.updateGraph = true;
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null && !ctrl.logicLights.Contains(primary))
                    ctrl.logicLights.Add(primary);
            }

            LanNetworkManager.EnsureEmitterVisible(go);
            LanNetworkManager.PlayAllParticleSystems(go);
            LanNetworkManager.SetupParticleSorting(go);
            ApplyRemoteHeldLightParams(go, state, remainScale);

            remoteState.FlareLight = go;
            remoteState.FlareFx = null;
            ModLog.Event(LogCat.World, $"[LightSync] held flare unified p{playerId} type={remoteState.FlareItemType}");
        }

        /// <summary>Match: small Light2D only (no stick prefab).</summary>
        internal void SpawnRemoteMatchLight(int playerId, RemotePlayerState remoteState, RemotePlayerProxy proxy,
            Vector3 localOff, PlayerStateMessage state, float remainScale)
        {
            if (proxy == null) return;
            if (remoteState.FlareFx != null)
            {
                UnityEngine.Object.DestroyImmediate(remoteState.FlareFx);
                remoteState.FlareFx = null;
            }
            if (remoteState.FlareLight != null)
            {
                UnityEngine.Object.DestroyImmediate(remoteState.FlareLight);
                remoteState.FlareLight = null;
            }

            Light2D template = TryResolveFlareLightTemplate(remoteState.FlareItemType, match: true);
            GameObject flareLight;
            if (template != null)
            {
                flareLight = UnityEngine.Object.Instantiate(template.gameObject);
                flareLight.name = $"RemoteMatchLight_P{playerId}";
                foreach (var fl in flareLight.GetComponentsInChildren<Flare>(true))
                    UnityEngine.Object.Destroy(fl);
                foreach (var rb in flareLight.GetComponentsInChildren<Rigidbody>(true))
                    UnityEngine.Object.Destroy(rb);
                foreach (var col in flareLight.GetComponentsInChildren<Collider>(true))
                    col.enabled = false;
            }
            else
            {
                flareLight = new GameObject($"RemoteMatchLight_P{playerId}");
                var created = flareLight.AddComponent<Light2D>();
                if (created.LightMaterial == null)
                    created.LightMaterial = Resources.Load("RadialLight") as Material;
            }

            flareLight.transform.SetParent(proxy.transform, false);
            flareLight.transform.localPosition = localOff;
            flareLight.transform.localRotation = Quaternion.identity;
            if (!flareLight.activeSelf)
                flareLight.SetActive(true);

            Light2D light = flareLight.GetComponentInChildren<Light2D>(true);
            if (light != null)
            {
                if (!light.gameObject.activeSelf)
                    light.gameObject.SetActive(true);
                light.lightsPlayer = true;
                light.updateGraph = true;
                // Defaults so peer sees glow even if first packet lacked FlareParams.
                if (light.LightRadius <= 0f)
                    light.LightRadius = state.FlareRadius > 0f ? state.FlareRadius : 180f;
                if (light.LightIntensity <= 0f)
                    light.LightIntensity = state.FlareIntensity > 0f ? state.FlareIntensity : 0.85f;
                if (light.LightMaterial == null)
                    light.LightMaterial = Resources.Load("RadialLight") as Material;
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null && !ctrl.logicLights.Contains(light))
                    ctrl.logicLights.Add(light);
            }
            ApplyRemoteHeldLightParams(flareLight, state, remainScale);
            remoteState.FlareLight = flareLight;
            remoteState.FlareFx = null;
            ModLog.Event(LogCat.World,
                $"[LightSync] held match light p{playerId} type={remoteState.FlareItemType ?? "match"} r={(light != null ? light.LightRadius : 0f):F0}");
        }

        private static void ApplyRemoteHeldLightParams(GameObject root, PlayerStateMessage state, float remainScale)
        {
            if (root == null) return;
            Light2D light = null;
            Flare fl = root.GetComponent<Flare>() ?? root.GetComponentInChildren<Flare>(true);
            if (fl != null && fl.light2D != null)
                light = fl.light2D;
            if (light == null)
                light = root.GetComponentInChildren<Light2D>(true);
            if (light == null) return;

            // Match packets always carry defaults on TX; apply even without FlareHasParams
            // so a late peer (params bit only on dirty ticks) still gets a visible radius.
            float radius = state.FlareHasParams && state.FlareRadius > 0f
                ? state.FlareRadius
                : (state.MatchActive ? (state.FlareRadius > 0f ? state.FlareRadius : 180f) : 0f);
            if (radius > 0f)
                light.LightRadius = radius;

            if (state.FlareHasParams || state.MatchActive)
            {
                float baseI = state.FlareIntensity > 0f ? state.FlareIntensity : (state.MatchActive ? 0.85f : 1f);
                if (fl == null)
                    light.LightIntensity = baseI * remainScale;
                else if (remainScale < 1f)
                    light.LightIntensity = Mathf.Min(light.LightIntensity, baseI) * remainScale;

                if (state.FlareHasParams
                    || state.FlareColorR + state.FlareColorG + state.FlareColorB > 0.01f)
                {
                    light.LightColor = new Color(
                        state.FlareColorR > 0f || state.FlareHasParams ? state.FlareColorR : 1f,
                        state.FlareColorG > 0f || state.FlareHasParams ? state.FlareColorG : 0.65f,
                        state.FlareColorB > 0f || state.FlareHasParams ? state.FlareColorB : 0.2f);
                }
                else if (state.MatchActive)
                {
                    light.LightColor = new Color(1f, 0.65f, 0.2f);
                }
            }
            else if (remainScale < 1f && fl == null)
            {
                light.LightIntensity = Mathf.Max(light.LightIntensity, 0.01f) * remainScale;
            }
        }

        private static GameObject ResolveFlareItemPrefab(string itemType, bool match)
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
            return itemDef != null ? itemDef.item as GameObject : null;
        }

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
