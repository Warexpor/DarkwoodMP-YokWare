using System;
using System.IO;
using System.Reflection;
using DWMPHorde.Logging;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// Embedded title-button art (MULTIPLAYER idle/hover, and СЕТЕВАЯ ИГРА for Russian). Pixel
    /// art built from the vanilla menu atlas glyphs of that language with the vanilla _0 → _1
    /// rollover look; both PNGs of a language share one canvas (the hover's glow padding),
    /// drawn point-filtered at the EXIT sprite's texel size. The art follows the game's
    /// language setting. CamUI looks down (Euler 90); UI lives in screen-pixel XZ.
    /// </summary>
    internal static class MenuButtonArt
    {
        private sealed class ArtSet
        {
            public string IdleResource, HoverResource;
            public Texture2D Idle, Hover;
            public bool Failed;
        }

        private static readonly ArtSet English = new ArtSet // process-scoped: loaded asset
        {
            IdleResource = "DWMPHorde.Resources.MenuButtons.multiplayer_idle.png",
            HoverResource = "DWMPHorde.Resources.MenuButtons.multiplayer_hover.png"
        };
        private static readonly ArtSet Russian = new ArtSet // process-scoped: loaded asset
        {
            IdleResource = "DWMPHorde.Resources.MenuButtons.multiplayer_idle_ru.png",
            HoverResource = "DWMPHorde.Resources.MenuButtons.multiplayer_hover_ru.png"
        };
        private static Mesh _quad; // process-scoped: loaded asset

        /// <summary>The art for the game's language (English when the Russian set cannot load).</summary>
        private static ArtSet Current()
        {
            ArtSet set = Loc.Russian ? Russian : English;
            if (!EnsureLoaded(set) && set != English)
                set = English;
            return set.Idle != null ? set : null;
        }

        private static bool EnsureLoaded(ArtSet set)
        {
            if (set.Idle != null)
                return true;
            if (set.Failed)
                return false;
            set.Idle = Load(set.IdleResource);
            set.Hover = Load(set.HoverResource);
            set.Failed = set.Idle == null;
            return !set.Failed;
        }

        public static bool TryAttachMultiplayerArt(GameObject buttonGo)
        {
            if (buttonGo == null)
                return false;
            ArtSet set = Current();
            if (set == null)
                return false;
            Texture2D idle = set.Idle;
            Texture2D hover = set.Hover;

            Shader shader = Shader.Find("tk2d/BlendVertexColor")
                ?? Shader.Find("Sprites/Default")
                ?? Shader.Find("Unlit/Transparent");
            if (shader == null)
            {
                ModLog.Warn(LogCat.Session, "Menu button art: no usable shader");
                return false;
            }

            // One art texel = one texel of the EXIT sprite this button was cloned from, so the
            // letters land on the same pixel grid as PLAY/OPTIONS/EXIT.
            if (!TryVanillaTexel(buttonGo, out float texel))
            {
                ModLog.Warn(LogCat.Session, "Menu button art: no EXIT sprite to size from — falling back to text");
                return false;
            }
            float targetW = idle.width * texel;
            float targetH = idle.height * texel;

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
            swap.Set = set;
            swap.Button = buttonGo.GetComponent<Button>();
            swap.Renderer = mr;
            swap.Follow = buttonGo.transform;
            swap.Col = col;
            swap.Texel = texel;

            // Hitbox must cover the long MULTIPLAYER glyph, not the short EXIT collider.
            MainMenuMultiplayerInject.FitButtonHitbox(buttonGo);

            ModLog.Event(LogCat.Session,
                "MULTIPLAYER art attached shader=" + shader.name
                + " size=" + targetW.ToString("F1") + "x" + targetH.ToString("F1")
                + " texel=" + texel.ToString("F2"));
            return true;
        }

        /// <summary>
        /// UV rect (0–1) of idle texture opaque pixels. The hitbox uses this,
        /// not the full canvas padding.
        /// </summary>
        public static bool TryGetIdleOpaqueUv(out float u0, out float v0, out float u1, out float v1)
        {
            u0 = v0 = 0f;
            u1 = v1 = 1f;
            ArtSet set = Current();
            Texture2D idle = set != null ? set.Idle : null;
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

        /// <summary>
        /// World size of one texel of the cloned EXIT sprite: its mesh face height over the
        /// sprite definition's trimmed height in texels (the menu atlas has texelSize 1).
        /// </summary>
        private static bool TryVanillaTexel(GameObject go, out float texel)
        {
            texel = 0f;
            tk2dBaseSprite sprite = go.GetComponent<tk2dBaseSprite>();
            tk2dSpriteDefinition def = sprite != null ? sprite.GetCurrentSpriteDef() : null;
            if (def == null || def.boundsData == null || def.boundsData.Length < 2 || def.boundsData[1].y < 1f)
                return false;
            if (!TryMeshFace(go, out _, out float meshH))
                return false;
            texel = meshH / def.boundsData[1].y;
            return texel > 0f;
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

        private static Texture2D Load(string resourceName)
        {
            try
            {
                byte[] bytes = ReadResource(resourceName);
                if (bytes == null)
                {
                    ModLog.Warn(LogCat.Session, "Menu button art missing: " + resourceName);
                    return null;
                }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, bytes))
                {
                    ModLog.Warn(LogCat.Session, "Menu button art decode failed: " + resourceName);
                    return null;
                }
                tex.name = resourceName;
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                return tex;
            }
            catch (Exception ex)
            {
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
            public ArtSet Set;
            public Button Button;
            public MeshRenderer Renderer;
            public Transform Follow;
            public Collider Col;
            public float Texel;
            private bool _wasHover;

            private void LateUpdate()
            {
                if (Follow == null || !Follow)
                {
                    Destroy(gameObject);
                    return;
                }

                // The language changed in Options: the other word, its own width and hitbox.
                ArtSet now = Current();
                bool relang = now != null && now != Set;
                if (relang)
                    Set = now;

                GameObject camObj = Core.CamUI;
                Camera cam = camObj != null ? camObj.GetComponent<Camera>() : null;
                PlaceFacingCam(transform, Follow, Col, cam, Set.Idle.width * Texel, Set.Idle.height * Texel);
                if (relang)
                    MainMenuMultiplayerInject.FitButtonHitbox(Follow.gameObject);

                if (Renderer == null || Renderer.sharedMaterial == null)
                    return;
                bool hover = Button != null && Button.rolledOver && !Button.disabled;
                if (hover == _wasHover && !relang)
                    return;
                _wasHover = hover;
                Renderer.sharedMaterial.mainTexture = hover && Set.Hover != null ? Set.Hover : Set.Idle;
            }
        }
    }
}
