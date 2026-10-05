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
    /// TX: pack continuous held lights into PlayerState + local held-light helpers.
    /// </summary>
    internal sealed class PlayerHeldLightPackNetHandlers
    {
        private readonly LanNetworkManager _net;

        // Continuous light dirty cache for the local send path.
        private bool _prevSentFlareActive;
        private bool _prevSentFlashActive;
        private bool _prevSentMatchActive;
        private float _lastSentFlareRadius, _lastSentFlareIntensity;
        private float _lastSentFlareColorR, _lastSentFlareColorG, _lastSentFlareColorB;
        private float _lastSentFlashRadius, _lastSentFlashIntensity;
        private float _lastSentFlashColorR, _lastSentFlashColorG, _lastSentFlashColorB;
        private string _lastSentFlareItemType;
        private float _lightParamsForceTimer;
        private float _localHeldLightStartTime = -1f;
        private float _localHeldLightLongevity = 3f;
        private const float LightParamsForceInterval = 0.15f; // ~6.6 Hz while active (was 1 Hz)
        private const float LightRadiusDirtyEps = 5f;
        private const float LightIntensityDirtyEps = 0.02f;
        private const float LightColorDirtyEps = 0.02f;

        internal PlayerHeldLightPackNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
        }

        internal void ResetLocalLightSendCache()
        {
            _prevSentFlareActive = false;
            _prevSentFlashActive = false;
            _prevSentMatchActive = false;
            _localHeldLightStartTime = -1f;
            _localHeldLightLongevity = 3f;
            _lastSentFlareRadius = 0f;
            _lastSentFlareIntensity = 0f;
            _lastSentFlareColorR = _lastSentFlareColorG = _lastSentFlareColorB = 0f;
            _lastSentFlashRadius = 0f;
            _lastSentFlashIntensity = 0f;
            _lastSentFlashColorR = _lastSentFlashColorG = _lastSentFlashColorB = 0f;
            _lastSentFlareItemType = null;
            _lightParamsForceTimer = 0f;
        }

        /// <summary>
        /// Pack continuous flare, match, and flashlight state into the
        /// conditional LightFlags payload.
        /// </summary>
        internal void PackContinuousLights(ref PlayerStateMessage msg, Player local)
        {
            _lightParamsForceTimer += LanNetworkManager.SendInterval;
            bool forceParams = _lightParamsForceTimer >= LightParamsForceInterval;
            if (forceParams)
                _lightParamsForceTimer = 0f;

            byte flags = 0;
            string curType = local.currentItem != null ? local.currentItem.type : null;
            // A flare is continuous only while the lit projectile is held,
            // not when the item is merely selected in the hotbar.
            Light2D heldFlareLight = null;
            Flare heldFlareComp = null;
            Light2D heldMatchLight = null;
            bool flareActive = TryGetLocalHeldFlareLight(local, out heldFlareLight, out heldFlareComp);
            // Match uses the same rule as flare: a lit projectile must remain
            // parented to heldItem while it is aimed.
            // Do NOT require currentItem.activated (throwables often stay deactivated while aimed).
            bool matchActive = !flareActive && TryGetLocalHeldMatchLight(local, out heldMatchLight);
            bool heldBurnLight = flareActive || matchActive;
            bool flashActive = !heldBurnLight
                && !InvItemClass.isNull(local.currentItem)
                && local.currentItem.baseClass != null
                && local.currentItem.baseClass.isFlashlight
                && local.currentItem.activated;

            if (heldBurnLight)
            {
                flags |= PlayerStateMessage.LightFlagFlare;
                if (matchActive)
                    flags |= PlayerStateMessage.LightFlagMatch;
                msg.FlareActive = flareActive;
                msg.MatchActive = matchActive;
                msg.FlareItemType = curType ?? (matchActive ? "match" : "flare");
                msg.FlareRadius = matchActive ? 180f : 650f;
                msg.FlareIntensity = matchActive ? 0.85f : 1f;
                msg.FlareColorR = 1f;
                msg.FlareColorG = matchActive ? 0.65f : 0.5f;
                msg.FlareColorB = matchActive ? 0.2f : 0.1f;

                Light2D itemLight = heldFlareLight != null ? heldFlareLight : heldMatchLight;
                Flare flareComp = heldFlareComp;
                if (itemLight == null && local.heldItem != null)
                    itemLight = local.heldItem.GetComponentInChildren<Light2D>(true);

                if (itemLight != null)
                {
                    // Radius: live value once lit (stable enough).
                    if (itemLight.LightRadius > 0f)
                        msg.FlareRadius = itemLight.LightRadius;

                    // Match stick Light2D intensity flickers every frame in SP. Streaming that
                    // dirties FlareParams (~6 Hz force) and strobes the peer. Keep fixed cruise.
                    if (!matchActive && itemLight.LightIntensity > 0f)
                        msg.FlareIntensity = itemLight.LightIntensity;

                    if (!matchActive
                        && (itemLight.LightColor.a > 0f
                            || itemLight.LightColor.r + itemLight.LightColor.g + itemLight.LightColor.b > 0.01f))
                    {
                        msg.FlareColorR = itemLight.LightColor.r;
                        msg.FlareColorG = itemLight.LightColor.g;
                        msg.FlareColorB = itemLight.LightColor.b;
                    }
                }

                // Local-space attach point (NOT world delta). World delta as localPos breaks
                // Apply the local offset under body rotation so the light and
                // effect stay with the hand.
                // Prefer heldItem root so prefab-internal Light2D/lightFlare offsets stay correct.
                if (local.heldItem != null)
                {
                    Transform ht = local.heldItem.transform;
                    Vector3 lp;
                    if (ht.parent == local.transform)
                        lp = ht.localPosition;
                    else
                        lp = local.transform.InverseTransformPoint(ht.position);
                    msg.FlareLocalX = lp.x;
                    msg.FlareLocalY = lp.y;
                    msg.FlareLocalZ = lp.z;
                }
                else if (itemLight != null)
                {
                    Vector3 lp = local.transform.InverseTransformPoint(itemLight.transform.position);
                    msg.FlareLocalX = lp.x;
                    msg.FlareLocalY = lp.y;
                    msg.FlareLocalZ = lp.z;
                }

                bool rising = flareActive ? !_prevSentFlareActive : !_prevSentMatchActive;
                if (rising)
                {
                    _localHeldLightStartTime = Time.time;
                    _localHeldLightLongevity = flareComp != null && flareComp.longevity > 0.05f
                        ? flareComp.longevity + Sync.WorldPhysicsSyncService.FlareBurnoutFadeSec
                        : (matchActive ? 8f : 5f);
                }

                // Remain from aim-start burn clock when known (else rising timer).
                if (flareActive && local.heldItem != null && Sync.FlareClock.AgeOf(local.heldItem) >= 0f)
                {
                    // Lit-on-aim clock (FlareClock): peers start their copy at this point of it.
                    float total = flareComp != null && flareComp.longevity > 0.05f
                        ? flareComp.longevity + Sync.FlareClock.FadeSec
                        : (_localHeldLightLongevity > 0.01f ? _localHeldLightLongevity : 3f + Sync.FlareClock.FadeSec);
                    float rem = Mathf.Clamp01(1f - Sync.FlareClock.AgeOf(local.heldItem) / total);
                    msg.HeldLightRemain01 = (byte)Mathf.Clamp(Mathf.RoundToInt(rem * 255f), 0, 255);
                    flags |= PlayerStateMessage.LightFlagRemain;
                }
                else if (_localHeldLightStartTime > 0f && _localHeldLightLongevity > 0.01f)
                {
                    float rem = 1f - (Time.time - _localHeldLightStartTime) / _localHeldLightLongevity;
                    msg.HeldLightRemain01 = (byte)Mathf.Clamp(Mathf.RoundToInt(rem * 255f), 0, 255);
                    flags |= PlayerStateMessage.LightFlagRemain;
                }

                bool typeChanged = !string.Equals(_lastSentFlareItemType, msg.FlareItemType, StringComparison.Ordinal);
                bool dirty = rising || forceParams
                    || Mathf.Abs(msg.FlareRadius - _lastSentFlareRadius) > LightRadiusDirtyEps
                    || Mathf.Abs(msg.FlareIntensity - _lastSentFlareIntensity) > LightIntensityDirtyEps
                    || Mathf.Abs(msg.FlareColorR - _lastSentFlareColorR) > LightColorDirtyEps
                    || Mathf.Abs(msg.FlareColorG - _lastSentFlareColorG) > LightColorDirtyEps
                    || Mathf.Abs(msg.FlareColorB - _lastSentFlareColorB) > LightColorDirtyEps;

                if (dirty)
                {
                    flags |= PlayerStateMessage.LightFlagFlareParams;
                    msg.FlareHasParams = true;
                    _lastSentFlareRadius = msg.FlareRadius;
                    _lastSentFlareIntensity = msg.FlareIntensity;
                    _lastSentFlareColorR = msg.FlareColorR;
                    _lastSentFlareColorG = msg.FlareColorG;
                    _lastSentFlareColorB = msg.FlareColorB;
                }

                if (rising || typeChanged)
                {
                    flags |= PlayerStateMessage.LightFlagFlareItemType;
                    msg.FlareHasItemType = true;
                    _lastSentFlareItemType = msg.FlareItemType;
                }

                if (rising)
                    ModLog.Event(LogCat.World, "[LightSync] local " + (matchActive ? "match" : "flare")
                        + " ON type=" + msg.FlareItemType
                        + " remain01=" + msg.HeldLightRemain01
                        + " r=" + msg.FlareRadius.ToString("F0"));
            }
            else if (_prevSentFlareActive || _prevSentMatchActive)
            {
                ModLog.Event(LogCat.World, "[LightSync] local held burn light OFF");
            }

            if (flashActive)
            {
                flags |= PlayerStateMessage.LightFlagFlashlight;
                msg.FlashlightActive = true;
                Light2D flash = Traverse.Create(local).Field("Flashlight").GetValue<Light2D>();
                if (flash != null)
                {
                    msg.FlashRadius = flash.LightRadius;
                    msg.FlashIntensity = flash.LightIntensity > 0f ? flash.LightIntensity : 1f;
                    msg.FlashColorR = flash.LightColor.r;
                    msg.FlashColorG = flash.LightColor.g;
                    msg.FlashColorB = flash.LightColor.b;
                    // Cone aim follows Flashlight child rotation (SP aims with body/mouse).
                    msg.FlashAimY = (short)Mathf.RoundToInt(flash.transform.eulerAngles.y);
                    flags |= PlayerStateMessage.LightFlagFlashAim;
                }
                else
                {
                    msg.FlashRadius = 400f;
                    msg.FlashIntensity = 1f;
                    msg.FlashColorR = 0.3f;
                    msg.FlashColorG = 0.3f;
                    msg.FlashColorB = 0.3f;
                    msg.FlashAimY = (short)Mathf.RoundToInt(local.transform.eulerAngles.y);
                    flags |= PlayerStateMessage.LightFlagFlashAim;
                }

                bool rising = !_prevSentFlashActive;
                bool dirty = rising || forceParams
                    || Mathf.Abs(msg.FlashRadius - _lastSentFlashRadius) > LightRadiusDirtyEps
                    || Mathf.Abs(msg.FlashIntensity - _lastSentFlashIntensity) > LightIntensityDirtyEps
                    || Mathf.Abs(msg.FlashColorR - _lastSentFlashColorR) > LightColorDirtyEps
                    || Mathf.Abs(msg.FlashColorG - _lastSentFlashColorG) > LightColorDirtyEps
                    || Mathf.Abs(msg.FlashColorB - _lastSentFlashColorB) > LightColorDirtyEps;

                if (dirty)
                {
                    flags |= PlayerStateMessage.LightFlagFlashParams;
                    msg.FlashHasParams = true;
                    _lastSentFlashRadius = msg.FlashRadius;
                    _lastSentFlashIntensity = msg.FlashIntensity;
                    _lastSentFlashColorR = msg.FlashColorR;
                    _lastSentFlashColorG = msg.FlashColorG;
                    _lastSentFlashColorB = msg.FlashColorB;
                }

                if (rising && Config.ModConfig.IsVerboseLightSync)
                    ModRuntime.LegacyInfo("[LightSync] local flashlight ON");
            }
            else if (_prevSentFlashActive && Config.ModConfig.IsVerboseLightSync)
            {
                ModRuntime.LegacyInfo("[LightSync] local flashlight OFF");
            }

            if (!heldBurnLight)
            {
                _lastSentFlareItemType = null;
                _localHeldLightStartTime = -1f;
            }

            msg.LightFlags = flags;
            _prevSentFlareActive = flareActive;
            _prevSentMatchActive = matchActive;
            _prevSentFlashActive = flashActive;
        }

        /// <summary>
        /// Held flare continuous light only while the lit projectile is still parented as heldItem
        /// (aim / pre-throw). Hotbar selection alone must NOT light the proxy (F1/F2).
        /// </summary>
        internal static bool TryGetLocalHeldFlareLight(Player local, out Light2D light, out Flare flare)
        {
            light = null;
            flare = null;
            if (local == null || local.heldItem == null)
                return false;
            // It must still be held by the player; after throwing, its parent is null.
            Transform ht = local.heldItem.transform;
            if (ht.parent == null)
                return false;

            flare = local.heldItem.GetComponent<Flare>()
                ?? local.heldItem.GetComponentInChildren<Flare>(true);
            if (flare != null && flare.light2D != null)
            {
                light = flare.light2D;
                return true;
            }
            // Flare-type throwable with Light2D but Flare not yet resolved
            string t = local.currentItem != null ? local.currentItem.type : null;
            if (!string.IsNullOrEmpty(t)
                && t.IndexOf("flare", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                light = local.heldItem.GetComponentInChildren<Light2D>(true);
                return light != null;
            }
            // Explicit Flare component without named type
            if (flare != null)
            {
                light = local.heldItem.GetComponentInChildren<Light2D>(true);
                return light != null;
            }
            return false;
        }

        /// <summary>
        /// Held match continuous light only while the lit projectile is parented as heldItem
        /// (aim / pre-throw). Mirrors <see cref="TryGetLocalHeldFlareLight"/> and does not require
        /// <c>currentItem.activated</c> (throwables often stay deactivated while aimed; that was
        /// why peers saw no held match glow).
        /// </summary>
        internal static bool TryGetLocalHeldMatchLight(Player local, out Light2D light)
        {
            light = null;
            if (local == null || local.heldItem == null)
                return false;
            Transform ht = local.heldItem.transform;
            if (ht.parent == null)
                return false;
            // Flare path owns Flare components.
            if (local.heldItem.GetComponent<Flare>() != null
                || local.heldItem.GetComponentInChildren<Flare>(true) != null)
                return false;

            string t = local.currentItem != null ? local.currentItem.type : null;
            bool typeMatch = !string.IsNullOrEmpty(t)
                && t.IndexOf("match", StringComparison.OrdinalIgnoreCase) >= 0;
            bool typeFlare = !string.IsNullOrEmpty(t)
                && t.IndexOf("flare", StringComparison.OrdinalIgnoreCase) >= 0;
            if (typeFlare)
                return false;

            InvItem bc = local.currentItem != null ? local.currentItem.baseClass : null;
            if (bc != null)
            {
                if (bc.isFlashlight) return false;
                if (bc.lightEmitter != null) return false; // torch etc.
            }

            light = local.heldItem.GetComponentInChildren<Light2D>(true);
            if (typeMatch)
                return true; // match by name even if Light2D still waking up
            // Fallback: throwable with small held Light2D (no flare / torch / flash).
            if (bc != null && bc.isThrowable && light != null
                && (bc.lightRadius <= 0f || bc.lightRadius < 350f))
                return true;
            return false;
        }

        /// <summary>
        /// Match / short-lived held light (event-path guard + continuous). Prefer
        /// <see cref="TryGetLocalHeldMatchLight"/> for transmission, which is heldItem-authoritative.
        /// </summary>
        internal static bool IsMatchLightItem(Player local)
        {
            if (TryGetLocalHeldMatchLight(local, out _))
                return true;
            // Hotbar/equip without held yet: type-only, no activation required.
            if (local == null || InvItemClass.isNull(local.currentItem) || local.currentItem.baseClass == null)
                return false;
            string t = local.currentItem.type ?? "";
            if (t.IndexOf("match", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }
    }
}
