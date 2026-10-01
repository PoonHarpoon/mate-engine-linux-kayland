using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class WaylandFrameBridge : MonoBehaviour
{
    const int HeaderSize=256, DefaultCaptureRate=60, MaximumCaptureRate=60;
    static readonly byte[] Magic={(byte)'M',(byte)'A',(byte)'T',(byte)'E',(byte)'E',(byte)'F',(byte)'R',(byte)'M'};
    string framePath, inputPath; RenderTexture texture; byte[] frameBytes; bool pending, sourceHidden; ulong sequence; int activeSlot=1;
    int captureRate=DefaultCaptureRate;
    Rect capturedRect;
    DesktopOutput capturedOutput;
    uint capturedFlags;
    Rect[] capturedOcclusion=Array.Empty<Rect>();
    AvatarWindowHandler sittingHandler;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
#if UNITY_STANDALONE_LINUX && !UNITY_EDITOR
        string path=Environment.GetEnvironmentVariable("MATEENGINE_PRESENTER_FRAME_FILE");
        if (WindowManager.IsNativeWaylandSession && !string.IsNullOrWhiteSpace(path)) {
            var host=new GameObject(nameof(WaylandFrameBridge)); DontDestroyOnLoad(host);
            var bridge=host.AddComponent<WaylandFrameBridge>(); bridge.framePath=path;
            bridge.inputPath=Environment.GetEnvironmentVariable("MATEENGINE_PRESENTER_INPUT_FILE");
            if (string.IsNullOrWhiteSpace(bridge.inputPath)) bridge.inputPath=path+".input";
        }
#endif
    }

    IEnumerator Start() {
        string configuredRate=Environment.GetEnvironmentVariable("MATEENGINE_PRESENTER_FPS");
        if (!string.IsNullOrWhiteSpace(configuredRate)) {
            if (!int.TryParse(configuredRate,out captureRate) || captureRate<1 || captureRate>MaximumCaptureRate) {
                Debug.LogWarning($"Invalid MATEENGINE_PRESENTER_FPS '{configuredRate}'; using {DefaultCaptureRate}.");
                captureRate=DefaultCaptureRate;
            }
        }
        Debug.Log($"Wayland presenter capture: {captureRate} FPS, graphics API {SystemInfo.graphicsDeviceType}, vertical flip {!SystemInfo.graphicsUVStartsAtTop}.");
        // WaitForSecondsRealtime rounds each wait up to the next Unity frame.
        // At a 144 Hz player rate, a 60 Hz wait therefore captures every third
        // frame (48 FPS). Check the deadline each rendered frame instead.
        double nextCapture=Time.realtimeSinceStartupAsDouble;
        double interval=1.0/captureRate;
        while (enabled) {
            yield return new WaitForEndOfFrame();
            double now=Time.realtimeSinceStartupAsDouble;
            if (now<nextCapture) continue;
            Capture();
            nextCapture+=interval;
            if (nextCapture<now) nextCapture=now+interval;
        }
    }
    void Capture() {
        if (pending || Screen.width<=0 || Screen.height<=0) return;
        var manager=WindowManager.Instance;
        if (manager==null || !manager.TryGetVisiblePetRect(out capturedRect)) return;
        var outputs=manager.NativeDesktop.Outputs;
        capturedOutput=outputs.OrderByDescending(o=>OverlapArea(o.Rect,capturedRect)).ThenBy(o=>(o.Rect.center-capturedRect.center).sqrMagnitude).FirstOrDefault();
        if (capturedOutput==null) return;
        capturedFlags=(WindowManager.PresenterTopmost || manager.NativeSitting)?1u:0u;
        if (!SystemInfo.graphicsUVStartsAtTop) capturedFlags|=2u;
        if (Matee.AvatarControls.AvatarControlPanel.KeyboardCaptureRequested) capturedFlags|=4u;
        if (manager.NativeSitting) capturedFlags|=8u;
        if (sittingHandler==null || !sittingHandler.isActiveAndEnabled) sittingHandler=FindObjectsByType<AvatarWindowHandler>(FindObjectsSortMode.None).FirstOrDefault(h=>h.isActiveAndEnabled);
        capturedOcclusion=sittingHandler!=null?sittingHandler.CaptureOcclusion():Array.Empty<Rect>();
        if (texture==null || texture.width!=Screen.width || texture.height!=Screen.height) {
            if (texture!=null) texture.Release(); texture=new RenderTexture(Screen.width,Screen.height,0,RenderTextureFormat.ARGB32); }
        ScreenCapture.CaptureScreenshotIntoRenderTexture(texture); pending=true;
        AsyncGPUReadback.Request(texture,0,TextureFormat.RGBA32,OnReadback);
    }
    void Update() {
        bool presenterAlive=false;
        try { presenterAlive=File.Exists(inputPath) && DateTime.UtcNow-File.GetLastWriteTimeUtc(inputPath)<TimeSpan.FromSeconds(2); } catch (IOException) { }
        bool shouldHide=presenterAlive && sequence>=3 && WaylandInputInstaller.Ready;
        if (shouldHide!=sourceHidden && WindowManager.Instance!=null) {
            if (WindowManager.Instance.SetPresenterSourceHidden(shouldHide)) {
                sourceHidden=shouldHide;
                Debug.Log(shouldHide?"Wayland presenter is live; minimized the Unity source surface.":"Wayland presenter heartbeat stopped; restored the Unity source surface.");
            }
        }
    }
    void OnReadback(AsyncGPUReadbackRequest request) {
        pending=false; if (request.hasError) { Debug.LogWarning("Wayland presenter frame readback failed."); return; }
        try { WriteFrame(request.GetData<byte>(),texture.width,texture.height); }
        catch (Exception e) { Debug.LogWarning($"Wayland presenter frame publish failed: {e.Message}"); enabled=false; }
    }
    void WriteFrame(NativeArray<byte> pixels,int width,int height) {
        int stride=checked(width*4), slotSize=checked(stride*height);
        if (pixels.Length!=slotSize) throw new InvalidDataException($"Unexpected RGBA frame size {pixels.Length}; expected {slotSize}.");
        if (frameBytes==null || frameBytes.Length!=slotSize) frameBytes=new byte[slotSize];
        pixels.CopyTo(frameBytes);
        foreach (var mask in capturedOcclusion) {
            int x0=Mathf.Clamp(Mathf.FloorToInt((mask.xMin-capturedRect.xMin)*width/capturedRect.width),0,width);
            int x1=Mathf.Clamp(Mathf.CeilToInt((mask.xMax-capturedRect.xMin)*width/capturedRect.width),0,width);
            int y0=Mathf.Clamp(Mathf.FloorToInt((mask.yMin-capturedRect.yMin)*height/capturedRect.height),0,height);
            int y1=Mathf.Clamp(Mathf.CeilToInt((mask.yMax-capturedRect.yMin)*height/capturedRect.height),0,height);
            for (int y=y0;y<y1;y++) {
                int row=((capturedFlags&2u)!=0?height-1-y:y)*stride;
                Array.Clear(frameBytes,row+x0*4,Math.Max(0,x1-x0)*4);
            }
        }
        string directory=Path.GetDirectoryName(framePath); if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        activeSlot=1-activeSlot; sequence++;
        using (var stream=new FileStream(framePath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite))
        using (var header=new MemoryStream(HeaderSize))
        using (var writer=new BinaryWriter(header)) {
            stream.SetLength(HeaderSize+(long)slotSize*2); stream.Position=HeaderSize+(long)activeSlot*slotSize;
            stream.Write(frameBytes,0,frameBytes.Length); stream.Flush();
            writer.Write(Magic); writer.Write(3u); writer.Write((uint)width); writer.Write((uint)height); writer.Write((uint)stride);
            writer.Write(1u); writer.Write((uint)slotSize); writer.Write((uint)activeSlot); writer.Write(0u);
            writer.Write(activeSlot==0?sequence:0ul); writer.Write(activeSlot==1?sequence:0ul);
            writer.Write(capturedRect.x); writer.Write(capturedRect.y); writer.Write(capturedRect.width); writer.Write(capturedRect.height);
            writer.Write(capturedFlags); writer.Write(0u);
            byte[] name=Encoding.UTF8.GetBytes(capturedOutput.Name);
            if (name.Length>=128) throw new InvalidDataException("Output name exceeds presenter protocol limit");
            writer.Write(name); writer.Write(new byte[128-name.Length]);
            writer.Write(capturedOutput.Rect.x); writer.Write(capturedOutput.Rect.y);
            writer.Write(capturedOutput.Rect.width); writer.Write(capturedOutput.Rect.height); writer.Write(capturedOutput.Scale);
            writer.Write(new byte[28]); writer.Flush();
            stream.Position=0; stream.Write(header.GetBuffer(),0,HeaderSize); stream.Flush();
        }
    }
    static float OverlapArea(Rect a,Rect b) => Mathf.Max(0,Mathf.Min(a.xMax,b.xMax)-Mathf.Max(a.xMin,b.xMin))*Mathf.Max(0,Mathf.Min(a.yMax,b.yMax)-Mathf.Max(a.yMin,b.yMin));
    void OnDestroy() { if (sourceHidden && WindowManager.Instance!=null) WindowManager.Instance.SetPresenterSourceHidden(false); if (texture!=null) texture.Release(); }
}
