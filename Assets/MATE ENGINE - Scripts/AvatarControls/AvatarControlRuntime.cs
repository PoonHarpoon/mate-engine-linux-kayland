using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace Matee.AvatarControls
{
    public static class AvatarControlRuntime
    {
        public static Manifest Current { get; private set; }
        public static string LastError { get; private set; }
        public static event Action<Manifest> Bound;
        public static event Action Unbound;
        static readonly Dictionary<Renderer, bool> isolation = new Dictionary<Renderer, bool>();
        static bool changingGroup;

        public static void Bind(Manifest manifest)
        {
            Unbind();
            LastError = null;
            Current = manifest;
            if (manifest.Root.GetComponent<AvatarMorphOverrideWriter>() == null)
                manifest.Root.AddComponent<AvatarMorphOverrideWriter>();
            manifest.Profile = AvatarProfileStore.Load(manifest);
            ApplyProfile(manifest);
            Bound?.Invoke(manifest);
        }

        public static void ReportError(string message)
        {
            LastError = message;
            Debug.LogError("[AvatarControls] " + message);
        }

        static void ApplyProfile(Manifest manifest)
        {
            var resolved = new Dictionary<string, ControlOverride>();
            foreach (var c in manifest.Controls)
            {
                if (!manifest.Profile.controls.TryGetValue(c.Id, out var o))
                {
                    var candidates = manifest.Profile.controls.Where(x => x.Value.signature == c.Signature).ToList();
                    if (candidates.Count != 1 || manifest.Controls.Count(x => x.Signature == c.Signature) != 1) continue;
                    o = candidates[0].Value;
                    manifest.Profile.controls.Remove(candidates[0].Key);
                    manifest.Profile.controls[c.Id] = o;
                }
                resolved[c.Id] = o;
                if (!string.IsNullOrEmpty(o.label)) c.Label = o.label;
                if (!string.IsNullOrEmpty(o.category)) c.Category = o.category;
                if (o.group != null) c.Group = o.group;
                if (o.groupMode != null) c.GroupMode = o.groupMode;
                if (o.dependency != null) c.Dependency = o.dependency;
                if (o.hidden.HasValue) c.Hidden = o.hidden.Value;
            }
            foreach (var c in manifest.Controls)
            {
                if (!resolved.TryGetValue(c.Id, out var o)) continue;
                if (o.value.HasValue) Set(c, o.value.Value, false, o.held ?? false);
            }
            ApplyCorrections();
        }

        public static void ChooseProfile(AvatarProfile profile)
        {
            var m = Current;
            if (m == null || !m.ProfileCandidates.Contains(profile)) return;
            changingGroup = true;
            foreach (var c in m.Controls) Set(c, c.DefaultValue, false, false);
            changingGroup = false;
            if (m.Universal != null) m.Universal.ClearManualOverrides();
            foreach (var c in m.Controls)
            {
                c.Label = c.OriginalLabel; c.Category = c.OriginalCategory; c.Group = c.OriginalGroup;
                c.GroupMode = c.OriginalGroupMode; c.Hidden = c.OriginalHidden; c.Dependency = c.OriginalDependency;
            }
            m.Profile = profile;
            m.ProfileAmbiguous = false;
            m.ProfileCandidates.Clear();
            m.Profile.exactHash = m.ExactHash; m.Profile.semanticHash = m.SemanticHash;
            ApplyProfile(m);
        }

        public static void Unbind()
        {
            EndIsolation();
            if (Current == null) return;
            if (Current.Universal != null) Current.Universal.ClearManualOverrides();
            Current = null;
            Unbound?.Invoke();
        }

        static void Morph(MorphTarget t, float value)
        {
            if (t.Renderer != null && t.Renderer.sharedMesh != null && t.Index < t.Renderer.sharedMesh.blendShapeCount)
                t.Renderer.SetBlendShapeWeight(t.Index, Mathf.Clamp(value, 0, 100));
        }

        public static void Set(Control c, float value, bool save = true, bool held = true)
        {
            if (Current == null || c == null || !Current.Controls.Contains(c)) return;
            if (c.Kind == ControlKind.PairedMorph) value = Mathf.Clamp(value, -100, 100);
            else value = Mathf.Clamp(value, 0, c.Kind == ControlKind.Morph ? 100 : 1);
            if (!changingGroup && value < .5f && c.GroupMode == "ExactlyOne" && !string.IsNullOrEmpty(c.Group) &&
                (c.Kind == ControlKind.Renderer || c.Kind == ControlKind.Outfit) &&
                !Current.Controls.Any(x => x != c && x.Group == c.Group && x.Value >= .5f)) return;
            if (c.Kind == ControlKind.Renderer)
            {
                if (value >= .5f) EnforceGroup(c);
                foreach (var renderer in c.Renderers) if (renderer != null) renderer.enabled = value >= 0.5f;
            }
            else if (c.Kind == ControlKind.Outfit && c.Clothes != null)
            {
                var entry = c.Clothes.entries[c.ClothesIndex];
                bool desired = value >= 0.5f;
                if (entry != null && entry.gameObjects != null)
                {
                    if (desired) EnforceGroup(c);
                    if (desired && !string.IsNullOrEmpty(entry.tag))
                        foreach (var other in Current.Controls.Where(x => x != c && x.Kind == ControlKind.Outfit && x.Clothes == c.Clothes && x.Group == c.Group))
                            Set(other, 0, false);
                    foreach (var obj in entry.gameObjects) if (obj != null) obj.SetActive(desired);
                }
            }
            else if (c.Kind == ControlKind.Morph)
            {
                foreach (var t in c.Targets) Morph(t, value);
                c.Held = held;
            }
            else if (c.Kind == ControlKind.PairedMorph)
            {
                foreach (var t in c.NegativeTargets) Morph(t, Mathf.Max(0, -value));
                foreach (var t in c.Targets) Morph(t, Mathf.Max(0, value));
                c.Held = held;
            }
            else if (c.Kind == ControlKind.Expression)
            {
                if (Current.Universal != null) Current.Universal.SetManualOverride(c.Id, c.IsVrm0 ? c.Vrm0Key.ToString() : c.Vrm1Key.Name, value, held);
                else if (c.IsVrm0 && Current.Proxy0 != null) Current.Proxy0.ImmediatelySetValue(c.Vrm0Key, value);
                else if (c.IsVrm1 && Current.Instance1 != null && Current.Instance1.Runtime?.Expression != null)
                    Current.Instance1.Runtime.Expression.SetWeight(c.Vrm1Key, value);
                c.Held = held;
            }
            c.Value = value;
            if (c.Kind == ControlKind.Renderer || c.Kind == ControlKind.Outfit) ApplyCorrections();
            if (save) SaveValue(c);
        }

        static void EnforceGroup(Control selected)
        {
            if (string.IsNullOrEmpty(selected.Group) || selected.GroupMode == "Independent") return;
            bool previous = changingGroup;
            changingGroup = true;
            foreach (var other in Current.Controls.Where(x => x != selected && x.Group == selected.Group &&
                (x.Kind == ControlKind.Renderer || x.Kind == ControlKind.Outfit) && x.Value >= .5f).ToList())
                Set(other, 0, false);
            changingGroup = previous;
        }

        static void ApplyCorrections()
        {
            if (Current == null) return;
            foreach (var c in Current.Controls.Where(x => x.Kind == ControlKind.Morph && !string.IsNullOrEmpty(x.Dependency)))
            {
                var source = Current.Find(c.Dependency);
                if (source == null || source == c || (source.Kind != ControlKind.Renderer && source.Kind != ControlKind.Outfit)) continue;
                bool visible = source.Kind == ControlKind.Outfit
                    ? source.Clothes != null && source.Clothes.entries[source.ClothesIndex].gameObjects.Any(x => x != null && x.activeSelf)
                    : source.Renderers.Any(x => x != null && x.enabled);
                foreach (var t in c.Targets) Morph(t, visible ? 100 : 0);
                c.Value = visible ? 100 : 0;
            }
        }

        public static void SyncAuthoredClothing()
        {
            if (Current == null) return;
            foreach (var c in Current.Controls.Where(x => x.Kind == ControlKind.Outfit && x.Clothes != null))
            {
                var entry = c.Clothes.entries[c.ClothesIndex];
                c.Value = entry != null && entry.gameObjects != null && entry.gameObjects.Any(x => x != null && x.activeSelf) ? 1 : 0;
            }
            ApplyCorrections();
        }

        public static void SaveValue(Control c)
        {
            if (Current == null) return;
            var o = GetOverride(c);
            o.value = c.Value;
            o.held = c.Kind == ControlKind.Expression || c.Kind == ControlKind.Morph || c.Kind == ControlKind.PairedMorph ? c.Held : (bool?)null;
            AvatarProfileStore.Save(Current);
        }

        public static ControlOverride GetOverride(Control c)
        {
            if (!Current.Profile.controls.TryGetValue(c.Id, out var o))
                Current.Profile.controls[c.Id] = o = new ControlOverride();
            o.signature = c.Signature;
            return o;
        }

        public static void SaveSetup(Control c)
        {
            var o = GetOverride(c);
            o.label = c.Label; o.category = c.Category; o.hidden = c.Hidden; o.group = c.Group;
            o.groupMode = c.GroupMode; o.dependency = c.Dependency;
            ApplyCorrections();
            AvatarProfileStore.Save(Current);
        }

        public static void Reset(Control c)
        {
            if (Current == null) return;
            Set(c, c.DefaultValue, false, false);
            if (c.Kind == ControlKind.Expression && Current.Universal != null) Current.Universal.ReleaseManualOverride(c.Id);
            var o = GetOverride(c);
            o.value = null; o.held = null;
            ApplyCorrections();
            AvatarProfileStore.Save(Current);
        }

        public static void ResetAll()
        {
            if (Current == null) return;
            changingGroup = true;
            foreach (var c in Current.Controls) Set(c, c.DefaultValue, false, false);
            changingGroup = false;
            if (Current.Universal != null) Current.Universal.ClearManualOverrides();
            Current.Profile.controls.Clear();
            foreach (var c in Current.Controls)
            {
                c.Label = c.OriginalLabel; c.Category = c.OriginalCategory; c.Group = c.OriginalGroup;
                c.GroupMode = c.OriginalGroupMode; c.Hidden = c.OriginalHidden; c.Dependency = c.OriginalDependency;
            }
            ApplyCorrections();
            AvatarProfileStore.Save(Current);
        }

        public static void BeginIsolation(Control c)
        {
            EndIsolation();
            if (Current == null || c == null) return;
            foreach (var renderer in Current.Root.GetComponentsInChildren<Renderer>(true))
            {
                isolation[renderer] = renderer.enabled;
                renderer.enabled = c.Renderers.Contains(renderer);
            }
        }

        public static void EndIsolation()
        {
            foreach (var item in isolation) if (item.Key != null) item.Key.enabled = item.Value;
            isolation.Clear();
        }
    }

    [DefaultExecutionOrder(12000)]
    public sealed class AvatarMorphOverrideWriter : MonoBehaviour
    {
        void LateUpdate()
        {
            var m = AvatarControlRuntime.Current;
            if (m == null || m.Root != gameObject) return;
            foreach (var c in m.Controls)
            {
                if (!c.Held) continue;
                if (c.Kind == ControlKind.Morph)
                {
                    foreach (var t in c.Targets)
                        if (t.Renderer != null) t.Renderer.SetBlendShapeWeight(t.Index, c.Value);
                }
                else if (c.Kind == ControlKind.PairedMorph)
                {
                    foreach (var t in c.NegativeTargets) if (t.Renderer != null) t.Renderer.SetBlendShapeWeight(t.Index, Mathf.Max(0, -c.Value));
                    foreach (var t in c.Targets) if (t.Renderer != null) t.Renderer.SetBlendShapeWeight(t.Index, Mathf.Max(0, c.Value));
                }
            }
        }
    }

    public static class AvatarProfileStore
    {
#if UNITY_EDITOR
        public static string TestFolderOverride;
#endif
        static string Folder
        {
            get
            {
#if UNITY_EDITOR
                if (!string.IsNullOrEmpty(TestFolderOverride)) return TestFolderOverride;
#endif
                return System.IO.Path.Combine(Application.persistentDataPath, "AvatarProfiles");
            }
        }
        static string FilePath(string semantic) => System.IO.Path.Combine(Folder, semantic + ".json");

        public static AvatarProfile Load(Manifest m)
        {
            Directory.CreateDirectory(Folder);
            var matches = new List<AvatarProfile>();
            foreach (var file in Directory.GetFiles(Folder, "*.json"))
            {
                try
                {
                    var p = JsonConvert.DeserializeObject<AvatarProfile>(File.ReadAllText(file));
                    if (p == null || p.version != 1 || p.controls == null || p.presets == null)
                        throw new InvalidDataException("Unsupported or incomplete profile");
                    matches.Add(p);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[AvatarControls] Profile damaged: " + file + ": " + e.Message);
                    try
                    {
                        var backup = JsonConvert.DeserializeObject<AvatarProfile>(File.ReadAllText(file + ".bak"));
                        if (backup != null && backup.version == 1 && backup.controls != null && backup.presets != null) matches.Add(backup);
                    }
                    catch (Exception recovery) { Debug.LogWarning("[AvatarControls] No usable backup for " + file + ": " + recovery.Message); }
                }
            }
            var exact = matches.Where(x => !string.IsNullOrEmpty(m.ExactHash) && x.exactHash == m.ExactHash).ToList();
            var semantic = matches.Where(x => x.semanticHash == m.SemanticHash).ToList();
            if (exact.Count == 1) return exact[0];
            if (exact.Count > 1 || semantic.Count > 1)
            {
                m.ProfileAmbiguous = true;
                m.ProfileCandidates.AddRange(exact.Count > 1 ? exact : semantic);
                Debug.LogWarning("[AvatarControls] Ambiguous profile match for " + m.Title + "; defaults used");
            }
            if (m.ProfileAmbiguous) return new AvatarProfile { exactHash = m.ExactHash, semanticHash = m.SemanticHash };
            if (semantic.Count == 1) return semantic[0];
            return new AvatarProfile { exactHash = m.ExactHash, semanticHash = m.SemanticHash };
        }

        public static string ImportLegacy(Manifest m)
        {
            if (m == null || !File.Exists(m.Path)) return "Legacy import requires a file-backed avatar.";
            var safeName = System.IO.Path.GetFileNameWithoutExtension(m.Path);
            foreach (char character in System.IO.Path.GetInvalidFileNameChars()) safeName = safeName.Replace(character, '_');
            var legacy = System.IO.Path.Combine(Application.persistentDataPath, "Blendshapes", safeName + "_Blendshapes.json");
            if (!File.Exists(legacy)) return "No legacy blendshape file found.";
            Dictionary<string, float> values;
            try { values = JsonConvert.DeserializeObject<Dictionary<string, float>>(File.ReadAllText(legacy)); }
            catch (Exception e) { return "Legacy file could not be read: " + e.Message; }
            if (values == null || values.Count == 0) return "Legacy file has no values.";
            var matched = new List<(Control control, float value)>();
            foreach (var item in values)
            {
                var candidates = m.Controls.Where(c => c.Kind == ControlKind.Morph && c.Targets.Count == 1 &&
                    LegacyPath(c.Targets[0].Renderer.transform, m.Root.transform) + ":" + c.Targets[0].Name == item.Key).ToList();
                if (candidates.Count != 1) return "Legacy import needs manual review: unmatched or ambiguous target " + item.Key;
                matched.Add((candidates[0], item.Value));
            }
            if (matched.Select(x => x.control.Id).Distinct().Count() != matched.Count) return "Legacy import is ambiguous.";
            foreach (var item in matched) AvatarControlRuntime.Set(item.control, item.value);
            return "Imported " + matched.Count + " sliders. The original legacy file was retained.";
        }

        static string LegacyPath(Transform t, Transform root)
        {
            var parts = new List<string>();
            while (t != null && t != root) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        public static void Save(Manifest m)
        {
            if (m == null || m.Profile == null) return;
            if (m.ProfileAmbiguous) { Debug.LogWarning("[AvatarControls] Choose a profile before saving overrides for " + m.Title); return; }
            try
            {
                Directory.CreateDirectory(Folder);
                var path = FilePath(m.SemanticHash);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(m.Profile, Formatting.Indented));
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
                else File.Move(temp, path);
            }
            catch (Exception e) { Debug.LogError("[AvatarControls] Profile save failed: " + e); }
        }
    }
}
