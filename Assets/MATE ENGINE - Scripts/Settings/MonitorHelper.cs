using UnityEngine;

public static class MonitorHelper
{
    /// <summary>
    /// Returns the taskbar Rect on whichever monitor contains the given window handle.
    /// </summary>
    public static RectInt GetTaskbarRectForWindow()
    {
        WindowManager.Instance.GetWindowRect(WindowManager.Instance.GetDock, out var dockRect);
        return dockRect;
    }
}
