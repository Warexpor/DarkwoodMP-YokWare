using System;
using System.IO;
using System.Reflection;
using DWMPHorde.Logging;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// Embedded title-button art (beveled MULTIPLAYER idle/hover).
    /// CamUI looks down (Euler 90) — UI lives in screen-pixel XZ; size from row spacing,
    /// not BoxCollider AABB.y (near-zero / undersized vs PLAY sprites).
    /// </summary>
    internal static class MenuButtonArt
    {
        private const string IdleResource = "DWMPHorde.Resources.MenuButtons.multiplayer_idle.png";
        private const string HoverResource = "DWMPHorde.Resources.MenuButtons.multiplayer_hover.png";
        /// <summary>Title row gap in PositionMe offset units (matches inject RowSpacing).</summary>
        private const float TitleRowSpacing = 60f;
        /// <summary>
        /// Visible letter height vs one title row. Keep under ~0.6 so MULTIPLAYER
        /// matches PLAY/OPTIONS face size (0.82 overshot and looked huge).
        /// </summary>
        private const float LetterHeightFracOfRow = 0.55f;

        private static Texture2D _idle;
        private static Texture2D _hover;
        private static bool _loadFailed;
        private static Mesh _quad;

        public static bool TryAttachMultiplayerArt(GameObject buttonGo)
        {
            if (buttonGo == null)
                return false;
            Texture2D idle = Load(IdleResource, ref _idle);
            Texture2D hover = Load(HoverResource, ref _hover);
            if (idle == null)
                return false;

            Shader shader = Shader.Find("tk2d/BlendVertexColor")
                ?? Shader.Find("Sprites/Default")
                ?? Shader.Find("Unlit/Transparent");
            if (shader == null)
            {
                ModLog.Warn(LogCat.Session, "Menu button art: no usable shader");
                return false;
            }

            float aspect = idle.height > 0 ? (float)idle.width / (float)idle.height : 5.35f;
            float rowPx = TitleRowSpacing * Core.ResolutionHeightModifier;
            // Opaque letter band may be shorter than the PNG (hover bloom padding).
            float contentFrac = EstimateOpaqueHeightFrac(idle);
            float targetH = rowPx * LetterHeightFracOfRow / contentFrac;
            float targetW = targetH * aspect;

            // Cap against quit/PLAY mesh face so we never exceed native title letter height.
            if (TryMeshFace(buttonGo, out _, out float meshH) && meshH > 1f)
            {
                float meshLetterH = meshH * 0.72f / contentFrac;
                if (meshLetterH > 8f && meshLetterH < targetH)
                {
                    targetH = meshLetterH;
                    targetW = targetH * aspect;
                }
            }

            if (targetW < 8f || targetH < 8f)
            {
                ModLog.Warn(LogCat.Session,
                    "Menu button art: computed size " + targetW.ToString("F1")
                    + "x" + targetH.ToString("F1") + " — falling back to text");
                return false;
            }

            Transform existing = buttonGo.transform.Find("YokWare_BtnArt");
            if (existing != null)
                UnityEngine.Object.DestroyImmediate(existing.gameObject);

            GameObject camObj = Core.CamUI;
            Camera cam = camObj != null ? camObj.GetComponent<Camera>() : null;
            Collider col = buttonGo.GetComponent<Collider>();

            var art = new GameObject("YokWare_BtnArt");
            art.layer = buttonGo.layer;

            var mr = art.AddComponent<MeshRenderer>();
            var mf = art.AddComponent<MeshFilter>();
            mf.sharedMesh = SharedQuad();

            var mat = new Material(shader);
            mat.mainTexture = idle;
            mat.color = Color.white;
            mr.sharedMaterial = mat;
            mr.enabled = true;

            PlaceFacingCam(art.transform, buttonGo.transform, col, cam, targetW, targetH);
            art.transform.SetParent(buttonGo.transform, true);

            var swap = art.AddComponent<MenuButtonArtHover>();
            swap.Idle = idle;
            swap.Hover = hover ?? idle;
            swap.Button = buttonGo.GetComponent<Button>();
            swap.Renderer = mr;
            swap.Follow = buttonGo.transform;
            swap.Col = col;
            swap.TargetW = targetW;
            swap.TargetH = targetH;

            // Hitbox must cover the long MULTIPLAYER glyph, not the short EXIT collider.
            MainMenuMultiplayerInject.FitButtonHitbox(buttonGo);

            ModLog.Event(LogCat.Session,
                "MULTIPLAYER art attached shader=" + shader.name
                + " size=" + targetW.ToString("F1") + "x" + targetH.ToString("F1")
                + " rowPx=" + rowPx.ToString("F1"));
            return true;
        }

        /// <summary>
        /// UV rect (0–1) of idle texture opaque pixels — hitbox uses this, not full canvas padding.
        /// </summary>
        public static bool TryGetIdleOpaqueUv(out float u0, out float v0, out float u1, out float v1)
        {
            u0 = v0 = 0f;
            u1 = v1 = 1f;
            Texture2D idle = Load(IdleResource, ref _idle);
            if (idle == null)
                return false;
            try
            {
                Color32[] px = idle.GetPixels32();
                int w = idle.width;
                int h = idle.height;
                int xMin = w, xMax = -1, yMin = h, yMax = -1;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (px[row + x].a <= 28)
                            continue;
                        if (x < xMin) xMin = x;
                        if (x > xMax) xMax = x;
                        if (y < yMin) yMin = y;
                        if (y > yMax) yMax = y;
                    }
                }
                if (xMax < xMin || yMax < yMin)
                    return false;
                u0 = xMin / (float)w;
                u1 = (xMax + 1) / (float)w;
                v0 = yMin / (float)h;
                v1 = (yMax + 1) / (float)h;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryMeshFace(GameObject go, out float faceW, out float faceH)
        {
            faceW = faceH = 0f;
            MeshFilter mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
                return false;
            Vector3 ms = mf.sharedMesh.bounds.size;
            Vector3 lossy = go.transform.lossyScale;
            faceW = Mathf.Abs(ms.x * lossy.x);
            faceH = Mathf.Abs(ms.y * lossy.y);
            if (faceH < 1f && Mathf.Abs(ms.z * lossy.z) > faceH)
                faceH = Mathf.Abs(ms.z * lossy.z);
            return faceW > 1f && faceH > 1f;
        }

        /// <summary>
        /// Fraction of texture height that has visible (non-near-zero alpha) pixels.
        /// Used so hover bloom padding does not shrink the letter faces.
        /// </summary>
        private static float EstimateOpaqueHeightFrac(Texture2D tex)
        {
            if (tex == null || tex.height < 2)
                return 1f;
            try
            {
                Color32[] px = tex.GetPixels32();
                int w = tex.width;
                int h = tex.height;
                int yMin = h, yMax = -1;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (px[row + x].a > 24)
                        {
                            if (y < yMin) yMin = y;
                            if (y > yMax) yMax = y;
                            break;
                        }
                    }
                }
                if (yMax < yMin)
                    return 1f;
                float frac = (yMax - yMin + 1) / (float)h;
                return Mathf.Clamp(frac, 0.35f, 1f);
            }
            catch
            {
                return 1f;
            }
        }

        private static void PlaceFacingCam(Transform art, Transform follow, Collider col,
            Camera cam, float targetW, float targetH)
        {
            // Anchor to the button transform (not collider center) so hitbox refits
            // don't create a feedback loop with LateUpdate.
            Vector3 pos = follow != null ? follow.position : (col != null ? col.bounds.center : Vector3.zero);
            Quaternion rot = cam != null ? cam.transform.rotation : follow.rotation;
            if (cam != null)
                pos -= cam.transform.forward * 0.5f;

            Transform parent = art.parent;
            if (parent != null)
                art.SetParent(null, true);
            art.SetPositionAndRotation(pos, rot);
            art.localScale = new Vector3(targetW, targetH, 1f);
            if (parent != null)
                art.SetParent(parent, true);
        }

        private static Mesh SharedQuad()
        {
            if (_quad != null)
                return _quad;
            _quad = new Mesh { name = "YokWare_BtnQuad" };
            _quad.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f)
            };
            _quad.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f)
            };
            _quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            _quad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            _quad.RecalculateBounds();
            return _quad;
        }

        private static Texture2D Load(string resourceName, ref Texture2D cache)
        {
            if (cache != null)
                return cache;
            if (_loadFailed)
                return null;
            try
            {
                byte[] bytes = ReadResource(resourceName);
                if (bytes == null)
                {
                    _loadFailed = true;
                    ModLog.Warn(LogCat.Session, "Menu button art missing: " + resourceName);
                    return null;
                }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, bytes))
                {
                    _loadFailed = true;
                    ModLog.Warn(LogCat.Session, "Menu button art decode failed: " + resourceName);
                    return null;
                }
                tex.name = resourceName;
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                cache = tex;
                return cache;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                ModLog.Warn(LogCat.Session, "Menu button art: " + ex.Message);
                return null;
            }
        }

        private static byte[] ReadResource(string resourceName)
        {
            Assembly asm = typeof(MenuButtonArt).Assembly;
            using (Stream stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    return null;
                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return bytes;
            }
        }

        private sealed class MenuButtonArtHover : MonoBehaviour
        {
            public Texture2D Idle;
            public Texture2D Hover;
            public Button Button;
            public MeshRenderer Renderer;
            public Transform Follow;
            public Collider Col;
            public float TargetW;
            public float TargetH;
            private bool _wasHover;

            private void LateUpdate()
            {
                if (Follow == null || !Follow)
                {
                    Destroy(gameObject);
                    return;
                }

                GameObject camObj = Core.CamUI;
                Camera cam = camObj != null ? camObj.GetComponent<Camera>() : null;
                PlaceFacingCam(transform, Follow, Col, cam, TargetW, TargetH);

                if (Renderer == null || Renderer.sharedMaterial == null)
                    return;
                bool hover = Button != null && Button.rolledOver && !Button.disabled;
                if (hover == _wasHover)
                    return;
                _wasHover = hover;
                Renderer.sharedMaterial.mainTexture = hover ? Hover : Idle;
            }
        }
    }
}
