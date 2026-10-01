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
        public static void TickThrownLightExpiry(LanNetworkManager net)
        {
            TickThrownLightFades();

            if (net == null || !net.IsConnected) return;
            if (net.Role != NetworkRole.Host) return;
            if (_s.Thrown.ThrownLights.Count == 0) return;

            float now = Time.time;
            for (int i = _s.Thrown.ThrownLights.Count - 1; i >= 0; i--)
            {
                var t = _s.Thrown.ThrownLights[i];
                if (t.Go == null)
                {
                    if (t.ThrowId > 0) _s.Thrown.ThrownById.Remove(t.ThrowId);
                    _s.Thrown.ThrownLights.RemoveAt(i);
                    continue;
                }
                if (now < t.ExpireAt)
                    continue;

                Vector3 pos = t.Go.transform.position;
                // Broadcast first so peers fade in parallel with host.
                if (t.ThrowId > 0)
                {
                    net.SendThrowableDespawn(new ThrowableDespawnMessage
                    {
                        ThrowId = t.ThrowId,
                        PosX = pos.x,
                        PosY = pos.y,
                        PosZ = pos.z
                    });
                    _s.Thrown.ThrownById.Remove(t.ThrowId);
                }
                BeginThrownLightFade(t.Go, FlareBurnoutFadeSec);
                _s.Thrown.ThrownLights.RemoveAt(i);
                Logging.ModLog.Event(Logging.LogCat.World, "[ThrowableDespawn] host expired throwId=" + t.ThrowId
                    + " type=" + t.ItemType + " pos=" + pos);
            }
        }

        public static void ApplyThrownDespawn(ThrowableDespawnMessage msg)
        {
            GameObject go = null;
            if (msg.ThrowId > 0 && _s.Thrown.ThrownById.TryGetValue(msg.ThrowId, out var track))
            {
                go = track.Go;
                _s.Thrown.ThrownById.Remove(msg.ThrowId);
            }
            if (go == null)
            {
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                int hitsN = OverlapNear(pos, 3f);
                for (int i = 0; i < hitsN; i++)
                {
                    if (_overlap3D[i] == null) continue;
                    GameObject root = _overlap3D[i].attachedRigidbody != null
                        ? _overlap3D[i].attachedRigidbody.gameObject
                        : _overlap3D[i].gameObject;
                    string n = root.name.ToLowerInvariant();
                    if (n.Contains("flare") || root.GetComponentInChildren<Light2D>(true) != null)
                    {
                        go = root;
                        break;
                    }
                }
            }

            for (int i = _s.Thrown.ThrownLights.Count - 1; i >= 0; i--)
            {
                if (_s.Thrown.ThrownLights[i].ThrowId == msg.ThrowId || _s.Thrown.ThrownLights[i].Go == go)
                    _s.Thrown.ThrownLights.RemoveAt(i);
            }

            if (go != null)
            {
                Logging.ModLog.Event(Logging.LogCat.World,
                    "[ThrowableDespawn] peer fade throwId=" + msg.ThrowId + " go=" + go.name);
                BeginThrownLightFade(go, FlareBurnoutFadeSec);
            }
            else
                Logging.ModLog.Event(Logging.LogCat.World, "[ThrowableDespawn] no go for throwId=" + msg.ThrowId);
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
            foreach (var auth in go.GetComponentsInChildren<NetworkFlareLifetime>(true))
                UnityEngine.Object.Destroy(auth);
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

        /// <summary>Compatibility name for the fade-then-extinguish path.</summary>
        private static void ExtinguishThrownLight(GameObject go)
        {
            BeginThrownLightFade(go, FlareBurnoutFadeSec);
        }

        /// <summary>Late-join: re-send still-burning thrown flares as grounded burns (no flight).</summary>
        public static void SendActiveThrownLightsTo(LanNetworkManager net, int playerId)
        {
            if (net == null || net.Role != NetworkRole.Host || playerId <= 0)
                return;
            int sent = 0;
            float now = Time.time;
            for (int i = 0; i < _s.Thrown.ThrownLights.Count; i++)
            {
                var t = _s.Thrown.ThrownLights[i];
                if (t.Go == null || now >= t.ExpireAt) continue;
                Vector3 p = t.Go.transform.position;
                // Peer needs remaining-until-dark (= until fade start + fade).
                float remain = Mathf.Max(0.15f, (t.ExpireAt - now) + FlareBurnoutFadeSec);
                // Distance=0 + zero vel + no land → grounded spawn branch.
                var msg = new ThrowableSpawnMessage
                {
                    ItemType = t.ItemType ?? "flare",
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    AimY = 0f,
                    Distance = 0f,
                    VelX = 0f,
                    VelY = 0f,
                    VelZ = 0f,
                    ThrowId = t.ThrowId,
                    LongevitySec = remain,
                    HasLandTarget = false
                };
                net.SendToPlayer(playerId, NetMessageType.ThrowableSpawn, w => msg.Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);
                sent++;
            }
            if (sent > 0)
                DWMPHorde.Logging.ModLog.Event(DWMPHorde.Logging.LogCat.Session,
                    "[BulkSync] Thrown lights (grounded) → p" + playerId + ": " + sent);
        }

    }
}
