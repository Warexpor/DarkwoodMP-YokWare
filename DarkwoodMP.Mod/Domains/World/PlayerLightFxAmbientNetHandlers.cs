using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Players;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Static emitter / remote-lantern ambient helpers for player light FX.</summary>
    internal sealed class PlayerLightFxAmbientNetHandlers
    {
        /// <summary>
        /// Ensures all Renderers on the emitter GameObject and its children are enabled,
        /// so the fire particle effect and light are actually drawn.
        /// </summary>
        internal static void EnsureEmitterVisible(GameObject emitter)
        {
            foreach (Renderer r in emitter.GetComponentsInChildren<Renderer>(true))
                r.enabled = true;
        }

        /// <summary>
        /// Explicitly plays all ParticleSystem components on the emitter and its children.
        /// Core.AddPrefab just calls Instantiate(); if playOnAwake is false on the prefab,
        /// the particles won't start without an explicit Play() call.
        /// </summary>
        internal static void PlayAllParticleSystems(GameObject emitter)
        {
            foreach (ParticleSystem ps in emitter.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (!ps.isPlaying)
                    ps.Play(true);
            }
        }

        /// <summary>
        /// Sets the ParticleSystemRenderer sorting order so torch/lantern fire
        /// renders above the player sprite. Without this, particles can appear
        /// behind the player's body (sortingOrder 0) and be invisible.
        /// </summary>
        internal static void SetupParticleSorting(GameObject emitter)
        {
            foreach (ParticleSystemRenderer pr in emitter.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                pr.sortingOrder = 300;
                pr.sortingLayerName = "Default";
            }
        }

        /// <summary>
        /// Configures a Light2D emitter so it renders correctly on the proxy.
        /// Sets lightsPlayer=true and registers with Controller.logicLights so
        /// the light mesh is drawn each frame.
        /// </summary>
        internal static void SetupLight2D(GameObject emitter)
        {
            Light2D lt = emitter.GetComponent<Light2D>();
            if (lt == null)
                return;

            if (!lt.lightsPlayer)
            {
                lt.lightsPlayer = true;
                lt.updateGraph = true;
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null && !ctrl.logicLights.Contains(lt))
                    ctrl.logicLights.Add(lt);
            }
        }

        private const string RemoteLanternAmbientName = "RemoteLanternAmbient";

        /// <summary>
        /// Peer lantern glow under the proxy only. Never clones PlayerLightDot (that is
        /// personal ambient vision and caused the dual-lantern look). Uses Light2D.Create
        /// for a real mesh; copies material only from local lightDot.
        /// </summary>
        internal static void ApplyRemoteLanternAmbient(
            RemotePlayerProxy proxy, int playerId, bool wantOn, PlayerLightStateMessage msg)
        {
            if (proxy == null) return;

            // Stock clone lightDots must stay inactive; they are not the network lantern.
            NeutralizeClonedPlayerLightDots(proxy.transform);
            // Destroy any legacy Instantiated PlayerLightDot copies named RemoteLanternAmbient
            // that still look like personal vision lights (double blob).
            DestroyLegacyClonedLanterns(proxy.transform);

            Transform ambientT = FindChildIncludingInactive(proxy.transform, RemoteLanternAmbientName);

            if (wantOn)
            {
                Light2D light = ambientT != null ? ambientT.GetComponent<Light2D>() : null;
                if (light == null)
                {
                    light = CreateRemoteLanternLight(proxy.transform);
                    if (light == null)
                    {
                        ModLog.Warn(LogCat.World, $"[Light] remote lantern spawn failed p{playerId}");
                        return;
                    }
                    ambientT = light.transform;
                }

                ambientT.gameObject.SetActive(true);
                ambientT.localPosition = Vector3.zero;
                ambientT.localRotation = Quaternion.identity;

                float radius = msg.LightRadius > 0f ? msg.LightRadius : 450f;
                light.LightRadius = radius;
                light.LightIntensity = msg.LightIntensity > 0f ? msg.LightIntensity : 1f;
                if (msg.LightColorR + msg.LightColorG + msg.LightColorB > 0.01f)
                    light.LightColor = new Color(msg.LightColorR, msg.LightColorG, msg.LightColorB, 0f);
                else
                    light.LightColor = new Color(1f, 0.85f, 0.45f, 0f);
                light.LightConeAngle = 360f;
                // Render + AI graph for area light, but this is NOT Player.Instance.lightDot.
                light.lightsPlayer = true;
                light.updateGraph = true;
                EnsureRadialMaterial(light);
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null && !ctrl.logicLights.Contains(light))
                    ctrl.logicLights.Add(light);

                ModLog.Event(LogCat.World,
                    $"[Light] remote lantern ON p{playerId} r={radius:F0} go={light.gameObject.name}");
            }
            else if (ambientT != null)
            {
                Light2D light = ambientT.GetComponent<Light2D>();
                if (light != null)
                {
                    try { light.unlightGraphNodes(); } catch { /* ok */ }
                    light.LightRadius = 0.001f;
                    light.lightsPlayer = false;
                    light.updateGraph = false;
                    var ctrl = Singleton<Controller>.Instance;
                    if (ctrl != null)
                        ctrl.logicLights.Remove(light);
                }
                // Destroy instead of hide to avoid a leftover duplicate mesh next time.
                UnityEngine.Object.Destroy(ambientT.gameObject);
                ModLog.Event(LogCat.World, $"[Light] remote lantern OFF p{playerId}");
            }
        }

        /// <summary>Factory radial only; never Instantiate(PlayerLightDot).</summary>
        internal static Light2D CreateRemoteLanternLight(Transform proxyRoot)
        {
            if (proxyRoot == null) return null;

            Material mat = null;
            Light2D.LightDetailSetting detail = Light2D.LightDetailSetting.Rays_100;
            if (Player.Instance != null)
            {
                Transform t = FindChildIncludingInactive(Player.Instance.transform, "PlayerLightDot");
                Light2D src = t != null ? t.GetComponent<Light2D>() : null;
                if (src != null)
                {
                    mat = src.LightMaterial;
                    detail = src.LightDetail;
                }
            }
            if (mat == null)
                mat = Resources.Load("RadialLight") as Material;

            Color col = new Color(1f, 0.85f, 0.45f, 0f);
            Light2D created = Light2D.Create(proxyRoot.position, mat, col, 450f, 360, detail);
            if (created == null) return null;
            created.gameObject.name = RemoteLanternAmbientName;
            created.transform.SetParent(proxyRoot, false);
            created.transform.localPosition = Vector3.zero;
            created.transform.localRotation = Quaternion.identity;
            created.transform.localScale = Vector3.one;
            return created;
        }

        internal static void EnsureRadialMaterial(Light2D light)
        {
            if (light == null) return;
            if (light.LightMaterial != null) return;
            Material radial = Resources.Load("RadialLight") as Material;
            if (radial != null) light.LightMaterial = radial;
        }

        /// <summary>Remove old Instantiate(PlayerLightDot) lanterns that caused dual blobs.</summary>
        internal static void DestroyLegacyClonedLanterns(Transform proxyRoot)
        {
            if (proxyRoot == null) return;
            for (int i = proxyRoot.childCount - 1; i >= 0; i--)
            {
                Transform c = proxyRoot.GetChild(i);
                if (c == null) continue;
                // Old builds left Instantiated copies still named PlayerLightDot (active).
                if (c.name != RemoteLanternAmbientName && c.name != "PlayerLightDot")
                    continue;
                if (c.name == "PlayerLightDot")
                {
                    // Stock clone; neutralize it only because another path handles it.
                    continue;
                }
                // RemoteLanternAmbient that still has nested "LightFlare" child = Instantiated template.
                bool looksLikePlayerDotClone = c.childCount > 0
                    || c.GetComponent<tk2dSprite>() != null
                    || c.GetComponentInChildren<tk2dSprite>(true) != null;
                if (!looksLikePlayerDotClone) continue;
                Light2D lt = c.GetComponent<Light2D>();
                if (lt != null)
                {
                    try { lt.unlightGraphNodes(); } catch { /* ok */ }
                    var ctrl = Singleton<Controller>.Instance;
                    if (ctrl != null) ctrl.logicLights.Remove(lt);
                }
                UnityEngine.Object.Destroy(c.gameObject);
            }
        }

        internal static Transform FindChildIncludingInactive(Transform root, string childName)
        {
            if (root == null || string.IsNullOrEmpty(childName)) return null;
            // Active-only fast path
            Transform t = root.Find(childName);
            if (t != null) return t;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform c = root.GetChild(i);
                if (c != null && c.name == childName)
                    return c;
            }
            return null;
        }

        /// <summary>
        /// Proxy is a player clone. Stock PlayerLightDot must stay out of logicLights so it
        /// cannot look like a second local lantern. Net lantern uses RemoteLanternAmbient only.
        /// </summary>
        internal static void NeutralizeClonedPlayerLightDots(Transform proxyRoot)
        {
            if (proxyRoot == null) return;
            for (int i = 0; i < proxyRoot.childCount; i++)
            {
                Transform c = proxyRoot.GetChild(i);
                if (c == null || c.name != "PlayerLightDot") continue;
                // Never neutralize our dedicated ambient if misnamed.
                if (c.name == RemoteLanternAmbientName) continue;

                Light2D lt = c.GetComponent<Light2D>();
                if (lt != null)
                {
                    try { lt.unlightGraphNodes(); } catch { /* ok */ }
                    lt.lightsPlayer = false;
                    lt.updateGraph = false;
                    lt.LightRadius = 0.001f;
                    var ctrl = Singleton<Controller>.Instance;
                    if (ctrl != null)
                        ctrl.logicLights.Remove(lt);
                }
                if (c.gameObject.activeSelf)
                    c.gameObject.SetActive(false);
            }
        }

        internal static void RemoveAllItemEmitters(Transform proxyRoot)
        {
            var animCtrl = proxyRoot.GetComponent<Players.SecondPlayerAnimController>();
            if (animCtrl != null)
                animCtrl.ClearEmittedItem();

            Transform emitter = proxyRoot.Find("ItemLightEmitter");
            if (emitter != null)
            {
                Light2D lt = emitter.GetComponent<Light2D>();
                if (lt != null)
                {
                    lt.unlightGraphNodes();
                    if (lt.lightsPlayer)
                    {
                        var ctrl = Singleton<Controller>.Instance;
                        if (ctrl != null)
                            ctrl.logicLights.Remove(lt);
                    }
                }
                Core.RemovePooledPrefab(emitter);
            }
            Transform particle = proxyRoot.Find("ItemParticleEmitter");
            if (particle != null)
                Core.RemovePooledPrefab(particle);

            Transform flare = proxyRoot.Find("FlareLight");
            if (flare != null)
            {
                Light2D fl = flare.GetComponent<Light2D>();
                if (fl != null)
                {
                    fl.unlightGraphNodes();
                    if (fl.lightsPlayer)
                    {
                        var ctrl = Singleton<Controller>.Instance;
                        if (ctrl != null)
                            ctrl.logicLights.Remove(fl);
                    }
                }
                UnityEngine.Object.DestroyImmediate(flare.gameObject);
            }

            RemoveClonedEmitters(proxyRoot);
        }

        internal static void RemoveClonedEmitters(Transform proxyRoot)
        {
            if (proxyRoot == null) return;

            HashSet<string> preserved = new HashSet<string>
            {
                "Flashlight",
                "PlayerLightDot",
                "PlayerFOVLight",
                "PlayerFOVLogic",
                "PlayerFOVLightDot",
                "PlayerShadow",
                "Shadow",
                "ItemLightEmitter",
                "ItemParticleEmitter",
                "FlareLight",
                "RemoteLanternAmbient",
            };

            List<Transform> toDestroy = new List<Transform>();

            foreach (Transform child in proxyRoot)
            {
                if (preserved.Contains(child.name))
                    continue;

                if (child.GetComponent<Light2D>() != null || child.GetComponent<ParticleSystem>() != null)
                    toDestroy.Add(child);
            }

            foreach (Transform t in toDestroy)
                Core.RemovePooledPrefab(t);
        }
    }
}
