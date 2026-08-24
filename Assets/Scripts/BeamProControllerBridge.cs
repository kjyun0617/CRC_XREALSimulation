using TMPro;
using UnityEngine;

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

    [Header("Scene Managers")]
    [SerializeField] private DetectorWorldMarkerManager markerManager;
    [SerializeField] private QRScanner qrScanner;
    [SerializeField] private RadiationReceiver radiationReceiver;

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

    [Header("Detector List Layout")]
    [SerializeField, Min(6f)] private float detectorListMinFontSize = 10f;
    [SerializeField, Min(10f)] private float detectorListMaxFontSize = 40f;
    [SerializeField, Min(0f)] private float detectorListHorizontalPadding = 20f;
    [SerializeField, Min(128f)] private float detectorListHeight = 320f;
    [SerializeField] private float detectorListCenterYOffset = 330f;

    [Header("Behavior")]
    [SerializeField] private bool autoFindReferences = true;
    [SerializeField] private bool logActions = true;

    private void Awake()
    {
        ResolveReferences();
        ConfigureControllerDetectorList();
        SyncControllerIpField();
    }

    private void OnEnable()
    {
        ResolveReferences();
        ConfigureControllerDetectorList();
        RadiationReceiver.OnServerStatusChanged += HandleServerStatusChanged;
        RadiationReceiver.OnDisplayTextChanged += HandleDisplayTextChanged;
        XREALCaptureManager.OnCaptureStateChanged += HandleCaptureStateChanged;
        RegisterControllerIpListener();
        SyncControllerIpField();
        SyncCurrentReceiverText();
    }

    private void LateUpdate()
    {
        if (controllerRadiationDisplayText == null)
            return;

        if (lastConfiguredScreenWidth != Screen.width ||
            lastConfiguredScreenHeight != Screen.height ||
            lastConfiguredSafeArea != Screen.safeArea)
        {
            ConfigureControllerDetectorList();
        }
    }

    private void OnDisable()
    {
        RadiationReceiver.OnServerStatusChanged -= HandleServerStatusChanged;

        RadiationReceiver.OnDisplayTextChanged -= HandleDisplayTextChanged;
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
    }
    /// <summary>
    /// Manually refresh manager references. Useful if objects are created after the controller prefab.
    /// </summary>
    public void RefreshReferences()
    {
        ResolveReferences();
        SyncControllerIpField();
        SyncCurrentReceiverText();
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
    /// Connects to the server. If controllerIpInputField has text, that IP is used.
    /// Otherwise, this triggers RadiationReceiver's existing connect-button behavior.
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
            : "";

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
        if (controllerStatusText == null)
            return;

        controllerStatusText.text = message;
        controllerStatusText.color = color;
    }

    private void HandleDisplayTextChanged(string message)
    {
        if (controllerRadiationDisplayText == null)
            return;

        ConfigureControllerDetectorList();
        controllerRadiationDisplayText.text = message;
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

        if (markerManager == null)
        {
            Warn("DetectorWorldMarkerManager not found. Cannot place detector.");
            return;
        }

        markerManager.PlaceDetector();
        Log("PlaceDetector");
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
        bool placementWasActive = markerManager != null && markerManager.HasActivePlacement;

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

        if (string.IsNullOrWhiteSpace(controllerIpInputField.text))
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

        if (qrScanner == null)
            qrScanner = UnityEngine.Object.FindFirstObjectByType<QRScanner>();

        if (radiationReceiver == null)
            radiationReceiver = UnityEngine.Object.FindFirstObjectByType<RadiationReceiver>();

        if (captureManager == null)
        {
            captureManager = UnityEngine.Object.FindFirstObjectByType<XREALCaptureManager>();
        }
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

    private void ShowControllerActionStatus(string message, Color color)
    {
        if (controllerStatusText == null)
            return;

        controllerStatusText.text = message;
        controllerStatusText.color = color;
    }

    private void Log(string message)
    {
        if (logActions)
            Debug.Log($"[BeamProControllerBridge] {message}");
    }

    private void Warn(string message)
    {
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
