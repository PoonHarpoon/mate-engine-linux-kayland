using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Matee.AvatarControls
{
    // Uses the same EventSystem as the native Wayland presenter input adapter.
    public sealed class AvatarControlPanel : MonoBehaviour
    {
        public GameObject EntryPanel;
        static readonly string[] Tabs = { "Outfit", "Body Accessories", "Body", "SFW", "Expressions", "Face Lab", "Advanced", "Setup", "Presets" };
        static readonly string[] Categories = { "Outfit/Tops", "Outfit/Bottoms", "Outfit/Underwear", "Outfit/Legwear", "Outfit/Shoes", "Accessories", "Body Accessories", "Body", "SFW", "Expressions", "Face Lab/Mouth & Novelty", "Face Lab/Eyes", "Face Lab/Brows", "Face Lab/Cheeks", "Face Lab/Teeth", "Advanced/Anatomy", "Advanced/Raw Renderers", "Advanced/Raw Morphs", "Advanced/Correctives", "Uncategorized" };
        static readonly Color Background = new Color(.075f, .085f, .11f, .96f);
        static readonly Color Row = new Color(.14f, .16f, .20f, .96f);
        static readonly Color Accent = new Color(.25f, .48f, .70f, 1);
        static readonly Color TextColor = new Color(.95f, .96f, .98f, 1);
        Font font;
        GameObject canvasRoot;
        Transform body;
        ScrollRect scroll;
        readonly Dictionary<Control, Toggle> visibleToggles = new Dictionary<Control, Toggle>();
        Text statusText;
        GameObject keyboardOverlay;
        InputField keyboardTarget;
        Action<string> keyboardChanged;
        bool keyboardShift;
        string tab = "Outfit", search = "", presetName = "";
        Manifest lastManifest;
        bool wasOpen;
        CanvasGroup originalGroup;
        float originalAlpha;
        bool originalInteractable, originalBlocks;
        public static bool KeyboardCaptureRequested { get; private set; }

        void Start()
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            BuildShell();
            AvatarControlRuntime.Bound += OnBound;
            AvatarControlRuntime.Unbound += OnUnbound;
        }

        void OnDestroy()
        {
            KeyboardCaptureRequested = false;
            AvatarControlRuntime.Bound -= OnBound;
            AvatarControlRuntime.Unbound -= OnUnbound;
            RevealOriginal();
            AvatarControlRuntime.EndIsolation();
            CloseKeyboard();
            if (canvasRoot != null) Destroy(canvasRoot);
        }

        void OnApplicationFocus(bool focused) { if (!focused) AvatarControlRuntime.EndIsolation(); }
        void OnDisable() { KeyboardCaptureRequested = false; wasOpen = false; RevealOriginal(); AvatarControlRuntime.EndIsolation(); CloseKeyboard(); if (canvasRoot != null) canvasRoot.SetActive(false); }
        void OnBound(Manifest manifest) { lastManifest = null; }
        void OnUnbound() { lastManifest = null; AvatarControlRuntime.EndIsolation(); }

        void Update()
        {
            if (canvasRoot == null) return;
            if (MateeInput.PresenterActive)
            {
                var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                var field = selected != null ? selected.GetComponent<InputField>() : null;
                foreach (var key in MateeInput.ConsumeTextEvents())
                {
                    if (field == null || !field.isFocused || !field.transform.IsChildOf(canvasRoot.transform)) continue;
                    if (key == 13) { field.DeactivateInputField(); continue; }
                    int caret = field.caretPosition;
                    if (key == 8) { if (caret > 0) { field.text = field.text.Remove(caret - 1, 1); field.caretPosition = caret - 1; } }
                    else if (key == 127) { if (caret < field.text.Length) field.text = field.text.Remove(caret, 1); }
                    else if (key >= 32 && key <= 0x10ffff && (key < 0xd800 || key > 0xdfff))
                    {
                        string value = char.ConvertFromUtf32((int)key);
                        field.text = field.text.Insert(caret, value); field.caretPosition = caret + value.Length;
                    }
                }
            }
            var manifest = AvatarControlRuntime.Current;
            bool open = EntryPanel != null && EntryPanel.activeInHierarchy &&
                (!string.IsNullOrEmpty(AvatarControlRuntime.LastError) || (manifest != null && manifest.Root != null && manifest.Controls.Count > 0));
            KeyboardCaptureRequested = open;
            if (open != wasOpen)
            {
                wasOpen = open;
                canvasRoot.SetActive(open);
                if (open) { ConcealOriginal(); RefreshBody(); } else { RevealOriginal(); AvatarControlRuntime.EndIsolation(); CloseKeyboard(); }
            }
            if (open && manifest != lastManifest) { lastManifest = manifest; RefreshBody(); }
        }

        void ConcealOriginal()
        {
            if (EntryPanel == null) return;
            originalGroup = EntryPanel.GetComponent<CanvasGroup>();
            if (originalGroup == null) originalGroup = EntryPanel.AddComponent<CanvasGroup>();
            originalAlpha = originalGroup.alpha; originalInteractable = originalGroup.interactable; originalBlocks = originalGroup.blocksRaycasts;
            originalGroup.alpha = 0; originalGroup.interactable = false; originalGroup.blocksRaycasts = false;
        }

        void RevealOriginal()
        {
            if (originalGroup == null) return;
            originalGroup.alpha = originalAlpha; originalGroup.interactable = originalInteractable; originalGroup.blocksRaycasts = originalBlocks;
            originalGroup = null;
        }

        static GameObject Node(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        static void Height(GameObject go, float height)
        {
            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = height; layout.preferredHeight = height;
        }

        static Image Image(GameObject go, Color color)
        {
            var image = go.AddComponent<Image>(); image.color = color; return image;
        }

        static HorizontalLayoutGroup Horizontal(GameObject go, int spacing = 6)
        {
            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing; layout.childControlWidth = true; layout.childControlHeight = true;
            layout.childForceExpandWidth = false; layout.childForceExpandHeight = true;
            return layout;
        }

        static VerticalLayoutGroup Vertical(GameObject go, int spacing = 6)
        {
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing; layout.childControlWidth = true; layout.childControlHeight = true;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            return layout;
        }

        Text Label(Transform parent, string value, int size = 16, int width = 0)
        {
            var go = Node("Label", parent);
            var label = go.AddComponent<Text>();
            label.font = font; label.text = value; label.color = TextColor; label.fontSize = size;
            label.alignment = TextAnchor.MiddleLeft; label.horizontalOverflow = HorizontalWrapMode.Wrap;
            if (width > 0) { var element = go.AddComponent<LayoutElement>(); element.preferredWidth = width; element.minWidth = width; }
            else if (parent.GetComponent<HorizontalLayoutGroup>() != null)
            {
                var element = go.AddComponent<LayoutElement>(); element.flexibleWidth = 1; element.minWidth = 0;
            }
            return label;
        }

        Button Button(Transform parent, string title, Action action, int width = 0)
        {
            var go = Node(title, parent); Image(go, Accent);
            var button = go.AddComponent<Button>();
            var colors = button.colors; colors.highlightedColor = new Color(.5f, .72f, .9f); button.colors = colors;
            var text = Label(go.transform, title, 14);
            text.alignment = TextAnchor.MiddleCenter;
            var rect = text.rectTransform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            var element = go.AddComponent<LayoutElement>();
            if (width > 0) { element.preferredWidth = width; element.minWidth = width; }
            else element.flexibleWidth = 1;
            if (parent.GetComponent<VerticalLayoutGroup>() != null) element.preferredHeight = 34;
            button.onClick.AddListener(() => action?.Invoke());
            return button;
        }

        InputField Field(Transform parent, string initial, Action<string> changed, int width = 0)
        {
            var go = Node("Input", parent); Image(go, Row);
            var input = go.AddComponent<InputField>();
            var text = Label(go.transform, initial, 15);
            text.supportRichText = false;
            text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = new Vector2(8, 2); text.rectTransform.offsetMax = new Vector2(-44, -2);
            input.textComponent = text; input.text = initial;
            if (MateeInput.PresenterActive)
            {
                var keyboard = Button(go.transform, "ABC", () => OpenKeyboard(input, changed));
                var rect = keyboard.GetComponent<RectTransform>(); rect.anchorMin = new Vector2(1, 0); rect.anchorMax = Vector2.one;
                rect.pivot = new Vector2(1, .5f); rect.sizeDelta = new Vector2(38, 0); rect.anchoredPosition = Vector2.zero;
            }
            var element = go.AddComponent<LayoutElement>();
            if (width > 0) { element.preferredWidth = width; element.minWidth = width; }
            else element.flexibleWidth = 1;
            if (parent.GetComponent<VerticalLayoutGroup>() != null) element.preferredHeight = 34;
            input.onEndEdit.AddListener(value => changed?.Invoke(value));
            return input;
        }

        Toggle Check(Transform parent, bool initial, Action<bool> changed)
        {
            var go = Node("Check", parent); var element = go.AddComponent<LayoutElement>(); element.preferredWidth = 28; element.minWidth = 28;
            var toggle = go.AddComponent<Toggle>();
            var bg = Node("Background", go.transform); Image(bg, Row);
            var rect = bg.GetComponent<RectTransform>(); rect.anchorMin = new Vector2(.5f, .5f); rect.anchorMax = rect.anchorMin;
            rect.sizeDelta = new Vector2(23, 23); rect.anchoredPosition = Vector2.zero;
            var mark = Node("Mark", bg.transform); var markImage = Image(mark, Accent);
            var markRect = mark.GetComponent<RectTransform>(); markRect.anchorMin = Vector2.zero; markRect.anchorMax = Vector2.one;
            markRect.offsetMin = new Vector2(4, 4); markRect.offsetMax = new Vector2(-4, -4);
            toggle.targetGraphic = bg.GetComponent<Image>(); toggle.graphic = markImage;
            toggle.SetIsOnWithoutNotify(initial);
            toggle.onValueChanged.AddListener(value => changed?.Invoke(value));
            return toggle;
        }

        Slider Range(Transform parent, float min, float max, float value, Action<float> changed)
        {
            var go = Node("Slider", parent);
            var element = go.AddComponent<LayoutElement>(); element.flexibleWidth = 1; element.minWidth = 80;
            var slider = go.AddComponent<Slider>();
            var bg = Node("Track", go.transform); Image(bg, Row);
            var bgRect = bg.GetComponent<RectTransform>(); bgRect.anchorMin = new Vector2(0, .5f); bgRect.anchorMax = new Vector2(1, .5f);
            bgRect.sizeDelta = new Vector2(-12, 6); bgRect.anchoredPosition = Vector2.zero;
            var fill = Node("Fill", bg.transform); var fillImage = Image(fill, Accent);
            var fillRect = fill.GetComponent<RectTransform>(); fillRect.anchorMin = Vector2.zero; fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero; fillRect.offsetMax = Vector2.zero;
            var handle = Node("Handle", go.transform); var handleImage = Image(handle, TextColor);
            var handleRect = handle.GetComponent<RectTransform>(); handleRect.anchorMin = new Vector2(.5f, .5f); handleRect.anchorMax = handleRect.anchorMin;
            handleRect.sizeDelta = new Vector2(15, 22);
            slider.fillRect = fillRect; slider.handleRect = handleRect; slider.targetGraphic = handleImage;
            slider.minValue = min; slider.maxValue = max; slider.SetValueWithoutNotify(value);
            slider.onValueChanged.AddListener(v => changed?.Invoke(v));
            return slider;
        }

        GameObject Line(Transform parent, int height = 32)
        {
            var go = Node("Row", parent); Horizontal(go); Height(go, height); return go;
        }

        void BuildShell()
        {
            canvasRoot = new GameObject("Avatar Controls Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasRoot.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 250;
            var scaler = canvasRoot.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
            var panel = Node("Avatar Controls", canvasRoot.transform); Image(panel, Background);
            var rect = panel.GetComponent<RectTransform>(); rect.anchorMin = new Vector2(1, .5f); rect.anchorMax = rect.anchorMin;
            rect.pivot = new Vector2(1, .5f); rect.anchoredPosition = new Vector2(-24, 0); rect.sizeDelta = new Vector2(650, 950);
            var vertical = Vertical(panel, 8); vertical.padding = new RectOffset(14, 14, 12, 12);
            var header = Line(panel.transform, 42); Label(header.transform, "Avatar Controls", 22);
            Button(header.transform, "Close", () => { if (EntryPanel != null) EntryPanel.SetActive(false); }, 82);
            var searchRow = Line(panel.transform, 38);
            Label(searchRow.transform, "Search", 15, 65);
            var searchField = Field(searchRow.transform, "", null);
            searchField.onValueChanged.AddListener(value => { search = value; RefreshBody(); });
            for (int row = 0; row < 2; row++)
            {
                var tabRow = Line(panel.transform, 38);
                for (int i = row * 5; i < Math.Min((row + 1) * 5, Tabs.Length); i++)
                {
                    var next = Tabs[i];
                    Button(tabRow.transform, next, () => { CloseKeyboard(); tab = next; RefreshBody(); });
                }
            }
            var scrollGo = Node("Scroll", panel.transform); var scrollElement = scrollGo.AddComponent<LayoutElement>();
            scrollElement.flexibleHeight = 1; scrollElement.minHeight = 100;
            scroll = scrollGo.AddComponent<ScrollRect>(); scroll.horizontal = false; scroll.vertical = true;
            scroll.scrollSensitivity = 45;
            var viewport = Node("Viewport", scrollGo.transform); Image(viewport, new Color(.04f, .05f, .07f, .75f)); viewport.AddComponent<Mask>().showMaskGraphic = true;
            var viewportRect = viewport.GetComponent<RectTransform>(); viewportRect.anchorMin = Vector2.zero; viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = Vector2.zero; viewportRect.offsetMax = new Vector2(-18, 0);
            var content = Node("Content", viewport.transform); body = content.transform;
            var contentRect = content.GetComponent<RectTransform>(); contentRect.anchorMin = new Vector2(0, 1); contentRect.anchorMax = Vector2.one;
            contentRect.pivot = new Vector2(.5f, 1); contentRect.anchoredPosition = Vector2.zero; contentRect.sizeDelta = Vector2.zero;
            var contentLayout = Vertical(content, 6); contentLayout.padding = new RectOffset(8, 8, 8, 8);
            var fitter = content.AddComponent<ContentSizeFitter>(); fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewportRect; scroll.content = contentRect;
            var barGo = Node("Scrollbar", scrollGo.transform); Image(barGo, Row);
            var barRect = barGo.GetComponent<RectTransform>(); barRect.anchorMin = new Vector2(1, 0);
            barRect.anchorMax = Vector2.one; barRect.pivot = new Vector2(1, .5f);
            barRect.sizeDelta = new Vector2(14, 0); barRect.anchoredPosition = Vector2.zero;
            var handle = Node("Handle", barGo.transform); Image(handle, Accent);
            var handleRect = handle.GetComponent<RectTransform>(); handleRect.anchorMin = Vector2.zero;
            handleRect.anchorMax = Vector2.one; handleRect.offsetMin = Vector2.zero; handleRect.offsetMax = Vector2.zero;
            var bar = barGo.AddComponent<Scrollbar>(); bar.targetGraphic = handle.GetComponent<Image>();
            bar.handleRect = handleRect; bar.direction = Scrollbar.Direction.BottomToTop;
            scroll.verticalScrollbar = bar; scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            statusText = Label(panel.transform, "", 14); Height(statusText.gameObject, 28);
            var footer = Line(panel.transform, 38);
            Button(footer.transform, "Reset category", () => { var m = AvatarControlRuntime.Current; if (m != null) foreach (var c in m.Controls.Where(c => InTab(c.Category)).ToList()) AvatarControlRuntime.Reset(c); RefreshBody(); });
            Button(footer.transform, "Restore defaults", () => { AvatarControlRuntime.ResetAll(); RefreshBody(); });
            Button(footer.transform, "Release face", () =>
            {
                var m = AvatarControlRuntime.Current;
                if (m != null) foreach (var c in m.Controls.Where(c => c.Kind == ControlKind.Expression ||
                    (c.Kind == ControlKind.Morph && (c.Category.StartsWith("Face Lab/") || c.Category == "Advanced/Visemes"))).ToList())
                    AvatarControlRuntime.Reset(c);
                RefreshBody();
            });
            canvasRoot.SetActive(false);
        }

        bool InTab(string category)
        {
            if (tab == "Outfit") return category.StartsWith("Outfit/") || category == "Accessories" || category == "Uncategorized";
            if (tab == "Advanced") return category.StartsWith("Advanced/");
            if (tab == "Face Lab") return category.StartsWith("Face Lab/");
            return category == tab;
        }

        void RefreshBody()
        {
            if (body == null) return;
            visibleToggles.Clear();
            AvatarControlRuntime.EndIsolation();
            for (int i = body.childCount - 1; i >= 0; i--)
            {
                var child = body.GetChild(i).gameObject;
                if (Application.isPlaying) { child.SetActive(false); Destroy(child); }
                else DestroyImmediate(child);
            }
            var m = AvatarControlRuntime.Current;
            if (m == null) { Section(AvatarControlRuntime.LastError ?? "No active avatar controls"); return; }
            if (!string.IsNullOrEmpty(AvatarControlRuntime.LastError)) Section("Import error: " + AvatarControlRuntime.LastError, 14);
            if (tab == "Setup") DrawSetup(m);
            else if (tab == "Presets") DrawPresets(m);
            else DrawControls(m);
        }

        void DrawControls(Manifest m)
        {
            var controls = m.Controls.Where(c => !c.Hidden && InTab(c.Category) &&
                (string.IsNullOrEmpty(search) || c.Label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || c.Category.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(c => c.Category).ThenBy(c => c.Label).ToList();
            string lastCategory = null;
            foreach (var c in controls)
            {
                if (c.Category != lastCategory) { Section(c.Category); lastCategory = c.Category; }
                var line = Line(body);
                var name = Label(line.transform, c.Label + (c.Held ? " [held]" : ""), 15, 215);
                if (c.Kind == ControlKind.Renderer || c.Kind == ControlKind.Outfit)
                    visibleToggles[c] = Check(line.transform, c.Value >= .5f, value =>
                    {
                        AvatarControlRuntime.Set(c, value ? 1 : 0);
                        foreach (var pair in visibleToggles)
                            if (pair.Value != null) pair.Value.SetIsOnWithoutNotify(pair.Key.Value >= .5f);
                    });
                else
                {
                    float hi = c.Kind == ControlKind.Morph || c.Kind == ControlKind.PairedMorph ? 100 : 1;
                    var valueText = Label(line.transform, c.Value.ToString(hi == 1 ? "0.00" : "0"), 14, 45);
                    Range(line.transform, c.Kind == ControlKind.PairedMorph ? -100 : 0, hi, c.Value, value =>
                    {
                        AvatarControlRuntime.Set(c, value);
                        valueText.text = c.Value.ToString(hi == 1 ? "0.00" : "0");
                        name.text = c.Label + (c.Held ? " [held]" : "");
                    });
                }
                Button(line.transform, "Reset", () => { AvatarControlRuntime.Reset(c); RefreshBody(); }, 58);
            }
            if (controls.Count == 0) Section("No controls in this section");
            if (tab == "Advanced")
            {
                Section("Inventory only: physics and materials cannot be edited here", 14);
                Section("Physics groups: " + m.Physics.Count);
                foreach (var item in m.Physics) Section(item, 14);
                Section("Material names: " + m.Materials.Count);
                foreach (var item in m.Materials) Section("Material · " + item, 14);
            }
        }

        void Section(string value, int size = 17)
        {
            var text = Label(body, value, size); Height(text.gameObject, 27);
        }

        void DrawSetup(Manifest m)
        {
            Section("Inspect and correct discovered controls");
            if (m.ProfileAmbiguous)
            {
                Section("Choose a matching profile before saving", 14);
                foreach (var candidate in m.ProfileCandidates.ToList())
                    Button(body, "Use profile " + (candidate.exactHash ?? candidate.semanticHash ?? "unknown").Substring(0, 8),
                        () => { AvatarControlRuntime.ChooseProfile(candidate); RefreshBody(); });
                return;
            }
            Button(body, "Import legacy blendshape settings", () => { Status(AvatarProfileStore.ImportLegacy(m)); RefreshBody(); });
            foreach (var c in m.Controls.Where(c => string.IsNullOrEmpty(search) || c.Label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(c => c.Category).ThenBy(c => c.Label))
            {
                var card = Node("Setup control", body); Image(card, Row); var layout = Vertical(card, 4); layout.padding = new RectOffset(8, 8, 5, 5);
                var fitter = card.AddComponent<ContentSizeFitter>(); fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                var name = Line(card.transform);
                Field(name.transform, c.Label, value => { c.Label = value; AvatarControlRuntime.SaveSetup(c); }, 230);
                Label(name.transform, c.Source + " · " + c.Confidence.ToString("0.00"), 13);
                Check(name.transform, c.Hidden, value => { c.Hidden = value; AvatarControlRuntime.SaveSetup(c); });
                if (c.Renderers.Count > 0)
                {
                    var inspect = Button(name.transform, "Hold to inspect", null, 125);
                    var hold = inspect.gameObject.AddComponent<AvatarRendererHold>(); hold.Target = c;
                }
                var category = Line(card.transform);
                Label(category.transform, c.Category, 14, 220);
                Button(category.transform, "Next category", () => { int index = Array.IndexOf(Categories, c.Category); c.Category = Categories[(index + 1) % Categories.Length]; AvatarControlRuntime.SaveSetup(c); RefreshBody(); }, 120);
                Label(category.transform, c.Explanation ?? "", 12);
                var links = Line(card.transform);
                Label(links.transform, "Group", 13, 45);
                Field(links.transform, c.Group ?? "", value => { c.Group = value; AvatarControlRuntime.SaveSetup(c); }, 105);
                Button(links.transform, c.GroupMode, () => { c.GroupMode = c.GroupMode == "Independent" ? "AtMostOne" : c.GroupMode == "AtMostOne" ? "ExactlyOne" : "Independent"; AvatarControlRuntime.SaveSetup(c); RefreshBody(); }, 105);
                var deps = m.Controls.Where(x => x.Kind == ControlKind.Renderer || x.Kind == ControlKind.Outfit).ToList();
                Button(links.transform, c.Dependency == null ? "No dependency" : (m.Find(c.Dependency)?.Label ?? "Unresolved"), () =>
                { int index = deps.FindIndex(x => x.Id == c.Dependency); c.Dependency = index + 1 < deps.Count ? deps[index + 1].Id : null; AvatarControlRuntime.SaveSetup(c); RefreshBody(); }, 145);
            }
        }

        void DrawPresets(Manifest m)
        {
            if (m.ProfileAmbiguous) { Section("Resolve the profile match in Setup before saving presets."); return; }
            Section("Named snapshots of avatar controls");
            Field(body, presetName, value => presetName = value);
            Button(body, "Save or overwrite preset", () =>
            {
                if (string.IsNullOrWhiteSpace(presetName)) { Status("Enter a preset name first."); return; }
                m.Profile.presets[presetName.Trim()] = m.Controls.Where(c =>
                    (c.Kind != ControlKind.Expression || c.Held) &&
                    (c.Kind != ControlKind.Morph || (!c.Hidden && string.IsNullOrEmpty(c.Dependency))))
                    .ToDictionary(c => c.Id, c => c.Value);
                AvatarProfileStore.Save(m); Status("Saved " + presetName.Trim()); RefreshBody();
            });
            foreach (var name in m.Profile.presets.Keys.ToList().OrderBy(x => x))
            {
                var line = Line(body);
                Label(line.transform, name, 15, 180);
                Button(line.transform, "Apply", () =>
                { foreach (var pair in m.Profile.presets[name]) { var c = m.Find(pair.Key); if (c != null) AvatarControlRuntime.Set(c, pair.Value); } RefreshBody(); }, 75);
                Button(line.transform, "Rename", () =>
                { if (!string.IsNullOrWhiteSpace(presetName) && !m.Profile.presets.ContainsKey(presetName.Trim()))
                  { m.Profile.presets[presetName.Trim()] = m.Profile.presets[name]; m.Profile.presets.Remove(name); AvatarProfileStore.Save(m); RefreshBody(); } }, 75);
                Button(line.transform, "Delete", () => { m.Profile.presets.Remove(name); AvatarProfileStore.Save(m); RefreshBody(); }, 75);
            }
        }

        void Status(string message) { if (statusText != null) statusText.text = message; }

        void OpenKeyboard(InputField target, Action<string> changed)
        {
            CloseKeyboard();
            keyboardTarget = target; keyboardChanged = changed; keyboardShift = false;
            keyboardOverlay = Node("On-screen keyboard", canvasRoot.transform); Image(keyboardOverlay, Background);
            var rect = keyboardOverlay.GetComponent<RectTransform>(); rect.anchorMin = new Vector2(.5f, 0);
            rect.anchorMax = rect.anchorMin; rect.pivot = new Vector2(.5f, 0); rect.anchoredPosition = new Vector2(0, 18);
            rect.sizeDelta = new Vector2(570, 300);
            var vertical = Vertical(keyboardOverlay, 6); vertical.padding = new RectOffset(10, 10, 8, 8);
            var title = Line(keyboardOverlay.transform, 32);
            Label(title.transform, "Text entry", 18);
            Button(title.transform, "Done", CloseKeyboard, 70);
            foreach (var row in new[] { "1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm-_." })
            {
                var line = Line(keyboardOverlay.transform, 43);
                foreach (char ch in row)
                    Button(line.transform, char.ToUpperInvariant(ch).ToString(), () => AppendKeyboard(keyboardShift ? char.ToUpperInvariant(ch).ToString() : ch.ToString()));
            }
            var actions = Line(keyboardOverlay.transform, 43);
            Button(actions.transform, "Shift", () => keyboardShift = !keyboardShift, 85);
            Button(actions.transform, "Space", () => AppendKeyboard(" "));
            Button(actions.transform, "Backspace", () =>
            {
                if (keyboardTarget != null && keyboardTarget.text.Length > 0)
                    UpdateKeyboard(keyboardTarget.text.Substring(0, keyboardTarget.text.Length - 1));
            }, 110);
            Button(actions.transform, "Clear", () => UpdateKeyboard(""), 65);
        }

        void AppendKeyboard(string value)
        {
            if (keyboardTarget != null) UpdateKeyboard(keyboardTarget.text + value);
        }

        void UpdateKeyboard(string value)
        {
            if (keyboardTarget == null) return;
            keyboardTarget.text = value;
            keyboardChanged?.Invoke(value);
        }

        void CloseKeyboard()
        {
            if (keyboardOverlay != null) Destroy(keyboardOverlay);
            keyboardOverlay = null; keyboardTarget = null; keyboardChanged = null;
        }
    }

    public sealed class AvatarRendererHold : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public Control Target;
        public void OnPointerDown(PointerEventData data) { if (data.button == PointerEventData.InputButton.Left) AvatarControlRuntime.BeginIsolation(Target); }
        public void OnPointerUp(PointerEventData data) => AvatarControlRuntime.EndIsolation();
        public void OnPointerExit(PointerEventData data) => AvatarControlRuntime.EndIsolation();
        void OnDisable() => AvatarControlRuntime.EndIsolation();
    }
}
