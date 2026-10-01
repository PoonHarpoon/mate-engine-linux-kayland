using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;

// Unity -batchmode -quit -nographics -executeMethod AvatarLibraryChecks.Run
public static class AvatarLibraryChecks
{
    static List<AvatarLibraryMenu.AvatarEntry> Read(string path) =>
        (List<AvatarLibraryMenu.AvatarEntry>)typeof(AvatarLibraryMenu)
            .GetMethod("ReadAvatarList", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { path });

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    // Explicit opt-in: this cleans the supplied library. Back it up first.
    // -executeMethod AvatarLibraryChecks.RunLibraryImport --avatar-library <avatars.json>
    public static void RunLibraryImport()
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "--avatar-library");
        Check(index >= 0 && index + 1 < args.Length, "Supply an explicit backed-up library path");
        string path = args[index + 1];
        Check(File.Exists(path), "Library must exist");
        var entries = Read(path);
        Check(entries.Count > 0, "Expected surviving avatars to import");
        string saved = File.ReadAllText(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        Check(Read(path).Count == entries.Count && File.ReadAllText(path) == saved && File.GetLastWriteTimeUtc(path) == stamp,
            "Repeated load must preserve cleaned library");
        foreach (var entry in entries)
        {
            using var gltf = new UniGLTF.GlbFileParser(entry.filePath).Parse();
            var vrm10 = UniVRM10.Vrm10Data.Parse(gltf);
            if (vrm10 != null)
            {
                using var importer = new UniVRM10.Vrm10Importer(vrm10);
                var instance = importer.LoadAsync(new UniGLTF.ImmediateCaller()).GetAwaiter().GetResult();
                Check(instance.Root != null, "VRM1 import failed");
                UnityEngine.Object.DestroyImmediate(instance.Root);
            }
            else
            {
                using var importer = new VRM.VRMImporterContext(new VRM.VRMData(gltf));
                var instance = importer.LoadAsync(new UniGLTF.ImmediateCaller()).GetAwaiter().GetResult();
                Check(instance.Root != null, "VRM0 import failed");
                UnityEngine.Object.DestroyImmediate(instance.Root);
            }
            Debug.Log("Avatar library import passed: " + entry.displayName);
        }
        Debug.Log("Avatar library persisted reload/import checks passed: " + entries.Count);
    }

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "matee-avatar-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string config = Path.Combine(root, "avatars.json");
        string model = Path.Combine(root, "valid.vrm");
        string thumbnail = Path.Combine(root, "thumbnail.png");
        File.WriteAllText(model, "Fixture: existence check only");
        File.WriteAllText(thumbnail, "Keep this thumbnail");
        var first = new AvatarLibraryMenu.AvatarEntry {
            displayName = "First", filePath = model, thumbnailPath = thumbnail,
            author = "Author", version = "1", polygonCount = 42, isNSFW = true
        };
        var second = new AvatarLibraryMenu.AvatarEntry {
            displayName = "Second", filePath = model, thumbnailPath = "missing.png",
            isSteamWorkshop = true, steamFileId = 123
        };
        File.WriteAllText(config, JsonConvert.SerializeObject(new[] {
            first, null, new AvatarLibraryMenu.AvatarEntry { filePath = "" },
            new AvatarLibraryMenu.AvatarEntry { filePath = " " },
            new AvatarLibraryMenu.AvatarEntry { filePath = "bad\0path" },
            new AvatarLibraryMenu.AvatarEntry { filePath = root },
            new AvatarLibraryMenu.AvatarEntry { filePath = Path.Combine(root, "missing.vrm") }, second
        }));
        var entries = Read(config);
        Check(entries.Count == 2 && entries[0].displayName == "First" && entries[1].displayName == "Second", "Cleanup/order");
        Check(entries[0].isOwner && !entries[1].isOwner && entries[1].steamFileId == 123, "Ownership migration");
        Check(entries[0].author == "Author" && entries[0].version == "1" && entries[0].polygonCount == 42 && entries[0].isNSFW, "Metadata");
        Check(File.Exists(model) && File.Exists(thumbnail), "Cleanup must not delete files");
        Check(JsonConvert.DeserializeObject<List<AvatarLibraryMenu.AvatarEntry>>(File.ReadAllText(config)).Count == 2, "Persist cleanup");
        string saved = File.ReadAllText(config);
        File.SetLastWriteTimeUtc(config, new DateTime(2001, 1, 1));
        var stamp = File.GetLastWriteTimeUtc(config);
        Check(Read(config).Count == 2 && File.ReadAllText(config) == saved && File.GetLastWriteTimeUtc(config) == stamp, "Unchanged reload must not write");
        // Simulate removal after the library was previously opened.
        File.Delete(model);
        Check(Read(config).Count == 0 && File.ReadAllText(config).Trim() == "[]", "All missing must persist an empty list");
        Check(File.Exists(thumbnail), "Orphan thumbnail must survive");
        foreach (string json in new[] { "null", "[]", "{broken", "{}" })
        {
            File.WriteAllText(config, json);
            Check(Read(config).Count == 0 && File.ReadAllText(config) == json, "Null/malformed config must remain intact");
        }
        Check(Read(Path.Combine(root, "absent.json")).Count == 0, "Absent library");
        Check(Read(root).Count == 0 && Directory.Exists(root), "Unreadable library");
        Debug.Log("Avatar library checks passed: cleanup, persistence, ordering, metadata, ownership, no-op reload, null/malformed/unreadable config and file preservation. Fixtures: " + root);
    }
}
