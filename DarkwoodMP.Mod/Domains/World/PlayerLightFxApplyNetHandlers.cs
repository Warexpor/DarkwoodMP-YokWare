using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>RX: PlayerLightState apply + pending stash for late proxy create.</summary>
    internal sealed class PlayerLightFxApplyNetHandlers
    {
        private readonly LanNetworkManager _net;

        private readonly Dictionary<int, PlayerLightStateMessage> _pendingPlayerLights =
            new Dictionary<int, PlayerLightStateMessage>();

        internal Dictionary<int, PlayerLightStateMessage> PendingPlayerLights => _pendingPlayerLights;

        internal PlayerLightFxApplyNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearPendingPlayerLights()
        {
            _pendingPlayerLights.Clear();
        }

        internal void ClearPendingPlayerLightsFor(int playerId)
        {
            if (playerId > 0)
                _pendingPlayerLights.Remove(playerId);
        }

        internal static bool IsAmbientLanternType(string type)
        {
            if (string.IsNullOrEmpty(type)) return true;
            return type.IndexOf("lantern", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal void HandlePlayerLightState(PlayerLightStateMessage msg)
        {
            int playerId = _net.CurrentReceivePlayerId;
            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            if (proxy == null)
            {
                // Early handshake / pre-proxy: stash and apply when proxy is created.
                if (playerId > 0)
                    PendingPlayerLights[playerId] = msg;
                else if (ModRuntime.VerboseLogging)
                {
                    ModLog.Event(LogCat.World,
                        $"[Light] RX drop p{playerId} proxy=null on={msg.LightOn} type={msg.ItemType ?? "-"} flash={msg.IsFlashlight} emit={msg.HasLightEmitter}");
                }
                return;
            }

            var remoteState = _net.GetOrCreateState(playerId);
            remoteState.LastLight = msg;

            // Normalize ambient-only type: empty ↔ "lantern" must not re-apply (TX thrash critical).
            if (msg.HasAmbientLight && string.IsNullOrEmpty(msg.ItemType)
                && !msg.HasLightEmitter && !msg.IsFlashlight && !msg.HasItemLight)
                msg.ItemType = "lantern";

            // Skip identical re-applies (onActivate + onDoneSwitch both fire → re-spawn thrash).
            string appliedType = remoteState.AppliedLightItemType ?? "";
            string nextType = msg.ItemType ?? "";
            bool typeSame = string.Equals(appliedType, nextType, StringComparison.Ordinal)
                || (msg.HasAmbientLight && remoteState.AppliedAmbient
                    && !msg.HasLightEmitter && !remoteState.AppliedEmitter
                    && IsAmbientLanternType(appliedType) && IsAmbientLanternType(nextType));
            bool sameAsApplied =
                remoteState.AppliedLightOn == msg.LightOn
                && remoteState.AppliedFlash == msg.IsFlashlight
                && remoteState.AppliedEmitter == msg.HasLightEmitter
                && remoteState.AppliedItemLight == msg.HasItemLight
                && remoteState.AppliedAmbient == msg.HasAmbientLight
                && typeSame
                && Mathf.Abs(remoteState.AppliedLightRadius - msg.LightRadius) < 0.5f;
            if (sameAsApplied)
            {
                if (Config.ModConfig.IsVerboseLightSync)
                    ModRuntime.LegacyInfo($"[Light] RX skip-noop p{playerId} type={msg.ItemType}");
                return;
            }

            string prev = $"on={remoteState.AppliedLightOn} type={remoteState.AppliedLightItemType ?? "-"} flash={remoteState.AppliedFlash} emit={remoteState.AppliedEmitter}";
            string next = $"on={msg.LightOn} type={msg.ItemType ?? "-"} flash={msg.IsFlashlight} emit={msg.HasLightEmitter} itemLight={msg.HasItemLight} ambient={msg.HasAmbientLight} r={msg.LightRadius:F0}";
            ModLog.Event(LogCat.World, $"[Light] RX p{playerId} {prev} → {next}");

            remoteState.AppliedLightOn = msg.LightOn;
            remoteState.AppliedFlash = msg.IsFlashlight;
            remoteState.AppliedEmitter = msg.HasLightEmitter;
            remoteState.AppliedItemLight = msg.HasItemLight;
            remoteState.AppliedAmbient = msg.HasAmbientLight;
            remoteState.AppliedLightItemType = msg.ItemType ?? "";
            remoteState.AppliedLightRadius = msg.LightRadius;

            // ---- Flashlight (directional cone) ----
            // Continuous B+ stream also drives Flashlight; event path is edge re-sync.
            Transform flashT = proxy.transform.Find("Flashlight");
            if (flashT != null)
            {
                flashT.gameObject.SetActive(msg.IsFlashlight && msg.LightOn);
                // With the lantern on, LightRadius is the lantern's: the stream owns the cone then.
                if (msg.IsFlashlight && msg.LightOn && msg.LightRadius > 0f && !msg.HasAmbientLight)
                {
                    Light2D lt = flashT.GetComponent<Light2D>();
                    if (lt != null)
                    {
                        lt.LightRadius = msg.LightRadius;
                        lt.LightColor = new Color(msg.LightColorR, msg.LightColorG, msg.LightColorB, 0f);
                        if (msg.LightIntensity > 0f)
                            lt.LightIntensity = msg.LightIntensity;
                    }
                }
            }

            // ---- Held item light (candles, etc.). Flares are continuous-only and stay out of this path. ----
            bool itemTypeIsFlare = !string.IsNullOrEmpty(msg.ItemType)
                && msg.ItemType.IndexOf("flare", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (msg.HasItemLight && !itemTypeIsFlare)
            {
                if (msg.LightOn && msg.LightRadius > 0f)
                {
                    var itemLightState = _net.GetOrCreateState(playerId);
                    GameObject itemLight = itemLightState.ItemLight;
                    if (itemLight == null)
                    {
                        itemLight = new GameObject($"RemoteItemLight_P{playerId}");
                        itemLight.transform.SetParent(proxy.transform);
                        itemLight.transform.localPosition = Vector3.zero;
                        var lt = itemLight.AddComponent<Light2D>();
                        if (lt.LightMaterial == null)
                            lt.LightMaterial = Resources.Load("RadialLight") as Material;
                        PlayerLightFxAmbientNetHandlers.PutOnLightLayer(itemLight);
                        lt.lightsPlayer = true;
                        lt.updateGraph = true;
                        itemLightState.ItemLight = itemLight;
                        ModRuntime.LegacyInfo($"[Light] created item light for player {playerId} type={msg.ItemType}");
                    }
                    Light2D itemLt = itemLight.GetComponent<Light2D>();
                    if (itemLt != null)
                    {
                        itemLt.LightRadius = msg.LightRadius;
                        itemLt.LightIntensity = msg.LightIntensity > 0f ? msg.LightIntensity : 1f;
                        itemLt.LightColor = new Color(msg.LightColorR, msg.LightColorG, msg.LightColorB);
                        var ctrl = Singleton<Controller>.Instance;
                        if (ctrl != null && !ctrl.logicLights.Contains(itemLt))
                            ctrl.logicLights.Add(itemLt);
                    }
                }
                else
                {
                    _net.DestroyRemoteItemLight(playerId);
                }
            }
            else if (!msg.IsFlashlight && !msg.HasLightEmitter)
            {
                // Switching to a non-light item; destroy any lingering item light.
                _net.DestroyRemoteItemLight(playerId);
            }

            // ---- Remote lantern ambient (vanilla: Player.modifyLightDot on local only) ----
            // NEVER reuse the cloned PlayerLightDot: Transform.Find skips inactive children,
            // so we used to spawn a second "PlayerLightDot" while the clone's original sat
            // disabled-but-still-in-logicLights → looked like lantern on both characters.
            // Dedicated RemoteLanternAmbient only on the proxy that owns the lantern.
            bool wantAmbient = msg.HasAmbientLight && msg.LightOn && msg.LightRadius > 0f;
            PlayerLightFxAmbientNetHandlers.ApplyRemoteLanternAmbient(proxy, playerId, wantAmbient, msg);

            // Clean up torch/lantern emitters when switching to non-emitter item
            // (flashlight, empty hand, etc.). The HasLightEmitter branch below only
            // cleans before spawning, and !LightOn only cleans if emitterRoot is found.
            if (!msg.HasLightEmitter)
            {
                if (proxy.transform.Find("ItemLightEmitter") != null
                    || proxy.transform.Find("ItemParticleEmitter") != null)
                {
                    ModLog.Event(LogCat.World, $"[Light] RX p{playerId} remove emitters (not HasLightEmitter)");
                    PlayerLightFxAmbientNetHandlers.RemoveAllItemEmitters(proxy.transform);
                }
            }

            // ---- Torch / Lantern light emitter ----
            Transform emitterRoot = proxy.transform.Find("ItemLightEmitter");
            if (msg.HasLightEmitter && msg.LightOn)
            {
                // Keep existing emitters if the same item type is still live; respawning kills
                // particles + snaps flame (torch VFX thrash on activate/switch double-fire).
                string wantType = msg.ItemType ?? "";
                Transform particleRoot = proxy.transform.Find("ItemParticleEmitter");
                var animCtrl = proxy.GetComponent<Players.SecondPlayerAnimController>();
                bool alreadyWired = emitterRoot != null
                    && animCtrl != null
                    && animCtrl.HasEmittedItem(wantType);

                if (alreadyWired)
                {
                    // Re-assert particles playing + position without destroy.
                    if (particleRoot != null)
                        PlayerLightFxAmbientNetHandlers.PlayAllParticleSystems(particleRoot.gameObject);
                    animCtrl.UpdateEmitterPosition();
                    string clip = animCtrl.CurrentTorsoClipName ?? "?";
                    ModLog.Event(LogCat.World,
                        $"[Light] RX p{playerId} keep emitters type={wantType} clip={clip}");
                }
                else if (!string.IsNullOrEmpty(wantType))
                {
                    PlayerLightFxAmbientNetHandlers.RemoveAllItemEmitters(proxy.transform);

                    InvItem itemDef = Singleton<ItemsDatabase>.Instance?.getItem(wantType, instantiate: false);
                    if (itemDef != null && itemDef.lightEmitter != null)
                    {
                        GameObject emitter = Core.AddPrefab(
                            itemDef.lightEmitter,
                            Vector3.zero,
                            Quaternion.Euler(90f, 0f, 0f),
                                proxy.gameObject);
                        if (emitter != null)
                        {
                            emitter.name = "ItemLightEmitter";
                            Collider ec = emitter.GetComponent<Collider>();
                            if (ec != null)
                                ec.enabled = false;

                            PlayerLightFxAmbientNetHandlers.EnsureEmitterVisible(emitter);
                            PlayerLightFxAmbientNetHandlers.SetupLight2D(emitter);
                            ModLog.Event(LogCat.World, $"[Light] RX p{playerId} spawn emitter type={wantType}");
                        }

                        if (itemDef._particleEmitter != null)
                        {
                            Transform staleP = proxy.transform.Find("ItemParticleEmitter");
                            if (staleP != null)
                                UnityEngine.Object.DestroyImmediate(staleP.gameObject);

                            GameObject pe = Core.AddPrefab(
                                itemDef._particleEmitter,
                                Vector3.zero,
                                Quaternion.Euler(90f, 0f, 0f),
                            proxy.gameObject);
                            if (pe != null)
                            {
                                pe.name = "ItemParticleEmitter";

                                foreach (var ad in pe.GetComponentsInChildren<AutoDestroyParticles>(true))
                                    UnityEngine.Object.Destroy(ad);

                                PlayerLightFxAmbientNetHandlers.EnsureEmitterVisible(pe);
                                PlayerLightFxAmbientNetHandlers.PlayAllParticleSystems(pe);
                                PlayerLightFxAmbientNetHandlers.SetupParticleSorting(pe);

                                ModLog.Event(LogCat.World, $"[Light] RX p{playerId} spawn particles type={wantType}");
                            }
                        }

                        animCtrl = proxy.GetComponent<Players.SecondPlayerAnimController>();
                        if (animCtrl != null)
                        {
                            animCtrl.SetEmittedItem(itemDef);
                            animCtrl.UpdateEmitterPosition();
                            string clip = animCtrl.CurrentTorsoClipName ?? "?";
                            bool hasClipKey = animCtrl.EmitterHasClip(clip);
                            ModLog.Event(LogCat.World,
                                $"[Light] RX p{playerId} wire anim type={wantType} clip={clip} epKey={hasClipKey}");
                        }
                    }
                    else
                    {
                        ModLog.Warn(LogCat.World, "[Light] item def or lightEmitter null for: " + wantType);
                    }
                }
            }
            else if (!msg.LightOn && emitterRoot != null)
            {
                ModLog.Event(LogCat.World, $"[Light] RX p{playerId} remove emitters (LightOn=false)");
                PlayerLightFxAmbientNetHandlers.RemoveAllItemEmitters(proxy.transform);
            }
        }

    }
}
