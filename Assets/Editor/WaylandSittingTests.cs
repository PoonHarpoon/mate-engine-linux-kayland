using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

// Run with Unity -batchmode -quit -nographics -executeMethod WaylandSittingTests.Run.
public static class WaylandSittingTests
{
    static void Check(bool value,string reason) { if (!value) throw new Exception(reason); }
    public static void Run()
    {
        var handles=new Dictionary<string,IntPtr>();
        IntPtr Handle(string id) { if (!handles.TryGetValue(id,out var h)) handles.Add(id,h=new IntPtr(handles.Count+1)); return h; }
        object Window(string id,bool visible=true,bool dock=false,bool normal=true,bool popup=false,bool maximized=false,bool own=false) =>
            new { uuid=id,pid=42,rect=new { x=-1000.25f,y=200.5f,width=800f,height=600f },visible,dock,normal,popup,maximized,own };
        string Payload(long seq,object[] windows) => JsonConvert.SerializeObject(new {
            version=1,sittingSupported=true,sequence=seq,mouseX=-50.5f,mouseY=20.25f,windows,
            outputs=new[]{new {name="left",rect=new {x=-1280f,y=-40f,width=1280f,height=1024f},scale=1.25f}} });
        var first=DesktopSnapshot.Parse(Payload(1,new[]{Window("a"),Window("panel",dock:true,normal:false),Window("dialog",normal:false),Window("popup",popup:true),Window("max",maximized:true),Window("own",own:true),Window("hidden",visible:false)}),Handle);
        Check(first.Fresh,"Snapshot must be fresh");
        Check(first.Windows[0].Rect.x==-1000.25f && first.Outputs[0].Scale==1.25f,"Fractional coordinates must survive");
        Check(first.Windows[0].SeatEligible && first.Windows[1].SeatEligible,"Applications and panels are seats");
        for(int i=2;i<first.Windows.Count;i++) Check(!first.Windows[i].SeatEligible,"Ineligible window became a seat");
        Check(first.Windows[2].Visible,"Dialogs must remain available as occluders");
        var second=DesktopSnapshot.Parse(Payload(2,new[]{Window("panel",dock:true,normal:false),Window("a",visible:false)}),Handle);
        Check(first.Windows[1].Id==second.Windows[0].Id,"Stable handle across reorder");
        Check(!second.TryGet(first.Windows[2].Id,out _),"Removed handle must fail");
        Check(first.Windows[0].Visible && !second.Windows[1].Visible,"Publishing must not mutate prior snapshot");
        Check(!second.TryGet(new IntPtr(999),out _),"Unknown handle must not resolve to self");
        var unsupported=DesktopSnapshot.Parse(Payload(3,new[]{Window("a")}).Replace("\"sittingSupported\":true","\"sittingSupported\":false"),Handle);
        Check(unsupported.Fresh && !unsupported.Windows[0].SeatEligible,"Missing seat capability must preserve presentation but disable sitting");
        Check(!DesktopSnapshot.Empty.Fresh,"Unavailable desktop must fail closed");
        foreach(string bad in new[]{"{}",Payload(3,new[]{Window("a"),Window("a")}),Payload(3,new[]{Window("a")}).Replace("\"version\":1","\"version\":2")}) {
            bool rejected=false;try { DesktopSnapshot.Parse(bad,Handle); } catch(FormatException) { rejected=true; }
            Check(rejected,"Malformed/duplicate/versioned snapshot accepted");
        }
        Debug.Log("Wayland sitting snapshot tests passed: stable IDs, removal, immutable snapshots, filtering, fractions and malformed input.");
    }
    // Exercise the production handler and movement adapter with a frozen desktop.
    // No compositor connection or real window changes are made by these fixtures.
    static void Field(object target,string name,object value) => target.GetType().GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(target,value);
    static object Call(object target,string name,params object[] args) => target.GetType().GetMethod(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(target,args);
    public static void RunHandler()
    {
        Run();
        RunMonitorRadius();
        string oldBackend=Environment.GetEnvironmentVariable("MATEENGINE_BACKEND"), oldSession=Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var previous=WindowManager.Instance;
        var go=new GameObject("SittingRegression"); go.SetActive(false);
        var cameraObject=new GameObject("SittingCamera"); cameraObject.SetActive(false);
        var texture=new RenderTexture(1536,1668,0);
        WindowManager manager=null; AvatarWindowHandler handler=null;
        try {
            Environment.SetEnvironmentVariable("MATEENGINE_BACKEND","wayland");
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE","wayland");
            manager=go.AddComponent<WindowManager>(); WindowManager.Instance=manager;
            var km=(KWinManager)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(KWinManager));
            var snapshot=DesktopSnapshot.Parse(JsonConvert.SerializeObject(new {
                version=1,sittingSupported=true,sequence=1,mouseX=2300f,mouseY=1000f,
                windows=new[]{new {uuid="target",pid=42,visible=true,normal=true,rect=new {x=2098f,y=1008f,width=856f,height=500f}}},
                outputs=new[]{new {name="test",scale=1f,rect=new {x=1080f,y=60f,width=2560f,height=1440f}}}
            }),id=>new IntPtr(12));
            Field(km,"_snapshot",snapshot);
            Field(km,"_visibleRect",(Rect?)new Rect(1524,219,1536,1668));
            Field(km,"<Sitting>k__BackingField",true);
            Field(km,"_sitDragCursor",snapshot.Mouse);
            Field(manager,"_windowManagerImplementation",km);
            Field(manager,"_isDragging",true);
            handler=go.AddComponent<AvatarWindowHandler>();
            var camera=cameraObject.AddComponent<Camera>(); camera.orthographic=true; camera.orthographicSize=1.1f;
            camera.transform.position=new Vector3(0,0,-2); camera.targetTexture=texture; handler.targetCamera=camera;
            Field(handler,"unityHWND",new IntPtr(99)); Field(handler,"snappedHWND",new IntPtr(12));
            Field(handler,"desktop",snapshot); Field(handler,"_snapCursorY",1000);
            Call(handler,"UpdateCachedWindows");
            // Captured failure: animated hip is y=1053, target top is y=1008,
            // but the cursor has not moved since attachment.
            bool stays=(bool)Call(handler,"IsStillNearSnappedWindow");
            Field(km,"_visibleRect",(Rect?)new Rect(1524,180,1536,1668));
            km.UpdateSeatedDrag(); km.TryGetVisibleRect(out var after);
            bool preservesAlignment=after.y==180;
            Check(stays && preservesAlignment,$"Seated regression: stationary cursor retains seat={stays}; alignment survives held drag={preservesAlignment}");
            Check(Call(handler,"EvaluateNativeSeat",new IntPtr(12),new Vector2(2292,1008),21f).ToString()=="Accepted","Exposed valid edge must be accepted by the production evaluator");
            Check(Call(handler,"EvaluateNativeSeat",new IntPtr(12),new Vector2(2292,1053),21f).ToString()=="OutsideRadius","Fresh snaps still require the hip to be near the edge");
            Check(Call(handler,"EvaluateNativeSeat",new IntPtr(99),new Vector2(2292,1008),21f).ToString()=="Ineligible","Unknown target must not become a seat");
            Field(handler,"_snapCursorY",950);
            Check(!(bool)Call(handler,"IsStillNearSnappedWindow"),"Actual vertical cursor movement must detach");
            Field(handler,"_snapCursorY",1000);
            Field(km,"_sitDragCursor",new Vector2(2290,1000));
            km.UpdateSeatedDrag(); km.TryGetVisibleRect(out after);
            Check(after.x==1534 && after.y==180,"Sliding must apply only incremental horizontal movement");
            km.UpdateSeatedDrag(); km.TryGetVisibleRect(out after);
            Check(after.x==1534 && after.y==180,"Unchanged cursor must not repeat the slide delta");
            Field(manager,"_isDragging",false);
            Check(!(bool)typeof(AvatarWindowHandler).GetProperty("IsDragging",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(handler),"Sitting uses the accepted movement drag lifecycle");
            Field(handler,"desktop",DesktopSnapshot.Empty); Call(handler,"UpdateCachedWindows");
            Check(!(bool)Call(handler,"IsStillNearSnappedWindow"),"Missing supporting surface must detach");
            Debug.Log("Sitting handler regression passed: animation cannot detach and held dragging preserves seat alignment.");
        } finally {
            if(handler!=null) { Field(handler,"snappedHWND",IntPtr.Zero); Field(handler,"unityHWND",IntPtr.Zero); }
            if(manager!=null) Field(manager,"_closing",true);
            UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(cameraObject); UnityEngine.Object.DestroyImmediate(texture);
            WindowManager.Instance=previous;
            Environment.SetEnvironmentVariable("MATEENGINE_BACKEND",oldBackend); Environment.SetEnvironmentVariable("XDG_SESSION_TYPE",oldSession);
        }
    }

    static void RunMonitorRadius()
    {
        var snapshot=DesktopSnapshot.Parse(JsonConvert.SerializeObject(new {
            version=1,sittingSupported=true,sequence=1,mouseX=0,mouseY=0,windows=new object[0],
            outputs=new[]{
                new {name="left",scale=1.25f,rect=new {x=-1920f,y=-100f,width=1920f,height=1080f}},
                new {name="main",scale=1f,rect=new {x=0f,y=0f,width=2560f,height=1440f}},
                new {name="right",scale=1.5f,rect=new {x=2560f,y=0f,width=3840f,height=2160f}}}
        }),id=>IntPtr.Zero);
        Check(AvatarWindowHandler.NativeProbeRadius(snapshot,new Vector2(-100,100))==30f,"1080 logical pixels at negative origin gives radius 30");
        Check(AvatarWindowHandler.NativeProbeRadius(snapshot,new Vector2(100,100))==40f,"1440 logical pixels gives radius 40");
        Check(AvatarWindowHandler.NativeProbeRadius(snapshot,new Vector2(2600,100))==60f,"2160 logical pixels gives radius 60, without double-applying output scale");
        Check(AvatarWindowHandler.NativeProbeRadius(snapshot,new Vector2(2559,100))==40f && AvatarWindowHandler.NativeProbeRadius(snapshot,new Vector2(2560,100))==60f,"Cross-output probe switches to the destination monitor");
        Check(AvatarWindowHandler.NativeProbeRadius(snapshot,new Vector2(100,-20))==40f,"Outside all outputs uses nearest geometry");
        Check(AvatarWindowHandler.NativeProbeRadius(DesktopSnapshot.Empty,Vector2.zero)==40f,"Unavailable output uses finite baseline");
        Debug.Log("Monitor-relative sitting radius tests passed.");
    }

}
