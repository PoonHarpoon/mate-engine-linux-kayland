using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public class AvatarBigScreenHandler : MonoBehaviour
{
    [Header("Keybinds")]
    public List<KeyCode> ToggleKeys = new List<KeyCode> { KeyCode.B };

    [Header("Animator & Bone Selection")]
    public Animator avatarAnimator;
    public HumanBodyBones attachBone = HumanBodyBones.Head;

    [Header("Camera")]
    public Camera MainCamera;
    [Tooltip("Override for Zoom: Camera FOV (Perspective) or Size (Orthographic). 0 = auto.")]
    public float TargetZoom = 0f;
    public float ZoomMoveSpeed = 10f;
    [Tooltip("Y-Offset to bone position (meters, before scaling)")]
    public float YOffset = 0.08f;

    [Header("Fade Animation")]
    public float FadeYOffset = 0.5f;
    public float FadeInDuration = 0.5f;
    public float FadeOutDuration = 0.5f;

    [Header("Canvas Blocking")]
    public GameObject moveCanvas;

    private bool isBigScreenActive = false;
    private Vector3 originalCamPos;
    private Quaternion originalCamRot;
    private float originalFOV;
    private float originalOrthoSize;
    private Rect originalVisibleRect;
    private bool originalRectSet = false;
    private Transform bone;
    private AvatarAnimatorController avatarAnimatorController;
    private bool moveCanvasWasActive = false;
    private Coroutine fadeCoroutine;
    private bool isFading = false;
    private bool isInDesktopTransition = false;

    public static List<AvatarBigScreenHandler> ActiveHandlers = new List<AvatarBigScreenHandler>();
    private static readonly int IsBigScreen = Animator.StringToHash("isBigScreen");

    public bool IsBigScreenActive => isBigScreenActive;

    void OnEnable()
    {
        if (!ActiveHandlers.Contains(this))
            ActiveHandlers.Add(this);
    }
    void OnDisable()
    {
        ActiveHandlers.Remove(this);
    }

    public void ToggleBigScreenFromUI()
    {
        if (!isBigScreenActive)
            ActivateBigScreen();
        else
            DeactivateBigScreen();
    }

    void Start()
    {
        if (MainCamera == null) MainCamera = Camera.main;
        if (avatarAnimator == null) avatarAnimator = GetComponent<Animator>();
        if (MainCamera != null)
        {
            originalCamPos = MainCamera.transform.position;
            originalCamRot = MainCamera.transform.rotation;
            originalFOV = MainCamera.fieldOfView;
            originalOrthoSize = MainCamera.orthographicSize;
        }
        if (WindowManager.Instance != null && WindowManager.Instance.TryGetVisiblePetRect(out Rect r))
        {
            originalVisibleRect = r;
            originalRectSet = true;
        }
        avatarAnimatorController = GetComponent<AvatarAnimatorController>();
    }

    public void SetAnimator(Animator a) => avatarAnimator = a;

    void Update()
    {
        foreach (var key in ToggleKeys)
        {
            if (Input.GetKeyDown(key))
            {
                if (!isBigScreenActive && !isFading)
                    ActivateBigScreen();
                else if (isBigScreenActive && !isFading)
                    DeactivateBigScreen();
                break;
            }
        }
        if (isBigScreenActive && MainCamera != null && bone != null && avatarAnimator != null && !isFading && !isInDesktopTransition)
            UpdateBigScreenCamera();
    }

    void UpdateBigScreenCamera()
    {
        var scale = avatarAnimator.transform.lossyScale.y;
        var headPos = bone.position;
        var neck = avatarAnimator.GetBoneTransform(HumanBodyBones.Neck);
        float headHeight = Mathf.Max(0.12f, neck ? Mathf.Abs(headPos.y - neck.position.y) : 0.25f) * scale;
        float buffer = 1.4f;

        Vector3 camPos = originalCamPos;
        camPos.x = headPos.x;
        camPos.y = headPos.y + YOffset * scale;
        MainCamera.transform.position = camPos;
        MainCamera.transform.rotation = Quaternion.identity;

        if (TargetZoom > 0f)
        {
            if (MainCamera.orthographic) MainCamera.orthographicSize = TargetZoom * scale;
            else MainCamera.fieldOfView = TargetZoom;
        }
        else
        {
            if (MainCamera.orthographic)
                MainCamera.orthographicSize = headHeight * buffer;
            else
            {
                float dist = Mathf.Abs(MainCamera.transform.position.z - headPos.z);
                MainCamera.fieldOfView = Mathf.Clamp(
                    2f * Mathf.Atan((headHeight * buffer) / (2f * dist)) * Mathf.Rad2Deg, 10f, 60f);
            }
        }
    }

    void ActivateBigScreen()
    {
        if (isBigScreenActive) return;
        SaveCameraState();

        if (moveCanvas != null)
            moveCanvasWasActive = moveCanvas.activeSelf;

        isBigScreenActive = true;
        if (avatarAnimator != null) avatarAnimator.SetBool(IsBigScreen, true);
        if (avatarAnimatorController != null) avatarAnimatorController.BlockDraggingOverride = true;
        if (moveCanvas != null && moveCanvas.activeSelf) moveCanvas.SetActive(false);

        bone = avatarAnimator ? avatarAnimator.GetBoneTransform(attachBone) : null;
        if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
        fadeCoroutine = StartCoroutine(BigScreenEnterSequence());
    }

    void DeactivateBigScreen()
    {
        if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
        fadeCoroutine = StartCoroutine(BigScreenExitSequence());
    }

    void SaveCameraState()
    {
        if (MainCamera != null)
        {
            originalCamPos = MainCamera.transform.position;
            originalCamRot = MainCamera.transform.rotation;
            originalFOV = MainCamera.fieldOfView;
            originalOrthoSize = MainCamera.orthographicSize;
        }
    }

    IEnumerator FadeCameraY(bool fadeIn)
    {
        isFading = true;
        if (avatarAnimator == null || bone == null || MainCamera == null)
        { isFading = false; yield break; }

        var scale = avatarAnimator.transform.lossyScale.y;
        var headPos = bone.position;
        float baseY = headPos.y + YOffset * scale;
        float fadeY = baseY + FadeYOffset;

        Vector3 camPos = MainCamera.transform.position;
        float fromX = camPos.x;
        float toX = fadeIn ? headPos.x : originalCamPos.x;
        float fromY = fadeIn ? fadeY : baseY;
        float toY = fadeIn ? baseY : fadeY;
        float duration = fadeIn ? FadeInDuration : FadeOutDuration;
        float time = 0f;

        var neck = avatarAnimator.GetBoneTransform(HumanBodyBones.Neck);
        float headHeight = Mathf.Max(0.12f, neck ? Mathf.Abs(headPos.y - neck.position.y) : 0.25f) * scale;
        float buffer = 1.4f;

        while (time < duration)
        {
            float curve = Mathf.SmoothStep(0, 1, time / duration);
            camPos.x = Mathf.Lerp(fromX, toX, curve);
            camPos.y = Mathf.Lerp(fromY, toY, curve);
            MainCamera.transform.position = camPos;
            MainCamera.transform.rotation = Quaternion.identity;
            if (TargetZoom > 0f)
            {
                if (MainCamera.orthographic) MainCamera.orthographicSize = TargetZoom * scale;
                else MainCamera.fieldOfView = TargetZoom;
            }
            else
            {
                if (MainCamera.orthographic)
                    MainCamera.orthographicSize = headHeight * buffer;
                else
                {
                    float dist = Mathf.Abs(MainCamera.transform.position.z - headPos.z);
                    MainCamera.fieldOfView = Mathf.Clamp(
                        2f * Mathf.Atan((headHeight * buffer) / (2f * dist)) * Mathf.Rad2Deg, 10f, 60f);
                }
            }
            time += Time.deltaTime;
            yield return null;
        }

        camPos.x = toX;
        camPos.y = toY;
        MainCamera.transform.position = camPos;
        MainCamera.transform.rotation = Quaternion.identity;
        if (TargetZoom > 0f)
        {
            if (MainCamera.orthographic) MainCamera.orthographicSize = TargetZoom * scale;
            else MainCamera.fieldOfView = TargetZoom;
        }
        else
        {
            if (MainCamera.orthographic)
                MainCamera.orthographicSize = headHeight * buffer;
            else
            {
                float dist = Mathf.Abs(MainCamera.transform.position.z - headPos.z);
                MainCamera.fieldOfView = Mathf.Clamp(
                    2f * Mathf.Atan((headHeight * buffer) / (2f * dist)) * Mathf.Rad2Deg, 10f, 60f);
            }
        }
        isFading = false;

        if (!fadeIn)
        {
            isBigScreenActive = false;
            if (avatarAnimator != null) avatarAnimator.SetBool(IsBigScreen, false);
            if (avatarAnimatorController != null) avatarAnimatorController.BlockDraggingOverride = false;
            if (moveCanvas != null && moveCanvasWasActive) moveCanvas.SetActive(true);
            if (WindowManager.Instance != null && originalRectSet)
            {
                WindowManager.Instance.SetVisiblePetPosition(originalVisibleRect.position);
            }
            if (MainCamera != null)
            {
                MainCamera.transform.position = originalCamPos;
                MainCamera.transform.rotation = originalCamRot;
                MainCamera.fieldOfView = originalFOV;
                MainCamera.orthographicSize = originalOrthoSize;
            }
        }
    }
    
    private RectInt FindBestMonitorRect(Rect visibleRect, Vector2 modelPosition)
    {
        if (WindowManager.Instance == null) return new RectInt(0, 0, Screen.currentResolution.width, Screen.currentResolution.height);
        WindowManager.Instance.QueryMonitors();
        List<RectInt> monitorRects = WindowManager.Instance.GetAllMonitors().Values.ToList();
        if (monitorRects.Count == 0) return new RectInt(0, 0, Screen.currentResolution.width, Screen.currentResolution.height);
        foreach (var monitor in monitorRects)
            if (monitor.Contains(Vector2Int.FloorToInt(modelPosition))) return monitor;
        int idx = 0;
        float maxArea = 0;
        for (int i = 0; i < monitorRects.Count; i++)
        {
            float overlap = OverlapArea(visibleRect, monitorRects[i]);
            if (overlap > maxArea) { idx = i; maxArea = overlap; }
        }
        if (maxArea == 0)
        {
            float nearest = float.MaxValue;
            for (int i = 0; i < monitorRects.Count; i++)
            {
                float distance = (visibleRect.center - (Vector2)monitorRects[i].center).sqrMagnitude;
                if (distance < nearest) { nearest = distance; idx = i; }
            }
        }
        return monitorRects[idx];
    }

    float OverlapArea(Rect a, RectInt b)
    {
        float x1 = Mathf.Max(a.x, b.x), x2 = Mathf.Min(a.x + a.width, b.x + b.width);
        float y1 = Mathf.Max(a.y, b.y), y2 = Mathf.Min(a.y + a.height, b.y + b.height);
        float w = x2 - x1, h = y2 - y1;
        return (w > 0 && h > 0) ? w * h : 0;
    }

    IEnumerator GlideAvatarDesktop(float duration, bool toFadeY)
    {
        isInDesktopTransition = true;
        if (avatarAnimator == null || bone == null || MainCamera == null)
        { isInDesktopTransition = false; yield break; }

        var scale = avatarAnimator.transform.lossyScale.y;
        var headPos = bone.position;
        Vector3 modelScreenPosition = MainCamera.WorldToScreenPoint(headPos);
        float baseY = headPos.y + YOffset * scale;
        float fadeY = baseY + FadeYOffset;

        Vector3 camPos = MainCamera.transform.position;
        float fromY = toFadeY ? baseY : fadeY;
        float toY = toFadeY ? fadeY : baseY;
        float time = 0f;

        while (time < duration)
        {
            camPos.y = Mathf.Lerp(fromY, toY, Mathf.SmoothStep(0, 1, time / duration));
            MainCamera.transform.position = camPos;
            time += Time.deltaTime;
            yield return null;
        }
        camPos.y = toY;
        MainCamera.transform.position = camPos;

        if (toFadeY && WindowManager.Instance != null)
        {
            if (WindowManager.Instance.TryGetVisiblePetRect(out Rect visibleRect))
            {
                Vector2 modelPosition = new Vector2(
                    visibleRect.xMin + modelScreenPosition.x * visibleRect.width / Mathf.Max(1, Screen.width),
                    visibleRect.yMax - modelScreenPosition.y * visibleRect.height / Mathf.Max(1, Screen.height));
                RectInt targetScreen = FindBestMonitorRect(visibleRect, modelPosition);
                float x = targetScreen.center.x - visibleRect.width * 0.5f;
                WindowManager.Instance.SetVisiblePetPosition(new Vector2(x, targetScreen.yMax - visibleRect.height));
                originalVisibleRect = visibleRect;
                originalRectSet = true;
            }
        }
        if (!toFadeY && MainCamera != null)
        {
            MainCamera.transform.position = originalCamPos;
            MainCamera.transform.rotation = originalCamRot;
            MainCamera.fieldOfView = originalFOV;
            MainCamera.orthographicSize = originalOrthoSize;
        }
        isInDesktopTransition = false;
    }

    IEnumerator BigScreenEnterSequence()
    {
        yield return StartCoroutine(GlideAvatarDesktop(0.4f, true));
        yield return StartCoroutine(FadeCameraY(true));
    }
    IEnumerator BigScreenExitSequence()
    {
        yield return StartCoroutine(FadeCameraY(false));
        yield return StartCoroutine(GlideAvatarDesktop(0.4f, false));
    }
}
