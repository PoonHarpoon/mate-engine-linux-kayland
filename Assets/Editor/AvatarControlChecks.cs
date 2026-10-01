using System;
using System.Linq;
using System.IO;
using System.Reflection;
using Matee.AvatarControls;
using UniGLTF;
using VRM;
using UniVRM10;
using UnityEngine;
using UnityEngine.UI;

// Run with Unity -batchmode -executeMethod AvatarControlChecks.Run.
public static class AvatarControlChecks
{
    public static void RunAll()
    {
        Run();
        RunAkai();
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("Avatar controls: " + message);
    }

    public static void Run()
    {
        var root = new GameObject("synthetic avatar");
        var mesh = new Mesh { name = "shirt mesh", vertices = new[] { Vector3.zero } };
        var isolatedProfiles = Path.Combine(Path.GetTempPath(), "matee-avatar-tests-" + Guid.NewGuid().ToString("N"));
        AvatarProfileStore.TestFolderOverride = isolatedProfiles;
        mesh.AddBlendShapeFrame("Fit", 100, new[] { Vector3.zero }, new[] { Vector3.zero }, new[] { Vector3.zero });
        try
        {
            var first = new GameObject("Shirt"); first.transform.SetParent(root.transform);
            var second = new GameObject("Shirt"); second.transform.SetParent(root.transform);
            var one = first.AddComponent<SkinnedMeshRenderer>(); one.sharedMesh = mesh;
            var two = second.AddComponent<SkinnedMeshRenderer>(); two.sharedMesh = mesh; two.enabled = false;
            second.SetActive(false);
            var manifest = AvatarControlScanner.Scan(root, "synthetic", true, null);
            var renderers = manifest.Controls.Where(c => c.Kind == ControlKind.Renderer).ToList();
            Require(renderers.Count == 2, "inactive renderer was not discovered");
            Require(renderers.Select(c => c.Id).Distinct().Count() == 2, "duplicate names got the same ID");
            Require(renderers.All(c => c.Category == "Outfit/Tops"), "shirt category was not inferred");
            Require(renderers.Any(c => c.DefaultValue == 0), "authored visibility was lost");
            Require(manifest.AllMorphs.Count == 2, "inactive morphs were not discovered");
            var inactiveControl = renderers.Single(c => c.DefaultValue == 0);
            AvatarControlRuntime.Bind(manifest);
            AvatarControlRuntime.Set(inactiveControl, 1);
            Require(two.enabled, "renderer control did not enable its target");
            AvatarControlRuntime.Set(inactiveControl, 0);
            AvatarControlRuntime.Unbind();
            File.WriteAllText(Path.Combine(isolatedProfiles, manifest.SemanticHash + ".json"), "{damaged");
            AvatarControlRuntime.Bind(AvatarControlScanner.Scan(root, "synthetic", true, null));
            Require(two.enabled, "damaged profile did not recover from backup");
            AvatarControlRuntime.Unbind();
            var panelHost = new GameObject("controls host");
            var menu = new GameObject("clothes menu");
            try
            {
                AvatarControlRuntime.Bind(manifest);
                var panel = panelHost.AddComponent<AvatarControlPanel>(); panel.EntryPanel = menu;
                var type = typeof(AvatarControlPanel);
                type.GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(panel, null);
                type.GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(panel, null);
                var canvas = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Single(x => x.name == "Avatar Controls Canvas");
                Require(canvas.GetComponent<GraphicRaycaster>() != null, "panel does not use EventSystem input");
                var scroll = canvas.GetComponentInChildren<ScrollRect>(true);
                Require(scroll != null && scroll.verticalScrollbar != null && scroll.scrollSensitivity >= 30,
                    "scrollbar or usable wheel speed missing");
                Require(canvas.GetComponentInChildren<Text>(true)?.font != null, "panel font missing");
                var rendererToggle = canvas.GetComponentInChildren<Toggle>(true);
                Require(rendererToggle != null, "renderer toggle missing");
                rendererToggle.isOn = !rendererToggle.isOn;
                Require(rendererToggle != null && rendererToggle.gameObject.activeInHierarchy,
                    "renderer toggle was rebuilt on a value change");
                var searchField = canvas.GetComponentsInChildren<InputField>(true).First();
                searchField.text = "no such control";
                Require(canvas.GetComponentsInChildren<Toggle>(true).Length == 0,
                    "search did not filter immediately");
                searchField.text = "";
                Require(canvas.GetComponentInChildren<Toggle>(true) != null, "clearing search did not restore controls");
                type.GetField("tab", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(panel, "Advanced");
                type.GetMethod("RefreshBody", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(panel, null);
                Require(canvas.GetComponentInChildren<Slider>(true) != null, "morph slider missing");
            }
            finally
            {
                AvatarControlRuntime.Unbind();
                UnityEngine.Object.DestroyImmediate(panelHost);
                UnityEngine.Object.DestroyImmediate(menu);
            }
            var clothes = root.AddComponent<MEClothes>();
            clothes.entries = new[]
            {
                new MEClothes.OutfitEntry { name = "One", tag = "top", gameObjects = new[] { first } },
                new MEClothes.OutfitEntry { name = "Two", tag = "top", gameObjects = new[] { second } }
            };
            var authored = AvatarControlScanner.Scan(root, "authored", true, null);
            Require(authored.Controls.Count(c => c.Kind == ControlKind.Outfit) == 2, "MEClothes entries missing");
            Require(authored.Controls.Count(c => c.Kind == ControlKind.Renderer) == 0, "MEClothes targets duplicated");
            AvatarControlRuntime.Bind(authored);
            var outfits = authored.Controls.Where(c => c.Kind == ControlKind.Outfit).ToList();
            AvatarControlRuntime.Set(outfits[1], 1);
            Require(!first.activeSelf && second.activeSelf, "MEClothes tag exclusivity failed");
            outfits[0].GroupMode = outfits[1].GroupMode = "ExactlyOne";
            AvatarControlRuntime.Set(outfits[1], 0);
            Require(second.activeSelf, "ExactlyOne allowed its last outfit to turn off");
            AvatarControlRuntime.ResetAll();
            Require(first.activeSelf && !second.activeSelf, "restore defaults did not restore authored outfit");
            AvatarControlRuntime.Unbind();
            var vrm1Root = new GameObject("synthetic vrm1");
            var vrm1Object = ScriptableObject.CreateInstance<VRM10Object>();
            var happy = ScriptableObject.CreateInstance<VRM10Expression>();
            var emptyLook = ScriptableObject.CreateInstance<VRM10Expression>();
            try
            {
                var face = new GameObject("Face"); face.transform.SetParent(vrm1Root.transform);
                var faceRenderer = face.AddComponent<SkinnedMeshRenderer>(); faceRenderer.sharedMesh = mesh;
                happy.MorphTargetBindings = new[] { new MorphTargetBinding { RelativePath = "Face", Index = 0, Weight = 1 } };
                vrm1Object.Expression.Happy = happy;
                vrm1Object.Expression.LookUp = emptyLook;
                vrm1Root.AddComponent<Vrm10Instance>().Vrm = vrm1Object;
                var vrm1 = AvatarControlScanner.Scan(vrm1Root, "synthetic vrm1", true, null);
                Require(vrm1.Controls.Any(c => c.Kind == ControlKind.Expression && c.Label == "happy"), "valid VRM1 expression missing");
                Require(!vrm1.Controls.Any(c => c.Kind == ControlKind.Expression && c.Label == "lookUp"), "empty VRM1 expression leaked");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(vrm1Root);
                UnityEngine.Object.DestroyImmediate(vrm1Object);
                UnityEngine.Object.DestroyImmediate(happy);
                UnityEngine.Object.DestroyImmediate(emptyLook);
            }
            Debug.Log("[AvatarControls] Synthetic discovery checks passed");
        }
        finally
        {
            AvatarControlRuntime.Unbind();
            AvatarProfileStore.TestFolderOverride = null;
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    public static void RunAkai()
    {
        var path = Path.GetFullPath("testModels/AKAI Original VRM by Shugan.vrm");
        Require(File.Exists(path), "local AKAI fixture missing");
        var bytes = File.ReadAllBytes(path);
        using (var gltf = new GlbBinaryParser(bytes, path).Parse())
        using (var importer = new VRMImporterContext(new VRMData(gltf)))
        {
            var instance = importer.LoadAsync(new ImmediateCaller()).GetAwaiter().GetResult();
            Require(instance.Root != null, "VRM0 import failed");
            try
            {
                var m = AvatarControlScanner.Scan(instance.Root, path, false, instance);
                Require(m.AllMorphs.Count == 94, "expected 94 morphs, got " + m.AllMorphs.Count);
                Require(m.Controls.Count(c => c.Kind == ControlKind.Renderer) == 17, "renderer inventory differs from fixture");
                Require(m.Controls.Count(c => c.Kind == ControlKind.Renderer && c.Category != "Advanced/Raw Renderers") == 14,
                    "primary renderer inventory differs from fixture");
                Require(m.Physics.Count == 3, "spring inventory differs from fixture");
                Require(m.Materials.Count == 18, "material inventory differs from fixture");
                Require(m.Controls.Count(c => c.Kind == ControlKind.Expression) == 13, "functional expression inventory differs from fixture");
                Require(!m.Controls.Any(c => c.Kind == ControlKind.Expression && c.Label.StartsWith("Look")), "empty look expression shown");
                Require(m.Controls.Any(c => c.Label == "Breast Size" && c.Kind == ControlKind.PairedMorph && c.Targets.Count > 1), "breast pair or followers missing");
                Require(m.Controls.Any(c => c.Label == "Butt Size" && c.Kind == ControlKind.PairedMorph), "butt pair missing");
                Debug.Log("[AvatarControls] AKAI discovery checks passed: " + m.AllMorphs.Count + " morphs, " + m.Controls.Count + " controls");
            }
            finally { UnityEngine.Object.DestroyImmediate(instance.Root); }
        }
    }
}
