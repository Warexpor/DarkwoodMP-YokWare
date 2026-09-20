using System;
using UnityEngine;

namespace DWMPHorde
{
    public static partial class MainMenuMultiplayerInject
    {
        /// <summary>
        /// Resize the root BoxCollider to the visible art/label. quitBtn's hitbox is too
        /// narrow for MULTIPLAYER and too large for Options-sized panel rows. Button
        /// raycasts that collider for hover/click.
        /// </summary>
        internal static void FitButtonHitbox(GameObject buttonGo)
        {
            if (buttonGo == null || !buttonGo)
                return;

            Renderer visual = null;
            Transform art = buttonGo.transform.Find("YokWare_BtnArt");
            if (art != null)
                visual = art.GetComponent<Renderer>();
            if (visual == null)
            {
                tk2dTextMesh tm = buttonGo.GetComponentInChildren<tk2dTextMesh>(true);
                if (tm != null)
                    visual = tm.GetComponent<Renderer>();
            }
            if (visual == null)
                return;

            // Art quad includes transparent padding; shrink to opaque glyph UVs so the
            // hitbox matches the smaller idle letters (not the full padded canvas).
            Bounds wb;
            if (art != null && TryOpaqueQuadWorldBounds(art, visual, out wb))
            {
                // ok
            }
            else
            {
                wb = visual.bounds;
            }

            if (wb.size.sqrMagnitude < 0.01f)
                return;

            // Flat CamUI quads have almost no thickness on one world axis; raycasts need depth.
            const float minThick = 12f;
            Vector3 size = wb.size;
            if (size.x <= size.y && size.x <= size.z)
                wb.Expand(new Vector3(Mathf.Max(0f, minThick - size.x), 0f, 0f));
            else if (size.y <= size.z)
                wb.Expand(new Vector3(0f, Mathf.Max(0f, minThick - size.y), 0f));
            else
                wb.Expand(new Vector3(0f, 0f, Mathf.Max(0f, minThick - size.z)));

            // Comfort pad so hover engages slightly outside the glyph.
            wb.Expand(new Vector3(wb.size.x * 0.10f, wb.size.y * 0.10f, wb.size.z * 0.10f));

            Transform t = buttonGo.transform;
            Vector3 c = wb.center;
            Vector3 e = wb.extents;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int xi = -1; xi <= 1; xi += 2)
            {
                for (int yi = -1; yi <= 1; yi += 2)
                {
                    for (int zi = -1; zi <= 1; zi += 2)
                    {
                        Vector3 local = t.InverseTransformPoint(c + new Vector3(e.x * xi, e.y * yi, e.z * zi));
                        min = Vector3.Min(min, local);
                        max = Vector3.Max(max, local);
                    }
                }
            }

            Vector3 localSize = max - min;
            if (localSize.x < 0.5f) localSize.x = 0.5f;
            if (localSize.y < 0.5f) localSize.y = 0.5f;
            if (localSize.z < 0.5f) localSize.z = 0.5f;

            BoxCollider box = buttonGo.GetComponent<BoxCollider>();
            if (box == null)
                box = buttonGo.AddComponent<BoxCollider>();
            box.center = (min + max) * 0.5f;
            box.size = localSize;
            box.enabled = true;

            // Only the root collider should receive the menu raycast.
            StripChildColliders(buttonGo);
        }

        /// <summary>
        /// World bounds of the opaque region of the CamUI-facing unit quad (−0.5..0.5).
        /// Uses idle texture when present so padding/bloom does not inflate the hitbox.
        /// </summary>
        private static bool TryOpaqueQuadWorldBounds(Transform art, Renderer visual, out Bounds worldBounds)
        {
            worldBounds = default;
            if (art == null || visual == null)
                return false;

            Texture2D tex = null;
            if (visual.sharedMaterial != null)
                tex = visual.sharedMaterial.mainTexture as Texture2D;

            // Prefer the idle resource; its letter core ignores hover bloom padding.
            if (!MenuButtonArt.TryGetIdleOpaqueUv(out float u0, out float v0, out float u1, out float v1))
            {
                if (tex == null || !TryOpaqueUv(tex, 28, out u0, out v0, out u1, out v1))
                    return false;
            }

            // Unit quad mesh: UV (0,0)=(-0.5,-0.5), (1,1)=(0.5,0.5)
            Vector3[] corners =
            {
                art.TransformPoint(new Vector3(Mathf.Lerp(-0.5f, 0.5f, u0), Mathf.Lerp(-0.5f, 0.5f, v0), 0f)),
                art.TransformPoint(new Vector3(Mathf.Lerp(-0.5f, 0.5f, u1), Mathf.Lerp(-0.5f, 0.5f, v0), 0f)),
                art.TransformPoint(new Vector3(Mathf.Lerp(-0.5f, 0.5f, u0), Mathf.Lerp(-0.5f, 0.5f, v1), 0f)),
                art.TransformPoint(new Vector3(Mathf.Lerp(-0.5f, 0.5f, u1), Mathf.Lerp(-0.5f, 0.5f, v1), 0f)),
            };
            worldBounds = new Bounds(corners[0], Vector3.zero);
            for (int i = 1; i < corners.Length; i++)
                worldBounds.Encapsulate(corners[i]);
            return worldBounds.size.sqrMagnitude > 0.01f;
        }

        private static bool TryOpaqueUv(Texture2D tex, byte alphaThr,
            out float u0, out float v0, out float u1, out float v1)
        {
            u0 = v0 = 0f;
            u1 = v1 = 1f;
            if (tex == null)
                return false;
            try
            {
                Color32[] px = tex.GetPixels32();
                int w = tex.width;
                int h = tex.height;
                int xMin = w, xMax = -1, yMin = h, yMax = -1;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (px[row + x].a <= alphaThr)
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

    }
}
