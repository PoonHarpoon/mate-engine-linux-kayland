using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UniVRM10;
using VRM;

namespace Matee.AvatarControls
{
    public enum ControlKind { Renderer, Outfit, Morph, PairedMorph, Expression }

    // Persistent descriptors never contain Unity objects. Targets are rebuilt for each imported instance.
    public sealed class Control
    {
        public string Id, Signature, Label, Category, Source, Group, GroupMode = "Independent", Explanation;
        public string OriginalLabel, OriginalCategory, OriginalGroup, OriginalGroupMode, OriginalDependency;
        public ControlKind Kind;
        public float Confidence, DefaultValue, Value;
        public bool Hidden, OriginalHidden, Held;
        public readonly List<MorphTarget> Targets = new List<MorphTarget>();
        public readonly List<MorphTarget> NegativeTargets = new List<MorphTarget>();
        public readonly List<Renderer> Renderers = new List<Renderer>();
        public MEClothes Clothes;
        public int ClothesIndex;
        public BlendShapeKey Vrm0Key;
        public ExpressionKey Vrm1Key;
        public bool IsVrm0, IsVrm1;
        public string Dependency;
    }

    public sealed class MorphTarget
    {
        public SkinnedMeshRenderer Renderer;
        public int Index;
        public string Name;
        public float Initial;
        public string Id;
    }

    [Serializable] public sealed class ControlOverride
    {
        public string signature;
        public string label, category, group, dependency;
        public bool? hidden;
        public float? value;
        public bool? held;
        public string groupMode;
    }

    [Serializable] public sealed class AvatarProfile
    {
        public int version = 1;
        public string exactHash, semanticHash;
        public Dictionary<string, ControlOverride> controls = new Dictionary<string, ControlOverride>();
        public Dictionary<string, Dictionary<string, float>> presets = new Dictionary<string, Dictionary<string, float>>();
    }

    public sealed class Manifest
    {
        public GameObject Root;
        public string Path, Title, Author, Version, ExactHash, SemanticHash;
        public readonly List<Control> Controls = new List<Control>();
        public readonly List<MorphTarget> AllMorphs = new List<MorphTarget>();
        public readonly List<string> Physics = new List<string>();
        public readonly List<string> Materials = new List<string>();
        public AvatarProfile Profile;
        public readonly List<AvatarProfile> ProfileCandidates = new List<AvatarProfile>();
        public bool ProfileAmbiguous;
        public VRMBlendShapeProxy Proxy0;
        public Vrm10Instance Instance1;
        public UniversalBlendshapes Universal;
        public bool IsAuthored;
        public Control Find(string id) => Controls.FirstOrDefault(c => c.Id == id);
    }

    public static class AvatarControlScanner
    {
        static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }

        public static string Normalize(string value) => new string((value ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        static string Pretty(string value)
        {
            value = (value ?? "").Replace(".baked", "").Replace("_", " ");
            if (value.StartsWith("RED ", StringComparison.OrdinalIgnoreCase)) value = value.Substring(4);
            return value;
        }

        // Sibling ordinals make duplicate names distinct without relying on Unity instance IDs.
        public static string Address(Transform node, Transform root)
        {
            var parts = new List<string>();
            while (node != null && node != root)
            {
                int ordinal = 0;
                if (node.parent != null)
                    for (int i = 0; i < node.GetSiblingIndex(); i++)
                        if (node.parent.GetChild(i).name == node.name) ordinal++;
                parts.Add(node.name.Replace("/", "%2F") + "#" + ordinal);
                node = node.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string RendererCategory(string name, out float confidence)
        {
            var n = Normalize(name);
            confidence = 0.9f;
            if (new[] { "body", "face", "hair", "head", "eyes" }.Any(x => n == x || n == "red" + x)) return "Advanced/Raw Renderers";
            if (n.Contains("sfw") || n.Contains("censor")) return "SFW";
            if (n.Contains("wing") || n.Contains("tail") || n.Contains("horn")) return "Body Accessories";
            if (n.Contains("vest") || n.Contains("shirt") || n.Contains("hoodie") || n.Contains("jacket")) return "Outfit/Tops";
            if (n.Contains("skirt") || n.Contains("pants") || n.Contains("short")) return "Outfit/Bottoms";
            if (n.Contains("pant") || n.Contains("bra") || n.Contains("underwear")) return "Outfit/Underwear";
            if (n.Contains("stocking") || n.Contains("sock")) return "Outfit/Legwear";
            if (n.Contains("shoe") || n.Contains("boot")) return "Outfit/Shoes";
            if (n.Contains("glove") || n.Contains("scarf")) return "Accessories";
            confidence = 0.4f;
            return "Uncategorized";
        }

        static string MorphCategory(string renderer, string name)
        {
            var r = Normalize(renderer); var n = Normalize(name);
            if (n.StartsWith("adapted") || n.StartsWith("corrective") || n.StartsWith("fix") || n.StartsWith("fit")) return "Advanced/Correctives";
            if (n.StartsWith("vrcv") || n == "aa" || n == "oh" || n == "ch") return "Advanced/Visemes";
            if (r.Contains("body"))
                return n.Contains("pussy") || n.Contains("nipple") || n.Contains("buttopen") ? "Advanced/Anatomy" : "Body";
            if (r.Contains("face") || r.Contains("eye"))
            {
                if (n.Contains("eye") || n.Contains("blink") || n.Contains("pupil")) return "Face Lab/Eyes";
                if (n.Contains("brow")) return "Face Lab/Brows";
                if (n.Contains("teeth")) return "Face Lab/Teeth";
                if (n.Contains("cheek")) return "Face Lab/Cheeks";
                return "Face Lab/Mouth & Novelty";
            }
            return "Advanced/Raw Morphs";
        }

        static bool Valid(VRM.BlendShapeClip clip, Transform root, HashSet<string> materials) => clip != null &&
            ((clip.Values != null && clip.Values.Any(b =>
            {
                var t = string.IsNullOrEmpty(b.RelativePath) ? root : root.Find(b.RelativePath);
                var mesh = t != null ? t.GetComponent<SkinnedMeshRenderer>()?.sharedMesh : null;
                return mesh != null && b.Index >= 0 && b.Index < mesh.blendShapeCount;
            })) || (clip.MaterialValues != null && clip.MaterialValues.Any(b => materials.Contains(b.MaterialName))));
        static bool Valid(VRM10Expression clip, Transform root, HashSet<string> materials) => clip != null &&
            ((clip.MorphTargetBindings != null && clip.MorphTargetBindings.Any(b =>
            {
                var t = string.IsNullOrEmpty(b.RelativePath) ? root : root.Find(b.RelativePath);
                var mesh = t != null ? t.GetComponent<SkinnedMeshRenderer>()?.sharedMesh : null;
                return mesh != null && b.Index >= 0 && b.Index < mesh.blendShapeCount;
            })) || (clip.MaterialColorBindings != null && clip.MaterialColorBindings.Any(b => materials.Contains(b.MaterialName))) ||
            (clip.MaterialUVBindings != null && clip.MaterialUVBindings.Any(b => materials.Contains(b.MaterialName))));
        static bool IsProcedural(string name)
        {
            var n = Normalize(name);
            return new[] { "a", "i", "u", "e", "o", "aa", "ih", "ou", "ee", "oh", "blink", "blinkl", "blinkr", "blinkleft", "blinkright", "lookup", "lookdown", "lookleft", "lookright" }.Contains(n);
        }

        public static Manifest Scan(GameObject root, string path, bool authored, UniGLTF.RuntimeGltfInstance gltf)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (!authored && gltf != null) gltf.ShowMeshes();
            var m = new Manifest { Root = root, Path = path, IsAuthored = authored,
                Title = System.IO.Path.GetFileNameWithoutExtension(path ?? root.name), Author = "Unknown", Version = "Unknown" };
            m.Proxy0 = root.GetComponentInChildren<VRMBlendShapeProxy>(true);
            m.Instance1 = root.GetComponentInChildren<Vrm10Instance>(true);
            m.Universal = root.GetComponentInChildren<UniversalBlendshapes>(true);
            var meta0 = root.GetComponentInChildren<VRMMeta>(true);
            if (meta0 != null && meta0.Meta != null)
            { m.Title = meta0.Meta.Title ?? m.Title; m.Author = meta0.Meta.Author ?? m.Author; m.Version = meta0.Meta.Version ?? m.Version; }
            if (m.Instance1 != null && m.Instance1.Vrm != null && m.Instance1.Vrm.Meta != null)
            {
                var meta = m.Instance1.Vrm.Meta;
                m.Title = meta.Name ?? m.Title;
                m.Author = meta.Authors != null && meta.Authors.Count > 0 ? meta.Authors[0] : m.Author;
                m.Version = meta.Version ?? m.Version;
            }
            var owned = new HashSet<Renderer>();
            foreach (var clothes in root.GetComponentsInChildren<MEClothes>(true))
            {
                if (clothes.isScriptLoader || clothes.entries == null) continue;
                for (int i = 0; i < clothes.entries.Length; i++)
                {
                    var entry = clothes.entries[i];
                    if (entry == null || string.IsNullOrEmpty(entry.name) || entry.gameObjects == null) continue;
                    var c = new Control { Id = "outfit:" + Address(clothes.transform, root.transform) + ":" + i,
                        Label = entry.name, Category = "Outfit/Author", Kind = ControlKind.Outfit,
                        Source = "MEClothes", Confidence = 1, Clothes = clothes, ClothesIndex = i,
                        Group = entry.tag, GroupMode = string.IsNullOrEmpty(entry.tag) ? "Independent" : "AtMostOne" };
                    foreach (var obj in entry.gameObjects.Where(x => x != null))
                        foreach (var renderer in obj.GetComponentsInChildren<Renderer>(true)) { c.Renderers.Add(renderer); owned.Add(renderer); }
                    c.DefaultValue = c.Value = entry.gameObjects.Any(x => x != null && x.activeSelf) ? 1 : 0;
                    m.Controls.Add(c);
                }
            }
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                if (owned.Contains(renderer)) continue;
                var mesh = renderer is SkinnedMeshRenderer sk ? sk.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                float confidence;
                var category = RendererCategory(renderer.name, out confidence);
                var id = "renderer:" + Address(renderer.transform, root.transform) + ":" + renderer.GetType().Name + ":" + mesh.name;
                var c = new Control { Id = id, Label = Pretty(renderer.name), Category = category, Kind = ControlKind.Renderer,
                    Source = "renderer", Confidence = confidence, DefaultValue = renderer.enabled ? 1 : 0, Value = renderer.enabled ? 1 : 0 };
                c.Renderers.Add(renderer); m.Controls.Add(c);
                if (renderer is SkinnedMeshRenderer smr)
                    for (int i = 0; i < mesh.blendShapeCount; i++)
                    {
                        var name = mesh.GetBlendShapeName(i);
                        m.AllMorphs.Add(new MorphTarget { Renderer = smr, Index = i, Name = name,
                            Initial = smr.GetBlendShapeWeight(i), Id = id + ":morph:" + i + ":" + name });
                    }
            }
            foreach (var t in m.AllMorphs)
            {
                var c = new Control { Id = t.Id, Label = t.Name, Category = MorphCategory(t.Renderer.name, t.Name),
                    Source = "mesh", Kind = ControlKind.Morph, Confidence = 1, DefaultValue = t.Initial, Value = t.Initial };
                c.Targets.Add(t); m.Controls.Add(c);
            }
            LinkMorphs(m);
            var materialNames = new HashSet<string>(root.GetComponentsInChildren<Renderer>(true)
                .SelectMany(r => r.sharedMaterials).Where(x => x != null).Select(x => x.name));
            if (m.Proxy0 != null && m.Proxy0.BlendShapeAvatar != null)
                foreach (var clip in m.Proxy0.BlendShapeAvatar.Clips.Where(x => Valid(x, m.Proxy0.transform, materialNames)))
                {
                    var key = BlendShapeKey.CreateFromClip(clip);
                    var name = clip.BlendShapeName;
                    if (string.IsNullOrEmpty(name)) name = clip.Preset.ToString();
                    m.Controls.Add(new Control { Id = "expression0:" + key.ToString(), Label = name,
                        Category = IsProcedural(name) ? "Advanced/Expressions" : "Expressions", Kind = ControlKind.Expression,
                        Source = "VRM0", Confidence = 1, IsVrm0 = true, Vrm0Key = key });
                }
            if (m.Instance1 != null && m.Instance1.Vrm != null)
                foreach (var pair in m.Instance1.Vrm.Expression.Clips.Where(x => Valid(x.Clip, m.Instance1.transform, materialNames)))
                {
                    var key = new ExpressionKey(pair.Preset, pair.Clip.name);
                    m.Controls.Add(new Control { Id = "expression1:" + key.Name, Label = key.Name,
                        Category = IsProcedural(key.Name) ? "Advanced/Expressions" : "Expressions", Kind = ControlKind.Expression,
                        Source = "VRM1", Confidence = 1, IsVrm1 = true, Vrm1Key = key });
                }
            foreach (var spring in root.GetComponentsInChildren<VRMSpringBone>(true))
                m.Physics.Add(string.Join(", ", spring.RootBones.Where(x => x != null).Select(x => x.name)));
            if (m.Instance1 != null && m.Instance1.SpringBone != null)
                foreach (var spring in m.Instance1.SpringBone.Springs)
                    m.Physics.Add(spring.Name + ": " + spring.Joints.Count + " joints");
            var mats = new HashSet<Material>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var material in r.sharedMaterials) if (material != null) mats.Add(material);
            m.Materials.AddRange(mats.Select(x => x.name).OrderBy(x => x, StringComparer.Ordinal));
            foreach (var c in m.Controls)
            {
                c.OriginalLabel = c.Label; c.OriginalCategory = c.Category; c.OriginalGroup = c.Group;
                c.OriginalGroupMode = c.GroupMode; c.OriginalHidden = c.Hidden; c.OriginalDependency = c.Dependency;
                c.Signature = c.Kind + "|" + c.Source + "|" + Normalize(c.Label) + "|" +
                    string.Join(";", c.Renderers.Select(r => r is SkinnedMeshRenderer sk ? sk.sharedMesh?.name : r.GetComponent<MeshFilter>()?.sharedMesh?.name)) + "|" +
                    string.Join(";", c.Targets.Select(t => t.Renderer.sharedMesh.name + ":" + t.Name));
            }
            m.SemanticHash = Hash(m.Title + "|" + m.Author + "|" + m.Version + "|" +
                string.Join("|", m.Controls.Where(x => x.Kind == ControlKind.Renderer || x.Kind == ControlKind.Outfit).Select(x => x.Signature).OrderBy(x => x, StringComparer.Ordinal)) + "|" +
                string.Join("|", m.AllMorphs.Select(x => x.Name + ":" + x.Renderer.sharedMesh.name).OrderBy(x => x, StringComparer.Ordinal)));
            if (File.Exists(path))
                using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
                    m.ExactHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            return m;
        }

        static void LinkMorphs(Manifest m)
        {
            var body = m.Controls.Where(c => c.Kind == ControlKind.Morph && Normalize(c.Targets[0].Renderer.name).Contains("body")).ToList();
            foreach (var driver in body)
            {
                var key = Normalize(driver.Label);
                foreach (var follower in m.Controls.Where(c => c.Kind == ControlKind.Morph && c != driver &&
                    !Normalize(c.Targets[0].Renderer.name).Contains("body") && Normalize(c.Label) == key).ToList())
                {
                    driver.Targets.AddRange(follower.Targets);
                    follower.Hidden = true;
                    follower.Explanation = "Follows " + driver.Label + " on body";
                }
            }
            Pair(m, body, "Boobs Small", "Boobs Big", "Breast Size");
            Pair(m, body, "No Butt", "Ass Big", "Butt Size");
            foreach (var c in m.Controls.Where(c => c.Category == "Advanced/Correctives"))
            {
                var token = Normalize(c.Label).Replace("adapted", "").Replace("corrective", "").Replace("fit", "");
                var matches = m.Controls.Where(x => x.Kind == ControlKind.Renderer && token.Length >= 4 &&
                    (Normalize(x.Label).Contains(token) || token.Contains(Normalize(x.Label)))).ToList();
                if (matches.Count == 1) { c.Dependency = matches[0].Id; c.Explanation = "Follows " + matches[0].Label + " visibility"; }
                else c.Explanation = "Unresolved clothing correction; configure in Setup";
            }
        }

        static void Pair(Manifest m, List<Control> body, string negative, string positive, string label)
        {
            var a = body.Where(c => Normalize(c.Label) == Normalize(negative)).ToList();
            var b = body.Where(c => Normalize(c.Label) == Normalize(positive)).ToList();
            if (a.Count != 1 || b.Count != 1) return;
            var pair = new Control { Id = "pair:" + a[0].Id + ":" + b[0].Id, Label = label,
                Category = "Body", Kind = ControlKind.PairedMorph, Source = "recognized pair", Confidence = 1,
                DefaultValue = b[0].Value - a[0].Value, Value = b[0].Value - a[0].Value };
            pair.NegativeTargets.AddRange(a[0].Targets); pair.Targets.AddRange(b[0].Targets);
            a[0].Hidden = b[0].Hidden = true;
            m.Controls.Add(pair);
        }
    }
}
