using System;
using UnityEngine;

public class RemoveTaskbarApp : MonoBehaviour
{

    private IntPtr _unityHwnd = IntPtr.Zero;

    private bool _isHidden = true;
    public bool IsHidden => _isHidden;

    void Start()
    {
#if UNITY_EDITOR
        return;
#endif
        _unityHwnd = WindowManager.Instance.UnityWindow;
        if (_unityHwnd != IntPtr.Zero)
        {
            WindowManager.Instance.HideFromTaskbar();
            _isHidden = true;
        }
    }

    public void ToggleAppMode()
    {
#if UNITY_EDITOR
        return;
#endif
        if (_unityHwnd == IntPtr.Zero)
            return;

        _isHidden = !_isHidden;
        WindowManager.Instance.HideFromTaskbar(_isHidden);
    }
}
