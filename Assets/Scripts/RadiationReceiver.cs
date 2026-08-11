using System.Collections;
using System.Collections.Generic;
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
    private readonly Dictionary<string, float> latestDeviceData = new Dictionary<string, float>();

    public string CurrentDisplayMessage => currentDisplayMessage;
    public IReadOnlyDictionary<string, float> LatestDeviceData => latestDeviceData;
    public delegate void RadiationDataReceivedSignature(Dictionary<string, float> deviceData);
    public static event RadiationDataReceivedSignature OnRadiationDataReceived;

    public delegate void ServerStatusChangedSignature(string message, Color color);
    public static event ServerStatusChangedSignature OnServerStatusChanged;

    [Header("UI")]
    [SerializeField] private TMP_Text displayText;
    [SerializeField] private TMP_InputField ipInputField;
    [SerializeField] private Button connectButton;
    [SerializeField] private TMP_Text statusText;

    [Header("Server")]
    [SerializeField] private string defaultIp = "192.168.0.60";
    [SerializeField] private int serverPort = 5002;

    [Header("Keyboard On Start")]
    [SerializeField] private bool openKeyboardOnStart = true;
    [SerializeField] private float keyboardOpenDelay = 0.5f;
    [SerializeField] private bool connectWhenKeyboardDone = true;

    [Header("QR Scan After Connect")]
    [SerializeField] private QRScanner qrScanner;
    [SerializeField] private bool startQrCameraAfterConnect = true;
    [SerializeField] private float qrStartDelayAfterConnect = 0.3f;

    private WebSocket websocket;
    private string savedIp;
    private bool isConnecting;
    private string currentStatusMessage = "";
    private Color currentStatusColor = Color.white;

    public string CurrentStatusMessage => currentStatusMessage;
    public Color CurrentStatusColor => currentStatusColor;
    public string CurrentServerIp => string.IsNullOrWhiteSpace(savedIp) ? defaultIp : savedIp;
    public bool IsConnected => websocket != null && websocket.State == WebSocketState.Open;
    public bool IsConnecting => isConnecting;

#if UNITY_ANDROID && !UNITY_EDITOR
    private TouchScreenKeyboard activeKeyboard;
#endif

    private void Awake()
    {
        // Load this in Awake so controller prefabs created during scene startup can
        // immediately copy the saved IP before this component's Start runs.
        savedIp = PlayerPrefs.GetString(ServerIpPlayerPrefsKey, defaultIp);
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

        if (openKeyboardOnStart)
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
        Connect(ip);
    }

    private void SaveIp(string ip)
    {
        ip = CleanIp(ip);
        if (string.IsNullOrEmpty(ip)) return;

        savedIp = ip;
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

    private async void Connect(string ip)
    {
        if (isConnecting)
            return;

        string cleanIp = CleanIp(ip);
        if (string.IsNullOrEmpty(cleanIp))
        {
            UpdateStatus("IP is empty", Color.red);
            return;
        }

        string url = $"ws://{cleanIp}:{serverPort}";
        Debug.Log($"Trying connecting to URL: {url}");

        isConnecting = true;

        if (websocket != null && websocket.State == WebSocketState.Open)
            await websocket.Close();

        ReplaceLatestDeviceData(null);
        UpdateStatus($"Connecting to... {url}", Color.yellow);

        websocket = new WebSocket(url);

        websocket.OnOpen += () =>
        {
            Debug.Log("Server connected!");
            isConnecting = false;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                ReplaceLatestDeviceData(null);
                UpdateStatus($"Connected: {cleanIp}", Color.green);

                if (startQrCameraAfterConnect)
                    StartCoroutine(StartQrCameraAfterDelay());
            });
        };

        websocket.OnMessage += (bytes) =>
        {
            string json = System.Text.Encoding.UTF8.GetString(bytes);
            Debug.Log($"Received data: {json}");

            try
            {
                var root = JObject.Parse(json);
                var dict = root["deviceDataDictionary"]?.ToObject<Dictionary<string, float>>();
                if (dict == null) return;

                string result = "";
                foreach (var kvp in dict)
                    result += $"Device: {kvp.Key}  Radiation: {kvp.Value}\n";

                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    ReplaceLatestDeviceData(dict);
                    UpdateDisplay(result);
                    OnRadiationDataReceived?.Invoke(new Dictionary<string, float>(dict));
                });
            }
            catch (System.Exception e)
            {
                Debug.LogError($"JSON parse error: {e.Message}");
            }
        };

        websocket.OnError += (e) =>
        {
            Debug.LogError($"Error: {e}");
            isConnecting = false;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                ReplaceLatestDeviceData(null);
                UpdateStatus($"Error: {e}", Color.red);
            });
        };

        websocket.OnClose += (e) =>
        {
            Debug.Log("Disconnected");
            isConnecting = false;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                ReplaceLatestDeviceData(null);
                UpdateStatus("Disconnected", Color.red);
            });
        };

        try
        {
            await websocket.Connect();
        }
        catch (System.Exception e)
        {
            isConnecting = false;
            Debug.LogError($"WebSocket connection failed: {e.Message}");
            ReplaceLatestDeviceData(null);
            UpdateStatus($"Connection failed: {e.Message}", Color.red);
        }
    }

    private void ReplaceLatestDeviceData(Dictionary<string, float> data)
    {
        latestDeviceData.Clear();

        if (data == null)
            return;

        foreach (var pair in data)
            latestDeviceData[pair.Key] = pair.Value;
    }

    private IEnumerator StartQrCameraAfterDelay()
    {
        yield return new WaitForSeconds(qrStartDelayAfterConnect);

        if (qrScanner == null)
            qrScanner = FindFirstObjectByType<QRScanner>();

        if (qrScanner != null)
            qrScanner.StartScanning();
        else
            Debug.LogWarning("QRScanner not found. QR camera cannot start automatically.");
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

    private async void OnDestroy()
    {
        if (websocket != null)
            await websocket.Close();
    }
}
