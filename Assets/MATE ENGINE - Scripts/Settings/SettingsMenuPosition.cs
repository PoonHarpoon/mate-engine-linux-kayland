using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[DefaultExecutionOrder(100)]
public class SettingsMenuPosition : MonoBehaviour
{
    [Serializable]
    public class MenuEntry
    {
        public RectTransform settingsMenu;
        [NonSerialized] public Vector2 lastApplied;
        [NonSerialized] public Vector2 lastCorrection;
    }

    [Header("Menus to track")]
    public List<MenuEntry> menus = new();

    [Header("Edge margin in Pixels")]
    public float edgeMargin = 50f;

    [Header("Monitor refresh (sec)")]
    public float monitorRefreshInterval = 2f;

    readonly List<RectInt> monitorRects = new();
    readonly Vector3[] corners = new Vector3[4];
    float monitorTimer;

    void Start()
    {
        RefreshMonitors();
        foreach (var entry in menus)
            if (entry.settingsMenu) entry.lastApplied = entry.settingsMenu.anchoredPosition;
    }

    void LateUpdate()
    {
        var manager = WindowManager.Instance;
        if (manager == null || !manager.TryGetVisiblePetRect(out var visible) || visible.width <= 0 || visible.height <= 0)
            return;

        monitorTimer += Time.unscaledDeltaTime;
        if (monitorTimer >= Mathf.Max(0.1f, monitorRefreshInterval))
        {
            monitorTimer = 0;
            RefreshMonitors();
        }

        RectInt monitor = BestMonitor(visible);
        float margin = Mathf.Max(0, edgeMargin);
        float left = Mathf.Max(visible.xMin, monitor.xMin + margin);
        float right = Mathf.Min(visible.xMax, monitor.xMax - margin);
        float top = Mathf.Max(visible.yMin, monitor.yMin + margin);
        float bottom = Mathf.Min(visible.yMax, monitor.yMax - margin);
        if (right <= left) { left = visible.xMin; right = visible.xMax; }
        if (bottom <= top) { top = visible.yMin; bottom = visible.yMax; }
        Rect safeScreen = Rect.MinMaxRect(
            (left - visible.xMin) * Screen.width / visible.width,
            (visible.yMax - bottom) * Screen.height / visible.height,
            (right - visible.xMin) * Screen.width / visible.width,
            (visible.yMax - top) * Screen.height / visible.height);

        foreach (var entry in menus)
        {
            var rect = entry.settingsMenu;
            if (!rect || !rect.gameObject.activeInHierarchy || !(rect.parent is RectTransform parent)) continue;

            // A bone follower supplies a fresh position each frame. For a stationary
            // menu, undo our previous correction before evaluating its new bounds.
            if ((rect.anchoredPosition - entry.lastApplied).sqrMagnitude < 0.01f)
                rect.anchoredPosition -= entry.lastCorrection;

            var canvas = rect.GetComponentInParent<Canvas>();
            Camera uiCamera = canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null : canvas != null && canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
            rect.GetWorldCorners(corners);
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            foreach (var corner in corners)
            {
                Vector2 point = RectTransformUtility.WorldToScreenPoint(uiCamera, corner);
                xMin = Mathf.Min(xMin, point.x); xMax = Mathf.Max(xMax, point.x);
                yMin = Mathf.Min(yMin, point.y); yMax = Mathf.Max(yMax, point.y);
            }

            float dx = Correction(xMin, xMax, safeScreen.xMin, safeScreen.xMax);
            float dy = Correction(yMin, yMax, safeScreen.yMin, safeScreen.yMax);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, Vector2.zero, uiCamera, out var localOrigin);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, new Vector2(dx, dy), uiCamera, out var localTarget);
            entry.lastCorrection = localTarget - localOrigin;
            rect.anchoredPosition += entry.lastCorrection;
            entry.lastApplied = rect.anchoredPosition;
        }
    }

    static float Correction(float min, float max, float safeMin, float safeMax)
    {
        if (max - min > safeMax - safeMin)
            return (safeMin + safeMax - min - max) * 0.5f;
        if (min < safeMin) return safeMin - min;
        if (max > safeMax) return safeMax - max;
        return 0;
    }

    void RefreshMonitors()
    {
        monitorRects.Clear();
        if (WindowManager.Instance == null) return;
        WindowManager.Instance.QueryMonitors();
        monitorRects.AddRange(WindowManager.Instance.GetAllMonitors().Values);
    }

    RectInt BestMonitor(Rect visible)
    {
        if (monitorRects.Count == 0)
            return new RectInt(0, 0, Screen.currentResolution.width, Screen.currentResolution.height);
        return monitorRects.OrderByDescending(m => Overlap(visible, m))
            .ThenBy(m => (visible.center - (Vector2)m.center).sqrMagnitude).First();
    }

    static float Overlap(Rect a, RectInt b) =>
        Mathf.Max(0, Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin)) *
        Mathf.Max(0, Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin));
}
