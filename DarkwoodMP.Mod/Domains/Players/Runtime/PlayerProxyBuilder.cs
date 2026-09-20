using System;
using BepInEx.Logging;
using UnityEngine;

namespace DWMPHorde.Players
{
    /// <summary>
    /// Whether the clone is a netcode-driven remote proxy or a full local Player clone.
    /// </summary>
    public enum PlayerCloneKind
    {
        /// <summary>Networked proxy with stripped gameplay components.</summary>
        Remote,
        /// <summary>Local split-screen player with full Player mechanics.</summary>
        LocalSecond
    }

    /// <summary>
    /// Factory for cloning a Player GameObject into either a Remote or LocalSecond proxy.
    /// </summary>
    public static class PlayerProxyBuilder
    {
        /// <summary>
        /// True while a LocalSecond clone is being instantiated. Used by patched methods to skip registerMe side-effects.
        /// </summary>
        public static bool IsSpawningCoopClone { get; private set; }

        private static float _lastInactiveLog = -999f;

        /// <summary>
        /// Creates a clone of sourcePlayer, strips unwanted components, and attaches the appropriate controller.
        /// </summary>
        /// <param name="sourcePlayer">The Player to copy.</param>
        /// <param name="objectName">Name for the new GameObject.</param>
        /// <param name="positionOffset">Spawn offset relative to source.</param>
        /// <param name="kind">Remote or LocalSecond.</param>
        /// <param name="log">Log output.</param>
        /// <returns>The cloned GameObject, or null on failure.</returns>
        public static GameObject CreatePlayerClone(
            Player sourcePlayer,
            string objectName,
            Vector3 positionOffset,
            PlayerCloneKind kind,
            ManualLogSource log)
        {
            if (sourcePlayer == null)
            {
                // Rate-limit: join load used to spam this every packet → multi-MB logs + freeze
                if (Time.unscaledTime - _lastInactiveLog > 5f)
                {
                    _lastInactiveLog = Time.unscaledTime;
                    log?.LogWarning("Cannot spawn player clone: source Player is null.");
                }
                return null;
            }

            GameObject original = sourcePlayer.gameObject;
            if (!original.activeInHierarchy)
            {
                if (Time.unscaledTime - _lastInactiveLog > 5f)
                {
                    _lastInactiveLog = Time.unscaledTime;
                    log?.LogWarning("Cannot spawn player clone: source Player is inactive (loading?).");
                }
                return null;
            }

            GameObject clone;
            IsSpawningCoopClone = kind == PlayerCloneKind.LocalSecond;

            // Create inactive dummy parent so Awake() doesn't fire on cloned components
            // (PlayerRagdoll.Awake calls EnforceFullRagdoll which doesn't exist in this build)
            GameObject dummyParent = new GameObject("DummyParent");
            dummyParent.SetActive(false);
            try
            {
                clone = UnityEngine.Object.Instantiate(original, dummyParent.transform);
            }
            catch (Exception ex)
            {
                log?.LogError("Instantiate player clone failed: " + ex);
                UnityEngine.Object.DestroyImmediate(dummyParent);
                return null;
            }
            finally
            {
                IsSpawningCoopClone = false;
            }

            if (clone == null)
            {
                log?.LogError("Instantiate returned null for player clone.");
                UnityEngine.Object.DestroyImmediate(dummyParent);
                return null;
            }

            // Strip ragdoll component before anything awakens
            // (PlayerRagdoll.Awake calls EnforceFullRagdoll which doesn't exist in this build)
            Component ragdoll = clone.GetComponent("PlayerRagdoll");
            if (ragdoll != null)
            {
                UnityEngine.Object.DestroyImmediate(ragdoll);
                log?.LogInfo("Player clone: destroyed PlayerRagdoll to prevent EnforceFullRagdoll crash.");
            }

            // Reparent to root and activate
            clone.transform.SetParent(null, false);
            clone.transform.position = original.transform.position + positionOffset;
            clone.SetActive(true);
            UnityEngine.Object.DestroyImmediate(dummyParent);

            if (kind == PlayerCloneKind.LocalSecond)
            {
                if (clone.GetComponent<CoopPlayerMarker>() == null)
                    clone.AddComponent<CoopPlayerMarker>();
            }

            clone.name = objectName;
            clone.SetActive(true);

            StripGameplayComponents(clone, kind);
            CleanupVision(clone, kind);
            RemoveUnityLights(clone);
            EnsureVisible(clone);

            if (kind == PlayerCloneKind.Remote)
            {
                if (clone.GetComponent<SecondPlayerAnimController>() == null)
                    clone.AddComponent<SecondPlayerAnimController>();
            }
            else if (kind == PlayerCloneKind.LocalSecond)
            {
                SecondPlayerAnimController remoteAnim = clone.GetComponent<SecondPlayerAnimController>();
                if (remoteAnim != null)
                    UnityEngine.Object.Destroy(remoteAnim);
            }

            if (kind == PlayerCloneKind.LocalSecond)
                PrepareLocalCoopPlayer(clone, sourcePlayer, log);

            log?.LogInfo(
                "Spawned player clone: "
                + objectName
                + " at "
                + clone.transform.position
                + " ("
                + kind
                + ")");
            return clone;
        }

        private static void PrepareLocalCoopPlayer(GameObject clone, Player template, ManualLogSource log)
        {
            Player player = clone.GetComponent<Player>();
            if (player == null)
            {
                log?.LogError("Local co-op clone is missing Player component after spawn.");
                return;
            }

            CoopPlayerBootstrap.RunLightweightStart(player, template);

            LocalPlayerBodyController bodyController = clone.GetComponent<LocalPlayerBodyController>();
            if (bodyController == null)
                bodyController = clone.AddComponent<LocalPlayerBodyController>();

            if (bodyController != null && template != null)
                bodyController.SetupFromMain(template);

            PlayerControlRouter.RegisterSecond(player);
            player.immobilised = true;
            log?.LogInfo("Local co-op second player ready (full Player mechanics).");
        }

        /// <summary>
        /// Destroys gameplay-specific components on the clone based on its kind.
        /// </summary>
        public static void StripGameplayComponents(GameObject clone, PlayerCloneKind kind)
        {
            if (kind == PlayerCloneKind.Remote)
            {
                // Must use DestroyImmediate so CharBase from Player is gone
                // before a standalone CharBase is added in AddCharBase().
                // Object.Destroy (non-immediate) queues destruction to end of
                // frame, leaving the proxy without CharBase after frame 1.
                Player player = clone.GetComponent<Player>();
                if (player != null)
                    UnityEngine.Object.DestroyImmediate(player);

                PlayerSkills skills = clone.GetComponent<PlayerSkills>();
                if (skills != null)
                    UnityEngine.Object.DestroyImmediate(skills);

                SkillsMenu menu = clone.GetComponent<SkillsMenu>();
                if (menu != null)
                    UnityEngine.Object.DestroyImmediate(menu);

                // Destroy crosshair component + its line game objects to prevent duplicate
                // crosshair lines on screen (Crosshair.Update checks Player.Instance.aiming,
                // so the proxy would mirror the local player's aim state)
                Crosshair crosshair = clone.GetComponentInChildren<Crosshair>(true);
                if (crosshair != null)
                {
                    if (crosshair.line1 != null)
                        UnityEngine.Object.DestroyImmediate(crosshair.line1.gameObject);
                    if (crosshair.line2 != null)
                        UnityEngine.Object.DestroyImmediate(crosshair.line2.gameObject);
                    UnityEngine.Object.DestroyImmediate(crosshair);
                }
            }

            if (kind == PlayerCloneKind.Remote)
            {
                foreach (Collider col in clone.GetComponentsInChildren<Collider>(true))
                    col.enabled = false;

                Rigidbody rb = clone.GetComponent<Rigidbody>();
                if (rb != null)
                    UnityEngine.Object.DestroyImmediate(rb);

                Rigidbody2D rb2d = clone.GetComponent<Rigidbody2D>();
                if (rb2d != null)
                    UnityEngine.Object.DestroyImmediate(rb2d);

                // Destroy AudioListener so all 2D sounds (Play(string,float)) use
                // the local player's listener, not the proxy's cloned listener.
                foreach (AudioListener al in clone.GetComponentsInChildren<AudioListener>(true))
                    UnityEngine.Object.DestroyImmediate(al);
            }
        }

        /// <summary>
        /// Disables or destroys vision/lighting components on the clone.
        /// Keeps the Flashlight child (Light2D) for networked light sync.
        /// </summary>
        public static void CleanupVision(GameObject root, PlayerCloneKind kind)
        {
            if (kind == PlayerCloneKind.Remote)
            {
                DestroyComponentByName(root, "PlayerVision");
                DestroyComponentByName(root, "VisionCone");
                DestroyComponentByName(root, "PlayerLight");

                foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                {
                    string name = child.name.ToLowerInvariant();
                    if (!name.Contains("vision")
                        && !name.Contains("cone"))
                        continue;

                    child.gameObject.SetActive(false);
                }

                // Keep Flashlight child but ensure its Light2D component is disabled initially
                Transform flashT = root.transform.Find("Flashlight");
                if (flashT != null)
                    flashT.gameObject.SetActive(false);

                // Cloned PlayerLightDot is local ambient vision — if left in logicLights with
                // lightsPlayer it reads as a second lantern on every peer. Neutralize hard.
                Transform lightDot = root.transform.Find("PlayerLightDot");
                // Find misses inactive; also scan direct children.
                if (lightDot == null)
                {
                    for (int i = 0; i < root.transform.childCount; i++)
                    {
                        Transform ch = root.transform.GetChild(i);
                        if (ch != null && ch.name == "PlayerLightDot")
                        {
                            lightDot = ch;
                            break;
                        }
                    }
                }
                if (lightDot != null)
                {
                    Light2D lt = lightDot.GetComponent<Light2D>();
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
                    lightDot.gameObject.SetActive(false);
                }
            }

            PlayerVisionController.From(root)?.SetAllVisionDisabled();
        }

        /// <summary>
        /// Destroys all Unity Light components on the clone (they conflict with Darkwood's Light2D system).
        /// </summary>
        public static void RemoveUnityLights(GameObject root)
        {
            foreach (Light light in root.GetComponentsInChildren<Light>(true))
                UnityEngine.Object.Destroy(light);
        }

        private static void DestroyComponentByName(GameObject root, string typeName)
        {
            Component component = root.GetComponent(typeName);
            if (component != null)
                UnityEngine.Object.Destroy(component);

            foreach (Component child in root.GetComponentsInChildren<Component>(true))
            {
                if (child != null && child.GetType().Name == typeName)
                    UnityEngine.Object.Destroy(child);
            }
        }

        private static void EnsureVisible(GameObject clone)
        {
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = true;

            if (clone.transform.localScale.x < 0.95f)
                clone.transform.localScale = Vector3.one;
        }
    }
}
