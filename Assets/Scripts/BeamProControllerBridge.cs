using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Bridge between the Beam Pro / XREAL virtual-controller UI and scene managers.
/// Attach this to a GameObject inside the XREALVirtualController_custom prefab or
/// to a scene object referenced by the controller buttons.
///
/// Button OnClick examples:
/// - BeamProControllerBridge.ConnectToServer()
/// - BeamProControllerBridge.StartQrScan()
/// - BeamProControllerBridge.StopQrScan()
/// - BeamProControllerBridge.PlaceDetector()
/// - BeamProControllerBridge.CancelPlace()
/// </summary>
public class BeamProControllerBridge : MonoBehaviour
{
    private const string WaitingForDataMessage = "Waiting for radiation data...";
    private const float WorkflowRefreshIntervalSeconds = 0.10f;
    private const float ActionStatusLifetimeSeconds = 4f;

    [Header("Scene Managers")]
    [SerializeField] private DetectorWorldMarkerManager markerManager;
    [SerializeField] private RoomCoordinateSystem roomCoordinateSystem;
    [SerializeField] private QRScanner qrScanner;
    [SerializeField] private RadiationReceiver radiationReceiver;
    [SerializeField] private RadiationSourceEstimator radiationSourceEstimator;

    [SerializeField] private XREALCaptureManager captureManager;
    [Header("Controller UI")]
    [Tooltip("Optional Beam Pro-side IP input field. If assigned, ConnectToServer() uses this value.")]
    [SerializeField] private TMP_InputField controllerIpInputField;

    [Tooltip("Optional Beam Pro-side status text.")]
    [SerializeField] private TMP_Text controllerStatusText;

    [Tooltip("Beam Pro-side radiation data text.")]
    [SerializeField] private TMP_Text controllerRadiationDisplayText;
    [Tooltip("Beam Pro-side capture status text.")]
    [SerializeField] private TMP_Text controllerCaptureStatusText;

    [Tooltip("Text child of the Start/Stop Record button.")]
    [SerializeField] private TMP_Text controllerRecordButtonText;

    [Header("Workflow Controls")]
    [Tooltip("Optional. Auto-found by the QRScanButton name when empty.")]
    [SerializeField] private Button controllerQrScanButton;

    [Tooltip("Optional. Auto-found by the PlaceDetectorButton name when empty.")]
    [SerializeField] private Button controllerPlaceButton;

    [Tooltip("Optional. Auto-found by the CancelPlaceButton name when empty.")]
    [SerializeField] private Button controllerCancelButton;

    [SerializeField] private TMP_Text controllerQrScanButtonText;
    [SerializeField] private TMP_Text controllerPlaceButtonText;
    [SerializeField] private TMP_Text controllerCancelButtonText;

    [Header("Detector List Layout")]
    [SerializeField, Min(6f)] private float detectorListMinFontSize = 10f;
    [SerializeField, Min(10f)] private float detectorListMaxFontSize = 40f;
    [SerializeField, Min(0f)] private float detectorListHorizontalPadding = 20f;
    [SerializeField, Min(128f)] private float detectorListHeight = 320f;
    [SerializeField] private float detectorListCenterYOffset = 330f;

    [Header("Workflow Guide Layout")]
    [SerializeField, Min(8f)] private float workflowGuideMinFontSize = 24f;
    [SerializeField, Min(12f)] private float workflowGuideMaxFontSize = 44f;
    [SerializeField, Min(128f)] private float workflowGuideHeight = 220f;
    [SerializeField] private float workflowGuideCenterYOffset = 330f;
    [SerializeField, Min(6f)] private float workflowButtonMinFontSize = 10f;
    [SerializeField, Min(10f)] private float workflowButtonMaxFontSize = 20f;

    [Header("Behavior")]
    [SerializeField] private bool autoFindReferences = true;
    [SerializeField] private bool logActions = true;

    private Color latestServerStatusColor = Color.white;
    private string transientActionMessage = "";
    private Color transientActionColor = Color.white;
    private float transientActionExpiresAt = float.NegativeInfinity;
    private float nextWorkflowRefreshTime;
    private float nextReferenceResolveTime;
    private bool workflowUiDirty = true;
    private bool receivedRadiationThisConnection;
    private string lastRenderedWorkflowText = "";

    private void Awake()
    {
        ResolveReferences();
        ResolveControllerControls();
        ConfigureControllerDetectorList();
        ConfigureControllerWorkflowGuide();
        ConfigureWorkflowButtonLabels();
        SyncControllerIpField();
        RefreshWorkflowUi(true);
    }

    private void OnEnable()
    {
        ResolveReferences();
        ResolveControllerControls();
        ConfigureControllerDetectorList();
        ConfigureControllerWorkflowGuide();
        ConfigureWorkflowButtonLabels();
        RadiationReceiver.OnServerStatusChanged += HandleServerStatusChanged;
        RadiationReceiver.OnDisplayTextChanged += HandleDisplayTextChanged;
        RadiationReceiver.OnServerConnectionChanged += HandleServerConnectionChanged;
        RadiationReceiver.OnRadiationDataFreshnessChanged += HandleRadiationDataFreshnessChanged;
        RadiationReceiver.OnRadiationDataReceived += HandleRadiationDataReceived;
        RoomCoordinateSystem.RoomStatusChanged += HandleRoomStatusChanged;
        XREALCaptureManager.OnCaptureStateChanged += HandleCaptureStateChanged;
        RegisterControllerIpListener();
        SyncControllerIpField();
        SyncCurrentReceiverText();

        receivedRadiationThisConnection =
            radiationReceiver != null &&
            radiationReceiver.IsConnected &&
            radiationReceiver.HasFreshRadiationData;
        workflowUiDirty = true;
        nextWorkflowRefreshTime = 0f;
        nextReferenceResolveTime = 0f;
        RefreshWorkflowUi(true);
    }

    private void LateUpdate()
    {
        float now = Time.unscaledTime;
        if (now >= nextReferenceResolveTime)
        {
            nextReferenceResolveTime = now + 1f;
            ResolveReferences();
            ResolveControllerControls();
        }

        if (lastConfiguredScreenWidth != Screen.width ||
            lastConfiguredScreenHeight != Screen.height ||
            lastConfiguredSafeArea != Screen.safeArea)
        {
            ConfigureControllerDetectorList();
            ConfigureControllerWorkflowGuide();
            ConfigureWorkflowButtonLabels();
        }

        if (workflowUiDirty || now >= nextWorkflowRefreshTime)
            RefreshWorkflowUi(false);
    }

    private void OnDisable()
    {
        RadiationReceiver.OnServerStatusChanged -= HandleServerStatusChanged;
        RadiationReceiver.OnDisplayTextChanged -= HandleDisplayTextChanged;
        RadiationReceiver.OnServerConnectionChanged -= HandleServerConnectionChanged;
        RadiationReceiver.OnRadiationDataFreshnessChanged -= HandleRadiationDataFreshnessChanged;
        RadiationReceiver.OnRadiationDataReceived -= HandleRadiationDataReceived;
        RoomCoordinateSystem.RoomStatusChanged -= HandleRoomStatusChanged;
        XREALCaptureManager.OnCaptureStateChanged -= HandleCaptureStateChanged;
        if (controllerIpInputField != null)
            controllerIpInputField.onEndEdit.RemoveListener(HandleControllerIpEndEdit);
    }

    private void HandleCaptureStateChanged(
        string message,
        bool recording)
    {
        if (controllerCaptureStatusText != null)
            controllerCaptureStatusText.text = message;

        if (controllerRecordButtonText != null)
        {
            controllerRecordButtonText.text =
                recording
                    ? "Stop Record"
                    : "Start Record";
        }

        workflowUiDirty = true;
    }
    /// <summary>
    /// Manually refresh manager references. Useful if objects are created after the controller prefab.
    /// </summary>
    public void RefreshReferences()
    {
        ResolveReferences();
        ResolveControllerControls();
        ConfigureControllerWorkflowGuide();
        ConfigureWorkflowButtonLabels();
        SyncControllerIpField();
        SyncCurrentReceiverText();
        RefreshWorkflowUi(true);
    }

    /// <summary>
    /// Opens the server IP input flow on the receiver and focuses the controller input field if assigned.
    /// </summary>
    public void OpenIpInput()
    {
        if (controllerIpInputField == null)
        {
            Warn("Controller IP input field is missing.");
            return;
        }

        controllerIpInputField.Select();
        controllerIpInputField.ActivateInputField();
        controllerIpInputField.caretPosition =
            controllerIpInputField.text.Length;

        Log("Controller IP input focused");
    }

    /// <summary>
    /// Connects to the server. If the controller field is empty, the receiver's
    /// saved/default server IP is used.
    /// </summary>
    public void ConnectToServer()
    {
        ResolveReferences();

        if (radiationReceiver == null)
        {
            Warn("RadiationReceiver not found.");
            return;
        }

        string ip = controllerIpInputField != null
            ? controllerIpInputField.text.Trim()
            : radiationReceiver.CurrentServerIp;

        if (string.IsNullOrWhiteSpace(ip))
            ip = radiationReceiver.CurrentServerIp;

        if (string.IsNullOrWhiteSpace(ip))
        {
            Warn("Server IP is empty.");
            return;
        }

        radiationReceiver.ConnectToServerWithIp(ip);

        Log($"Connecting with controller IP: {ip}");
    }

    public void Connect()
    {
        ConnectToServer();
    }

    private void HandleControllerIpEndEdit(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return;

        ConnectToServer();
    }

    private void HandleServerStatusChanged(
        string message,
        Color color)
    {
        ResolveReferences();

        // RadiationReceiver may Awake after this prefab. Its first status update
        // is a reliable point at which the PlayerPrefs-backed address is loaded.
        SyncControllerIpField();
        latestServerStatusColor = color;
        workflowUiDirty = true;
    }

    private void HandleDisplayTextChanged(string message)
    {
        workflowUiDirty = true;
        if (controllerRadiationDisplayText == null)
            return;

        ConfigureControllerDetectorList();
        controllerRadiationDisplayText.text = message;
    }

    private void HandleServerConnectionChanged(bool connected)
    {
        if (!connected)
            receivedRadiationThisConnection = false;
        else if (radiationReceiver != null && radiationReceiver.HasFreshRadiationData)
            receivedRadiationThisConnection = true;

        workflowUiDirty = true;
    }

    private void HandleRadiationDataFreshnessChanged(bool fresh)
    {
        if (fresh)
            receivedRadiationThisConnection = true;

        workflowUiDirty = true;
    }

    private void HandleRadiationDataReceived(Dictionary<string, float> _)
    {
        receivedRadiationThisConnection = true;
        workflowUiDirty = true;
    }

    private void SyncCurrentReceiverText()
    {
        if (radiationReceiver == null)
        {
            HandleDisplayTextChanged(WaitingForDataMessage);
            return;
        }

        HandleServerStatusChanged(
            radiationReceiver.CurrentStatusMessage,
            radiationReceiver.CurrentStatusColor
        );

        HandleDisplayTextChanged(
            radiationReceiver.CurrentDisplayMessage
        );
    }

    public void StartQrScan()
    {
        ResolveReferences();

        if ((roomCoordinateSystem != null && roomCoordinateSystem.HasPendingPlacement) ||
            (markerManager != null && markerManager.HasActivePlacement))
        {
            Warn("Place or cancel the current preview before scanning another QR");
            return;
        }

        if (captureManager != null &&
            captureManager.IsBusy)
        {
            Warn(
                "Cannot start QR scan while capture camera is busy."
            );
            return;
        }

        // The controller prefab has no separate Connect button. Starting a scan
        // must therefore also use the entered/saved IP when no connection exists.
        ConnectIfNeeded();

        if (qrScanner == null)
        {
            Warn("QRScanner not found. Cannot start QR scan.");
            return;
        }

        // This button starts the scanner directly, so suppress the receiver's
        // delayed post-connect auto-start for the same connection attempt.
        radiationReceiver?.CancelPendingQrScan();
        qrScanner.StartScanning();
        Log("StartQrScan");
    }

    public void StopQrScan()
    {
        ResolveReferences();

        bool stoppedPendingStart =
            radiationReceiver != null && radiationReceiver.CancelPendingQrScan();

        if (qrScanner == null)
        {
            if (!stoppedPendingStart)
                Warn("QRScanner not found. Cannot stop QR scan.");
            return;
        }

        qrScanner.StopScanning();
        Log("StopQrScan");
    }

    public void RestartQrScan()
    {
        StopQrScan();
        StartQrScan();
    }

    public void PlaceDetector()
    {
        ResolveReferences();

        if (roomCoordinateSystem != null &&
            roomCoordinateSystem.HasPendingPlacement)
        {
            if (roomCoordinateSystem.TryConfirmPendingPlacement(out string roomMessage))
            {
                ShowControllerActionStatus(roomMessage, Color.green);
                Log(roomMessage);
            }
            else
            {
                ShowControllerActionStatus(roomMessage, Color.yellow);
                Warn(roomMessage);
            }

            return;
        }

        if (markerManager == null)
        {
            Warn("DetectorWorldMarkerManager not found. Cannot place detector.");
            return;
        }

        if (markerManager.TryPlaceCurrentDetector(out string detectorMessage))
        {
            ShowControllerActionStatus(detectorMessage, Color.green);
            Log(detectorMessage);
        }
        else
        {
            ShowControllerActionStatus(detectorMessage, Color.yellow);
            Warn(detectorMessage);
        }
    }

    public void PlaceCurrentDetector()
    {
        PlaceDetector();
    }

    public void CancelPlace()
    {
        ResolveReferences();

        bool qrCameraWasActive = qrScanner != null && qrScanner.IsScanActive;
        bool delayedScanWasPending =
            radiationReceiver != null && radiationReceiver.IsQrScanPending;
        bool scanWasActive = qrCameraWasActive || delayedScanWasPending;
        bool roomPlacementWasActive =
            roomCoordinateSystem != null && roomCoordinateSystem.HasPendingPlacement;
        bool placementWasActive = roomPlacementWasActive ||
                                  (markerManager != null && markerManager.HasActivePlacement);

        if (delayedScanWasPending)
            radiationReceiver.CancelPendingQrScan();

        if (qrCameraWasActive)
            qrScanner.StopScanning();

        if (scanWasActive && !placementWasActive)
        {
            ShowControllerActionStatus("QR scan cancelled", Color.white);
            Log("QR scan cancelled");
            return;
        }

        // ROOM_ORIGIN owns Cancel while its transaction is active. Returning here
        // is essential: cancellation must never fall through and delete the most
        // recently committed detector.
        if (roomPlacementWasActive && roomCoordinateSystem != null)
        {
            if (roomCoordinateSystem.TryCancelPendingPlacement(out string roomMessage))
            {
                ShowControllerActionStatus(roomMessage, Color.white);
                Log(roomMessage);
            }
            else
            {
                ShowControllerActionStatus(roomMessage, Color.yellow);
                Warn(roomMessage);
            }

            return;
        }

        if (markerManager == null)
        {
            Warn("DetectorWorldMarkerManager not found. Cannot cancel placement.");
            return;
        }

        if (markerManager.TryCancelCurrentDetector(out string resultMessage))
        {
            ShowControllerActionStatus(resultMessage, Color.white);
            Log(resultMessage);
        }
        else
        {
            ShowControllerActionStatus(resultMessage, Color.yellow);
            Warn(resultMessage);
        }
    }

    public void CancelPlacement()
    {
        CancelPlace();
    }

    public void ClearSavedMarkers()
    {
        ResolveReferences();

        if (markerManager == null)
        {
            Warn("DetectorWorldMarkerManager not found. Cannot clear markers.");
            return;
        }

        markerManager.ClearSavedMarkers();
        Log("ClearSavedMarkers");
    }

    public void PrintSavedCoordinates()
    {
        ResolveReferences();

        if (markerManager == null)
        {
            Warn("DetectorWorldMarkerManager not found. Cannot print coordinates.");
            return;
        }

        markerManager.PrintSavedCoordinatesToLog();
        Log("PrintSavedCoordinates");
    }

    private void RegisterControllerIpListener()
    {
        if (controllerIpInputField == null)
            return;

        // Remove first so re-enabling the controller cannot register duplicates.
        controllerIpInputField.onEndEdit.RemoveListener(HandleControllerIpEndEdit);
        controllerIpInputField.onEndEdit.AddListener(HandleControllerIpEndEdit);
    }

    private void SyncControllerIpField()
    {
        if (controllerIpInputField == null || radiationReceiver == null)
            return;

        // Do not overwrite text while the user is editing it, but always replace
        // serialized prefab defaults with the last address saved by the receiver.
        if (!controllerIpInputField.isFocused)
            controllerIpInputField.SetTextWithoutNotify(radiationReceiver.CurrentServerIp);
    }

    private void ConnectIfNeeded()
    {
        if (radiationReceiver == null ||
            radiationReceiver.IsConnected ||
            radiationReceiver.IsConnecting)
        {
            return;
        }

        string ip = controllerIpInputField != null
            ? controllerIpInputField.text.Trim()
            : radiationReceiver.CurrentServerIp;

        if (!string.IsNullOrWhiteSpace(ip))
            radiationReceiver.ConnectToServerWithIp(ip);
    }

    private void ResolveReferences()
    {
        if (!autoFindReferences)
            return;

        if (markerManager == null)
            markerManager = UnityEngine.Object.FindFirstObjectByType<DetectorWorldMarkerManager>();

        if (roomCoordinateSystem == null)
            roomCoordinateSystem = UnityEngine.Object.FindFirstObjectByType<RoomCoordinateSystem>();

        if (qrScanner == null)
            qrScanner = UnityEngine.Object.FindFirstObjectByType<QRScanner>();

        if (radiationReceiver == null)
            radiationReceiver = UnityEngine.Object.FindFirstObjectByType<RadiationReceiver>();

        if (radiationSourceEstimator == null)
            radiationSourceEstimator = UnityEngine.Object.FindFirstObjectByType<RadiationSourceEstimator>();

        if (captureManager == null)
        {
            captureManager = UnityEngine.Object.FindFirstObjectByType<XREALCaptureManager>();
        }
    }

    private void ResolveControllerControls()
    {
        Transform searchRoot = transform.root;
        if (searchRoot == null)
            return;

        if (controllerQrScanButton == null)
            controllerQrScanButton = FindButtonByName(searchRoot, "QRScanButton");

        if (controllerPlaceButton == null)
            controllerPlaceButton = FindButtonByName(searchRoot, "PlaceDetectorButton");

        if (controllerCancelButton == null)
            controllerCancelButton = FindButtonByName(searchRoot, "CancelPlaceButton");

        if (controllerQrScanButtonText == null && controllerQrScanButton != null)
            controllerQrScanButtonText = controllerQrScanButton.GetComponentInChildren<TMP_Text>(true);

        if (controllerPlaceButtonText == null && controllerPlaceButton != null)
            controllerPlaceButtonText = controllerPlaceButton.GetComponentInChildren<TMP_Text>(true);

        if (controllerCancelButtonText == null && controllerCancelButton != null)
            controllerCancelButtonText = controllerCancelButton.GetComponentInChildren<TMP_Text>(true);
    }

    private static Button FindButtonByName(Transform root, string buttonName)
    {
        Button[] buttons = root.GetComponentsInChildren<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            Button candidate = buttons[i];
            if (candidate != null &&
                string.Equals(candidate.name, buttonName, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private void HandleRoomStatusChanged(string message, Color color)
    {
        ShowControllerActionStatus(message, color);
    }

    private int lastConfiguredScreenWidth = -1;
    private int lastConfiguredScreenHeight = -1;
    private Rect lastConfiguredSafeArea = new Rect(-1f, -1f, -1f, -1f);

    private void ConfigureControllerDetectorList()
    {
        if (controllerRadiationDisplayText == null)
            return;

        float minFontSize = Mathf.Max(6f, detectorListMinFontSize);
        float maxFontSize = Mathf.Max(minFontSize, detectorListMaxFontSize);

        controllerRadiationDisplayText.enableAutoSizing = true;
        controllerRadiationDisplayText.fontSizeMin = minFontSize;
        controllerRadiationDisplayText.fontSizeMax = maxFontSize;
        controllerRadiationDisplayText.fontSize = maxFontSize;
        controllerRadiationDisplayText.alignment = TextAlignmentOptions.TopLeft;
        controllerRadiationDisplayText.textWrappingMode = TextWrappingModes.NoWrap;
        controllerRadiationDisplayText.overflowMode = TextOverflowModes.Overflow;
        controllerRadiationDisplayText.richText = false;
        controllerRadiationDisplayText.raycastTarget = false;
        controllerRadiationDisplayText.margin = new Vector4(8f, 8f, 8f, 8f);

        RectTransform rect = controllerRadiationDisplayText.rectTransform;
        Rect safeArea = Screen.safeArea;
        float safeLeft = 0f;
        float safeRight = 1f;

        if (Screen.width > 0 && safeArea.width > 0f)
        {
            safeLeft = Mathf.Clamp01(safeArea.xMin / Screen.width);
            safeRight = Mathf.Clamp01(safeArea.xMax / Screen.width);
        }

        if (safeRight <= safeLeft)
        {
            safeLeft = 0f;
            safeRight = 1f;
        }

        float safeMidpoint = (safeLeft + safeRight) * 0.5f;
        rect.anchorMin = new Vector2(safeLeft, 0.5f);
        rect.anchorMax = new Vector2(safeMidpoint, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, detectorListCenterYOffset);
        rect.sizeDelta = new Vector2(
            -2f * Mathf.Max(0f, detectorListHorizontalPadding),
            Mathf.Max(128f, detectorListHeight));

        lastConfiguredScreenWidth = Screen.width;
        lastConfiguredScreenHeight = Screen.height;
        lastConfiguredSafeArea = safeArea;
    }

    private void ConfigureControllerWorkflowGuide()
    {
        if (controllerStatusText == null)
            return;

        float minFontSize = Mathf.Max(8f, workflowGuideMinFontSize);
        float maxFontSize = Mathf.Max(minFontSize, workflowGuideMaxFontSize);

        controllerStatusText.enableAutoSizing = true;
        controllerStatusText.fontSizeMin = minFontSize;
        controllerStatusText.fontSizeMax = maxFontSize;
        controllerStatusText.fontSize = maxFontSize;
        controllerStatusText.alignment = TextAlignmentOptions.MidlineLeft;
        controllerStatusText.textWrappingMode = TextWrappingModes.Normal;
        controllerStatusText.overflowMode = TextOverflowModes.Ellipsis;
        controllerStatusText.richText = false;
        controllerStatusText.raycastTarget = false;
        controllerStatusText.margin = new Vector4(12f, 8f, 12f, 8f);

        Rect safeArea = Screen.safeArea;
        float safeLeft = 0f;
        float safeRight = 1f;
        if (Screen.width > 0 && safeArea.width > 0f)
        {
            safeLeft = Mathf.Clamp01(safeArea.xMin / Screen.width);
            safeRight = Mathf.Clamp01(safeArea.xMax / Screen.width);
        }

        if (safeRight <= safeLeft)
        {
            safeLeft = 0f;
            safeRight = 1f;
        }

        float safeMidpoint = (safeLeft + safeRight) * 0.5f;
        RectTransform rect = controllerStatusText.rectTransform;
        rect.anchorMin = new Vector2(safeMidpoint, 0.5f);
        rect.anchorMax = new Vector2(safeRight, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, workflowGuideCenterYOffset);
        rect.sizeDelta = new Vector2(
            -2f * Mathf.Max(0f, detectorListHorizontalPadding),
            Mathf.Max(128f, workflowGuideHeight));

        lastConfiguredScreenWidth = Screen.width;
        lastConfiguredScreenHeight = Screen.height;
        lastConfiguredSafeArea = safeArea;
    }

    private void ConfigureWorkflowButtonLabels()
    {
        ConfigureWorkflowButtonText(controllerQrScanButtonText);
        ConfigureWorkflowButtonText(controllerPlaceButtonText);
        ConfigureWorkflowButtonText(controllerCancelButtonText);

        if (controllerIpInputField == null)
            return;

        if (controllerIpInputField.placeholder is TMP_Text placeholderText)
        {
            placeholderText.text = "Server IP";
            ConfigureSingleLineText(
                placeholderText,
                workflowButtonMinFontSize,
                workflowButtonMaxFontSize);
        }

        if (controllerIpInputField.textComponent != null)
        {
            ConfigureSingleLineText(
                controllerIpInputField.textComponent,
                workflowButtonMinFontSize,
                workflowButtonMaxFontSize);
        }
    }

    private void ConfigureWorkflowButtonText(TMP_Text label)
    {
        if (label == null)
            return;

        ConfigureSingleLineText(
            label,
            workflowButtonMinFontSize,
            workflowButtonMaxFontSize);
        label.alignment = TextAlignmentOptions.Center;
        label.margin = new Vector4(3f, 1f, 3f, 1f);
    }

    private static void ConfigureSingleLineText(
        TMP_Text label,
        float requestedMinFontSize,
        float requestedMaxFontSize)
    {
        float minFontSize = Mathf.Max(6f, requestedMinFontSize);
        float maxFontSize = Mathf.Max(minFontSize, requestedMaxFontSize);
        label.enableAutoSizing = true;
        label.fontSizeMin = minFontSize;
        label.fontSizeMax = maxFontSize;
        label.fontSize = maxFontSize;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.richText = false;
    }

    private void RefreshWorkflowUi(bool force)
    {
        float now = Time.unscaledTime;
        nextWorkflowRefreshTime = now + WorkflowRefreshIntervalSeconds;

        if (!string.IsNullOrEmpty(transientActionMessage) &&
            now >= transientActionExpiresAt)
        {
            transientActionMessage = "";
        }

        WorkflowPresentation presentation = BuildWorkflowPresentation();

        bool showGuide = !string.IsNullOrWhiteSpace(presentation.guideText);
        if (controllerStatusText != null &&
            controllerStatusText.gameObject.activeSelf != showGuide)
        {
            controllerStatusText.gameObject.SetActive(showGuide);
        }

        if (controllerStatusText != null &&
            (force || !string.Equals(
                lastRenderedWorkflowText,
                presentation.guideText,
                StringComparison.Ordinal)))
        {
            controllerStatusText.text = presentation.guideText;
            lastRenderedWorkflowText = presentation.guideText;
        }

        if (controllerStatusText != null)
            controllerStatusText.color = presentation.guideColor;

        ApplyWorkflowButton(
            controllerQrScanButton,
            controllerQrScanButtonText,
            presentation.scanLabel,
            presentation.scanEnabled);
        ApplyWorkflowButton(
            controllerPlaceButton,
            controllerPlaceButtonText,
            presentation.placeLabel,
            presentation.placeEnabled);
        ApplyWorkflowButton(
            controllerCancelButton,
            controllerCancelButtonText,
            presentation.cancelLabel,
            presentation.cancelEnabled);

        workflowUiDirty = false;
    }

    private static void ApplyWorkflowButton(
        Button button,
        TMP_Text label,
        string labelText,
        bool interactable)
    {
        if (label != null && !string.Equals(label.text, labelText, StringComparison.Ordinal))
            label.text = labelText;

        if (button != null && button.interactable != interactable)
            button.interactable = interactable;
    }

    private WorkflowPresentation BuildWorkflowPresentation()
    {
        bool serverAvailable = radiationReceiver != null;
        bool connected = serverAvailable && radiationReceiver.IsConnected;
        bool connecting = serverAvailable && radiationReceiver.IsConnecting;
        bool freshData = connected && radiationReceiver.HasFreshRadiationData;
        bool roomPending =
            roomCoordinateSystem != null && roomCoordinateSystem.HasPendingPlacement;
        bool roomPoseValid =
            roomPending && roomCoordinateSystem.HasValidPendingPose;
        bool roomCalibrated =
            roomCoordinateSystem != null && roomCoordinateSystem.IsCalibrated;
        bool detectorPending =
            markerManager != null && markerManager.HasActivePlacement;
        bool detectorPoseValid =
            detectorPending && markerManager.HasValidActivePlacementPose;
        bool scanActive =
            (qrScanner != null && qrScanner.IsScanActive) ||
            (radiationReceiver != null && radiationReceiver.IsQrScanPending);
        bool captureBusy = captureManager != null && captureManager.IsBusy;

        int placedCount = markerManager != null
            ? markerManager.CurrentRoomPlacedDetectorCount
            : 0;
        string ignoredUndoDetectorId = "";
        bool undoWaitingForRestore = false;
        bool hasUndoDetector =
            markerManager != null &&
            markerManager.TryPeekLastPlacedDetector(
                out ignoredUndoDetectorId,
                out undoWaitingForRestore);

        string requiredAction = "";
        string requiredInstruction = "";
        Color guideColor = new Color(0.72f, 0.90f, 1f, 1f);

        bool hasBlockingActionStatus =
            !string.IsNullOrWhiteSpace(transientActionMessage) &&
            Time.unscaledTime < transientActionExpiresAt &&
            transientActionColor != Color.green &&
            transientActionColor != Color.white;

        if (hasBlockingActionStatus)
        {
            requiredAction = "ACTION REQUIRED";
            requiredInstruction = CompactUiMessage(transientActionMessage, 88);
            guideColor = transientActionColor;
        }
        else if (!serverAvailable)
        {
            requiredAction = "SERVER SETUP REQUIRED";
            requiredInstruction = "Restart the app or check the RadiationReceiver setup.";
            guideColor = new Color(1f, 0.55f, 0.50f, 1f);
        }
        else if (scanActive)
        {
            requiredAction = roomCalibrated
                ? "SCAN DETECTOR QR"
                : "SCAN ROOM QR";
            requiredInstruction = roomCalibrated
                ? "Point the Beam Pro camera at the detector QR."
                : "Point the Beam Pro camera at the room reference QR.";
        }
        else if (roomPending)
        {
            string roomId = SafeUiValue(roomCoordinateSystem.PendingRoomId, "ROOM QR");
            if (roomPoseValid)
            {
                requiredAction = "TAP PLACE ROOM";
                requiredInstruction = $"{roomId} is aligned on the vertical wall.";
                guideColor = new Color(0.55f, 1f, 0.68f, 1f);
            }
            else
            {
                requiredAction = "AIM AT THE ROOM QR";
                requiredInstruction =
                    $"Center the glasses on {roomId} attached to a vertical wall.";
                guideColor = new Color(1f, 0.86f, 0.38f, 1f);
            }
        }
        else if (detectorPending)
        {
            string detectorId = SafeUiValue(
                markerManager.ActivePlacementDetectorId,
                "DETECTOR");
            if (detectorPoseValid)
            {
                requiredAction = "TAP PLACE DETECTOR";
                requiredInstruction = $"{detectorId} is aligned at the gray preview.";
                guideColor = new Color(0.55f, 1f, 0.68f, 1f);
            }
            else
            {
                requiredAction = "AIM AT THE DETECTOR POSITION";
                requiredInstruction =
                    $"Keep aiming until the gray preview for {detectorId} appears.";
                guideColor = new Color(1f, 0.86f, 0.38f, 1f);
            }
        }
        else if (connecting)
        {
            requiredAction = "WAIT FOR SERVER CONNECTION";
            requiredInstruction =
                $"Connecting to {SafeUiValue(radiationReceiver.CurrentServerIp, "server")}.";
            guideColor = new Color(1f, 0.86f, 0.38f, 1f);
        }
        else if (!connected)
        {
            requiredAction = "CONNECT SERVER";
            requiredInstruction = "Check Server IP, then tap CONNECT & SCAN.";
            guideColor = latestServerStatusColor.a > 0.01f
                ? latestServerStatusColor
                : new Color(1f, 0.55f, 0.50f, 1f);
        }
        else if (qrScanner == null ||
                 markerManager == null ||
                 roomCoordinateSystem == null)
        {
            requiredAction = "APP SETUP NOT READY";
            requiredInstruction = "Restart the app before scanning a QR.";
            guideColor = new Color(1f, 0.55f, 0.50f, 1f);
        }
        else if (captureBusy)
        {
            requiredAction = "FINISH CAMERA CAPTURE";
            requiredInstruction = "QR scanning is locked while the capture camera is busy.";
            guideColor = new Color(1f, 0.86f, 0.38f, 1f);
        }
        else if (!roomCalibrated)
        {
            string lastRoomId = SafeUiValue(roomCoordinateSystem.LastRoomId, "");
            requiredAction = "SCAN ROOM QR";
            requiredInstruction = string.IsNullOrEmpty(lastRoomId)
                ? "Tap SCAN ROOM QR."
                : $"Tap SCAN ROOM QR and scan {lastRoomId}.";
        }
        else if (placedCount == 0)
        {
            requiredAction = "ADD FIRST DETECTOR";
            requiredInstruction = "Tap ADD DETECTOR and scan its QR.";
        }
        else if (placedCount < 4)
        {
            int remaining = 4 - placedCount;
            requiredAction = remaining == 1
                ? "ADD 1 MORE DETECTOR"
                : $"ADD {remaining} MORE DETECTORS";
            requiredInstruction =
                "Four placed detectors are required for source estimation.";
        }
        else if (!freshData)
        {
            requiredAction = receivedRadiationThisConnection
                ? "RESTORE THE CPS STREAM"
                : "START THE CPS STREAM";
            requiredInstruction = receivedRadiationThisConnection
                ? "No fresh CPS is arriving from the server."
                : "The server must send a CPS snapshot before measurement starts.";
            guideColor = new Color(1f, 0.78f, 0.34f, 1f);
        }
        else if (radiationSourceEstimator != null)
        {
            switch (radiationSourceEstimator.State)
            {
                case RadiationSourceEstimator.EstimatorState.WaitingForDetectors:
                    requiredAction = "CHECK LIVE CPS FOR ALL DETECTORS";
                    requiredInstruction =
                        "Every placed detector ID must be present in the server data.";
                    guideColor = new Color(1f, 0.86f, 0.38f, 1f);
                    break;
                case RadiationSourceEstimator.EstimatorState.InsufficientGeometry:
                    requiredAction = "REPOSITION DETECTORS";
                    requiredInstruction =
                        "Place detectors in a wide square instead of along one line.";
                    guideColor = new Color(1f, 0.86f, 0.38f, 1f);
                    break;
                case RadiationSourceEstimator.EstimatorState.OutOfSearchBounds:
                    requiredAction = "CHECK ROOM AND DETECTOR POSITIONS";
                    requiredInstruction =
                        "The source estimate is outside the configured search area.";
                    guideColor = new Color(1f, 0.86f, 0.38f, 1f);
                    break;
                case RadiationSourceEstimator.EstimatorState.PoorFit:
                    requiredAction = "CHECK DETECTOR POSITIONS AND CPS";
                    requiredInstruction =
                        "The current readings do not produce a reliable source estimate.";
                    guideColor = new Color(1f, 0.86f, 0.38f, 1f);
                    break;
            }
        }

        string guideText = string.IsNullOrWhiteSpace(requiredAction)
            ? ""
            : string.IsNullOrWhiteSpace(requiredInstruction)
                ? requiredAction
                : requiredAction + "\n" + requiredInstruction;

        return new WorkflowPresentation
        {
            guideText = guideText,
            guideColor = guideColor,
            scanLabel = scanActive
                ? "Scanning..."
                : !connected
                    ? "Connect & Scan"
                    : !roomCalibrated
                        ? "Scan Room QR"
                        : "Add Detector",
            placeLabel = roomPending ? "Place Room" : "Place Detector",
            cancelLabel = scanActive ? "Cancel Scan" : "Cancel Place",
            scanEnabled =
                radiationReceiver != null && qrScanner != null &&
                !captureBusy && !scanActive && !roomPending && !detectorPending,
            placeEnabled =
                (roomPending && roomPoseValid) ||
                (detectorPending && detectorPoseValid),
            cancelEnabled =
                scanActive || roomPending || detectorPending ||
                (connected && hasUndoDetector && !undoWaitingForRestore)
        };
    }

    private static string SafeUiValue(string value, string fallback)
    {
        string safe = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
        return CompactUiMessage(safe, 32);
    }

    private static string CompactUiMessage(string message, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "";

        string compact = message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (compact.Length <= maximumLength)
            return compact;

        return compact.Substring(0, Mathf.Max(1, maximumLength - 3)) + "...";
    }

    private struct WorkflowPresentation
    {
        public string guideText;
        public Color guideColor;
        public string scanLabel;
        public string placeLabel;
        public string cancelLabel;
        public bool scanEnabled;
        public bool placeEnabled;
        public bool cancelEnabled;
    }

    private void ShowControllerActionStatus(string message, Color color)
    {
        transientActionMessage = message ?? "";
        transientActionColor = color;
        transientActionExpiresAt =
            Time.unscaledTime + ActionStatusLifetimeSeconds;
        workflowUiDirty = true;
        RefreshWorkflowUi(true);
    }

    private void Log(string message)
    {
        if (logActions)
            Debug.Log($"[BeamProControllerBridge] {message}");
    }

    private void Warn(string message)
    {
        ShowControllerActionStatus(message, Color.yellow);
        Debug.LogWarning($"[BeamProControllerBridge] {message}");
    }

    public void ToggleVideoRecording()
    {
        ResolveReferences();

        if (captureManager == null)
        {
            Warn("XREALCaptureManager not found.");
            return;
        }

        captureManager.ToggleVideoRecording();
        Log("ToggleVideoRecording");
    }

    public void TakeJpegPhoto()
    {
        ResolveReferences();

        if (captureManager == null)
        {
            Warn("XREALCaptureManager not found.");
            return;
        }

        captureManager.TakeJpegPhoto();
        Log("TakeJpegPhoto");
    }
}
