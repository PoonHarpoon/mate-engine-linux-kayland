using UnityEngine;
using System.Collections;
using System.Runtime.InteropServices;
using System;
using System.IO;
using System.Globalization;
using System.Runtime;

public class MemoryTrim : MonoBehaviour
{
    public bool enableAutoTrim = false;
    
    private const int MADV_PAGEOUT = 21; 
    
    [DllImport("libc.so.6", EntryPoint = "madvise", SetLastError = true)]
    private static extern int madvise(IntPtr addr, IntPtr length, int advice);

    [DllImport("libc.so.6", EntryPoint = "malloc_trim")]
    private static extern int malloc_trim(int pad);

    private static MemoryTrim _instance;
    public static MemoryTrim Instance => _instance ??= FindFirstObjectByType<MemoryTrim>();

    public void SetAutoTrimEnabled(bool enabled)
    {
        enableAutoTrim = enabled;
        CancelInvoke(nameof(StartupTrim));
        CancelInvoke(nameof(PeriodicTrim));
        if (enableAutoTrim)
        {
            TrimNow();
            Invoke(nameof(StartupTrim), 10f);
            InvokeRepeating(nameof(PeriodicTrim), 600f, 600f); 
        }
    }
    
    void DelayedStartupTrim()
    {
        if (enableAutoTrim) TrimNow();
    }

    void Awake()
    {
        _instance = this;
        if (enableAutoTrim)
        {
             TrimNow();
             Invoke(nameof(StartupTrim), 10f);
             InvokeRepeating(nameof(PeriodicTrim), 600f, 600f);
             Invoke(nameof(DelayedStartupTrim), 15f);
        }
    }

    void OnDisable()
    {
        CancelInvoke(nameof(StartupTrim));
        CancelInvoke(nameof(PeriodicTrim));
    }

    public void TrimNow()
    {
        #if UNITY_EDITOR
        return;
        #endif
        StartCoroutine(TrimRoutine());
    }

    private IEnumerator TrimRoutine()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        AsyncOperation op = Resources.UnloadUnusedAssets();
        while (!op.isDone) yield return null;
        
        malloc_trim(0);
        
        // Page Out Logic
        if (!File.Exists("/proc/self/maps")) yield break;

        string[] lines;
        try
        {
            lines = File.ReadAllLines("/proc/self/maps");
        }
        catch (Exception e)
        {
            Debug.LogError($"[MemoryTrim] Failed to read maps: {e.Message}");
            yield break;
        }

        int pagesReclaimed = 0;

        foreach (var line in lines)
        {
            try 
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                string addressRange = parts[0];
                string permissions = parts[1];
                
                if (permissions.StartsWith("rw") && permissions.Contains("p"))
                {
                    var addresses = addressRange.Split('-');
                    if (addresses.Length != 2) continue;

                    long start = long.Parse(addresses[0], NumberStyles.HexNumber);
                    long end = long.Parse(addresses[1], NumberStyles.HexNumber);
                    long length = end - start;
                    
                    if (length < 4096) continue;
                    
                    int result = madvise(new IntPtr(start), new IntPtr(length), MADV_PAGEOUT);
                    
                    if (result == 0) pagesReclaimed++;
                }
            }
            catch
            {
                // Swallow parsing errors to keep the loop robust
            }
            
            // Yield every few iterations to prevent frame freeze if map is huge
            if (pagesReclaimed % 50 == 0) yield return null;
        }

        Debug.Log($"[MemoryTrim] Aggressive trim complete. Segments advised: {pagesReclaimed}");
    }

    void StartupTrim() => TrimNow();
    void PeriodicTrim() => TrimNow();
}