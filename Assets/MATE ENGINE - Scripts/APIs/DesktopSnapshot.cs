using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

// DTOs are private to deserialization; published state has no mutable collection API.
public sealed class DesktopWindow
{
    public IntPtr Id { get; }
    public string Uuid { get; }
    public int Pid { get; }
    public Rect Rect { get; }
    public bool Visible { get; }
    public bool Dock { get; }
    public bool Desktop { get; }
    public bool Own { get; }
    public bool Fullscreen { get; }
    public bool Maximized { get; }
    public string ClassName { get; }
    public bool SeatEligible { get; }
    internal DesktopWindow(WindowData w, IntPtr id, bool sittingSupported)
    {
        Id=id; Uuid=w.uuid; Pid=w.pid; Rect=w.rect.Value; Visible=w.visible;
        Dock=w.dock; Desktop=w.desktop; Own=w.own; Fullscreen=w.fullscreen;
        Maximized=w.maximized; ClassName=w.className ?? "";
        SeatEligible=sittingSupported && Visible && !Own && !Desktop && !w.popup && !Fullscreen && !Maximized &&
            (Dock || (w.normal && Rect.width>=200 && Rect.height>=60));
    }
}
public sealed class DesktopOutput
{
    public string Name { get; }
    public Rect Rect { get; }
    public float Scale { get; }
    internal DesktopOutput(OutputData o) { Name=o.name; Rect=o.rect.Value; Scale=o.scale; }
}
public sealed class DesktopSnapshot
{
    public static readonly DesktopSnapshot Empty = new DesktopSnapshot();
    public readonly long Sequence, ReceivedAt;
    public readonly Vector2 Mouse;
    public readonly bool SittingSupported;
    public readonly IReadOnlyList<DesktopWindow> Windows;
    public readonly IReadOnlyList<DesktopOutput> Outputs;
    readonly IReadOnlyDictionary<IntPtr,DesktopWindow> byId;
    public bool Fresh => ReceivedAt!=0 && (Stopwatch.GetTimestamp()-ReceivedAt)/(double)Stopwatch.Frequency<2;
    private DesktopSnapshot()
    {
        Windows=Array.Empty<DesktopWindow>(); Outputs=Array.Empty<DesktopOutput>();
        byId=new ReadOnlyDictionary<IntPtr,DesktopWindow>(new Dictionary<IntPtr,DesktopWindow>());
    }
    private DesktopSnapshot(SnapshotData data, Func<string,IntPtr> handle)
    {
        SittingSupported=data.sittingSupported; Sequence=data.sequence; ReceivedAt=Stopwatch.GetTimestamp(); Mouse=new Vector2(data.mouseX,data.mouseY);
        var windows=data.windows.Select(w=>new DesktopWindow(w,handle(w.uuid),SittingSupported)).ToArray();
        Windows=Array.AsReadOnly(windows); Outputs=Array.AsReadOnly(data.outputs.Select(o=>new DesktopOutput(o)).ToArray());
        byId=new ReadOnlyDictionary<IntPtr,DesktopWindow>(windows.ToDictionary(w=>w.Id));
    }
    public bool TryGet(IntPtr id, out DesktopWindow window) => byId.TryGetValue(id,out window);
    public static DesktopSnapshot Parse(string json, Func<string,IntPtr> handle)
    {
        var data=JsonConvert.DeserializeObject<SnapshotData>(json);
        if (data==null || data.version!=1 || !string.IsNullOrEmpty(data.error) || data.sequence<=0 || data.windows==null || data.outputs==null)
            throw new FormatException(data?.error ?? "Invalid desktop snapshot");
        if (data.windows.Length>8192 || data.outputs.Length>64 || data.outputs.Length==0 ||
            data.windows.Any(w=>w==null || string.IsNullOrEmpty(w.uuid) || w.rect==null || !w.rect.Valid) ||
            data.windows.Select(w=>w.uuid).Distinct().Count()!=data.windows.Length ||
            data.outputs.Any(o=>o==null || string.IsNullOrEmpty(o.name) || o.rect==null || !o.rect.Valid || o.rect.width<=0 || o.rect.height<=0 || !Finite(o.scale) || o.scale<=0) ||
            !Finite(data.mouseX) || !Finite(data.mouseY)) throw new FormatException("Invalid desktop geometry");
        return new DesktopSnapshot(data,handle);
    }
    internal static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}
[Serializable] internal sealed class GeometryData {
    public float x,y,width,height;
    public Rect Value => new Rect(x,y,width,height);
    public bool Valid => DesktopSnapshot.Finite(x) && DesktopSnapshot.Finite(y) && DesktopSnapshot.Finite(width) && DesktopSnapshot.Finite(height) && width>=0 && height>=0;
}
[Serializable] internal sealed class WindowData {
    public string uuid,className; public int pid; public GeometryData rect;
    public bool visible,dock,desktop,normal,popup,fullscreen,maximized,own;
}
[Serializable] internal sealed class OutputData { public string name; public GeometryData rect; public float scale; }
[Serializable] internal sealed class SnapshotData {
    public int version; public bool sittingSupported; public long sequence; public string error; public float mouseX,mouseY;
    public WindowData[] windows; public OutputData[] outputs;
}
