using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus;
using UnityEngine;
using KWinUUID = System.String;

[DBusInterface("org.kde.kwin.Scripting")]
internal interface IScripting : IDBusObject
{
    Task<int> loadScriptAsync(string path, string name);
    Task unloadScriptAsync(string name);
    Task<bool> isScriptLoadedAsync(string name);
}

[DBusInterface("org.kde.kwin.Script")]
internal interface IScriptInstance : IDBusObject
{
    Task runAsync();
}

[DBusInterface("org.kdotool.callback")]
public interface IKWinCallback : IDBusObject
{
    Task ResultAsync(string id, string message);
    Task ErrorAsync(string id, string message);
    Task FinishAsync(string id);
    Task DragPositionAsync(string id, string x, string y);
    Task DesktopSnapshotAsync(string epoch, string json);
    Task DesktopTickAsync(string epoch);
}

public class KWinCallbackReceiver : IKWinCallback
{
    public ObjectPath ObjectPath => "/";
    public Action<string, float, float> OnDragPosition;
    public Action<string,string> OnDesktopSnapshot;
    public async Task DesktopSnapshotAsync(string epoch, string json) { OnDesktopSnapshot?.Invoke(epoch,json); await Task.Delay(16); }
    public Task DesktopTickAsync(string epoch) => Task.Delay(16);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<List<string>>> _pendingTasks = new();
    private readonly ConcurrentDictionary<string, List<string>> _results = new();

    public void PrepareId(string id, TaskCompletionSource<List<string>> tcs)
    {
        _pendingTasks[id] = tcs;
        _results[id] = new List<string>();
    }

    public Task ResultAsync(string id, string message)
    {
        if (_results.TryGetValue(id, out var list))
            list.Add(message);
        return Task.CompletedTask;
    }

    public Task ErrorAsync(string id, string message)
    {
        if (_pendingTasks.TryRemove(id, out var tcs))
        {
            _results.TryRemove(id, out _);
            tcs.SetException(new Exception($"KWin Script Error: {message}"));
        }
        return Task.CompletedTask;
    }

    public Task FinishAsync(string id)
    {
        if (_pendingTasks.TryRemove(id, out var tcs))
        {
            if (_results.TryRemove(id, out var list))
                tcs.SetResult(list);
        }
        return Task.CompletedTask;
    }

    public Task DragPositionAsync(string id, string x, string y)
    {
        if (float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) &&
            float.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var py) &&
            DesktopSnapshot.Finite(px) && DesktopSnapshot.Finite(py))
            OnDragPosition?.Invoke(id, px, py);
        return Task.CompletedTask;
    }

    public void CancelAll()
    {
        foreach (var pending in _pendingTasks)
            if (_pendingTasks.TryRemove(pending.Key, out var tcs))
                tcs.TrySetCanceled();
        _results.Clear();
    }
}

public class KWinManager : IDisposable, IWindowManagerImplementation
{
    private enum ScriptDeletionMode
    {
        DeleteOnFinishExecution,
        DeleteOnApplicationQuit
    }
    
    private Connection _connection;
    private ConnectionInfo _connectionInfo;
    private KWinCallbackReceiver _callbackHandler;
    private string _kdeVersion;
    private KWinUUID _unityUuid;
    private string _tempPath;
    private IScripting _scripting;
    private string _template;
    // Setup runs on Unity's synchronization context while discovery runs on a
    // worker. These flags cross that boundary and must not be cached per-core.
    private volatile bool _dbusReady;
    private volatile bool _initialized;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly SemaphoreSlim _scriptSemaphore = new(1, 1);
    private readonly Task _loopTask;
    private int _scriptSequence;
    private bool _disposed;

    private DesktopSnapshot _snapshot = DesktopSnapshot.Empty;
    public DesktopSnapshot Snapshot { get { var s=Volatile.Read(ref _snapshot); return s.Fresh?s:DesktopSnapshot.Empty; } }
    private readonly string _observerSource;
    private string _observerEpoch;
    private readonly List<string> _observerScriptNames = new();
    private int _observerAttempts;
    private bool _observerRetriesExhaustedLogged;
    private long _lastObserverStart;
    private readonly object _snapshotLock = new object();
    private readonly int _ownerPid = Process.GetCurrentProcess().Id;
    private readonly bool _observerDiagnostics = Environment.GetEnvironmentVariable("MATEENGINE_KWIN_DIAGNOSTICS") == "1";
    private Rect? _visibleRect;
    private string _placementOutput;
    private bool _startupPositioned;
    public IntPtr SelfWindow => Snapshot.Windows.FirstOrDefault(w=>w.Uuid==_unityUuid)?.Id ?? IntPtr.Zero;
    public bool Sitting { get; private set; }
    private Vector2 _sitDragCursor;
    private Dictionary<KWinUUID, IntPtr> _uuidToPtrMap = new();
    private int _ptrCounter = 1;
    


    public bool IsDragging { get; set; }
    
    private volatile bool _isProcessing;
    private readonly bool _dragDiagnostics = Environment.GetEnvironmentVariable("MATEENGINE_DRAG_DIAGNOSTICS") == "1";
    private int _moveRequests, _moveSkipped, _moveCompleted;
    private long _moveElapsedMs, _moveMaxMs;
    private readonly Stopwatch _moveReportTimer = Stopwatch.StartNew();
    private string NativeDragScriptName => $"MateEngine_NativeDrag_{_ownerPid}";
    private volatile bool _nativeDragRequested, _nativeDragScriptRunning;
    private string _nativeDragId;
    private string _loadedNativeDragId;
    private sealed class DragPoint {
        public readonly Vector2 Position;
        public DragPoint(Vector2 position) { Position=position; }
    }
    private DragPoint _nativeDragPoint=new DragPoint(Vector2.zero);
    private long _nativeDragStartDeadlineUtcTicks;
    
    public KWinManager(Connection connection, ConnectionInfo connectionInfo) 
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _kdeVersion = Environment.GetEnvironmentVariable("KDE_SESSION_VERSION") ?? "6";
        _tempPath = Application.temporaryCachePath;
        if (_observerDiagnostics) UnityEngine.Debug.Log($"KWin observer diagnostics: script directory {_tempPath}.");
        _observerSource = Resources.Load<TextAsset>("MateeDesktopObserver").text;
        _loopTask = Task.Run(() => Update(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
        _connection = connection;
        _connectionInfo = connectionInfo;
    }
    
    public async Task SetupDBus()
    {
        if (_initialized) return;
        _template = $@"
            function send(msg) {{
                callDBus('{_connectionInfo.LocalName}', '/', 'org.kdotool.callback', 'Result', 'placeholder', msg);
            }}
            function err(msg) {{
                callDBus('{_connectionInfo.LocalName}', '/', 'org.kdotool.callback', 'Error', 'placeholder', msg);
            }}
            function done() {{
                callDBus('{_connectionInfo.LocalName}', '/', 'org.kdotool.callback', 'Finish', 'placeholder');
            }}";

        _callbackHandler = new KWinCallbackReceiver();
        _callbackHandler.OnDesktopSnapshot = (epoch,json) => {
            lock (_snapshotLock) {
                if (epoch!=_observerEpoch || _disposed) return;
                try {
                    var next=DesktopSnapshot.Parse(json,GetPtrFromUuid);
                    if (next.Sequence<=Volatile.Read(ref _snapshot).Sequence) return;
                    if (Volatile.Read(ref _snapshot)==DesktopSnapshot.Empty)
                    {
                        UnityEngine.Debug.Log($"KWin desktop observer ready: {next.Windows.Count} windows, {next.Outputs.Count} outputs; version 1.");
                        if (!next.SittingSupported) UnityEngine.Debug.LogWarning("KWin hidden/maximizeMode capabilities unavailable; window sitting disabled, native presentation remains available.");
                    }
                    Volatile.Write(ref _snapshot,next);
                    var active=new HashSet<string>(next.Windows.Select(w=>w.Uuid));
                    foreach(var removed in _uuidToPtrMap.Keys.Where(id=>!active.Contains(id)).ToArray()) _uuidToPtrMap.Remove(removed);
                } catch(Exception e) {
                    Volatile.Write(ref _snapshot,DesktopSnapshot.Empty);
                    UnityEngine.Debug.LogWarning("KWin desktop observer: "+e.Message);
                    _observerEpoch=null;
                }
            }
        };
        _callbackHandler.OnDragPosition = (id, x, y) =>
        {
            if (!_nativeDragRequested || id != _nativeDragId) return;
            Volatile.Write(ref _nativeDragPoint,new DragPoint(new Vector2(x,y)));
            if (!_nativeDragScriptRunning)
                UnityEngine.Debug.Log("KWin native drag cursor stream active; presenter follows cursor without per-move window scripts.");
            _nativeDragScriptRunning = true;
        };
        await _connection.RegisterObjectAsync(_callbackHandler);
        
        await _connection.RegisterServiceAsync("org.kdotool.callback");
        
        _scripting = _connection.CreateProxy<IScripting>("org.kde.KWin", "/Scripting");

        _dbusReady = true;
        if (_observerDiagnostics) UnityEngine.Debug.Log("KWin observer diagnostics: D-Bus callback registered.");
    }

    private async Task Update(CancellationToken cancellationToken)
    {
        if (_observerDiagnostics) UnityEngine.Debug.Log("KWin observer diagnostics: update loop started.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_dbusReady)
                        await EnsureDesktopObserver();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogException(e);
                }

                if (!_initialized)
                {
                    _unityUuid = GetSelfWindowUuid();
                    if (_unityUuid != string.Empty)
                        _initialized = true;
                }
            
                await Task.Delay(50, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task EnsureDesktopObserver()
    {
        long retryTicks = _initialized ? TimeSpan.FromSeconds(3).Ticks : TimeSpan.FromMilliseconds(750).Ticks;
        if (Snapshot.Fresh || DateTime.UtcNow.Ticks-Interlocked.Read(ref _lastObserverStart)<retryTicks) return;
        if (_observerAttempts >= 8) {
            if (!_observerRetriesExhaustedLogged) {
                UnityEngine.Debug.LogWarning("KWin desktop observer exhausted eight script IDs without a snapshot; native Wayland window control is unavailable.");
                _observerRetriesExhaustedLogged = true;
            }
            return;
        }
        if (_observerDiagnostics) UnityEngine.Debug.Log("KWin observer diagnostics: loading script.");
        Interlocked.Exchange(ref _lastObserverStart,DateTime.UtcNow.Ticks);
        lock (_snapshotLock) { _observerEpoch=Guid.NewGuid().ToString("N"); Volatile.Write(ref _snapshot,DesktopSnapshot.Empty); }
        await _scriptSemaphore.WaitAsync(_cancellationTokenSource.Token);
        try {
            // KWin 6.7 assigns a new script ID from its loaded-script count.
            // If an earlier script with a higher ID is still running, that
            // number can already own the D-Bus object path. Keep a failed
            // attempt loaded as a slot reservation and try the next ID.
            string scriptName=$"MateEngine_DesktopObserver_{_ownerPid}_{++_observerAttempts}";
            string path=Path.Combine(_tempPath,scriptName+".js");
            string source=_observerSource.Replace("__BUS__",_connectionInfo.LocalName).Replace("__EPOCH__",_observerEpoch)
                .Replace("__PID__",_ownerPid.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("__PRESENTER_PID__", (int.TryParse(Environment.GetEnvironmentVariable("MATEENGINE_PRESENTER_PID"), out var presenterPid) && presenterPid>0 ? presenterPid : 0)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture));
            await File.WriteAllTextAsync(path,source);
            if (_observerDiagnostics) UnityEngine.Debug.Log($"KWin observer diagnostics: script file written ({source.Length} characters).");
            int id=await _scripting.loadScriptAsync(path,scriptName);
            if (id<0) throw new Exception("KWin rejected desktop observer");
            _observerScriptNames.Add(scriptName);
            if (_observerDiagnostics) UnityEngine.Debug.Log($"KWin observer diagnostics: script loaded with id {id}.");
            await _connection.CreateProxy<IScriptInstance>("org.kde.KWin",$"/Scripting/Script{id}").runAsync();
            if (_observerDiagnostics) UnityEngine.Debug.Log("KWin observer diagnostics: script started.");
            if (!_pendingDeletions.Contains(path)) _pendingDeletions.Add(path);
        } finally { _scriptSemaphore.Release(); }
    }

    private IntPtr GetPtrFromUuid(string uuid)
    {
        if (string.IsNullOrEmpty(uuid)) return IntPtr.Zero;
    
        if (_uuidToPtrMap.TryGetValue(uuid, out var existingPtr))
            return existingPtr;
    
        var newPtr = new IntPtr(_ptrCounter++);
        _uuidToPtrMap[uuid] = newPtr;
        return newPtr;
    }

    public bool BeginNativeDrag(Vector2 startPosition)
    {
        if (!_initialized || _disposed || Sitting || !_kdeVersion.StartsWith("6")) return false;
        _nativeDragId = Guid.NewGuid().ToString("N");
        if (TryGetVisibleRect(out var visible)) startPosition=visible.position;
        Volatile.Write(ref _nativeDragPoint,new DragPoint(startPosition));
        _nativeDragRequested = true;
        _nativeDragScriptRunning = false;
        _nativeDragStartDeadlineUtcTicks = DateTime.UtcNow.AddMilliseconds(500).Ticks;
        _ = StartNativeDragScriptAsync(_nativeDragId, startPosition);
        return true;
    }

    public bool IsNativeDragPositioning
    {
        get
        {
            if (_nativeDragRequested && !_nativeDragScriptRunning && DateTime.UtcNow.Ticks > _nativeDragStartDeadlineUtcTicks)
            {
                _nativeDragRequested = false;
                _ = StopNativeDragScriptAsync(_nativeDragId);
                UnityEngine.Debug.LogWarning("KWin native drag cursor stream did not start; using per-move scripts for this drag.");
            }
            return _nativeDragRequested;
        }
    }

    public void EndNativeDrag()
    {
        string id = _nativeDragId;
        bool wasRunning = _nativeDragScriptRunning;
        _nativeDragRequested = false;
        _nativeDragScriptRunning = false;
        if (wasRunning)
        {
            // Commit the final visible position once, for source-window recovery.
            var position=Volatile.Read(ref _nativeDragPoint).Position;
            if (_visibleRect.HasValue) { var r=_visibleRect.Value; r.position=position; _visibleRect=r; }
            _ = CommitNativeDragPositionAsync(Vector2Int.RoundToInt(position));
        }
        _ = StopNativeDragScriptAsync(id);
    }

    private async Task StartNativeDragScriptAsync(string id, Vector2 startPosition)
    {
        string scriptPath = Path.Combine(_tempPath, NativeDragScriptName + ".js");
        try
        {
            await _scriptSemaphore.WaitAsync(_cancellationTokenSource.Token);
            try
            {
                if (!_nativeDragRequested || id != _nativeDragId) return;
                // A killed player may leave this fixed-name script loaded.
                if (await _scripting.isScriptLoadedAsync(NativeDragScriptName))
                    await _scripting.unloadScriptAsync(NativeDragScriptName);
                _loadedNativeDragId = null;
                string script = $@"
                    const dragId = '{id}';
                    const start = {{ x: {startPosition.x.ToString(CultureInfo.InvariantCulture)}, y: {startPosition.y.ToString(CultureInfo.InvariantCulture)} }};
                    const startCursorX = workspace.cursorPos.x;
                    const startCursorY = workspace.cursorPos.y;
                    function reportPosition() {{
                        const now = workspace.cursorPos;
                        callDBus('{_connectionInfo.LocalName}', '/', 'org.kdotool.callback', 'DragPosition',
                                 dragId, String(start.x + now.x - startCursorX),
                                 String(start.y + now.y - startCursorY));
                    }}
                    workspace.cursorPosChanged.connect(reportPosition);
                    workspace.windowRemoved.connect(function(window) {{
                        if (window.internalId.toString() === '{_unityUuid}')
                            workspace.cursorPosChanged.disconnect(reportPosition);
                    }});
                    reportPosition();";
                await File.WriteAllTextAsync(scriptPath, script);
                int scriptId = await _scripting.loadScriptAsync(scriptPath, NativeDragScriptName);
                if (scriptId == -1) throw new Exception("KWin rejected the native drag script.");
                _loadedNativeDragId = id;
                var instance = _connection.CreateProxy<IScriptInstance>("org.kde.KWin", $"/Scripting/Script{scriptId}");
                await instance.runAsync();
            }
            finally { _scriptSemaphore.Release(); }
        }
        catch (Exception e)
        {
            if (id == _nativeDragId)
            {
                _nativeDragRequested = false;
                UnityEngine.Debug.LogWarning($"Native Wayland drag cursor stream unavailable; using per-move KWin scripts: {e.Message}");
            }
        }
        if (!_nativeDragRequested || id != _nativeDragId) _ = StopNativeDragScriptAsync(id);
    }

    private async Task StopNativeDragScriptAsync(string id)
    {
        try
        {
            await _scriptSemaphore.WaitAsync(_cancellationTokenSource.Token);
            try
            {
                if (_loadedNativeDragId != id) return;
                if (await _scripting.isScriptLoadedAsync(NativeDragScriptName))
                    await _scripting.unloadScriptAsync(NativeDragScriptName);
                _loadedNativeDragId = null;
                string scriptPath = Path.Combine(_tempPath, NativeDragScriptName + ".js");
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
            finally { _scriptSemaphore.Release(); }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { UnityEngine.Debug.LogWarning($"KWin native drag script cleanup failed: {e.Message}"); }
    }

    private async Task CommitNativeDragPositionAsync(Vector2Int position)
    {
        while (_isProcessing && !_disposed) await Task.Delay(10);
        if (_disposed) return;
        if (_dragDiagnostics) Interlocked.Increment(ref _moveRequests);
        await MoveWindow(position);
    }
    
    public void SetWindowPosition(Vector2Int position) => SetVisiblePosition(position);

    public bool SampleVisibleRect()
    {
        bool rehomed=false;
        var snapshot=Snapshot;
        var outputs=snapshot.Outputs;
        if (!_startupPositioned && _initialized && outputs.Count>0 && !_nativeDragRequested && !Sitting) {
            var source=snapshot.Windows.FirstOrDefault(w=>w.Uuid==_unityUuid);
            if (source!=null && source.Rect.width>0 && source.Rect.height>0) {
                var output=outputs.FirstOrDefault(o=>o.Rect.Contains(snapshot.Mouse))
                    ?? outputs.OrderBy(o=>(o.Rect.center-snapshot.Mouse).sqrMagnitude).First();
                var rect=source.Rect;
                // KWin's first frame can include a titlebar until the borderless
                // request applies. The presenter displays Unity's rendered pixels.
                rect.size=new Vector2(Screen.width,Screen.height);
                rect.center=output.Rect.center;
                _visibleRect=rect;
                _placementOutput=output.Name;
                _startupPositioned=true;
                _ = CommitNativeDragPositionAsync(Vector2Int.RoundToInt(rect.position));
                UnityEngine.Debug.Log($"Native Wayland startup placement: source={source.Rect}, output={output.Name} {output.Rect}, visible={rect}.");
                rehomed=true;
            }
        }
        if (_nativeDragRequested && !Sitting) {
            var source=_visibleRect ?? GetWindowGeometry(_unityUuid);
            source.position=Volatile.Read(ref _nativeDragPoint).Position; _visibleRect=source;
        }
        if (_visibleRect.HasValue && outputs.Count>0) {
            var rect=_visibleRect.Value;
            var nearest=outputs.OrderBy(o=>(o.Rect.center-rect.center).sqrMagnitude).First();
            if (_placementOutput!=null && !outputs.Any(o=>o.Name==_placementOutput)) {
                EndNativeDrag();
                rect.center=nearest.Rect.center; _visibleRect=rect;
                _sitDragCursor=Snapshot.Mouse;
                _ = CommitNativeDragPositionAsync(Vector2Int.RoundToInt(rect.position));
                rehomed=true;
            }
            _placementOutput=(outputs.FirstOrDefault(o=>o.Rect.Contains(rect.center)) ?? nearest).Name;
        }
        return rehomed;
    }
    public bool TryGetVisibleRect(out Rect rect)
    {
        if (_visibleRect.HasValue) { rect=_visibleRect.Value; return rect.width>0 && rect.height>0; }
        rect=GetWindowGeometry(_unityUuid);
        if (rect.width>0 && rect.height>0) _visibleRect=rect;
        return rect.width>0 && rect.height>0;
    }
    public void SetVisiblePosition(Vector2 position)
    {
        if (!TryGetVisibleRect(out var rect)) return;
        rect.position=position; _visibleRect=rect;
        // The presenter owns visible motion. Source synchronization is only needed
        // for fallback restoration, and must never gate presenter movement.
        if (!Sitting && !_nativeDragRequested) _ = MoveWindow(Vector2Int.RoundToInt(position));
    }
    public void SetSitting(bool value)
    {
        if (value==Sitting) return;
        TryGetVisibleRect(out var rect);
        if (value) EndNativeDrag();
        Sitting=value; _visibleRect=rect;
        _sitDragCursor=Snapshot.Mouse;
        if (!value) _ = CommitNativeDragPositionAsync(Vector2Int.RoundToInt(rect.position));
    }
    public void BeginSeatedDrag() { _sitDragCursor=Snapshot.Mouse; }
    public void UpdateSeatedDrag()
    {
        var cursor=Snapshot.Mouse;
        if (TryGetVisibleRect(out var rect))
            SetVisiblePosition(rect.position+new Vector2(cursor.x-_sitDragCursor.x,0));
        _sitDragCursor=cursor;
    }

    public void SetWindowSize(Vector2Int size)
    {
        if (TryGetVisibleRect(out var rect)) { rect.size=size; _visibleRect=rect; }
        _ = RunSelfWindowScriptAsync("ResizeWin", $@"
            w.frameGeometry = {{ x: w.frameGeometry.x, y: w.frameGeometry.y, width: {size.x}, height: {size.y} }};");
    }

    public bool SetWindowMinimized(bool minimized)
    {
        if (!_initialized || string.IsNullOrEmpty(_unityUuid))
            return false;
        _ = RunSelfWindowScriptAsync("PresenterVisibility", $"w.minimized = {minimized.ToString().ToLowerInvariant()};");
        return true;
    }

    public Vector2Int GetWindowPosition() => TryGetVisibleRect(out var rect)?Vector2Int.RoundToInt(rect.position):Vector2Int.zero;
    public int GetWindowPid(IntPtr window) => Snapshot.TryGet(window,out var w)?w.Pid:-1;
    public Vector2Int GetMousePosition() => Vector2Int.RoundToInt(Snapshot.Mouse);
    public List<IntPtr> FindWindowsByPid(int targetPid) => Snapshot.Windows.Where(w=>w.Pid==targetPid).Select(w=>w.Id).ToList();
    public List<IntPtr> GetAllWindows() => Snapshot.Windows.Select(w=>w.Id).ToList();
    public Vector2Int GetWindowSize(IntPtr window) {
        if (window==IntPtr.Zero && TryGetVisibleRect(out var r)) return Vector2Int.RoundToInt(r.size);
        return Snapshot.TryGet(window,out var w)?Vector2Int.RoundToInt(w.Rect.size):Vector2Int.zero;
    }
    public bool GetWindowRect(IntPtr window,out RectInt rectInt) {
        if (Snapshot.TryGet(window,out var w)) { rectInt=RoundRect(w.Rect); return true; }
        rectInt=default; return false;
    }
    internal static RectInt RoundRect(Rect r) => new RectInt(Mathf.RoundToInt(r.x),Mathf.RoundToInt(r.y),Mathf.RoundToInt(r.width),Mathf.RoundToInt(r.height));
    public Vector2Int GetTotalDisplaySize() {
        var outputs=Snapshot.Outputs;
        if (outputs.Count==0) return Vector2Int.zero;
        return Vector2Int.CeilToInt(new Vector2(outputs.Max(o=>o.Rect.xMax)-outputs.Min(o=>o.Rect.xMin),outputs.Max(o=>o.Rect.yMax)-outputs.Min(o=>o.Rect.yMin)));
    }
    public bool IsWindowVisible(IntPtr window) => Snapshot.TryGet(window,out var w) && w.Visible;
    public bool IsWindowFullscreen(IntPtr window) => Snapshot.TryGet(window,out var w) && w.Fullscreen;
    public bool IsWindowMaximized(IntPtr window) => Snapshot.TryGet(window,out var w) && w.Maximized;
    public List<IntPtr> GetClientStackingList() => GetAllWindows();
    public List<(IntPtr Id, RectInt Rect)> GetAllMonitors() => Snapshot.Outputs.Select((o,i)=>(new IntPtr(i+1),RoundRect(o.Rect))).ToList();
    public bool IsDesktop(IntPtr window) => Snapshot.TryGet(window,out var w) && w.Desktop;
    public bool IsDock(IntPtr window) => Snapshot.TryGet(window,out var w) && w.Dock;
    public string GetClassName(IntPtr window) => Snapshot.TryGet(window,out var w)?w.ClassName:"";
    public void SetTopmost(bool topmost)
    {
        _ = RunSelfWindowScriptAsync("SetTopmost", $"w.keepAbove = {(topmost ? "true" : "false")};");
    }

    public void HideFromTaskbar(bool reallyHide)
    {
        _ = RunSelfWindowScriptAsync("SetSkipTaskbar", $"w.skipTaskbar = {(reallyHide ? "true" : "false")};");
    }

    public void SetWindowBorderless(bool value)
    {
        _ = RunSelfWindowScriptAsync("SetBorder", $"w.noBorder = {(value ? "true" : "false")};");
    }

    public void SetWindowType(WindowType type)
    {
        // Wayland surface roles are selected by the client at creation time. KWin can
        // safely change decoration, but cannot turn this toplevel into an X11 dock.
        SetWindowBorderless(type != WindowType.ShowBorder);
        if (type is WindowType.Dock or WindowType.Desktop)
            UnityEngine.Debug.LogWarning($"KWin Wayland backend cannot change a running toplevel into a {type} role.");
    }
    public void SetXUnityWindow(IntPtr unityWindow) { }
    public void SetSnapedWindow(IntPtr window) { SetSitting(window!=IntPtr.Zero); }

    private KWinUUID GetSelfWindowUuid() => Snapshot.Windows.FirstOrDefault(w=>w.Pid==_ownerPid)?.Uuid ?? string.Empty;

    public async Task<bool> WaitForSelfWindowAsync(int timeoutMilliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (_initialized && !string.IsNullOrEmpty(_unityUuid))
                return true;
            await Task.Delay(50);
        }
        return false;
    }

    private Rect GetWindowGeometry(string uuid) => Snapshot.Windows.FirstOrDefault(w=>w.Uuid==uuid)?.Rect ?? default;

    private async Task MoveWindow(Vector2Int pos)
    {
        if (!_initialized | _isProcessing)
        {
            if (_dragDiagnostics) Interlocked.Increment(ref _moveSkipped);
            return;
        }
        _isProcessing = true;
        Stopwatch moveTimer = _dragDiagnostics ? Stopwatch.StartNew() : null;
        try
        {
            await RunSelfWindowScriptAsync("MoveWin", $@"
                w.frameGeometry = {{ x: {pos.x}, y: {pos.y}, width: w.frameGeometry.width, height: w.frameGeometry.height }};");
        }
        finally
        {
            _isProcessing = false;
            if (moveTimer != null)
            {
                long elapsed = moveTimer.ElapsedMilliseconds;
                Interlocked.Increment(ref _moveCompleted);
                Interlocked.Add(ref _moveElapsedMs, elapsed);
                long oldMax;
                do { oldMax = Interlocked.Read(ref _moveMaxMs); }
                while (elapsed > oldMax && Interlocked.CompareExchange(ref _moveMaxMs, elapsed, oldMax) != oldMax);
                if (_moveReportTimer.ElapsedMilliseconds >= 2000)
                {
                    _moveReportTimer.Restart();
                    int completed = Interlocked.Exchange(ref _moveCompleted, 0);
                    long totalMs = Interlocked.Exchange(ref _moveElapsedMs, 0);
                    UnityEngine.Debug.Log($"Wayland drag KWin: requested {Interlocked.Exchange(ref _moveRequests, 0)}, skipped {Interlocked.Exchange(ref _moveSkipped, 0)}, completed {completed}, script mean {(completed > 0 ? totalMs / completed : 0)} ms, max {Interlocked.Exchange(ref _moveMaxMs, 0)} ms / 2 s.");
                }
            }
        }
    }

    private async Task RunSelfWindowScriptAsync(string operation, string operationScript)
    {
        if (!_initialized || string.IsNullOrEmpty(_unityUuid))
            return;

        string requestId = $"MateEngine_{operation}_{Interlocked.Increment(ref _scriptSequence)}";
        string scriptName = requestId + ".js";
        string jsScript = _template.Replace("placeholder", requestId) + $@"
            for (let w of workspace.{(_kdeVersion.StartsWith("5") ? "clientList" : "windowList")}()) {{
                if (w.internalId.toString() == ""{_unityUuid}"") {{
                    {operationScript}
                    break;
                }}
            }}
            done();";

        try
        {
            await File.WriteAllTextAsync(Path.Combine(_tempPath, scriptName), jsScript);
            await ExecuteKWinScript(scriptName, ScriptDeletionMode.DeleteOnFinishExecution);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning($"KWin {operation} request failed: {e.Message}");
        }
    }
    
    private async Task<List<string>> ExecuteKWinScript(string scriptFileName, ScriptDeletionMode deletionMode)
    {
        if (!_dbusReady || _disposed) return new List<string>();

        await _scriptSemaphore.WaitAsync(_cancellationTokenSource.Token);
        string scriptPath = Path.Combine(_tempPath, scriptFileName);
        string requestId = Path.GetFileNameWithoutExtension(scriptFileName);
        try
        {
            var tcs = new TaskCompletionSource<List<string>>();
            _callbackHandler.PrepareId(requestId, tcs);

            // KWin retains a loaded script if the previous player is killed
            // before Dispose runs. Reclaim that fixed plugin name so a crash
            // cannot permanently break window discovery on later launches.
            if (await _scripting.isScriptLoadedAsync(requestId))
                await _scripting.unloadScriptAsync(requestId);
            int scriptId = await _scripting.loadScriptAsync(scriptPath, requestId);
            if (scriptId == -1)
                throw new Exception($"Script {scriptFileName} failed to load.");

            var instance = _kdeVersion.StartsWith("5")
                ? _connection.CreateProxy<IScriptInstance>("org.kde.KWin", $"/{scriptId}")
                : _connection.CreateProxy<IScriptInstance>("org.kde.KWin", $"/Scripting/Script{scriptId}");

            await instance.runAsync();
            var timeout = Task.Delay(2000, _cancellationTokenSource.Token);
            var completedTask = await Task.WhenAny(tcs.Task, timeout);
            if (completedTask == timeout)
                throw new TimeoutException($"Script {scriptFileName} timed out.");
            return await tcs.Task;
        }
        finally
        {
            try { await _scripting.unloadScriptAsync(requestId); }
            catch when (_disposed || _cancellationTokenSource.IsCancellationRequested) { }
            if (deletionMode == ScriptDeletionMode.DeleteOnFinishExecution && File.Exists(scriptPath))
                File.Delete(scriptPath);
            if (deletionMode == ScriptDeletionMode.DeleteOnApplicationQuit && !_pendingDeletions.Contains(scriptPath))
                _pendingDeletions.Add(scriptPath);
            _scriptSemaphore.Release();
        }
    }

    private readonly List<string> _pendingDeletions = new();

    public void Dispose()
    {
        if (_disposed) return;
        if (_scripting != null)
            foreach (var name in _observerScriptNames)
                _ = _scripting.unloadScriptAsync(name);
        _disposed = true;
        Volatile.Write(ref _snapshot,DesktopSnapshot.Empty);
        _cancellationTokenSource.Cancel();
        _callbackHandler?.CancelAll();

        foreach (var script in _pendingDeletions.Where(File.Exists))
        {
            File.Delete(script);
        }

        if (_callbackHandler != null)
            _connection?.UnregisterObject(_callbackHandler);
        _cancellationTokenSource.Dispose();
    }
}
