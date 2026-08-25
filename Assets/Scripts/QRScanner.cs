using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ZXing;
using ZXing.Common;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

/// <summary>
/// Opens the Beam Pro / Android device camera inside Unity and scans QR codes.
/// Fixed for plane-placement workflow:
/// - Stops camera preview immediately after a QR is detected.
/// - Fires both legacy and detailed QR events.
/// - Still notifies placement managers even when the same QR is scanned again.
/// - Shows the QR status text only while the scanner is starting or scanning.
/// </summary>
public class QRScanner : MonoBehaviour
{
    public delegate void FNotifyDetectorNameSignature(string inText);
    public static event FNotifyDetectorNameSignature OnQRDetected;

    public delegate void FNotifyDetectorDetailedSignature(
        string qrText,
        Vector2 imageCenter,
        int imageWidth,
        int imageHeight,
        float qrPixelSize
    );
    public static event FNotifyDetectorDetailedSignature OnQRDetectedDetailed;
    public static event Action OnScanStarted;

    [Header("UI")]
    [SerializeField] private RawImage cameraPreview;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private Button scanButton;
    [SerializeField] private Button stopButton;

    [Header("Camera")]
    [SerializeField] private bool preferRearCamera = true;
    [SerializeField] private int requestedWidth = 1280;
    [SerializeField] private int requestedHeight = 720;
    [SerializeField] private int requestedFPS = 30;

    [Header("Scan")]
    [SerializeField] private float scanInterval = 0.15f;
    [SerializeField] private bool autoStopAfterFirstResult = true;

    [Tooltip("If true, the QR text is remembered, but duplicate scans still notify placement managers.")]
    [SerializeField] private bool rememberScannedQrTexts = true;

    [Tooltip("Hide the RawImage as soon as QR is detected, before placement events are fired.")]
    [SerializeField] private bool hidePreviewBeforeNotify = true;

    public List<string> detectorNameList = new List<string>();

    public string LastQrText { get; private set; }
    public Vector2 LastQrCenterInImage { get; private set; }
    public float LastQrPixelSize { get; private set; }
    public int LastImageWidth { get; private set; }
    public int LastImageHeight { get; private set; }
    public bool IsScanActive => isStarting || isScanning;

    private WebCamTexture camTexture;
    private Coroutine startCoroutine;
    private Coroutine scanCoroutine;
    private int scanSessionId;
    private bool isStarting;
    private bool isScanning;
    private bool hasHandledCurrentResult;

    private void Start()
    {
        if (scanButton != null)
            scanButton.onClick.AddListener(StartScanning);

        if (stopButton != null)
            stopButton.onClick.AddListener(StopScanning);

        WarnIfStatusTextIsChildOfPreview();
        SetPreviewVisible(false);
        SetStatusVisible(false);
        UpdateStatus("QR scan ready");
    }

    public void StartScanning()
    {
        if (isStarting || isScanning)
            return;

        SetStatusVisible(true);
        hasHandledCurrentResult = false;
        int sessionId = ++scanSessionId;
        OnScanStarted?.Invoke();
        startCoroutine = StartCoroutine(RequestPermissionAndStartCamera(sessionId));
    }

    public void StopScanning()
    {
        scanSessionId++;
        isStarting = false;
        isScanning = false;

        if (startCoroutine != null)
        {
            StopCoroutine(startCoroutine);
            startCoroutine = null;
        }

        if (scanCoroutine != null)
        {
            StopCoroutine(scanCoroutine);
            scanCoroutine = null;
        }

        StopCameraNow();
        SetPreviewVisible(false);
        SetStatusVisible(false);
    }

    private void StopCameraNow()
    {
        if (camTexture != null)
        {
            if (camTexture.isPlaying)
                camTexture.Stop();

            Destroy(camTexture);
            camTexture = null;
        }

        if (cameraPreview != null)
            cameraPreview.texture = null;
    }

    private IEnumerator RequestPermissionAndStartCamera(int sessionId)
    {
        isStarting = true;
        UpdateStatus("Requesting camera permission...");

#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            Permission.RequestUserPermission(Permission.Camera);

            float permissionWaitTime = 0f;
            while (IsCurrentScanSession(sessionId) &&
                   !Permission.HasUserAuthorizedPermission(Permission.Camera) &&
                   permissionWaitTime < 10f)
            {
                permissionWaitTime += Time.deltaTime;
                yield return null;
            }
        }
#endif

        if (!IsCurrentScanSession(sessionId))
            yield break;

        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);

        if (!IsCurrentScanSession(sessionId))
            yield break;

        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            isStarting = false;
            startCoroutine = null;
            UpdateStatus("Camera permission denied");
            Debug.LogError("Camera permission denied.");
            SetStatusVisible(false);
            yield break;
        }

        yield return StartCamera(sessionId);
    }

    private IEnumerator StartCamera(int sessionId)
    {
        if (!IsCurrentScanSession(sessionId))
            yield break;

        WebCamDevice[] devices = WebCamTexture.devices;

        if (devices == null || devices.Length == 0)
        {
            isStarting = false;
            startCoroutine = null;
            UpdateStatus("No camera found");
            Debug.LogError("No WebCamTexture camera found.");
            SetStatusVisible(false);
            yield break;
        }

        WebCamDevice selectedDevice = devices[0];

        for (int i = 0; i < devices.Length; i++)
        {
            Debug.Log($"Camera[{i}] name={devices[i].name}, isFrontFacing={devices[i].isFrontFacing}");

            if (preferRearCamera && !devices[i].isFrontFacing)
            {
                selectedDevice = devices[i];
                break;
            }
        }

        WebCamTexture sessionTexture =
            new WebCamTexture(selectedDevice.name, requestedWidth, requestedHeight, requestedFPS);
        camTexture = sessionTexture;
        sessionTexture.Play();

        float waitTime = 0f;
        while (IsCurrentScanSession(sessionId) &&
               sessionTexture != null &&
               sessionTexture.width <= 16 &&
               waitTime < 3f)
        {
            waitTime += Time.deltaTime;
            yield return null;
        }

        if (!IsCurrentScanSession(sessionId))
        {
            StopCameraForSession(sessionTexture);
            yield break;
        }

        if (sessionTexture == null || sessionTexture.width <= 16)
        {
            isStarting = false;
            startCoroutine = null;
            UpdateStatus("Camera failed to start");
            Debug.LogError("Camera failed to start.");
            StopCameraNow();
            SetPreviewVisible(false);
            SetStatusVisible(false);
            yield break;
        }

        if (cameraPreview != null)
        {
            cameraPreview.texture = sessionTexture;
            cameraPreview.rectTransform.localEulerAngles = new Vector3(0f, 0f, -sessionTexture.videoRotationAngle);

            if (sessionTexture.videoVerticallyMirrored)
                cameraPreview.uvRect = new Rect(0f, 1f, 1f, -1f);
            else
                cameraPreview.uvRect = new Rect(0f, 0f, 1f, 1f);
        }

        SetPreviewVisible(true);

        isStarting = false;
        isScanning = true;
        startCoroutine = null;
        scanCoroutine = StartCoroutine(ScanLoop());

        UpdateStatus($"Camera on: {selectedDevice.name}\nPoint camera at QR code");
        Debug.Log($"QR camera started: {selectedDevice.name}, size={sessionTexture.width}x{sessionTexture.height}");
    }

    private bool IsCurrentScanSession(int sessionId)
    {
        return sessionId == scanSessionId;
    }

    private void StopCameraForSession(WebCamTexture sessionTexture)
    {
        if (sessionTexture == null)
            return;

        if (sessionTexture.isPlaying)
            sessionTexture.Stop();

        if (cameraPreview != null && cameraPreview.texture == sessionTexture)
            cameraPreview.texture = null;

        if (camTexture == sessionTexture)
            camTexture = null;

        Destroy(sessionTexture);
    }

    private IEnumerator ScanLoop()
    {
        while (isScanning)
        {
            ScanFrame();
            yield return new WaitForSeconds(scanInterval);
        }
    }

    private void ScanFrame()
    {
        if (hasHandledCurrentResult)
            return;

        if (camTexture == null || !camTexture.isPlaying)
            return;

        if (camTexture.width <= 16 || camTexture.height <= 16)
            return;

        try
        {
            int imageWidth = camTexture.width;
            int imageHeight = camTexture.height;
            Color32[] pixels = camTexture.GetPixels32();
            byte[] bytes = new byte[pixels.Length * 4];

            for (int i = 0; i < pixels.Length; i++)
            {
                int index = i * 4;
                bytes[index] = pixels[i].r;
                bytes[index + 1] = pixels[i].g;
                bytes[index + 2] = pixels[i].b;
                bytes[index + 3] = pixels[i].a;
            }

            RGBLuminanceSource source = new RGBLuminanceSource(
                bytes,
                imageWidth,
                imageHeight,
                RGBLuminanceSource.BitmapFormat.RGBA32
            );

            HybridBinarizer binarizer = new HybridBinarizer(source);
            BinaryBitmap bitmap = new BinaryBitmap(binarizer);
            ZXing.QrCode.QRCodeReader reader = new ZXing.QrCode.QRCodeReader();
            Result result = reader.decode(bitmap);

            if (result == null || string.IsNullOrWhiteSpace(result.Text))
                return;

            HandleDetectedResult(result, imageWidth, imageHeight);
        }
        catch (ReaderException)
        {
            // Normal when there is no QR code in the image.
        }
        catch (System.Exception e)
        {
            Debug.LogError($"QR scan error: {e.Message}");
        }
    }

    private void HandleDetectedResult(Result result, int imageWidth, int imageHeight)
    {
        hasHandledCurrentResult = true;

        string qrText = result.Text.Trim();
        Vector2 center = CalculateCenter(result.ResultPoints, imageWidth, imageHeight);
        float pixelSize = CalculateQrPixelSize(result.ResultPoints);

        LastQrText = qrText;
        LastQrCenterInImage = center;
        LastQrPixelSize = pixelSize;
        LastImageWidth = imageWidth;
        LastImageHeight = imageHeight;

        bool isRoomOrigin = RoomCoordinateSystem.IsRoomOriginCode(qrText);
        bool alreadyScanned = !isRoomOrigin && detectorNameList.Exists(
            value => string.Equals(value, qrText, StringComparison.OrdinalIgnoreCase));
        if (rememberScannedQrTexts && !isRoomOrigin && !alreadyScanned)
            detectorNameList.Add(qrText);

        Debug.Log($"QR detected: {qrText}, center={center}, pixelSize={pixelSize:F1}, duplicate={alreadyScanned}");

        // Stop preview first so the user immediately returns to placement mode.
        if (autoStopAfterFirstResult && hidePreviewBeforeNotify)
        {
            isScanning = false;
            if (scanCoroutine != null)
            {
                StopCoroutine(scanCoroutine);
                scanCoroutine = null;
            }
            StopCameraNow();
            SetPreviewVisible(false);
        }

        UpdateStatus(
            isRoomOrigin
                ? $"Room QR detected: {qrText}\nAim glasses at the wall QR, then press Place."
                : $"QR detected: {qrText}\nAim glasses at the real QR/device, then press Place.");

        // Fire events even for duplicates. Re-scanning the same detector is useful for repositioning.
        OnQRDetected?.Invoke(qrText);
        OnQRDetectedDetailed?.Invoke(qrText, center, imageWidth, imageHeight, pixelSize);

        if (autoStopAfterFirstResult && !hidePreviewBeforeNotify)
        {
            StopScanning();
            SetStatusVisible(false);
        }
    }

    private Vector2 CalculateCenter(ResultPoint[] points, int imageWidth, int imageHeight)
    {
        if (points == null || points.Length == 0)
            return new Vector2(imageWidth * 0.5f, imageHeight * 0.5f);

        float sumX = 0f;
        float sumY = 0f;

        for (int i = 0; i < points.Length; i++)
        {
            sumX += points[i].X;
            sumY += points[i].Y;
        }

        return new Vector2(sumX / points.Length, sumY / points.Length);
    }

    private float CalculateQrPixelSize(ResultPoint[] points)
    {
        if (points == null || points.Length < 2)
            return -1f;

        float maxDistance = 0f;
        for (int i = 0; i < points.Length; i++)
        {
            for (int j = i + 1; j < points.Length; j++)
            {
                float dx = points[i].X - points[j].X;
                float dy = points[i].Y - points[j].Y;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                if (distance > maxDistance)
                    maxDistance = distance;
            }
        }

        return maxDistance;
    }

    private void SetPreviewVisible(bool visible)
    {
        if (cameraPreview != null)
            cameraPreview.gameObject.SetActive(visible);
    }

    private void UpdateStatus(string message)
    {
        if (statusText != null)
            statusText.text = message;

        // Updating the message must never make QR instructions remain visible
        // after detection. Visibility follows the actual scanner lifecycle.
        SetStatusVisible(isStarting || isScanning);
        Debug.Log(message);
    }

    private void SetStatusVisible(bool visible)
    {
        if (statusText != null)
            statusText.gameObject.SetActive(visible);
    }

    private void WarnIfStatusTextIsChildOfPreview()
    {
        if (cameraPreview == null || statusText == null)
            return;

        if (statusText.transform.IsChildOf(cameraPreview.transform))
        {
            Debug.LogWarning("QRScanner statusText is a child of cameraPreview. Move QRStatusText under Canvas, not under CameraPreview, or it will disappear when preview is hidden.");
        }
    }

    private void OnDestroy()
    {
        StopScanning();
    }
}
