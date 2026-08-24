using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NativeWebSocket;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;


public class RadiationReceiver : MonoBehaviour
{
    private const string ServerIpPlayerPrefsKey = "ServerIP";
    private const string WaitingForDataMessage = "Waiting for radiation data...";

    public delegate void DisplayTextChangedSignature(string message);
    public static event DisplayTextChangedSignature OnDisplayTextChanged;

    private string currentDisplayMessage = WaitingForDataMessage;
    private readonly Dictionary<string, float> latestDeviceData =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    public string CurrentDisplayMessage => currentDisplayMessage;
    public IReadOnlyDictionary<string, float> LatestDeviceData => latestDeviceData;
    public delegate void RadiationDataReceivedSignature(Dictionary<string, float> deviceData);
    public static event RadiationDataReceivedSignature OnRadiationDataReceived;

    public delegate void ServerStatusChangedSignature(string message, Color color);
    public static event ServerStatusChangedSignature OnServerStatusChanged;

    /// <summary>
    /// Raised on Unity's main thread whenever the usable server connection changes.
    /// False is also published while connecting so marker/UI consumers can hide
    /// stale server-backed content immediately.
    /// </summary>
    public static event Action<bool> OnServerConnectionChanged;

    [Header("UI")]
    [SerializeField] private TMP_Text displayText;
    [SerializeField] private TMP_InputField ipInputField;
    [SerializeField] private Button connectButton;
    [SerializeField] private TMP_Text statusText;

    [Header("Server")]
    [SerializeField] private string defaultIp = "192.168.0.60";
    [SerializeField] private int serverPort = 5002;

    [Header("Saved Server Reconnection")]
    [SerializeField] private bool autoReconnectSavedServer = true;
    [SerializeField, Min(0f)] private float automaticReconnectStartDelay = 0.35f;
    [SerializeField, Min(0.25f)] private float automaticReconnectInitialRetryDelay = 2f;
    [SerializeField, Min(0.25f)] private float automaticReconnectMaxRetryDelay = 15f;
    [SerializeField, Min(1f)] private float connectionAttemptTimeout = 10f;
    [SerializeField] private bool reconnectWhenApplicationResumes = true;

    [Header("Keyboard On Start")]
    [SerializeField] private bool openKeyboardOnStart = true;
    [SerializeField] private float keyboardOpenDelay = 0.5f;
    [SerializeField] private bool connectWhenKeyboardDone = true;

    [Header("QR Scan After Connect")]
    [SerializeField] private QRScanner qrScanner;
    [Tooltip("Applies to explicit/manual connections. Restoring a saved server never starts QR scanning.")]
    [SerializeField] private bool startQrCameraAfterConnect = true;
    [SerializeField] private float qrStartDelayAfterConnect = 0.3f;

    private WebSocket websocket;
    private Coroutine qrStartCoroutine;
    private Coroutine automaticReconnectCoroutine;
    private Coroutine connectionAttemptTimeoutCoroutine;
    private int automaticQrStartGeneration;
    private volatile int connectionAttemptGeneration;
    private int automaticReconnectGeneration;
    private bool automaticQrStartExpected;
    private string savedIp;
    private string activeServerIp;
    private bool hasSavedServerIp;
    private bool isConnecting;
    private bool isServerConnected;
    private bool hasStarted;
    private volatile bool isShuttingDown;
    private float nextAutomaticReconnectDelay;
    private string currentStatusMessage = "";
    private Color currentStatusColor = Color.white;

    public string CurrentStatusMessage => currentStatusMessage;
    public Color CurrentStatusColor => currentStatusColor;
    public string CurrentServerIp => string.IsNullOrWhiteSpace(savedIp) ? defaultIp : savedIp;
    public bool HasSavedServerIp => hasSavedServerIp;
    public bool IsConnected => isServerConnected;
    public bool IsConnecting => isConnecting;
    public bool IsQrScanPending => automaticQrStartExpected || qrStartCoroutine != null;

#if UNITY_ANDROID && !UNITY_EDITOR
    private TouchScreenKeyboard activeKeyboard;
#endif

    private void Awake()
    {
        // NativeWebSocket callbacks can arrive away from Unity's thread. Create
        // the dispatcher GameObject here so callbacks never construct Unity
        // objects while merely trying to enqueue a main-thread state change.
        _ = UnityMainThreadDispatcher.Instance;

        // Load this in Awake so controller prefabs created during scene startup can
        // immediately copy the saved IP before this component's Start runs.
        string persistedIp = PlayerPrefs.GetString(ServerIpPlayerPrefsKey, "");
        savedIp = CleanIp(persistedIp);
        hasSavedServerIp = PlayerPrefs.HasKey(ServerIpPlayerPrefsKey) &&
                           !string.IsNullOrWhiteSpace(savedIp);

        if (!hasSavedServerIp)
            savedIp = CleanIp(defaultIp);

        ResetAutomaticReconnectDelay();
    }

    private void Start()
    {
        if (ipInputField != null)
        {
            ipInputField.text = savedIp;
            ipInputField.onEndEdit.AddListener(OnIpInputEnded);
        }

        if (connectButton != null)
            connectButton.onClick.AddListener(OnConnectButtonClicked);

        if (qrScanner == null)
            qrScanner = FindFirstObjectByType<QRScanner>();

        UpdateDisplay(WaitingForDataMessage);
        UpdateStatus("Disconnected", Color.red);
        PublishServerConnection(false, true);
        hasStarted = true;

        if (autoReconnectSavedServer && hasSavedServerIp)
            ScheduleAutomaticReconnect(automaticReconnectStartDelay);
        else if (openKeyboardOnStart)
            StartCoroutine(OpenKeyboardAfterDelay());
    }

    private IEnumerator OpenKeyboardAfterDelay()
    {
        yield return new WaitForSeconds(keyboardOpenDelay);
        StartIpInput();
    }

    public void StartIpInput()
    {
        if (ipInputField == null)
        {
            Debug.LogError("ipInputField is not assigned.");
            return;
        }

        ipInputField.Select();
        ipInputField.ActivateInputField();
        ipInputField.caretPosition = ipInputField.text.Length;
        ipInputField.selectionAnchorPosition = ipInputField.text.Length;
        ipInputField.selectionFocusPosition = ipInputField.text.Length;

#if UNITY_ANDROID && !UNITY_EDITOR
        activeKeyboard = TouchScreenKeyboard.Open(
            ipInputField.text,
            TouchScreenKeyboardType.NumbersAndPunctuation,
            false,
            false,
            false,
            false,
            "Server IP"
        );
#endif

        UpdateStatus("Enter server IP", Color.white);
    }

    private void OnIpInputEnded(string ip)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Android path is handled by activeKeyboard in Update().
#else
        if (connectWhenKeyboardDone)
            SaveAndConnect(ip);
        else
            SaveIp(ip);
#endif
    }

    private void OnConnectButtonClicked()
    {
        ConnectUsingInputField();
    }

    public void ConnectUsingInputField()
    {
        if (ipInputField == null)
        {
            UpdateStatus("IP input field missing", Color.red);
            return;
        }

        SaveAndConnect(ipInputField.text);
    }

    public void ConnectToServerWithIp(string ip)
    {
        SaveAndConnect(ip);
    }

    public void SetIpText(string ip)
    {
        if (ipInputField != null)
            ipInputField.text = ip;
    }

    private void SaveAndConnect(string ip)
    {
        ip = CleanIp(ip);
        if (string.IsNullOrEmpty(ip))
        {
            UpdateStatus("IP is empty", Color.red);
            return;
        }

        SaveIp(ip);
        Connect(ip, true, false);
    }

    private void SaveIp(string ip)
    {
        ip = CleanIp(ip);
        if (string.IsNullOrEmpty(ip)) return;

        savedIp = ip;
        hasSavedServerIp = true;

        if (ipInputField != null)
            ipInputField.SetTextWithoutNotify(savedIp);

        PlayerPrefs.SetString(ServerIpPlayerPrefsKey, savedIp);
        PlayerPrefs.Save();
    }

    private string CleanIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return "";

        ip = ip.Trim();
        ip = ip.Replace("ws://", "").Replace("wss://", "");

        int slashIndex = ip.IndexOf('/');
        if (slashIndex >= 0)
            ip = ip.Substring(0, slashIndex);

        int colonIndex = ip.IndexOf(':');
        if (colonIndex >= 0)
            ip = ip.Substring(0, colonIndex);

        return ip.Trim();
    }

    private async void Connect(
        string ip,
        bool allowAutomaticQrStart,
        bool isAutomaticReconnect)
    {
        string cleanIp = CleanIp(ip);
        if (string.IsNullOrEmpty(cleanIp))
        {
            UpdateStatus("IP is empty", Color.red);
            PublishServerConnection(false, true);
            return;
        }

        if (isAutomaticReconnect && (isConnecting || IsConnected))
            return;

        string url = $"ws://{cleanIp}:{serverPort}";
        Debug.Log($"Trying connecting to URL: {url}");

        CancelScheduledAutomaticReconnect();
        CancelConnectionAttemptTimeout();
        if (!isAutomaticReconnect)
            ResetAutomaticReconnectDelay();

        int connectionGeneration = ++connectionAttemptGeneration;
        isConnecting = true;
        activeServerIp = cleanIp;
        PublishServerConnection(false, true);

        CancelPendingQrScan();
        int qrStartGeneration = automaticQrStartGeneration;
        automaticQrStartExpected =
            allowAutomaticQrStart && startQrCameraAfterConnect;

        WebSocket previousWebsocket = websocket;
        websocket = null;
        CancelOrCloseSupersededConnection(previousWebsocket);

        if (isShuttingDown || connectionGeneration != connectionAttemptGeneration)
            return;

        ReplaceLatestDeviceData(null);
        UpdateStatus($"Connecting to... {url}", Color.yellow);

        WebSocket connection;

        try
        {
            connection = new WebSocket(url);
        }
        catch (Exception creationException)
        {
            isConnecting = false;
            CancelAutomaticQrStartIfCurrent(qrStartGeneration);
            ReplaceLatestDeviceData(null);
            UpdateDisplay(WaitingForDataMessage);
            UpdateStatus($"Invalid server address: {creationException.Message}", Color.red);
            PublishServerConnection(false, true);
            ScheduleNextAutomaticReconnect();
            return;
        }

        websocket = connection;
        connectionAttemptTimeoutCoroutine = StartCoroutine(
            CancelConnectionAttemptAfterTimeout(
                connectionGeneration,
                connection,
                Mathf.Max(1f, connectionAttemptTimeout)));

        connection.OnOpen += () =>
        {
            Debug.Log("Server connected!");

            if (!IsConnectionAttemptCurrentBeforeDispatch(connectionGeneration))
                return;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (!IsCurrentConnectionAttempt(connectionGeneration, connection))
                    return;

                CancelConnectionAttemptTimeout();
                isConnecting = false;
                ResetAutomaticReconnectDelay();
                CancelScheduledAutomaticReconnect();
                ReplaceLatestDeviceData(null);
                UpdateDisplay(WaitingForDataMessage);
                UpdateStatus($"Connected: {cleanIp}", Color.green);
                PublishServerConnection(true, true);

                if (qrStartGeneration == automaticQrStartGeneration)
                {
                    bool shouldStartQr = automaticQrStartExpected;
                    automaticQrStartExpected = false;

                    if (shouldStartQr)
                    {
                        if (qrScanner == null)
                            qrScanner = FindFirstObjectByType<QRScanner>();

                        if (qrScanner == null || !qrScanner.IsScanActive)
                        {
                            qrStartCoroutine =
                                StartCoroutine(StartQrCameraAfterDelay(qrStartGeneration));
                        }
                    }
                }
            });
        };

        connection.OnMessage += (bytes) =>
        {
            if (!IsConnectionAttemptCurrentBeforeDispatch(connectionGeneration))
                return;

            string json = System.Text.Encoding.UTF8.GetString(bytes);
            Debug.Log($"Received data: {json}");

            try
            {
                var root = JObject.Parse(json);
                var dict = root["deviceDataDictionary"]?.ToObject<Dictionary<string, float>>();
                if (dict == null) return;

                Dictionary<string, float> normalizedData = CreateNormalizedDeviceData(dict);

                string result = BuildCpsDetectorList(normalizedData);

                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    if (!IsCurrentConnectionAttempt(connectionGeneration, connection) ||
                        !IsConnected)
                        return;

                    ReplaceLatestDeviceData(normalizedData);
                    UpdateDisplay(result);
                    OnRadiationDataReceived?.Invoke(
                        new Dictionary<string, float>(latestDeviceData, StringComparer.OrdinalIgnoreCase));
                });
            }
            catch (System.Exception e)
            {
                Debug.LogError($"JSON parse error: {e.Message}");
            }
        };

        connection.OnError += (e) =>
        {
            Debug.LogError($"Error: {e}");

            if (!IsConnectionAttemptCurrentBeforeDispatch(connectionGeneration))
                return;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (!IsCurrentConnectionAttempt(connectionGeneration, connection))
                    return;

                CancelConnectionAttemptTimeout();
                isConnecting = false;
                CancelAutomaticQrStartIfCurrent(qrStartGeneration);
                ReplaceLatestDeviceData(null);
                UpdateDisplay(WaitingForDataMessage);
                UpdateStatus($"Error: {e}", Color.red);
                PublishServerConnection(false, true);
                ScheduleNextAutomaticReconnect();
            });
        };

        connection.OnClose += (e) =>
        {
            Debug.Log("Disconnected");

            if (!IsConnectionAttemptCurrentBeforeDispatch(connectionGeneration))
                return;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (!IsCurrentConnectionAttempt(connectionGeneration, connection))
                    return;

                CancelConnectionAttemptTimeout();
                isConnecting = false;
                CancelAutomaticQrStartIfCurrent(qrStartGeneration);
                ReplaceLatestDeviceData(null);
                UpdateDisplay(WaitingForDataMessage);
                UpdateStatus("Disconnected", Color.red);
                PublishServerConnection(false, true);
                ScheduleNextAutomaticReconnect();
            });
        };

        try
        {
            await connection.Connect();
        }
        catch (Exception e)
        {
            Debug.LogError($"WebSocket connection failed: {e.Message}");

            if (!IsConnectionAttemptCurrentBeforeDispatch(connectionGeneration))
                return;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (!IsCurrentConnectionAttempt(connectionGeneration, connection))
                    return;

                CancelConnectionAttemptTimeout();
                isConnecting = false;
                CancelAutomaticQrStartIfCurrent(qrStartGeneration);
                ReplaceLatestDeviceData(null);
                UpdateDisplay(WaitingForDataMessage);
                UpdateStatus($"Connection failed: {e.Message}", Color.red);
                PublishServerConnection(false, true);
                ScheduleNextAutomaticReconnect();
            });
        }
    }

    private bool IsCurrentConnectionAttempt(int generation, WebSocket connection)
    {
        return !isShuttingDown &&
               generation == connectionAttemptGeneration &&
               ReferenceEquals(websocket, connection);
    }

    private bool IsConnectionAttemptCurrentBeforeDispatch(int generation)
    {
        return !isShuttingDown && generation == connectionAttemptGeneration;
    }

    private IEnumerator CancelConnectionAttemptAfterTimeout(
        int generation,
        WebSocket connection,
        float timeout)
    {
        yield return new WaitForSecondsRealtime(timeout);

        if (!IsCurrentConnectionAttempt(generation, connection) || !isConnecting)
            yield break;

        // The socket can become Open just before its main-thread OnOpen action is
        // drained. Give that action one more frame before timing it out.
        bool socketAlreadyOpen = false;

        try
        {
            socketAlreadyOpen = connection.State == WebSocketState.Open;
        }
        catch (Exception stateException)
        {
            Debug.LogWarning($"WebSocket timeout state check failed: {stateException.Message}");
        }

        if (socketAlreadyOpen)
            yield return null;

        if (!IsCurrentConnectionAttempt(generation, connection) || !isConnecting)
            yield break;

        connectionAttemptTimeoutCoroutine = null;
        connectionAttemptGeneration++;
        websocket = null;
        isConnecting = false;
        CancelAutomaticQrStartIfCurrent(automaticQrStartGeneration);

        try
        {
            connection.CancelConnection();
        }
        catch (Exception cancelException)
        {
            Debug.LogWarning($"Timed-out WebSocket cancel failed: {cancelException.Message}");
        }

        ReplaceLatestDeviceData(null);
        UpdateDisplay(WaitingForDataMessage);
        UpdateStatus($"Connection timed out: {activeServerIp}", Color.red);
        PublishServerConnection(false, true);
        ScheduleNextAutomaticReconnect();
    }

    private void CancelConnectionAttemptTimeout()
    {
        if (connectionAttemptTimeoutCoroutine == null)
            return;

        StopCoroutine(connectionAttemptTimeoutCoroutine);
        connectionAttemptTimeoutCoroutine = null;
    }

    private void CancelOrCloseSupersededConnection(WebSocket connection)
    {
        if (connection == null)
            return;

        try
        {
            WebSocketState state = connection.State;
            if (state == WebSocketState.Connecting ||
                state == WebSocketState.Open ||
                state == WebSocketState.Closing)
            {
                // Cancellation guarantees cleanup cannot hold the replacement
                // connection behind an unresponsive close handshake.
                connection.CancelConnection();
            }
        }
        catch (Exception closeException)
        {
            Debug.LogWarning($"Previous WebSocket cleanup failed: {closeException.Message}");
        }
    }

    private void PublishServerConnection(bool connected, bool force)
    {
        bool changed = isServerConnected != connected;
        isServerConnected = connected;

        if (changed || force)
            OnServerConnectionChanged?.Invoke(connected);
    }

    private void ResetAutomaticReconnectDelay()
    {
        nextAutomaticReconnectDelay =
            Mathf.Max(0.25f, automaticReconnectInitialRetryDelay);
    }

    private void ScheduleNextAutomaticReconnect()
    {
        if (!autoReconnectSavedServer ||
            !hasSavedServerIp ||
            isShuttingDown ||
            automaticReconnectCoroutine != null)
        {
            return;
        }

        float maximumDelay = Mathf.Max(
            Mathf.Max(0.25f, automaticReconnectInitialRetryDelay),
            automaticReconnectMaxRetryDelay);
        float delay = Mathf.Clamp(nextAutomaticReconnectDelay, 0.25f, maximumDelay);
        nextAutomaticReconnectDelay = Mathf.Min(maximumDelay, delay * 2f);
        ScheduleAutomaticReconnect(delay);
    }

    private void ScheduleAutomaticReconnect(float delay)
    {
        if (!isActiveAndEnabled ||
            isShuttingDown ||
            !autoReconnectSavedServer ||
            !hasSavedServerIp ||
            IsConnected ||
            isConnecting ||
            automaticReconnectCoroutine != null)
        {
            return;
        }

        int generation = ++automaticReconnectGeneration;
        automaticReconnectCoroutine = StartCoroutine(
            AutomaticReconnectAfterDelay(Mathf.Max(0f, delay), generation));
    }

    private IEnumerator AutomaticReconnectAfterDelay(float delay, int generation)
    {
        if (delay > 0f)
            yield return new WaitForSecondsRealtime(delay);

        if (generation != automaticReconnectGeneration || isShuttingDown)
            yield break;

        automaticReconnectCoroutine = null;

        if (!IsConnected && !isConnecting && hasSavedServerIp)
        {
            // Restoring a saved placement must never force the QR camera open.
            Connect(savedIp, false, true);
        }
    }

    private void CancelScheduledAutomaticReconnect()
    {
        automaticReconnectGeneration++;

        if (automaticReconnectCoroutine != null)
        {
            StopCoroutine(automaticReconnectCoroutine);
            automaticReconnectCoroutine = null;
        }
    }

    private void ReplaceLatestDeviceData(Dictionary<string, float> data)
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

    private Dictionary<string, float> CreateNormalizedDeviceData(Dictionary<string, float> data)
    {
        Dictionary<string, float> normalized =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        if (data == null)
            return normalized;

        foreach (var pair in data)
        {
            string detectorId = string.IsNullOrWhiteSpace(pair.Key) ? "" : pair.Key.Trim();
            if (!string.IsNullOrEmpty(detectorId))
                normalized[detectorId] = pair.Value;
        }

        return normalized;
    }

    private string BuildCpsDetectorList(Dictionary<string, float> data)
    {
        if (data == null || data.Count == 0)
            return WaitingForDataMessage;

        List<string> detectorIds = new List<string>(data.Keys);
        detectorIds.Sort(StringComparer.OrdinalIgnoreCase);

        StringBuilder builder = new StringBuilder(32 + detectorIds.Count * 24);
        builder.Append("DETECTOR | CPS");

        for (int i = 0; i < detectorIds.Count; i++)
        {
            string detectorId = detectorIds[i];
            float value = data[detectorId];

            builder.Append('\n')
                .Append(SanitizeDetectorIdForDisplay(detectorId))
                .Append(" | ");

            if (value >= 0f && !float.IsNaN(value) && !float.IsInfinity(value))
                builder.Append(value.ToString("F3", CultureInfo.InvariantCulture));
            else
                builder.Append("--");
        }

        return builder.ToString();
    }

    private string SanitizeDetectorIdForDisplay(string detectorId)
    {
        return string.IsNullOrEmpty(detectorId)
            ? "--"
            : detectorId.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
    }

    private IEnumerator StartQrCameraAfterDelay(int generation)
    {
        yield return new WaitForSeconds(qrStartDelayAfterConnect);

        if (generation != automaticQrStartGeneration)
            yield break;

        qrStartCoroutine = null;

        if (qrScanner == null)
            qrScanner = FindFirstObjectByType<QRScanner>();

        if (qrScanner == null)
            Debug.LogWarning("QRScanner not found. QR camera cannot start automatically.");
        else if (!qrScanner.IsScanActive)
            qrScanner.StartScanning();
    }

    public bool CancelPendingQrScan()
    {
        bool hadPendingStart = automaticQrStartExpected || qrStartCoroutine != null;
        automaticQrStartGeneration++;
        automaticQrStartExpected = false;

        if (qrStartCoroutine != null)
        {
            StopCoroutine(qrStartCoroutine);
            qrStartCoroutine = null;
        }

        return hadPendingStart;
    }

    private void CancelAutomaticQrStartIfCurrent(int generation)
    {
        if (generation == automaticQrStartGeneration)
            CancelPendingQrScan();
    }

    private void UpdateStatus(string message, Color color)
    {
        currentStatusMessage = message;
        currentStatusColor = color;

        if (statusText != null)
        {
            statusText.text = message;
            statusText.color = color;
        }

        OnServerStatusChanged?.Invoke(message, color);
    }

    private void UpdateDisplay(string message)
    {
        currentDisplayMessage = string.IsNullOrEmpty(message)
            ? WaitingForDataMessage
            : message;

        if (displayText != null)
            displayText.text = currentDisplayMessage;

        OnDisplayTextChanged?.Invoke(currentDisplayMessage);
    }

    private void Update()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        if (websocket != null)
            websocket.DispatchMessageQueue();
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        if (activeKeyboard != null && ipInputField != null)
        {
            ipInputField.text = activeKeyboard.text;

            if (activeKeyboard.status == TouchScreenKeyboard.Status.Done)
            {
                SaveAndConnect(ipInputField.text);
                ipInputField.DeactivateInputField();
                activeKeyboard = null;
            }
            else if (activeKeyboard.status == TouchScreenKeyboard.Status.Canceled ||
                     activeKeyboard.status == TouchScreenKeyboard.Status.LostFocus)
            {
                SaveIp(ipInputField.text);
                ipInputField.DeactivateInputField();
                activeKeyboard = null;
            }
        }
#endif
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus ||
            !hasStarted ||
            !reconnectWhenApplicationResumes ||
            IsConnected ||
            isConnecting)
        {
            return;
        }

        ScheduleAutomaticReconnect(automaticReconnectStartDelay);
    }

    private void OnDestroy()
    {
        isShuttingDown = true;
        connectionAttemptGeneration++;
        CancelScheduledAutomaticReconnect();
        CancelConnectionAttemptTimeout();
        CancelPendingQrScan();
        isConnecting = false;
        PublishServerConnection(false, true);

        WebSocket closingConnection = websocket;
        websocket = null;
        CancelOrCloseSupersededConnection(closingConnection);
    }
}
