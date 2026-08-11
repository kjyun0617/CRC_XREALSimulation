using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Head-locked AR glasses HUD for server status, detector readings, gaze highlighting,
/// and four-direction off-screen detector indicators. It builds its own world-space
/// canvas so no Inspector UI references are required.
/// </summary>
[DisallowMultipleComponent]
public class ARDetectorHud : MonoBehaviour
{
    private const float ReferenceWidth = 1920f;
    private const float ReferenceHeight = 1080f;

    [Header("References")]
    [SerializeField] private DetectorWorldMarkerManager markerManager;
    [SerializeField] private RadiationReceiver radiationReceiver;
    [SerializeField] private Camera targetCamera;

    [Header("XREAL Viewport Layout")]
    [Tooltip("Distance of the head-locked canvas from the center eye in meters.")]
    [SerializeField, Min(0.2f)] private float canvasDistanceMeters = 1.5f;

    [Tooltip("Normalized bottom-right anchor. 0.94/0.06 keeps text inside the 46-52 degree XREAL display edge.")]
    [SerializeField] private Vector2 hudViewportAnchor = new Vector2(0.94f, 0.06f);

    [SerializeField] private Vector2 hudPixelSize = new Vector2(720f, 430f);
    [SerializeField, Min(10f)] private float hudFontSize = 27f;
    [SerializeField] private string radiationUnit = "uSv/h";

    [Header("Gaze Selection")]
    [Tooltip("A placed sphere is selected when it is this close to the view-center ray.")]
    [SerializeField, Range(1f, 12f)] private float gazeSelectionAngleDegrees = 4f;

    [Header("Off-screen Indicators")]
    [SerializeField, Range(0f, 0.15f)] private float screenEdgeMargin = 0.05f;
    [SerializeField, Min(10f)] private float indicatorFontSize = 28f;

    private readonly Dictionary<string, float> latestDeviceData =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> sortedDeviceIds = new List<string>();
    private readonly List<DetectorWorldMarkerManager.DetectorHudMarkerState> markerStates =
        new List<DetectorWorldMarkerManager.DetectorHudMarkerState>();
    private readonly List<OffscreenState> offscreenStates = new List<OffscreenState>();
    private readonly List<TMP_Text> indicatorPool = new List<TMP_Text>();
    private readonly StringBuilder textBuilder = new StringBuilder(512);

    private GameObject canvasObject;
    private RectTransform canvasRect;
    private Canvas canvas;
    private TMP_Text hudText;
    private RectTransform indicatorLayer;
    private bool eventsSubscribed;
    private string serverStatus = "Disconnected";
    private Color serverStatusColor = Color.red;

    public void Initialize(DetectorWorldMarkerManager manager, Camera camera)
    {
        if (manager != null)
            markerManager = manager;

        if (camera != null)
            targetCamera = camera;

        EnsureReferences();
        PullCurrentReceiverState();
        EnsureCanvas();
    }

    private void Awake()
    {
        EnsureReferences();
    }

    private void OnEnable()
    {
        SubscribeEvents();
        PullCurrentReceiverState();
    }

    private void Start()
    {
        EnsureReferences();
        PullCurrentReceiverState();
        EnsureCanvas();
    }

    private void OnDisable()
    {
        UnsubscribeEvents();
    }

    private void OnDestroy()
    {
        UnsubscribeEvents();

        if (canvasObject != null)
            Destroy(canvasObject);
    }

    private void LateUpdate()
    {
        EnsureReferences();
        EnsureCanvas();

        if (targetCamera == null || canvasRect == null || markerManager == null)
            return;

        UpdateCanvasPoseFromRuntimeProjection();

        markerManager.FillHudMarkerStates(markerStates);
        int selectedMarkerIndex = FindGazeSelectedMarker();

        UpdateHudText(selectedMarkerIndex);
        UpdateOffscreenIndicators();
    }

    private void EnsureReferences()
    {
        if (markerManager == null)
            markerManager = GetComponent<DetectorWorldMarkerManager>();

        if (markerManager == null)
            markerManager = FindFirstObjectByType<DetectorWorldMarkerManager>();

        if (radiationReceiver == null)
        {
            radiationReceiver = FindFirstObjectByType<RadiationReceiver>();
            if (radiationReceiver != null)
                PullCurrentReceiverState();
        }

        if (targetCamera == null)
            targetCamera = Camera.main;
    }

    private void SubscribeEvents()
    {
        if (eventsSubscribed)
            return;

        RadiationReceiver.OnServerStatusChanged += HandleServerStatusChanged;
        RadiationReceiver.OnRadiationDataReceived += HandleRadiationDataReceived;
        eventsSubscribed = true;
    }

    private void UnsubscribeEvents()
    {
        if (!eventsSubscribed)
            return;

        RadiationReceiver.OnServerStatusChanged -= HandleServerStatusChanged;
        RadiationReceiver.OnRadiationDataReceived -= HandleRadiationDataReceived;
        eventsSubscribed = false;
    }

    private void PullCurrentReceiverState()
    {
        if (radiationReceiver == null)
            return;

        serverStatus = string.IsNullOrWhiteSpace(radiationReceiver.CurrentStatusMessage)
            ? (radiationReceiver.IsConnected ? "Connected" : "Disconnected")
            : radiationReceiver.CurrentStatusMessage;
        serverStatusColor = radiationReceiver.CurrentStatusColor;

        latestDeviceData.Clear();
        foreach (var pair in radiationReceiver.LatestDeviceData)
            latestDeviceData[pair.Key.Trim()] = pair.Value;
    }

    private void HandleServerStatusChanged(string message, Color color)
    {
        serverStatus = string.IsNullOrWhiteSpace(message) ? "Disconnected" : message;
        serverStatusColor = color;

        if (message.IndexOf("Connecting", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("Disconnected", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            latestDeviceData.Clear();
        }
    }

    private void HandleRadiationDataReceived(Dictionary<string, float> data)
    {
        latestDeviceData.Clear();
        if (data == null)
            return;

        foreach (var pair in data)
        {
            string detectorId = string.IsNullOrWhiteSpace(pair.Key) ? "" : pair.Key.Trim();
            if (!string.IsNullOrEmpty(detectorId))
                latestDeviceData[detectorId] = pair.Value;
        }
    }

    private void EnsureCanvas()
    {
        if (canvasObject != null)
            return;

        canvasObject = new GameObject(
            "ARDetectorHUD_Runtime",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler));
        canvasObject.layer = 5;

        canvasRect = canvasObject.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(ReferenceWidth, ReferenceHeight);

        canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 100;
        canvas.worldCamera = targetCamera;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 10f;
        scaler.referencePixelsPerUnit = 100f;

        indicatorLayer = CreateFullScreenLayer(canvasRect);
        hudText = CreateHudText(canvasRect);
    }

    private RectTransform CreateFullScreenLayer(RectTransform parent)
    {
        GameObject layerObject = new GameObject("DetectorDirectionIndicators", typeof(RectTransform));
        layerObject.layer = 5;
        RectTransform rect = layerObject.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        return rect;
    }

    private TMP_Text CreateHudText(RectTransform parent)
    {
        GameObject textObject = new GameObject(
            "ServerAndDetectorStatusText",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TextMeshProUGUI));
        textObject.layer = 5;

        RectTransform rect = textObject.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = hudViewportAnchor;
        rect.anchorMax = hudViewportAnchor;
        rect.pivot = new Vector2(1f, 0f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = hudPixelSize;

        TextMeshProUGUI text = textObject.GetComponent<TextMeshProUGUI>();
        text.alignment = TextAlignmentOptions.BottomRight;
        text.fontSize = hudFontSize;
        text.color = Color.white;
        text.richText = true;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.raycastTarget = false;
        text.outlineColor = Color.black;
        text.outlineWidth = 0.25f;
        text.margin = new Vector4(8f, 8f, 8f, 8f);
        return text;
    }

    private void UpdateCanvasPoseFromRuntimeProjection()
    {
        float distance = Mathf.Max(0.2f, canvasDistanceMeters);
        Vector3 bottomLeft = targetCamera.ViewportToWorldPoint(new Vector3(0f, 0f, distance));
        Vector3 topLeft = targetCamera.ViewportToWorldPoint(new Vector3(0f, 1f, distance));
        Vector3 center = targetCamera.ViewportToWorldPoint(new Vector3(0.5f, 0.5f, distance));

        float visibleWorldHeight = Vector3.Distance(bottomLeft, topLeft);
        float uniformScale = visibleWorldHeight / ReferenceHeight;

        canvasRect.position = center;
        canvasRect.rotation = targetCamera.transform.rotation;
        canvasRect.localScale = Vector3.one * uniformScale;

        if (canvas.worldCamera != targetCamera)
            canvas.worldCamera = targetCamera;
    }

    private int FindGazeSelectedMarker()
    {
        int bestIndex = -1;
        float bestAngle = gazeSelectionAngleDegrees;
        Vector3 cameraPosition = targetCamera.transform.position;
        Vector3 cameraForward = targetCamera.transform.forward;

        for (int i = 0; i < markerStates.Count; i++)
        {
            Vector3 toMarker = markerStates[i].worldPosition - cameraPosition;
            if (Vector3.Dot(cameraForward, toMarker) <= 0f)
                continue;

            Vector3 viewport = targetCamera.WorldToViewportPoint(markerStates[i].worldPosition);
            if (viewport.z <= 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
                continue;

            float angle = Vector3.Angle(cameraForward, toMarker);
            if (angle <= bestAngle)
            {
                bestAngle = angle;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private void UpdateHudText(int selectedMarkerIndex)
    {
        if (hudText == null)
            return;

        string selectedId = selectedMarkerIndex >= 0
            ? markerStates[selectedMarkerIndex].detectorId
            : "";
        Color selectedColor = selectedMarkerIndex >= 0
            ? markerStates[selectedMarkerIndex].color
            : Color.white;

        textBuilder.Clear();
        textBuilder.Append("<color=#")
            .Append(ColorUtility.ToHtmlStringRGB(serverStatusColor))
            .Append("><b>SERVER</b>  ")
            .Append(EscapeRichText(serverStatus))
            .Append("</color>");

        sortedDeviceIds.Clear();
        foreach (string detectorId in latestDeviceData.Keys)
            sortedDeviceIds.Add(detectorId);
        sortedDeviceIds.Sort(StringComparer.OrdinalIgnoreCase);

        if (sortedDeviceIds.Count == 0)
        {
            textBuilder.Append("\n<color=#B8B8B8>Waiting for detector data...</color>");
        }
        else
        {
            for (int i = 0; i < sortedDeviceIds.Count; i++)
            {
                string detectorId = sortedDeviceIds[i];
                bool selected = string.Equals(detectorId, selectedId, StringComparison.OrdinalIgnoreCase);

                textBuilder.Append("\n");
                if (selected)
                {
                    textBuilder.Append("<color=#")
                        .Append(ColorUtility.ToHtmlStringRGB(selectedColor))
                        .Append("><b>> ");
                }
                else
                {
                    textBuilder.Append("<color=#E0E0E0>  ");
                }

                textBuilder.Append(EscapeRichText(detectorId))
                    .Append("   ")
                    .Append(latestDeviceData[detectorId].ToString("F3"));

                if (!string.IsNullOrWhiteSpace(radiationUnit))
                    textBuilder.Append(" ").Append(EscapeRichText(radiationUnit));

                textBuilder.Append(selected ? "</b></color>" : "</color>");
            }
        }

        // A placed marker can still be looked at before its serial appears in the latest server packet.
        if (!string.IsNullOrEmpty(selectedId) && !latestDeviceData.ContainsKey(selectedId))
        {
            float value = markerStates[selectedMarkerIndex].radiationValue;
            textBuilder.Append("\n<color=#")
                .Append(ColorUtility.ToHtmlStringRGB(selectedColor))
                .Append("><b>> ")
                .Append(EscapeRichText(selectedId))
                .Append(value >= 0f ? $"   {value:F3}" : "   --");

            if (!string.IsNullOrWhiteSpace(radiationUnit))
                textBuilder.Append(" ").Append(EscapeRichText(radiationUnit));

            textBuilder.Append("</b></color>");
        }

        hudText.text = textBuilder.ToString();
    }

    private void UpdateOffscreenIndicators()
    {
        offscreenStates.Clear();

        for (int i = 0; i < markerStates.Count; i++)
        {
            Vector3 viewport = targetCamera.WorldToViewportPoint(markerStates[i].worldPosition);
            bool inside = viewport.z > 0f &&
                          viewport.x >= screenEdgeMargin && viewport.x <= 1f - screenEdgeMargin &&
                          viewport.y >= screenEdgeMargin && viewport.y <= 1f - screenEdgeMargin;

            if (inside)
                continue;

            CardinalDirection direction = GetCardinalDirection(markerStates[i].worldPosition, viewport);
            offscreenStates.Add(new OffscreenState
            {
                marker = markerStates[i],
                viewport = viewport,
                direction = direction
            });
        }

        EnsureIndicatorPoolSize(offscreenStates.Count);

        int leftCount = 0;
        int rightCount = 0;
        int upCount = 0;
        int downCount = 0;

        for (int i = 0; i < indicatorPool.Count; i++)
        {
            bool active = i < offscreenStates.Count;
            indicatorPool[i].gameObject.SetActive(active);
            if (!active)
                continue;

            OffscreenState state = offscreenStates[i];
            int stackIndex;

            switch (state.direction)
            {
                case CardinalDirection.Left:
                    stackIndex = leftCount++;
                    break;
                case CardinalDirection.Right:
                    stackIndex = rightCount++;
                    break;
                case CardinalDirection.Up:
                    stackIndex = upCount++;
                    break;
                default:
                    stackIndex = downCount++;
                    break;
            }

            ConfigureIndicator(indicatorPool[i], state, stackIndex);
        }
    }

    private CardinalDirection GetCardinalDirection(Vector3 worldPosition, Vector3 viewport)
    {
        if (viewport.z > 0f)
        {
            float horizontalOverflow = Mathf.Abs(viewport.x - 0.5f) / 0.5f;
            float verticalOverflow = Mathf.Abs(viewport.y - 0.5f) / 0.5f;

            if (horizontalOverflow >= verticalOverflow)
                return viewport.x < 0.5f ? CardinalDirection.Left : CardinalDirection.Right;

            return viewport.y < 0.5f ? CardinalDirection.Down : CardinalDirection.Up;
        }

        Vector3 local = targetCamera.transform.InverseTransformPoint(worldPosition);
        float horizontalAngle = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
        float verticalAngle = Mathf.Atan2(local.y, Mathf.Max(0.0001f, Mathf.Abs(local.z))) * Mathf.Rad2Deg;

        if (Mathf.Abs(horizontalAngle) >= Mathf.Abs(verticalAngle))
            return horizontalAngle < 0f ? CardinalDirection.Left : CardinalDirection.Right;

        return verticalAngle < 0f ? CardinalDirection.Down : CardinalDirection.Up;
    }

    private void EnsureIndicatorPoolSize(int count)
    {
        while (indicatorPool.Count < count)
        {
            GameObject indicatorObject = new GameObject(
                $"DetectorDirection_{indicatorPool.Count}",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            indicatorObject.layer = 5;

            RectTransform rect = indicatorObject.GetComponent<RectTransform>();
            rect.SetParent(indicatorLayer, false);
            rect.sizeDelta = new Vector2(420f, 64f);

            TextMeshProUGUI text = indicatorObject.GetComponent<TextMeshProUGUI>();
            text.fontSize = indicatorFontSize;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            text.outlineColor = Color.black;
            text.outlineWidth = 0.25f;
            indicatorPool.Add(text);
        }
    }

    private void ConfigureIndicator(TMP_Text indicator, OffscreenState state, int stackIndex)
    {
        RectTransform rect = indicator.rectTransform;
        float stackOffset = AlternatingStackOffset(stackIndex, 0.055f);
        Vector2 anchor;

        switch (state.direction)
        {
            case CardinalDirection.Left:
                anchor = new Vector2(
                    screenEdgeMargin,
                    Mathf.Clamp(state.viewport.z > 0f ? state.viewport.y + stackOffset : 0.5f + stackOffset, 0.15f, 0.85f));
                rect.pivot = new Vector2(0f, 0.5f);
                indicator.alignment = TextAlignmentOptions.MidlineLeft;
                indicator.text = $"<  {state.marker.detectorId}";
                break;

            case CardinalDirection.Right:
                anchor = new Vector2(
                    1f - screenEdgeMargin,
                    Mathf.Clamp(state.viewport.z > 0f ? state.viewport.y + stackOffset : 0.5f + stackOffset, 0.38f, 0.85f));
                rect.pivot = new Vector2(1f, 0.5f);
                indicator.alignment = TextAlignmentOptions.MidlineRight;
                indicator.text = $"{state.marker.detectorId}  >";
                break;

            case CardinalDirection.Up:
                anchor = new Vector2(
                    Mathf.Clamp(state.viewport.z > 0f ? state.viewport.x + stackOffset : 0.5f + stackOffset, 0.15f, 0.85f),
                    1f - screenEdgeMargin);
                rect.pivot = new Vector2(0.5f, 1f);
                indicator.alignment = TextAlignmentOptions.Top;
                indicator.text = $"^  {state.marker.detectorId}";
                break;

            default:
                anchor = new Vector2(
                    Mathf.Clamp(state.viewport.z > 0f ? state.viewport.x + stackOffset : 0.5f + stackOffset, 0.15f, 0.62f),
                    screenEdgeMargin);
                rect.pivot = new Vector2(0.5f, 0f);
                indicator.alignment = TextAlignmentOptions.Bottom;
                indicator.text = $"v  {state.marker.detectorId}";
                break;
        }

        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.anchoredPosition = Vector2.zero;
        indicator.color = state.marker.color;
    }

    private float AlternatingStackOffset(int index, float step)
    {
        if (index <= 0)
            return 0f;

        int level = (index + 1) / 2;
        return level * step * (index % 2 == 1 ? 1f : -1f);
    }

    private string EscapeRichText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return value.Replace("<", "[").Replace(">", "]");
    }

    private enum CardinalDirection
    {
        Left,
        Right,
        Up,
        Down
    }

    private struct OffscreenState
    {
        public DetectorWorldMarkerManager.DetectorHudMarkerState marker;
        public Vector3 viewport;
        public CardinalDirection direction;
    }
}
