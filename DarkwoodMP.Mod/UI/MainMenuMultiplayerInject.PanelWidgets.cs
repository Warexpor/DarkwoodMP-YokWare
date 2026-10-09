using System;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde
{
    public static partial class MainMenuMultiplayerInject
    {
        private static GameObject CloneButton(GameObject template, Transform parent,
            string name, string label, Action onFire, string tagKind, bool useTextLabel = true)
        {
            GameObject go = UnityEngine.Object.Instantiate(template, parent);
            go.name = name;
            go.SetActive(true);
            Tag(go, tagKind);

            LocalizedText[] locs = go.GetComponentsInChildren<LocalizedText>(true);
            for (int i = 0; i < locs.Length; i++)
            {
                if (locs[i] != null)
                    UnityEngine.Object.DestroyImmediate(locs[i]);
            }
            Renderer[] rends = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rends.Length; i++)
            {
                if (rends[i] != null)
                    rends[i].enabled = false;
            }

            StripChildColliders(go);
            Collider rootCol = go.GetComponent<Collider>();
            if (rootCol != null)
                rootCol.enabled = true;

            Button btn = go.GetComponent<Button>();
            if (btn != null)
            {
                btn.function = "";
                btn.popupType = "";
                btn.localized = false;
                btn.sprite = null;
                btn.disabled = false;
                btn.noRollover = false;
                btn.OnFire = () => Guarded(onFire);
            }

            if (useTextLabel)
            {
                // Panel rows: same outlined bitmap look as Video / Profiles menus.
                tk2dTextMesh tm = CreateLabel(go.transform, label, settingsStyle: true);
                if (tm != null)
                {
                    tm.transform.localScale *= PanelLabelScale;
                    if (btn != null)
                        ApplySettingsButtonColors(btn, tm);
                }
                FitButtonHitbox(go);
            }
            return go;
        }

        /// <summary>
        /// Vanilla Options hover = idle gray → rollover white. We had forced both to white.
        /// </summary>
        private static void ApplySettingsButtonColors(Button btn, tk2dTextMesh tm)
        {
            if (btn == null || tm == null)
                return;

            Button refBtn = FindSettingsStyleButton();
            Color idle;
            Color hover;
            if (refBtn != null)
            {
                hover = refBtn.rolloverColor;
                if (refBtn.textMesh != null)
                    idle = refBtn.textMesh.color;
                else
                    idle = refBtn.baseColor;
            }
            else
            {
                idle = new Color(0.55f, 0.55f, 0.55f, 1f);
                hover = Color.white;
            }

            // If the reference was already hovered/white, keep a visible delta.
            if (ColorsNearlyEqual(idle, hover))
            {
                idle = new Color(0.55f, 0.55f, 0.55f, 1f);
                hover = Color.white;
            }

            tm.color = idle;
            tm.color2 = idle;
            tm.Commit();
            btn.baseColor = idle;
            btn.rolloverColor = hover;
            btn.textMesh = tm;
        }

        private static bool ColorsNearlyEqual(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.04f
                && Mathf.Abs(a.g - b.g) < 0.04f
                && Mathf.Abs(a.b - b.b) < 0.04f
                && Mathf.Abs(a.a - b.a) < 0.04f;
        }

        private static Button FindSettingsStyleButton()
        {
            if (_menu == null)
                return null;
            if (_menu.VideoMenu != null)
            {
                Transform fs = _menu.VideoMenu.transform.Find("FullscreenBtn");
                if (fs != null)
                {
                    Button b = fs.GetComponent<Button>();
                    if (b != null && b.textMesh != null)
                        return b;
                }
                Button[] btns = _menu.VideoMenu.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < btns.Length; i++)
                {
                    if (btns[i] != null && btns[i].textMesh != null)
                        return btns[i];
                }
            }
            if (_menu.profilesMenuBack != null)
            {
                Button back = _menu.profilesMenuBack.GetComponent<Button>();
                if (back != null && back.textMesh != null)
                    return back;
            }
            return null;
        }

        private static void WireButton(GameObject go, Action onFire)
        {
            if (go == null || !go)
                return;
            Button btn = go.GetComponent<Button>();
            if (btn == null)
                return;
            btn.disabled = false;
            btn.noRollover = false;
            btn.function = "";
            btn.popupType = "";
            btn.localized = false;
            btn.OnFire = () => Guarded(onFire);
            StripChildColliders(go);
            Collider rootCol = go.GetComponent<Collider>();
            if (rootCol != null)
                rootCol.enabled = true;
            if (btn.textMesh == null)
            {
                tk2dTextMesh tm = go.GetComponentInChildren<tk2dTextMesh>(true);
                if (tm != null)
                {
                    btn.textMesh = tm;
                    btn.baseColor = tm.color;
                }
            }
        }

        private static void StripChildColliders(GameObject root)
        {
            if (root == null)
                return;
            Collider[] cols = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                Collider c = cols[i];
                if (c == null || c.gameObject == root)
                    continue;
                try { UnityEngine.Object.DestroyImmediate(c); }
                catch { c.enabled = false; }
            }
        }

        private static void Tag(GameObject go, string kind)
        {
            if (go == null)
                return;
            YokWareUiTag tag = go.GetComponent<YokWareUiTag>();
            if (tag == null)
                tag = go.AddComponent<YokWareUiTag>();
            tag.Kind = kind;
        }

        private static tk2dTextMesh FindLabelSource(bool settingsStyle)
        {
            if (_menu == null)
                return null;

            if (settingsStyle)
            {
                // Video options / Profiles use the outlined white bitmap look from the screenshots.
                if (_menu.VideoMenu != null)
                {
                    Transform fs = _menu.VideoMenu.transform.Find("FullscreenBtn");
                    if (fs != null)
                    {
                        Button b = fs.GetComponent<Button>();
                        if (b != null && b.textMesh != null)
                            return b.textMesh;
                    }
                    Button[] btns = _menu.VideoMenu.GetComponentsInChildren<Button>(true);
                    for (int i = 0; i < btns.Length; i++)
                    {
                        if (btns[i] != null && btns[i].textMesh != null)
                            return btns[i].textMesh;
                    }
                }
                if (_menu.profilesMenuBack != null)
                {
                    Button back = _menu.profilesMenuBack.GetComponent<Button>();
                    if (back != null && back.textMesh != null)
                        return back.textMesh;
                    tk2dTextMesh backTm = _menu.profilesMenuBack.GetComponentInChildren<tk2dTextMesh>(true);
                    if (backTm != null)
                        return backTm;
                }
            }

            return _menu.CurrentVersion;
        }

        private static tk2dTextMesh CreateLabel(Transform parent, string text, bool settingsStyle)
        {
            tk2dTextMesh source = FindLabelSource(settingsStyle);
            if (source == null)
            {
                ModLog.Warn(LogCat.Session, "Menu label source missing — button label blank");
                return null;
            }

            GameObject labelGo = UnityEngine.Object.Instantiate(source.gameObject, parent);
            labelGo.name = LabelName;

            MonoBehaviour[] mbs = labelGo.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < mbs.Length; i++)
            {
                if (mbs[i] == null || mbs[i] is tk2dTextMesh)
                    continue;
                try { UnityEngine.Object.DestroyImmediate(mbs[i]); }
                catch { /* ignore */ }
            }
            Collider[] cols = labelGo.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] == null)
                    continue;
                try { UnityEngine.Object.DestroyImmediate(cols[i]); }
                catch { /* ignore */ }
            }

            labelGo.transform.localPosition = Vector3.zero;
            labelGo.transform.localRotation = Quaternion.identity;
            labelGo.transform.localScale = source.transform.localScale;
            labelGo.SetActive(true);

            tk2dTextMesh tm = labelGo.GetComponent<tk2dTextMesh>();
            if (tm == null)
                return null;
            YokWareUiTag tag = parent.GetComponent<YokWareUiTag>();
            if (tag != null)
                tag.LabelEn = text;
            string shown = Loc.T(text);
            tm.anchor = TextAnchor.MiddleCenter;
            if (tm.maxChars < shown.Length + 4)
                tm.maxChars = shown.Length + 8;
            tm.text = shown;
            // Colors: settingsStyle applied by ApplySettingsButtonColors (idle≠hover).
            // Non-settings fallback keeps source colors.
            tm.Commit();

            Collider col = parent.GetComponent<Collider>();
            Renderer rend = labelGo.GetComponent<Renderer>();
            if (rend != null)
                rend.enabled = true;

            if (col != null && rend != null
                && rend.bounds.size.x > 0.001f && rend.bounds.size.y > 0.001f)
            {
                if (settingsStyle)
                {
                    // Keep Video/Profiles at native size; shrink only if wider than the hitbox.
                    // Scaling up to the title quit collider made HOST/JOIN huge vs Options.
                    if (TryHitboxFace(parent.gameObject, out float faceW, out _))
                    {
                        float fitW = faceW * 0.95f / rend.bounds.size.x;
                        if (fitW < 0.99f)
                            labelGo.transform.localScale *= Mathf.Max(fitW, 0.35f);
                    }
                }
                else if (TryHitboxFace(parent.gameObject, out float faceW, out float faceH))
                {
                    float fitH = faceH * 0.62f / rend.bounds.size.y;
                    float fitW = faceW * 0.92f / rend.bounds.size.x;
                    float factor = Mathf.Clamp(Mathf.Min(fitH, fitW), 0.02f, 50f);
                    labelGo.transform.localScale *= factor;
                }

                Vector3 pos = parent.position;
                GameObject camObj = Core.CamUI;
                Camera cam = camObj != null ? camObj.GetComponent<Camera>() : null;
                if (cam != null)
                    pos -= cam.transform.forward * 1f;
                labelGo.transform.position = pos;
            }
            return tm;
        }

        /// <summary>
        /// Visible face of a flat UI BoxCollider (world AABB.y is often ~0 under CamUI).
        /// </summary>
        private static bool TryHitboxFace(GameObject go, out float faceW, out float faceH)
        {
            faceW = faceH = 0f;
            if (go == null)
                return false;
            var box = go.GetComponent<BoxCollider>();
            if (box == null)
                return false;
            Vector3 lossy = go.transform.lossyScale;
            float ax = Mathf.Abs(box.size.x * lossy.x);
            float ay = Mathf.Abs(box.size.y * lossy.y);
            float az = Mathf.Abs(box.size.z * lossy.z);
            float t = ax, u = ay, v = az;
            if (t > u) { float s = t; t = u; u = s; }
            if (u > v) { float s = u; u = v; v = s; }
            if (t > u) { float s = t; t = u; u = s; }
            faceH = u;
            faceW = v;
            return faceH > 0.05f && faceW > 0.05f;
        }

    }
}
