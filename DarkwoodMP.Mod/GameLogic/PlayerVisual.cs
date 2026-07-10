using System;
using UnityEngine;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Visual representation of a remote player with capsule mesh, name tag, and health bar.
/// Uses reflection to access UnityEngine.UI types at runtime since they're not available at compile time.
/// </summary>
public class PlayerVisual : MonoBehaviour
{
    public string DisplayName { get; set; } = "Player";
    public Color PlayerColor { get; set; } = Color.green;
    public float HealthPercent { get; set; } = 1.0f;

    private static readonly Type? _canvasType;
    private static readonly Type? _imageType;
    private static readonly Type? _rectTransformType;
    private static readonly Type? _textType;
    private static readonly Type? _textMeshType;
    private static readonly Type? _colorUtilityType;

    static PlayerVisual()
    {
        _canvasType = Type.GetType("UnityEngine.Canvas, UnityEngine");
        _textMeshType = Type.GetType("UnityEngine.TextMesh, UnityEngine");
        // Try UnityEngine.UI first, then UnityEngine fallback
        _imageType = Type.GetType("UnityEngine.UI.Image, UnityEngine.UI")
            ?? Type.GetType("UnityEngine.UI.Image, UnityEngine");
        _rectTransformType = Type.GetType("UnityEngine.UI.RectTransform, UnityEngine.UI")
            ?? Type.GetType("UnityEngine.RectTransform, UnityEngine");
        _textType = Type.GetType("UnityEngine.UI.Text, UnityEngine.UI")
            ?? Type.GetType("UnityEngine.UI.Text, UnityEngine");
        _colorUtilityType = Type.GetType("UnityEngine.ColorUtility, UnityEngine");
    }

    private MeshRenderer _meshRenderer;
    private object? _nameRect;
    private object? _nameText;
    private object? _healthFill;
    private float _targetHealth;

    public void Initialize(int playerId, string name, Color color)
    {
        DisplayName = name;
        PlayerColor = color;
        _meshRenderer = GetComponent<MeshRenderer>();
        if (_meshRenderer != null)
        {
            var mat = _meshRenderer.material;
            mat.color = color;
        }

        CreateNameTag();
        CreateHealthBar();
        _targetHealth = 1.0f;
    }

    public void UpdateHealth(float health)
    {
        _targetHealth = Mathf.Clamp01(health / 100f);
    }

    private void Update()
    {
        // Fast lerp factor for responsive health bar updates
        HealthPercent = Mathf.Lerp(HealthPercent, _targetHealth, Time.deltaTime * 15f);

        if (_healthFill != null)
        {
            var setRect = _healthFill.GetType().GetProperty("rect")?.SetMethod;
            if (setRect != null)
            {
                var rect = _healthFill.GetType().GetProperty("rect")?.GetValue(_healthFill);
                if (rect != null)
                {
                    var widthProp = rect.GetType().GetProperty("width");
                    var oldW = widthProp?.GetValue(rect);
                    widthProp?.SetValue(rect, 40f * HealthPercent);
                    setRect.Invoke(_healthFill, new object[] { rect });
                }
            }
        }

        if (_nameText != null)
        {
            var textProp = _nameText.GetType().GetProperty("text");
            textProp?.SetValue(_nameText, DisplayName);
        }
    }

    private void CreateNameTag()
    {
        if (_textMeshType == null || _canvasType == null || _textType == null || _rectTransformType == null) return;

        var nameObj = new GameObject("NameTag");
        nameObj.transform.SetParent(transform);
        nameObj.transform.localPosition = new Vector3(0, 2.2f, 0);

        var canvas = nameObj.AddComponent(_canvasType);
        if (canvas == null) return;
        var setMode = canvas.GetType().GetProperty("renderMode")?.SetMethod;
        if (setMode != null) setMode.Invoke(canvas, new object[] { 2 }); // RenderMode.WorldSpace = 2
        var mainCam = Camera.main;
        var setCam = canvas.GetType().GetProperty("worldCamera")?.SetMethod;
        if (setCam != null) setCam.Invoke(canvas, new object[] { mainCam });

        var text = nameObj.AddComponent(_textType);
        if (text == null) return;
        _nameText = text;
        text.GetType().GetProperty("text")?.SetValue(text, DisplayName);
        text.GetType().GetProperty("fontSize")?.SetValue(text, 14);
        text.GetType().GetProperty("fontStyle")?.SetValue(text, GetFontStyleBold());
        text.GetType().GetProperty("alignment")?.SetValue(text, GetTextAnchorMiddleCenter());
        text.GetType().GetProperty("color")?.SetValue(text, Color.white);

        var rect = nameObj.AddComponent(_rectTransformType);
        if (rect == null) return;
        _nameRect = rect;
        var sizeDelta = rect.GetType().GetProperty("sizeDelta");
        if (sizeDelta != null) sizeDelta.SetValue(rect, new Vector2(60, 20));
    }

    private void CreateHealthBar()
    {
        if (_rectTransformType == null || _imageType == null || _canvasType == null) return;

        var healthObj = new GameObject("HealthBar");
        healthObj.transform.SetParent(transform);
        healthObj.transform.localPosition = new Vector3(0, 2.0f, 0);

        var canvas = healthObj.AddComponent(_canvasType);
        if (canvas == null) return;
        var setMode = canvas.GetType().GetProperty("renderMode")?.SetMethod;
        if (setMode != null) setMode.Invoke(canvas, new object[] { 2 });
        var setCam = canvas.GetType().GetProperty("worldCamera")?.SetMethod;
        if (setCam != null) setCam.Invoke(canvas, new object[] { Camera.main });

        var bg = new GameObject("HealthBarBg");
        bg.transform.SetParent(healthObj.transform);
        object? bgRect = bg.AddComponent(_rectTransformType);
        if (bgRect != null)
        {
            SetProperty(bgRect, "sizeDelta", new Vector2(40, 4));
            SetProperty(bgRect, "anchoredPosition", Vector2.zero);
        }

        object? bgImage = bg.AddComponent(_imageType);
        if (bgImage != null) SetProperty(bgImage, "color", Color.red * 0.6f);

        var fill = new GameObject("HealthBarFill");
        fill.transform.SetParent(healthObj.transform);
        _healthFill = fill.AddComponent(_rectTransformType);
        if (_healthFill != null)
        {
            SetProperty(_healthFill, "sizeDelta", new Vector2(40, 4));
            SetProperty(_healthFill, "anchoredPosition", new Vector2(-20, 0));
        }

        object? fillImage = fill.AddComponent(_imageType);
        if (fillImage != null) SetProperty(fillImage, "color", Color.green);
    }

    private static void SetProperty(object obj, string propName, object value)
    {
        obj.GetType().GetProperty(propName)?.SetValue(obj, value);
    }

    private static object GetFontStyleBold()
    {
        var t = Type.GetType("UnityEngine.FontStyle, UnityEngine");
        if (t != null)
            return Enum.Parse(t, "Bold");
        return 1;
    }

    private static object GetTextAnchorMiddleCenter()
    {
        var t = Type.GetType("UnityEngine.TextAnchor, UnityEngine");
        if (t != null)
            return Enum.Parse(t, "MiddleCenter");
        return 5;
    }

    private static Mesh CreateCapsuleMesh()
    {
        var mesh = new Mesh();
        mesh.name = "PlayerCapsule";

        var vertices = new[]
        {
            new Vector3(-0.4f, -1f, 0f),
            new Vector3(0f, -1f, 0.4f),
            new Vector3(0.4f, -1f, 0f),
            new Vector3(0f, -1f, -0.4f),
            new Vector3(-0.4f, 1f, 0f),
            new Vector3(0f, 1f, 0.4f),
            new Vector3(0.4f, 1f, 0f),
            new Vector3(0f, 1f, -0.4f),
            new Vector3(0f, 1.4f, 0f),
            new Vector3(0f, -1.4f, 0f),
        };

        var triangles = new int[]
        {
            0, 1, 2,  2, 1, 3,
            2, 3, 6,  6, 3, 7,
            6, 7, 4,  4, 7, 5,
            4, 5, 0,  0, 5, 1,
            4, 5, 6,  4, 6, 7,
            0, 3, 1,  0, 2, 3,
            8, 4, 5,  8, 5, 6,  8, 6, 7,  8, 7, 4,
            9, 0, 3,  9, 3, 1,  9, 1, 2,  9, 2, 0,
        };

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
