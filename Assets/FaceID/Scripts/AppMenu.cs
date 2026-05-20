// Copyright (c) NanoSight.
//
// In-VR settings menu toggled with the left Menu button (OVRInput.Button.Start).
// Three-tab layout (Server / Options / Log), all standard Unity UI driven by OVRInputModule.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NanoSight.FaceID
{
    /// <summary>
    /// Floating settings panel. Opens/closes with the left "Menu" button — same input from the
    /// controller's 3-line button or the hand-tracking palm pinch (both map to
    /// <c>OVRInput.Button.Start</c>). All interactive controls (tabs, button, toggle, input
    /// field, sliders) are standard Unity UI elements driven by an OVRRaycaster + OVRInputModule.
    ///
    /// Code-spawns its entire UI: drop the component on an empty GO and wire FaceIdentifier +
    /// CenterEyeAnchor in the Inspector.
    /// </summary>
    public class AppMenu : MonoBehaviour
    {
        [Header("Refs")]
        [SerializeField] private FaceIdentifier m_faceIdentifier;
        [SerializeField] private Transform m_eyeAnchor;
        [SerializeField] private RayVisualizer[] m_rayVisualizers;

        [Header("Placement (metres)")]
        [SerializeField, Range(0.4f, 2f)] private float m_spawnDistance = 0.75f;
        [SerializeField, Range(-0.4f, 0.4f)] private float m_spawnYOffset = -0.05f;

        [Header("Layout (millimetres)")]
        [SerializeField] private Vector2 m_panelSizeMm = new(340f, 420f);

        [Header("Fade")]
        [SerializeField, Range(0.05f, 0.6f)] private float m_fadeSeconds = 0.15f;

        private GameObject m_root;
        private CanvasGroup m_group;
        private bool m_open;
        private float m_targetAlpha;

        private enum Tab { Server, Options, Log }
        private Tab m_currentTab = Tab.Server;
        private readonly Button[] m_tabButtons = new Button[3];
        private readonly GameObject[] m_tabPanels = new GameObject[3];

        // Server tab widgets.
        private TMP_InputField m_urlInput;
        private TextMeshProUGUI m_statusText;

        // Log tab widgets.
        private TextMeshProUGUI m_logText;
        private ScrollRect m_logScroll;

        // Options tab — keep refs to live controls so we can refresh them when the tab opens.
        private readonly List<Action> m_optionsRefreshers = new();

        private CancellationTokenSource m_pingCts;
        private static readonly StringBuilder s_sb = new(4096);

        private static readonly Color Cyan   = new(0.31f, 0.93f, 0.78f, 1f);
        private static readonly Color BgDark = new(0.04f, 0.06f, 0.08f, 0.94f);
        private static readonly Color Tint   = new(0.10f, 0.13f, 0.15f, 1f);

        private void Awake()
        {
            if (m_faceIdentifier == null)
            {
                Debug.LogError($"[{nameof(AppMenu)}] FaceIdentifier not assigned — disabling.", this);
                enabled = false;
                return;
            }
            if (m_eyeAnchor == null && Camera.main != null)
                m_eyeAnchor = Camera.main.transform;
        }

        private void OnEnable()
        {
            BuildPanelIfNeeded();
            ServerRequestLog.OnEntryAdded += RefreshLogIfOpen;
        }

        private void OnDisable()
        {
            ServerRequestLog.OnEntryAdded -= RefreshLogIfOpen;
            m_pingCts?.Cancel();
            m_pingCts?.Dispose();
            m_pingCts = null;
        }

        private void Update()
        {
            if (OVRInput.GetDown(OVRInput.Button.Start))
                Toggle();

            if (m_group != null && !Mathf.Approximately(m_group.alpha, m_targetAlpha))
            {
                var step = Time.deltaTime / Mathf.Max(0.01f, m_fadeSeconds);
                m_group.alpha = Mathf.MoveTowards(m_group.alpha, m_targetAlpha, step);
                m_group.blocksRaycasts = m_group.alpha > 0.01f;
                m_group.interactable = m_group.blocksRaycasts;
            }
        }

        // ---- Open / close / tabs ----

        private void Toggle() { if (m_open) Close(); else Open(); }

        private void Open()
        {
            if (m_eyeAnchor != null)
            {
                var forward = m_eyeAnchor.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
                forward.Normalize();
                m_root.transform.position = m_eyeAnchor.position
                                            + forward * m_spawnDistance
                                            + Vector3.up * m_spawnYOffset;
                m_root.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            }

            m_open = true;
            m_targetAlpha = 1f;
            SelectTab(m_currentTab);
        }

        private void Close()
        {
            m_open = false;
            m_targetAlpha = 0f;
        }

        private void SelectTab(Tab tab)
        {
            m_currentTab = tab;
            for (int i = 0; i < m_tabPanels.Length; i++)
                if (m_tabPanels[i] != null) m_tabPanels[i].SetActive(i == (int)tab);

            for (int i = 0; i < m_tabButtons.Length; i++)
            {
                if (m_tabButtons[i] == null) continue;
                var label = m_tabButtons[i].GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.color = i == (int)tab ? Cyan : new Color(1f, 1f, 1f, 0.45f);
                var img = m_tabButtons[i].targetGraphic as Image;
                if (img != null)
                    img.color = i == (int)tab ? new Color(0.07f, 0.10f, 0.12f, 1f) : BgDark;
            }

            switch (tab)
            {
                case Tab.Server:
                    if (m_urlInput != null && !m_urlInput.isFocused)
                        m_urlInput.SetTextWithoutNotify(m_faceIdentifier.Server.ServerUrl);
                    if (m_statusText != null && string.IsNullOrEmpty(m_statusText.text))
                        m_statusText.text = "<color=#888>(untested)</color>";
                    break;
                case Tab.Options:
                    foreach (var refresh in m_optionsRefreshers) refresh();
                    break;
                case Tab.Log:
                    RefreshLog();
                    break;
            }
        }

        // ---- Server tab ----

        private void OnUrlChanged(string newUrl)
        {
            m_faceIdentifier.Server.SetServerUrl(newUrl?.Trim() ?? string.Empty);
        }

        private async void Reconnect()
        {
            if (m_statusText == null) return;
            m_statusText.text = "<color=#888>testing…</color>";

            m_pingCts?.Cancel();
            m_pingCts?.Dispose();
            m_pingCts = new CancellationTokenSource();
            var token = m_pingCts.Token;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok;
            try { ok = await m_faceIdentifier.Server.HealthCheckAsync(token); }
            catch { ok = false; }
            sw.Stop();

            if (token.IsCancellationRequested || this == null) return;

            m_statusText.text = ok
                ? $"<color=#5FE8C5>connected</color> · {sw.Elapsed.TotalMilliseconds:0} ms"
                : "<color=#E94F4F>no response</color>";
        }

        // ---- Log refresh ----

        private void RefreshLogIfOpen()
        {
            if (m_open && m_currentTab == Tab.Log) RefreshLog();
        }

        private void RefreshLog()
        {
            if (m_logText == null) return;

            var entries = ServerRequestLog.Snapshot();
            s_sb.Clear();
            if (entries.Length == 0)
            {
                s_sb.Append("<color=#888>no requests yet</color>");
            }
            else
            {
                var local = System.TimeZoneInfo.Local;
                for (int i = 0; i < entries.Length; i++)
                {
                    var e = entries[i];
                    var t = System.TimeZoneInfo.ConvertTimeFromUtc(e.TimeUtc, local);
                    string tag, body;
                    switch (e.Outcome)
                    {
                        case ServerRequestLog.Status.Recognised:
                            tag = "<color=#5FE8C5>OK </color>";
                            body = $"{Escape(e.Name)} · {e.Confidence:0.00}";
                            break;
                        case ServerRequestLog.Status.Unknown:
                            tag = "<color=#FFC857>?? </color>";
                            body = $"unknown · best={e.Confidence:0.00}";
                            break;
                        default:
                            tag = "<color=#E94F4F>ERR</color>";
                            body = Escape(TrimText(e.Detail, 48));
                            break;
                    }
                    s_sb.AppendFormat("{0:HH:mm:ss} {1} #{2} {3} <color=#666>{4:0}ms</color>\n",
                        t, tag, e.TrackId, body, e.LatencyMs);
                }
            }
            m_logText.text = s_sb.ToString();
            if (m_logScroll != null) m_logScroll.verticalNormalizedPosition = 1f;
        }

        // ---- UI build ----

        private void BuildPanelIfNeeded()
        {
            if (m_root != null) return;

            m_root = new GameObject("AppMenu Panel");
            m_root.transform.SetParent(transform, false);
            m_root.transform.localScale = Vector3.one * 0.001f;

            var canvas = m_root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 300;
            m_root.AddComponent<CanvasScaler>();
            m_root.AddComponent<GraphicRaycaster>();
            TryAddOVRRaycaster(m_root);

            m_group = m_root.AddComponent<CanvasGroup>();
            m_group.alpha = 0f;
            m_group.blocksRaycasts = false;
            m_group.interactable = false;

            var rect = m_root.GetComponent<RectTransform>();
            rect.sizeDelta = m_panelSizeMm;

            if (m_rayVisualizers != null)
                foreach (var rv in m_rayVisualizers)
                    if (rv != null) rv.SetTarget(rect);

            var bg = AddImage(m_root.transform, "Background", BgDark);
            Stretch(bg.rectTransform);

            // Tab bar.
            var tabBar = new GameObject("Tabs", typeof(RectTransform));
            tabBar.transform.SetParent(m_root.transform, false);
            var tabBarRect = tabBar.GetComponent<RectTransform>();
            tabBarRect.anchorMin = new Vector2(0f, 1f);
            tabBarRect.anchorMax = new Vector2(1f, 1f);
            tabBarRect.pivot = new Vector2(0.5f, 1f);
            tabBarRect.sizeDelta = new Vector2(0f, 36f);
            tabBarRect.anchoredPosition = Vector2.zero;
            var tabLayout = tabBar.AddComponent<HorizontalLayoutGroup>();
            tabLayout.spacing = 0f;
            tabLayout.childForceExpandWidth = true;
            tabLayout.childForceExpandHeight = true;

            m_tabButtons[0] = AddTabButton(tabBar.transform, "SERVER",  () => SelectTab(Tab.Server));
            m_tabButtons[1] = AddTabButton(tabBar.transform, "OPTIONS", () => SelectTab(Tab.Options));
            m_tabButtons[2] = AddTabButton(tabBar.transform, "LOG",     () => SelectTab(Tab.Log));

            // Content area.
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(m_root.transform, false);
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 0f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.offsetMin = new Vector2(12f, 12f);
            contentRect.offsetMax = new Vector2(-12f, -42f);

            m_tabPanels[0] = BuildServerPanel(content.transform);
            m_tabPanels[1] = BuildOptionsPanel(content.transform);
            m_tabPanels[2] = BuildLogPanel(content.transform);

            SelectTab(Tab.Server);
        }

        private GameObject BuildServerPanel(Transform parent)
        {
            var panel = NewPanel(parent, "ServerPanel");
            var layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 10f;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperLeft;

            AddLabel(panel.transform, "Server URL", smaller: true, dim: true);
            m_urlInput = AddInputField(panel.transform, m_faceIdentifier.Server.ServerUrl,
                                       "http://192.168.x.x:8000/api/oculus/identify");
            m_urlInput.onEndEdit.AddListener(OnUrlChanged);

            AddButton(panel.transform, "RECONNECT", Reconnect);

            AddLabel(panel.transform, "Status", smaller: true, dim: true);
            m_statusText = AddBody(panel.transform, "<color=#888>(untested)</color>");

            return panel;
        }

        private GameObject BuildOptionsPanel(Transform parent)
        {
            var panel = NewPanel(parent, "OptionsPanel");

            // ScrollRect wrapper so the long options list fits.
            // The Scroll GO needs a Graphic with raycastTarget=true so OVRInputModule can pick up
            // pinch+drag events fired in empty space inside the scroll area — without it the
            // pointer ray "passes through" the gaps between rows and the ScrollRect never sees
            // any drag, so the panel just sits there frozen.
            var scroll = new GameObject("Scroll", typeof(RectTransform), typeof(Image));
            scroll.transform.SetParent(panel.transform, false);
            Stretch(scroll.GetComponent<RectTransform>());
            var scrollBg = scroll.GetComponent<Image>();
            scrollBg.color = new Color(0f, 0f, 0f, 0.001f);  // invisible but raycast-able
            scrollBg.raycastTarget = true;
            var sr = scroll.AddComponent<ScrollRect>();
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 12f;

            var viewport = new GameObject("Viewport",
                typeof(RectTransform), typeof(Image), typeof(Mask));
            viewport.transform.SetParent(scroll.transform, false);
            var vpRect = viewport.GetComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            // Leave 16 px on the right for the scrollbar (12 width + 4 gap).
            vpRect.offsetMin = new Vector2(2f, 2f);
            vpRect.offsetMax = new Vector2(-16f, -2f);
            viewport.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
            viewport.GetComponent<Mask>().showMaskGraphic = false;
            sr.viewport = vpRect;

            var content = new GameObject("OptionsContent", typeof(RectTransform));
            content.transform.SetParent(viewport.transform, false);
            var ct = content.GetComponent<RectTransform>();
            ct.anchorMin = new Vector2(0f, 1f);
            ct.anchorMax = new Vector2(1f, 1f);
            ct.pivot = new Vector2(0.5f, 1f);
            ct.sizeDelta = new Vector2(0f, 0f);
            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var vLayout = content.AddComponent<VerticalLayoutGroup>();
            vLayout.spacing = 6f;
            vLayout.childForceExpandWidth = true;
            vLayout.childForceExpandHeight = false;
            vLayout.childAlignment = TextAnchor.UpperLeft;
            sr.content = ct;

            // Visible vertical scrollbar on the right. Pinch+drag the handle to scroll.
            var sbComp = BuildScrollbar(scroll.transform);
            sr.verticalScrollbar = sbComp;
            sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

            BuildOptionsContent(content.transform);

            return panel;
        }

        /// <summary>
        /// Builds a vertical scrollbar anchored to the right edge of <paramref name="parent"/>.
        /// Pinch+drag the cyan handle to scroll the parent ScrollRect.
        /// </summary>
        private static Scrollbar BuildScrollbar(Transform parent)
        {
            var sb = new GameObject("Scrollbar", typeof(RectTransform), typeof(Image));
            sb.transform.SetParent(parent, false);
            var sbRect = sb.GetComponent<RectTransform>();
            sbRect.anchorMin = new Vector2(1f, 0f);
            sbRect.anchorMax = new Vector2(1f, 1f);
            sbRect.pivot = new Vector2(1f, 0.5f);
            sbRect.sizeDelta = new Vector2(12f, 0f);
            sbRect.anchoredPosition = Vector2.zero;
            var sbBg = sb.GetComponent<Image>();
            sbBg.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            sbBg.type = Image.Type.Sliced;
            sbBg.color = Tint;

            var slidingArea = new GameObject("Sliding Area", typeof(RectTransform));
            slidingArea.transform.SetParent(sb.transform, false);
            var saRect = slidingArea.GetComponent<RectTransform>();
            saRect.anchorMin = Vector2.zero;
            saRect.anchorMax = Vector2.one;
            saRect.offsetMin = new Vector2(2f, 2f);
            saRect.offsetMax = new Vector2(-2f, -2f);

            var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handle.transform.SetParent(slidingArea.transform, false);
            var handleRect = handle.GetComponent<RectTransform>();
            handleRect.anchorMin = Vector2.zero;
            handleRect.anchorMax = Vector2.one;
            handleRect.sizeDelta = Vector2.zero;
            var handleImg = handle.GetComponent<Image>();
            handleImg.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            handleImg.type = Image.Type.Sliced;
            handleImg.color = Cyan;

            var scrollbar = sb.AddComponent<Scrollbar>();
            scrollbar.targetGraphic = handleImg;
            scrollbar.handleRect = handleRect;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            return scrollbar;
        }

        private void BuildOptionsContent(Transform parent)
        {
            var fi = m_faceIdentifier;
            var lm = fi.LabelManager;
            var tk = fi.Tracker;
            var server = fi.Server;

            // ---- DISPLAY ----
            AddSectionHeader(parent, "DISPLAY");
            AddToggleRow(parent, "Status HUD chip",
                () => fi.ShowStatusHud, v => fi.ShowStatusHud = v);
            AddToggleRow(parent, "Face brackets",
                () => fi.ShowFaceBoxes, v => fi.ShowFaceBoxes = v);
            AddToggleRow(parent, "Searching placeholder",
                () => fi.ShowPlaceholderWhileIdentifying, v => fi.ShowPlaceholderWhileIdentifying = v);
            if (lm != null)
            {
                AddSliderRow(parent, "Name font size",
                    () => lm.CurrentNameFontSize, v => lm.SetNameFontSize(v),
                    min: 8f, max: 40f, format: "0");
                AddSliderRow(parent, "Details font size",
                    () => lm.CurrentDetailsFontSize, v => lm.SetDetailsFontSize(v),
                    min: 6f, max: 28f, format: "0");
                AddSliderRow(parent, "Panel scale",
                    () => lm.CurrentPanelScale, v => lm.SetPanelScale(v),
                    min: 0.2f, max: 3f, format: "0.00");
                AddToggleRow(parent, "Scale panel by distance",
                    () => lm.CurrentScaleByDistance, v => lm.SetScaleByDistance(v));
                AddSliderRow(parent, "Reference distance (m)",
                    () => lm.CurrentReferenceDistance, v => lm.SetReferenceDistance(v),
                    min: 0.3f, max: 5f, format: "0.00");
            }

            // ---- TRACKING ----
            AddSectionHeader(parent, "TRACKING");
            AddSliderRow(parent, "IoU match threshold",
                () => tk.IouMatchThreshold, v => tk.IouMatchThreshold = v,
                min: 0.05f, max: 0.9f, format: "0.00");
            AddSliderRow(parent, "Lost grace (s)",
                () => tk.LostGraceSeconds, v => tk.LostGraceSeconds = v,
                min: 0f, max: 5f, format: "0.00");
            AddSliderRow(parent, "Centroid fallback dist",
                () => tk.CentroidFallbackDistance, v => tk.CentroidFallbackDistance = v,
                min: 0f, max: 0.5f, format: "0.00");
            AddSliderRow(parent, "Max size ratio",
                () => tk.MaxSizeRatio, v => tk.MaxSizeRatio = v,
                min: 1.1f, max: 3f, format: "0.00");

            // ---- DETECTION & SERVER ----
            AddSectionHeader(parent, "DETECTION");
            AddSliderRow(parent, "Detection interval (s)",
                () => fi.DetectionInterval, v => fi.DetectionInterval = v,
                min: 0.02f, max: 2f, format: "0.00");
            AddSliderRow(parent, "Crop padding ratio",
                () => fi.CropPaddingRatio, v => fi.CropPaddingRatio = v,
                min: 0f, max: 1.5f, format: "0.00");
            AddSliderRow(parent, "Min confidence to show",
                () => fi.MinConfidenceToShow, v => fi.MinConfidenceToShow = v,
                min: 0f, max: 1f, format: "0.00");
            AddSliderRow(parent, "Server timeout (s)",
                () => server.TimeoutSeconds, v => server.TimeoutSeconds = Mathf.RoundToInt(v),
                min: 1f, max: 60f, format: "0");
        }

        private GameObject BuildLogPanel(Transform parent)
        {
            var panel = NewPanel(parent, "LogPanel");

            var scrollGO = new GameObject("Scroll", typeof(RectTransform), typeof(Image));
            scrollGO.transform.SetParent(panel.transform, false);
            Stretch(scrollGO.GetComponent<RectTransform>());
            var scrollBg = scrollGO.GetComponent<Image>();
            scrollBg.color = new Color(0f, 0f, 0f, 0.25f);

            m_logScroll = scrollGO.AddComponent<ScrollRect>();
            m_logScroll.horizontal = false;
            m_logScroll.vertical = true;
            m_logScroll.movementType = ScrollRect.MovementType.Clamped;

            var viewport = new GameObject("Viewport",
                typeof(RectTransform), typeof(Image), typeof(Mask));
            viewport.transform.SetParent(scrollGO.transform, false);
            Stretch(viewport.GetComponent<RectTransform>(), padding: 4f);
            viewport.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
            viewport.GetComponent<Mask>().showMaskGraphic = false;
            m_logScroll.viewport = viewport.GetComponent<RectTransform>();

            var contentGO = new GameObject("LogContent", typeof(RectTransform));
            contentGO.transform.SetParent(viewport.transform, false);
            var ctRect = contentGO.GetComponent<RectTransform>();
            ctRect.anchorMin = new Vector2(0f, 1f);
            ctRect.anchorMax = new Vector2(1f, 1f);
            ctRect.pivot = new Vector2(0.5f, 1f);
            ctRect.sizeDelta = Vector2.zero;
            var ctFitter = contentGO.AddComponent<ContentSizeFitter>();
            ctFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            m_logScroll.content = ctRect;

            m_logText = contentGO.AddComponent<TextMeshProUGUI>();
            m_logText.text = "";
            m_logText.fontSize = 9f;
            m_logText.alignment = TextAlignmentOptions.TopLeft;
            m_logText.enableWordWrapping = true;
            m_logText.richText = true;
            m_logText.color = Color.white;
            m_logText.margin = new Vector4(2f, 2f, 2f, 2f);

            return panel;
        }

        // ---- UI factories ----

        private static GameObject NewPanel(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Stretch(go.GetComponent<RectTransform>());
            return go;
        }

        private static Image AddImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            img.type = Image.Type.Sliced;
            img.color = color;
            return img;
        }

        private static void Stretch(RectTransform r, float padding = 0f)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(padding, padding);
            r.offsetMax = new Vector2(-padding, -padding);
        }

        private static void AddSectionHeader(Transform parent, string text)
        {
            var go = new GameObject("Section " + text, typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            go.transform.SetParent(parent, false);
            go.text = text;
            go.fontSize = 11;
            go.fontStyle = FontStyles.Bold;
            go.alignment = TextAlignmentOptions.MidlineLeft;
            go.color = Cyan;
            go.margin = new Vector4(2f, 8f, 0f, 2f);
            var le = go.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 22f;
        }

        private void AddToggleRow(Transform parent, string label, Func<bool> getter, Action<bool> setter)
        {
            var toggle = AddToggle(parent, label, getter());
            toggle.onValueChanged.AddListener(v => setter(v));
            m_optionsRefreshers.Add(() => toggle.SetIsOnWithoutNotify(getter()));
        }

        private void AddSliderRow(Transform parent, string label,
            Func<float> getter, Action<float> setter,
            float min, float max, string format)
        {
            var row = new GameObject("Slider " + label, typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var rowLE = row.AddComponent<LayoutElement>();
            rowLE.preferredHeight = 40f;
            var vRow = row.AddComponent<VerticalLayoutGroup>();
            vRow.spacing = 2f;
            vRow.childForceExpandWidth = true;
            vRow.childForceExpandHeight = false;

            // Top line: label + numeric value.
            var topRow = new GameObject("LabelRow", typeof(RectTransform));
            topRow.transform.SetParent(row.transform, false);
            var topLE = topRow.AddComponent<LayoutElement>();
            topLE.preferredHeight = 16f;
            var hLayout = topRow.AddComponent<HorizontalLayoutGroup>();
            hLayout.childForceExpandWidth = false;
            hLayout.childForceExpandHeight = true;
            hLayout.childAlignment = TextAnchor.MiddleLeft;

            var nameText = new GameObject("Label", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            nameText.transform.SetParent(topRow.transform, false);
            nameText.text = label;
            nameText.fontSize = 10;
            nameText.alignment = TextAlignmentOptions.MidlineLeft;
            nameText.color = Color.white;
            var nameLE = nameText.gameObject.AddComponent<LayoutElement>();
            nameLE.flexibleWidth = 1f;

            var valueText = new GameObject("Value", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            valueText.transform.SetParent(topRow.transform, false);
            valueText.fontSize = 10;
            valueText.alignment = TextAlignmentOptions.MidlineRight;
            valueText.color = Cyan;
            var valueLE = valueText.gameObject.AddComponent<LayoutElement>();
            valueLE.preferredWidth = 50f;

            // Bottom line: the slider track + handle.
            var slider = BuildSlider(row.transform, min, max, getter());
            slider.onValueChanged.AddListener(v =>
            {
                valueText.text = v.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
                setter(v);
            });
            valueText.text = getter().ToString(format, System.Globalization.CultureInfo.InvariantCulture);

            m_optionsRefreshers.Add(() =>
            {
                slider.SetValueWithoutNotify(getter());
                valueText.text = getter().ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            });
        }

        private static Slider BuildSlider(Transform parent, float min, float max, float value)
        {
            var go = new GameObject("Slider", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 18f;

            var slider = go.AddComponent<Slider>();
            slider.minValue = min;
            slider.maxValue = max;

            // Background bar.
            var bgGO = new GameObject("Background", typeof(RectTransform));
            bgGO.transform.SetParent(go.transform, false);
            var bgRT = bgGO.GetComponent<RectTransform>();
            bgRT.anchorMin = new Vector2(0f, 0.4f);
            bgRT.anchorMax = new Vector2(1f, 0.6f);
            bgRT.offsetMin = Vector2.zero;
            bgRT.offsetMax = Vector2.zero;
            var bgImg = bgGO.AddComponent<Image>();
            bgImg.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            bgImg.type = Image.Type.Sliced;
            bgImg.color = Tint;

            // Fill area + Fill.
            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(go.transform, false);
            var faRT = fillArea.GetComponent<RectTransform>();
            faRT.anchorMin = new Vector2(0f, 0.4f);
            faRT.anchorMax = new Vector2(1f, 0.6f);
            faRT.offsetMin = new Vector2(5f, 0f);
            faRT.offsetMax = new Vector2(-5f, 0f);

            var fillGO = new GameObject("Fill", typeof(RectTransform));
            fillGO.transform.SetParent(fillArea.transform, false);
            var fillRT = fillGO.GetComponent<RectTransform>();
            fillRT.anchorMin = Vector2.zero;
            fillRT.anchorMax = Vector2.one;
            fillRT.offsetMin = Vector2.zero;
            fillRT.offsetMax = Vector2.zero;
            var fillImg = fillGO.AddComponent<Image>();
            fillImg.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            fillImg.type = Image.Type.Sliced;
            fillImg.color = Cyan;

            // Handle area + Handle.
            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(go.transform, false);
            var haRT = handleArea.GetComponent<RectTransform>();
            haRT.anchorMin = new Vector2(0f, 0f);
            haRT.anchorMax = new Vector2(1f, 1f);
            haRT.offsetMin = new Vector2(8f, 0f);
            haRT.offsetMax = new Vector2(-8f, 0f);

            var handleGO = new GameObject("Handle", typeof(RectTransform));
            handleGO.transform.SetParent(handleArea.transform, false);
            var handleRT = handleGO.GetComponent<RectTransform>();
            handleRT.anchorMin = new Vector2(0f, 0.5f);
            handleRT.anchorMax = new Vector2(0f, 0.5f);
            handleRT.sizeDelta = new Vector2(16f, 16f);
            var handleImg = handleGO.AddComponent<Image>();
            handleImg.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/Knob.psd");
            handleImg.color = Color.white;

            slider.fillRect = fillRT;
            slider.handleRect = handleRT;
            slider.targetGraphic = handleImg;
            slider.direction = Slider.Direction.LeftToRight;
            slider.SetValueWithoutNotify(value);

            return slider;
        }

        private static Button AddTabButton(Transform parent, string label, Action onClick)
        {
            var go = new GameObject("Tab_" + label, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            img.type = Image.Type.Sliced;
            img.color = BgDark;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => onClick());

            var tx = new GameObject("Label", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            tx.transform.SetParent(go.transform, false);
            Stretch(tx.rectTransform);
            tx.text = label;
            tx.fontSize = 13;
            tx.fontStyle = FontStyles.Bold;
            tx.alignment = TextAlignmentOptions.Center;
            tx.color = new Color(1f, 1f, 1f, 0.45f);
            return btn;
        }

        private static Button AddButton(Transform parent, string label, Action onClick)
        {
            var go = new GameObject("Button_" + label, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            img.type = Image.Type.Sliced;
            img.color = new Color(0.31f, 0.93f, 0.78f, 0.85f);

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => onClick());

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 36f;

            var tx = new GameObject("Label", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            tx.transform.SetParent(go.transform, false);
            Stretch(tx.rectTransform);
            tx.text = label;
            tx.fontSize = 14;
            tx.fontStyle = FontStyles.Bold;
            tx.alignment = TextAlignmentOptions.Center;
            tx.color = new Color(0.04f, 0.06f, 0.08f, 1f);
            return btn;
        }

        private static Toggle AddToggle(Transform parent, string label, bool value)
        {
            var go = new GameObject("Toggle_" + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 24f;

            var hLayout = go.AddComponent<HorizontalLayoutGroup>();
            hLayout.spacing = 10f;
            hLayout.childAlignment = TextAnchor.MiddleLeft;
            hLayout.childForceExpandHeight = true;
            hLayout.childForceExpandWidth = false;
            hLayout.padding = new RectOffset(4, 4, 0, 0);

            var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(go.transform, false);
            var bgImg = bg.GetComponent<Image>();
            bgImg.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            bgImg.type = Image.Type.Sliced;
            bgImg.color = Tint;
            var bgLE = bg.AddComponent<LayoutElement>();
            bgLE.preferredWidth = 20f;
            bgLE.preferredHeight = 20f;

            var check = new GameObject("Checkmark", typeof(RectTransform), typeof(Image));
            check.transform.SetParent(bg.transform, false);
            Stretch(check.GetComponent<RectTransform>(), padding: 4f);
            var checkImg = check.GetComponent<Image>();
            checkImg.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/Knob.psd");
            checkImg.color = Cyan;

            var labelGO = new GameObject("Label", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            labelGO.transform.SetParent(go.transform, false);
            labelGO.text = label;
            labelGO.fontSize = 11;
            labelGO.alignment = TextAlignmentOptions.MidlineLeft;
            labelGO.color = Color.white;
            var labelLE = labelGO.gameObject.AddComponent<LayoutElement>();
            labelLE.flexibleWidth = 1f;

            var toggle = go.AddComponent<Toggle>();
            toggle.targetGraphic = bgImg;
            toggle.graphic = checkImg;
            toggle.isOn = value;
            return toggle;
        }

        private static TMP_InputField AddInputField(Transform parent, string value, string placeholder)
        {
            var go = new GameObject("InputField", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            img.type = Image.Type.Sliced;
            img.color = Tint;

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 36f;

            var textGO = new GameObject("Text", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            textGO.transform.SetParent(go.transform, false);
            Stretch(textGO.rectTransform, padding: 8f);
            textGO.text = value ?? string.Empty;
            textGO.fontSize = 12;
            textGO.alignment = TextAlignmentOptions.MidlineLeft;
            textGO.color = Color.white;
            textGO.enableWordWrapping = false;
            textGO.overflowMode = TextOverflowModes.Ellipsis;

            var placeholderGO = new GameObject("Placeholder", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            placeholderGO.transform.SetParent(go.transform, false);
            Stretch(placeholderGO.rectTransform, padding: 8f);
            placeholderGO.text = placeholder ?? string.Empty;
            placeholderGO.fontSize = 12;
            placeholderGO.alignment = TextAlignmentOptions.MidlineLeft;
            placeholderGO.color = new Color(1f, 1f, 1f, 0.35f);
            placeholderGO.fontStyle = FontStyles.Italic;

            var input = go.AddComponent<TMP_InputField>();
            input.targetGraphic = img;
            input.textViewport = go.GetComponent<RectTransform>();
            input.textComponent = textGO;
            input.placeholder = placeholderGO;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.contentType = TMP_InputField.ContentType.Standard;
            input.SetTextWithoutNotify(value ?? string.Empty);
            return input;
        }

        private static TextMeshProUGUI AddLabel(Transform parent, string text,
            bool smaller = false, bool dim = false)
        {
            var go = new GameObject("Label", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            go.transform.SetParent(parent, false);
            go.text = text;
            go.fontSize = smaller ? 9 : 12;
            go.alignment = TextAlignmentOptions.MidlineLeft;
            go.color = dim ? new Color(1f, 1f, 1f, 0.55f) : Color.white;
            var le = go.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = go.fontSize + 4f;
            return go;
        }

        private static TextMeshProUGUI AddBody(Transform parent, string text)
        {
            var go = new GameObject("Body", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            go.transform.SetParent(parent, false);
            go.text = text;
            go.fontSize = 12;
            go.alignment = TextAlignmentOptions.MidlineLeft;
            go.color = Color.white;
            go.richText = true;
            var le = go.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 20f;
            return go;
        }

        private static void TryAddOVRRaycaster(GameObject canvasGo)
        {
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("OVRRaycaster");
                if (type == null) continue;
                canvasGo.AddComponent(type);
                return;
            }
            Debug.LogWarning($"[{nameof(AppMenu)}] OVRRaycaster not found — the menu Canvas will " +
                             "fall back to the plain GraphicRaycaster (only screen pointer).");
        }

        private static string Escape(string s) =>
            string.IsNullOrEmpty(s) ? "—" : s.Replace("<", "&lt;").Replace(">", "&gt;");

        private static string TrimText(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");
    }
}
