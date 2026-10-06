using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    public static partial class WorldPhysicsSyncService
    {
        /// <summary>
        /// Every frame, all roles: advance active light fades (a peer's held match burning out)
        /// and forget thrown flares that are gone or fully dark. Thrown flares burn out on each
        /// peer's own vanilla clock (<see cref="FlareClock"/>); nothing is broadcast.
        /// </summary>
        public static void TickThrownLights()
        {
            TickThrownLightFades();
            var flares = _s.Thrown.ThrownFlares;
            for (int i = flares.Count - 1; i >= 0; i--)
            {
                if (flares[i] == null || FlareClock.BurntOut(flares[i]))
                    flares.RemoveAt(i);
            }
        }

        /// <summary>A thrown flare burning in this world (own throw or a peer's copy): a joiner gets it.</summary>
        public static void NoteThrownFlare(GameObject go)
        {
            if (go != null && !_s.Thrown.ThrownFlares.Contains(go))
                _s.Thrown.ThrownFlares.Add(go);
        }

        /// <summary>Vanilla waitToDie: ramp intensity to 0 over fadeSec, then kill lights/particles/lightFlare.</summary>
        public static void BeginThrownLightFade(GameObject go, float fadeSec = FlareBurnoutFadeSec,
            GameObject siblingDestroy = null)
        {
            if (go == null) return;
            // Already fading?
            for (int i = 0; i < _s.Thrown.ThrownLightFades.Count; i++)
            {
                if (_s.Thrown.ThrownLightFades[i].Go == go)
                    return;
            }

            Light2D[] lights = go.GetComponentsInChildren<Light2D>(true);
            float startI = 1f;
            if (lights != null && lights.Length > 0 && lights[0] != null)
                startI = Mathf.Max(0.01f, lights[0].LightIntensity);

            Logging.ModLog.Event(Logging.LogCat.World,
                "[ThrowableFade] begin go=" + go.name
                + " lights=" + (lights != null ? lights.Length : 0)
                + " startI=" + startI.ToString("F2")
                + " fadeSec=" + fadeSec.ToString("F1"));

            // Stop looping audio gently (vanilla AudioObject.Stop(1f)).
            try
            {
                foreach (var ao in go.GetComponentsInChildren<AudioObject>(true))
                {
                    if (ao != null)
                        ao.Stop(1f);
                }
                if (siblingDestroy != null)
                {
                    foreach (var ao in siblingDestroy.GetComponentsInChildren<AudioObject>(true))
                    {
                        if (ao != null)
                            ao.Stop(1f);
                    }
                }
            }
            catch { /* optional */ }

            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps != null)
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
            if (siblingDestroy != null)
            {
                foreach (var ps in siblingDestroy.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (ps != null)
                        ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }

            if (fadeSec <= 0.05f)
            {
                ExtinguishThrownLightImmediate(go);
                if (siblingDestroy != null)
                    UnityEngine.Object.Destroy(siblingDestroy);
                return;
            }

            _s.Thrown.ThrownLightFades.Add(new ThrownLightFade
            {
                Go = go,
                EndTime = Time.time + fadeSec,
                Duration = fadeSec,
                StartIntensity = startI,
                Lights = lights,
                SiblingDestroy = siblingDestroy
            });
        }

        private static void TickThrownLightFades()
        {
            if (_s.Thrown.ThrownLightFades.Count == 0) return;
            float now = Time.time;
            for (int i = _s.Thrown.ThrownLightFades.Count - 1; i >= 0; i--)
            {
                var f = _s.Thrown.ThrownLightFades[i];
                if (f.Go == null)
                {
                    if (f.SiblingDestroy != null)
                        UnityEngine.Object.Destroy(f.SiblingDestroy);
                    _s.Thrown.ThrownLightFades.RemoveAt(i);
                    continue;
                }
                float left = f.EndTime - now;
                float t = 1f - Mathf.Clamp01(left / Mathf.Max(0.01f, f.Duration));
                float inten = Mathf.Lerp(f.StartIntensity, 0f, t);
                float alphaScale = 1f - t;
                if (f.Lights != null)
                {
                    for (int j = 0; j < f.Lights.Length; j++)
                    {
                        if (f.Lights[j] != null)
                            f.Lights[j].LightIntensity = inten;
                    }
                }
                // Dim lightFlare sprites with the light (vanilla waitToDie fades then destroys).
                DimFlareSprites(f.Go, alphaScale);
                if (f.SiblingDestroy != null)
                    DimFlareSprites(f.SiblingDestroy, alphaScale);

                if (now >= f.EndTime)
                {
                    ExtinguishThrownLightImmediate(f.Go);
                    if (f.SiblingDestroy != null)
                    {
                        ExtinguishThrownLightImmediate(f.SiblingDestroy);
                        UnityEngine.Object.Destroy(f.SiblingDestroy);
                    }
                    _s.Thrown.ThrownLightFades.RemoveAt(i);
                }
            }
        }

        private static void DimFlareSprites(GameObject go, float alphaScale)
        {
            if (go == null) return;
            foreach (var fl in go.GetComponentsInChildren<Flare>(true))
            {
                if (fl != null && fl.lightFlare != null)
                {
                    Color c = fl.lightFlare.color;
                    c.a = Mathf.Clamp01(alphaScale);
                    fl.lightFlare.color = c;
                }
            }
        }

        private static void ExtinguishThrownLightImmediate(GameObject go)
        {
            if (go == null) return;

            // V2: destroy glow sprites like vanilla waitToDie Destroy(lightFlare).
            foreach (var fl in go.GetComponentsInChildren<Flare>(true))
            {
                if (fl == null) continue;
                if (fl.lightFlare != null)
                {
                    UnityEngine.Object.Destroy(fl.lightFlare.gameObject);
                    fl.lightFlare = null;
                }
            }

            foreach (var lt in go.GetComponentsInChildren<Light2D>(true))
            {
                try
                {
                    lt.unlightGraphNodes();
                    var ctrl = Singleton<Controller>.Instance;
                    if (ctrl != null)
                        ctrl.logicLights.Remove(lt);
                }
                catch { /* ignore */ }
                lt.LightIntensity = 0f;
                lt.gameObject.SetActive(false);
            }
            foreach (var fl in go.GetComponentsInChildren<Flare>(true))
                UnityEngine.Object.Destroy(fl);
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps != null)
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }

            Logging.ModLog.Event(Logging.LogCat.World, "[ThrowableFade] extinguished go=" + go.name);
        }

        /// <summary>True if this GO (or child) is already in an active light fade.</summary>
        public static bool IsThrownLightFading(GameObject go)
        {
            if (go == null) return false;
            for (int i = 0; i < _s.Thrown.ThrownLightFades.Count; i++)
            {
                if (_s.Thrown.ThrownLightFades[i].Go == go || _s.Thrown.ThrownLightFades[i].SiblingDestroy == go)
                    return true;
            }
            return false;
        }

        /// <summary>Late join: thrown flares still burning, as grounded copies at their current age.</summary>
        public static void SendActiveThrownLightsTo(LanNetworkManager net, int playerId)
        {
            if (net == null || net.Role != NetworkRole.Host || playerId <= 0)
                return;
            int sent = 0;
            var flares = _s.Thrown.ThrownFlares;
            for (int i = 0; i < flares.Count; i++)
            {
                GameObject go = flares[i];
                if (go == null || FlareClock.BurntOut(go)) continue;
                Vector3 p = go.transform.position;
                // Distance=0 + zero vel + no land → grounded spawn branch.
                var msg = new ThrowableSpawnMessage
                {
                    ItemType = FlareItemTypeOf(go),
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    FlareAge = FlareClock.AgeOf(go),
                    HasLandTarget = false
                };
                net.SendToPlayer(playerId, NetMessageType.ThrowableSpawn, w => msg.Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);
                sent++;
            }
            if (sent > 0)
                DWMPHorde.Logging.ModLog.Event(DWMPHorde.Logging.LogCat.Session,
                    "[BulkSync] Thrown flares (grounded) → p" + playerId + ": " + sent);
        }

        /// <summary>The item type a thrown flare object is (its Item component), "flare" by default.</summary>
        private static string FlareItemTypeOf(GameObject go)
        {
            Item item = go.GetComponent<Item>();
            if (item != null && item.invItem != null && !string.IsNullOrEmpty(item.invItem.type))
                return item.invItem.type;
            return "flare";
        }
    }
}
