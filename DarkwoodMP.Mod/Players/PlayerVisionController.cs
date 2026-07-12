using System.Reflection;
using UnityEngine;

namespace DWMPHorde.Players
{
    /// <summary>
    /// Toggles Darkwood vision the same way as Player.switchVisibilty / flashlight equip:
    /// FOV cone via PlayerFOVLogic (+ synced PlayerFOVLight / PlayerFOVLightDot) vs handheld Flashlight.
    /// </summary>
    public sealed class PlayerVisionController
    {
        private static readonly MethodInfo GetFovAngleMethod =
            typeof(Player).GetMethod("getFOVAngle", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly GameObject _root;
        private Light2D _fovLogic;
        private Light2D _fovLight;
        private Light2D _fovDot;
        private Light2D _lightDot;
        private Light2D _flashlight;

        private PlayerVisionController(GameObject root)
        {
            _root = root;
            BindLights();
        }

        /// <summary>
        /// Creates a controller for the given root GameObject, or null if root is null.
        /// </summary>
        public static PlayerVisionController From(GameObject root)
        {
            return root == null ? null : new PlayerVisionController(root);
        }

        private void BindLights()
        {
            Transform root = _root.transform;
            _fovLogic = FindLight(root, "PlayerFOVLogic");
            _fovLight = FindLight(root, "PlayerFOVLight");
            _fovDot = FindLight(root, "PlayerFOVLightDot");
            _lightDot = FindLight(root, "PlayerLightDot");
            _flashlight = FindLight(root, "Flashlight");
        }

        private static Light2D FindLight(Transform root, string childName)
        {
            Transform child = root.Find(childName);
            return child == null ? null : child.GetComponent<Light2D>();
        }

        /// <summary>
        /// Enables/disables the FOV cone and ambient dot lights on the proxy.
        /// NOTE: the ground shadow ("Shadow" child) is intentionally excluded —
        /// it is toggled separately so that exiting spectator mode doesn't
        /// permanently hide the shadow.
        /// </summary>
        public void SetVisionConeEnabled(bool enabled)
        {
            if (_fovLogic != null)
                _fovLogic.gameObject.SetActive(enabled);

            if (_fovLight != null)
                _fovLight.gameObject.SetActive(enabled);

            if (_fovDot != null)
                _fovDot.gameObject.SetActive(enabled);

            if (_lightDot != null)
                _lightDot.gameObject.SetActive(enabled);
        }

        /// <summary>
        /// Spectator when local FOV is dead/off: restore both pieces of player vision —
        /// FOV cone (Logic/Light) AND ambient vision circle (FOVLightDot).
        /// Direction follows the proxy transform (PlayerState torso/legs already rotate it).
        /// </summary>
        public void ApplySpectatorConeDefaults()
        {
            // Typical hideout FOV-ish values (vanilla getFOVAngle varies with items).
            const float coneAngle = 80f;
            const float coneRadius = 18f;
            const float circleRadius = 9f;
            ApplyConeShape(_fovLogic, coneAngle, coneRadius);
            ApplyConeShape(_fovLight, coneAngle, coneRadius * 0.9f);
            // FOVLightDot is the ambient *circle* around the player — never force a cone angle.
            ApplyCircleShape(_fovDot, circleRadius);
            if (_lightDot != null)
            {
                _lightDot.gameObject.SetActive(true);
                if (_lightDot.LightRadius < 1f)
                    _lightDot.LightRadius = 8f;
            }
            SetVisionConeEnabled(true);
        }

        private static void ApplyConeShape(Light2D light, float angle, float radius)
        {
            if (light == null) return;
            light.gameObject.SetActive(true);
            // 360 = full circle (no "cone"); force a real wedge when missing/wrong.
            if (light.LightConeAngle <= 0f || light.LightConeAngle >= 359f)
                light.LightConeAngle = angle;
            if (light.LightRadius < 1f)
                light.LightRadius = radius;
        }

        /// <summary>Ambient vision circle (PlayerFOVLightDot) — full 360, not a wedge.</summary>
        private static void ApplyCircleShape(Light2D light, float radius)
        {
            if (light == null) return;
            light.gameObject.SetActive(true);
            light.LightConeAngle = 360f;
            if (light.LightRadius < 1f)
                light.LightRadius = radius;
        }

        /// <summary>
        /// Enables or disables the Flashlight GameObject.
        /// </summary>
        public void SetFlashlightEnabled(bool enabled)
        {
            if (_flashlight != null)
                _flashlight.gameObject.SetActive(enabled);
        }

        /// <summary>
        /// Disables both the vision cone and the flashlight.
        /// </summary>
        public void SetAllVisionDisabled()
        {
            SetVisionConeEnabled(false);
            SetFlashlightEnabled(false);
        }

        /// <summary>
        /// Copies live FOV cone angles/radius from another PlayerVisionController (after getFOVAngle has run on the source).
        /// </summary>
        public void SyncFovConeFrom(PlayerVisionController source)
        {
            if (source == null)
                return;

            CopyLightCone(_fovLogic, source._fovLogic);
            CopyLightCone(_fovLight, source._fovLight);
            CopyLightCone(_fovDot, source._fovDot);
            // Do NOT copy PlayerLightDot (lantern ambient) — that is per-player via net sync.
            // Copying it made the spectated proxy inherit the local player's lantern radius.
        }

        /// <summary>
        /// Refreshes the main player's FOV angles then copies them to this controller's lights.
        /// Use this for one-time syncs (e.g. on enter spectate).
        /// </summary>
        public void SyncFovConeFrom(Player main)
        {
            if (main == null)
                return;

            RefreshMainFov(main);
            CopyLightCone(_fovLogic, main.FOVLogic);
            CopyLightCone(_fovLight, FindLight(main.transform, "PlayerFOVLight"));
            CopyLightCone(_fovDot, main.FOVDot);
            // Lantern ambient is network-owned on remotes — never copy local lightDot here.
        }

        /// <summary>
        /// Copies current FOV values from the main player WITHOUT calling RefreshMainFov.
        /// Use this for per-frame syncs — the Player's own Update already runs getFOVAngle().
        /// </summary>
        public void CopyFovValuesFrom(Player main)
        {
            if (main == null)
                return;

            CopyLightCone(_fovLogic, main.FOVLogic);
            CopyLightCone(_fovLight, FindLight(main.transform, "PlayerFOVLight"));
            CopyLightCone(_fovDot, main.FOVDot);
            // Lantern ambient is network-owned on remotes — never copy local lightDot here.
        }

        /// <summary>
        /// Invokes the private getFOVAngle method so the main player's FOV cone values are up-to-date before syncing.
        /// </summary>
        public static void RefreshMainFov(Player main)
        {
            if (main == null || GetFovAngleMethod == null)
                return;

            GetFovAngleMethod.Invoke(main, null);
        }

        /// <summary>
        /// Returns true if the main player's Flashlight child GameObject is active.
        /// </summary>
        public static bool IsFlashlightActiveOn(Player main)
        {
            if (main == null)
                return false;

            Transform flash = main.transform.Find("Flashlight");
            return flash != null && flash.gameObject.activeInHierarchy;
        }

        private static void CopyLightCone(Light2D dst, Light2D src)
        {
            if (dst == null || src == null)
                return;

            dst.LightConeAngle = src.LightConeAngle;
            dst.LightRadius = src.LightRadius;
            dst.LightColor = src.LightColor;
        }

        /// <summary>
        /// Ensure FOVLightDot stays a full ambient circle (spectate sync can inherit a wedge).
        /// </summary>
        public void EnsureVisionCircle()
        {
            if (_fovDot == null) return;
            _fovDot.gameObject.SetActive(true);
            // Wedge angles look like a second cone; force circle for ambient vision.
            if (_fovDot.LightConeAngle > 0f && _fovDot.LightConeAngle < 359f)
                _fovDot.LightConeAngle = 360f;
            if (_fovDot.LightRadius < 1f)
                _fovDot.LightRadius = 9f;
        }
    }
}
