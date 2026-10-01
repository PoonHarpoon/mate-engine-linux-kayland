using System;
using System.IO;
using System.Collections;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public static class MateeInput
{
    static string path;
    static int polledFrame=-1, x, y, scrollX, scrollY, previousScrollX, previousScrollY;
    static bool pointerInside;
    static Vector2 frameScroll;
    static uint buttons; static readonly uint[] down=new uint[3], up=new uint[3], previousDown=new uint[3], previousUp=new uint[3];
    static ulong previousSequence;
    static uint previousKeyTotal;
    static bool keyStreamReady;
    static readonly List<uint> typedKeys=new List<uint>();
    static readonly bool[] downFrame=new bool[3], upFrame=new bool[3];
    public static bool PresenterActive => !string.IsNullOrEmpty(InputPath);
    static string InputPath {
        get {
            if (path==null) { path=Environment.GetEnvironmentVariable("MATEENGINE_PRESENTER_INPUT_FILE");
                if (string.IsNullOrWhiteSpace(path)) { string frames=Environment.GetEnvironmentVariable("MATEENGINE_PRESENTER_FRAME_FILE"); if (!string.IsNullOrWhiteSpace(frames)) path=frames+".input"; } }
            return path;
        }
    }
    static void Poll() {
        if (polledFrame==Time.frameCount) return; polledFrame=Time.frameCount;
        Array.Clear(downFrame,0,3); Array.Clear(upFrame,0,3);
        frameScroll=Vector2.zero;
        typedKeys.Clear();
        if (!PresenterActive || !File.Exists(InputPath)) return;
        try {
            byte[] b=File.ReadAllBytes(InputPath); if (b.Length<72 || System.Text.Encoding.ASCII.GetString(b,0,8)!="MATEEINP") return;
            uint version=BitConverter.ToUInt32(b,8); if (version!=1 && version!=2) return;
            x=BitConverter.ToInt32(b,12); y=BitConverter.ToInt32(b,16); buttons=BitConverter.ToUInt32(b,20);
            pointerInside=BitConverter.ToUInt32(b,24)!=0;
            scrollX=BitConverter.ToInt32(b,28); scrollY=BitConverter.ToInt32(b,32);
            frameScroll=new Vector2((scrollX-previousScrollX)/120f,(scrollY-previousScrollY)/120f);
            previousScrollX=scrollX; previousScrollY=scrollY;
            ulong inputSequence=BitConverter.ToUInt64(b,40);
            for (int i=0;i<3;i++) { down[i]=BitConverter.ToUInt32(b,48+i*4); up[i]=BitConverter.ToUInt32(b,60+i*4);
                downFrame[i]=inputSequence>previousSequence && down[i]!=previousDown[i];
                upFrame[i]=inputSequence>previousSequence && up[i]!=previousUp[i];
                previousDown[i]=down[i]; previousUp[i]=up[i]; }
            previousSequence=inputSequence;
            if (version==2 && b.Length>=332) {
                uint total=BitConverter.ToUInt32(b,72);
                if (keyStreamReady) {
                    uint count=Math.Min(total-previousKeyTotal,64u);
                    for (uint n=count;n>0;n--) {
                        uint index=(total-n)%64u;
                        typedKeys.Add(BitConverter.ToUInt32(b,76+(int)index*4));
                    }
                }
                previousKeyTotal=total; keyStreamReady=true;
            }
        } catch (IOException) { }
    }
    public static uint[] ConsumeTextEvents() { Poll(); var result=typedKeys.ToArray(); typedKeys.Clear(); return result; }
    public static Vector3 MousePosition { get { if (!PresenterActive) return Input.mousePosition; Poll(); return new Vector3(x,Mathf.Max(0,Screen.height-1-y),0); } }
    public static bool PointerInside { get { if (!PresenterActive) return true; Poll(); return pointerInside; } }
    public static Vector2 MouseScrollDelta { get { if (!PresenterActive) return Input.mouseScrollDelta; Poll(); return frameScroll; } }
    public static bool GetMouseButton(int button) { if (!PresenterActive) return Input.GetMouseButton(button); Poll(); return button>=0&&button<3&&(buttons&(1u<<button))!=0; }
    public static bool GetMouseButtonDown(int button) { if (!PresenterActive) return Input.GetMouseButtonDown(button); Poll(); return button>=0&&button<3&&downFrame[button]; }
    public static bool GetMouseButtonUp(int button) { if (!PresenterActive) return Input.GetMouseButtonUp(button); Poll(); return button>=0&&button<3&&upFrame[button]; }
}

public sealed class WaylandPresenterBaseInput : BaseInput
{
    public override bool mousePresent => true;
    public override Vector2 mousePosition => MateeInput.MousePosition;
    public override Vector2 mouseScrollDelta => MateeInput.MouseScrollDelta;
    public override bool GetMouseButton(int button) => MateeInput.GetMouseButton(button);
    public override bool GetMouseButtonDown(int button) => MateeInput.GetMouseButtonDown(button);
    public override bool GetMouseButtonUp(int button) => MateeInput.GetMouseButtonUp(button);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install() {
#if UNITY_STANDALONE_LINUX && !UNITY_EDITOR
        if (!WindowManager.IsNativeWaylandSession || !MateeInput.PresenterActive) return;
        var host=new GameObject(nameof(WaylandPresenterBaseInput)); DontDestroyOnLoad(host);
        var input=host.AddComponent<WaylandPresenterBaseInput>(); host.AddComponent<WaylandInputInstaller>().Input=input;
#endif
    }
}

public sealed class WaylandInputInstaller : MonoBehaviour
{
    static readonly FieldInfo FocusField=typeof(EventSystem).GetField("m_HasFocus",BindingFlags.Instance|BindingFlags.NonPublic);
    public static bool Ready { get; private set; }
    public BaseInput Input { get; set; }
    IEnumerator Start() {
        var interval=new WaitForSecondsRealtime(0.5f);
        while (enabled) {
            var current=EventSystem.current;
            var module=current!=null?current.GetComponent<StandaloneInputModule>():null;
            if (module!=null && module.inputOverride!=Input) {
                module.inputOverride=Input;
                Debug.Log($"Wayland presenter input attached to EventSystem '{current.name}'.");
            }
            // The stock StandaloneInputModule ignores all input while its
            // window is unfocused. The Unity source is intentionally minimized
            // once the presenter is live, so keep only UGUI's internal input
            // focus state active; OS focus and keyboard focus are unchanged.
            if (module!=null && FocusField!=null) {
                FocusField.SetValue(current,true);
                Ready=module.inputOverride==Input;
            }
            yield return interval;
        }
    }
    void OnDestroy() { Ready=false; }
}
