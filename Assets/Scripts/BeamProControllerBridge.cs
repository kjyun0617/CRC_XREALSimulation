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

    [Header("Controller UI")]
    [Tooltip("Optional Beam Pro-side IP input field. If assigned, ConnectToServer() uses this value.")]
    [SerializeField] private TMP_InputField controllerIpInputField;

    [Tooltip("Optional Beam Pro-side status text.")]
    [SerializeField] private TMP_Text controllerStatusText;

    [Tooltip("Beam Pro-side radiation data text.")]
    [SerializeField]
    private TMP_Text controllerRadiationDisplayText;

    [Header("Behavior")]
    [SerializeField] private bool autoFindReferences = true;
    [SerializeField] private bool logActions = true;

    private void Awake()
    {
        ResolveReferences();
        SyncControllerIpField();
    }

    private void OnEnable()
    {
        ResolveReferences();
        RadiationReceiver.OnServerStatusChanged += HandleServerStatusChanged;
        RadiationReceiver.OnDisplayTextChanged += HandleDisplayTextChanged;
        RegisterControllerIpListener();
        SyncControllerIpField();
        SyncCurrentReceiverText();
    }

    private void OnDisable()
    {
        RadiationReceiver.OnServerStatusChanged -= HandleServerStatusChanged;

        RadiationReceiver.OnDisplayTextChanged -= HandleDisplayTextChanged;

        if (controllerIpInputField != null)
            controllerIpInputField.onEndEdit.RemoveListener(HandleControllerIpEndEdit);
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

        // The controller prefab has no separate Connect button. Starting a scan
        // must therefore also use the entered/saved IP when no connection exists.
        ConnectIfNeeded();

        if (qrScanner == null)
        {
            Warn("QRScanner not found. Cannot start QR scan.");
            return;
        }

        qrScanner.StartScanning();
        Log("StartQrScan");
    }

    public void StopQrScan()
    {
        ResolveReferences();

        if (qrScanner == null)
        {
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

        if (markerManager == null)
        {
            Warn("DetectorWorldMarkerManager not found. Cannot cancel placement.");
            return;
        }

        markerManager.CancelCurrentFollowingDetector();
        Log("CancelPlace");
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

}
