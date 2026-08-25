using System;
using UnityEngine;

/// <summary>
/// Establishes a stable, gravity-aligned room coordinate frame from a dedicated
/// ROOM_ORIGIN QR workflow.
///
/// QRScanner currently provides identity text and 2D image metadata, not a world
/// pose. The user therefore scans the QR, aims the glasses' center ray back at the
/// same QR on a vertical wall, and presses the existing Place button. The center
/// gaze/AR-plane intersection is the origin; gravity is +Y and the wall normal
/// pointing into the room is +Z.
/// </summary>
[DisallowMultipleComponent]
public sealed class RoomCoordinateSystem : MonoBehaviour
{
    private const string GenericRoomOriginPayload = "ROOM_ORIGIN";
    private const string LastRoomIdPlayerPrefsKey = "RadVis.LastRoomOriginId";

    public static event Action<string, Color> RoomStatusChanged;

    [Header("References")]
    [SerializeField] private DetectorWorldMarkerManager markerManager;
    [SerializeField] private DetectorCoordinateDatabase coordinateDatabase;
    [SerializeField] private Camera glassesCamera;

    [Header("Wall Calibration")]
    [Tooltip("Maximum absolute dot product between a valid wall normal and world up. 0 is perfectly vertical.")]
    [SerializeField, Range(0.05f, 0.6f)]
    private float maximumWallNormalVerticalDot = 0.30f;

    [Tooltip("The QR must be viewed from this minimum horizontal distance so its wall-normal sign is unambiguous.")]
    [SerializeField, Min(0.02f)] private float minimumViewerOffsetMeters = 0.08f;

    [Header("Tracking Lifetime")]
    [Tooltip("Require ROOM_ORIGIN again after returning from the Android background, because the XR session origin may have been recreated or recentered.")]
    [SerializeField] private bool invalidateCalibrationOnApplicationResume = true;

    [Header("Placement Preview")]
    [SerializeField, Min(0.03f)] private float previewDiameterMeters = 0.14f;
    [SerializeField] private Color previewColor = new Color(0.72f, 0.76f, 0.80f, 1f);
    [SerializeField, Range(0.05f, 0.8f)] private float previewAlpha = 0.30f;

    public event Action<string, Pose> RoomCalibrated;

    public bool IsCalibrated { get; private set; }
    public bool HasPendingPlacement { get; private set; }
    public bool HasValidPendingPose => hasValidPendingPose;
    public string RoomId { get; private set; } = "";
    public string PendingRoomId => pendingRoomId;
    public string LastRoomId { get; private set; } = "";
    public Transform CoordinateFrame => coordinateFrame;

    private Transform coordinateFrame;
    private GameObject previewObject;
    private Renderer previewRenderer;
    private Material previewMaterial;
    private string pendingRoomId = "";
    private Pose pendingWorldPose = new Pose(Vector3.zero, Quaternion.identity);
    private bool hasValidPendingPose;
    private bool eventsSubscribed;
    private bool applicationWasPaused;

    private void Awake()
    {
        ResolveReferences();
        LastRoomId = PlayerPrefs.GetString(LastRoomIdPlayerPrefsKey, "").Trim();
    }

    private void OnEnable()
    {
        SubscribeEvents();
    }

    private void Start()
    {
        ResolveReferences();

        if (!string.IsNullOrEmpty(LastRoomId))
        {
            Debug.Log(
                $"[RoomCoordinateSystem] Last room was {LastRoomId}. " +
                "Scan its ROOM_ORIGIN QR to restore the stable room frame.");
        }
    }

    private void OnDisable()
    {
        UnsubscribeEvents();
        SetPreviewVisible(false);
    }

    private void OnDestroy()
    {
        UnsubscribeEvents();

        if (previewMaterial != null)
            Destroy(previewMaterial);
        if (previewObject != null)
            Destroy(previewObject);
        if (coordinateFrame != null)
            Destroy(coordinateFrame.gameObject);
    }

    private void LateUpdate()
    {
        if (HasPendingPlacement)
            RefreshPendingPose();
    }

    public void Initialize(
        DetectorWorldMarkerManager detectorMarkerManager,
        DetectorCoordinateDatabase detectorCoordinateDatabase,
        Camera viewCamera)
    {
        if (detectorMarkerManager != null)
            markerManager = detectorMarkerManager;
        if (detectorCoordinateDatabase != null)
            coordinateDatabase = detectorCoordinateDatabase;
        if (viewCamera != null)
            glassesCamera = viewCamera;

        ResolveReferences();
        SubscribeEvents();
    }

    public static bool IsRoomOriginCode(string qrText)
    {
        return TryParseRoomOriginCode(qrText, out _);
    }

    public static void PublishStatus(string message, Color color)
    {
        RoomStatusChanged?.Invoke(message, color);
    }

    /// <summary>
    /// Exact payloads only, to avoid treating a detector whose ID happens to begin
    /// with ROOM- as a coordinate command. Supported forms:
    /// ROOM_ORIGIN, ROOM_ORIGIN:&lt;roomId&gt;, and ROOM-&lt;digits&gt;.
    /// </summary>
    public static bool TryParseRoomOriginCode(string qrText, out string roomId)
    {
        roomId = "";
        if (string.IsNullOrWhiteSpace(qrText))
            return false;

        string payload = qrText.Trim();
        if (string.Equals(
                payload,
                GenericRoomOriginPayload,
                StringComparison.OrdinalIgnoreCase))
        {
            roomId = GenericRoomOriginPayload;
            return true;
        }

        string prefix = GenericRoomOriginPayload + ":";
        if (!payload.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            const string numberedRoomPrefix = "ROOM-";
            if (!payload.StartsWith(
                    numberedRoomPrefix,
                    StringComparison.OrdinalIgnoreCase) ||
                payload.Length <= numberedRoomPrefix.Length)
            {
                return false;
            }

            for (int i = numberedRoomPrefix.Length; i < payload.Length; i++)
            {
                if (!char.IsDigit(payload[i]))
                    return false;
            }

            roomId = payload;
            return true;
        }

        roomId = payload.Substring(prefix.Length).Trim();
        return !string.IsNullOrEmpty(roomId);
    }

    public bool TryConfirmPendingPlacement(out string message)
    {
        if (!HasPendingPlacement)
        {
            message = "No ROOM_ORIGIN placement is active";
            PublishStatus(message, Color.yellow);
            return false;
        }

        RefreshPendingPose();
        if (!hasValidPendingPose)
        {
            message = "Aim the glasses center at ROOM_ORIGIN on a detected vertical wall";
            PublishStatus(message, Color.yellow);
            return false;
        }

        EnsureCoordinateFrame();
        coordinateFrame.gameObject.SetActive(true);
        coordinateFrame.SetPositionAndRotation(
            pendingWorldPose.position,
            pendingWorldPose.rotation);
        coordinateFrame.localScale = Vector3.one;

        RoomId = pendingRoomId;
        LastRoomId = RoomId;
        IsCalibrated = true;
        HasPendingPlacement = false;
        hasValidPendingPose = false;
        pendingRoomId = "";
        SetPreviewVisible(false);

        PlayerPrefs.SetString(LastRoomIdPlayerPrefsKey, RoomId);
        PlayerPrefs.Save();

        int capturedCount = markerManager != null
            ? markerManager.CapturePlacedDetectorRoomCoordinates(this)
            : 0;
        int restoredCount = markerManager != null
            ? markerManager.RestoreMissingMarkersFromRoomCoordinates(this)
            : 0;

        RadiationSourceEstimator estimator =
            FindFirstObjectByType<RadiationSourceEstimator>();
        if (estimator != null)
        {
            estimator.SetCoordinateFrame(coordinateFrame);
            estimator.RequestEstimateNow();
        }

        Pose calibratedPose = new Pose(
            coordinateFrame.position,
            coordinateFrame.rotation);
        RoomCalibrated?.Invoke(RoomId, calibratedPose);

        message =
            $"Room origin placed: {RoomId} " +
            $"(captured {capturedCount}, restored {restoredCount})";
        Debug.Log($"[RoomCoordinateSystem] {message}, worldPose={calibratedPose}");
        PublishStatus(message, Color.green);
        return true;
    }

    public bool TryCancelPendingPlacement(out string message)
    {
        if (!HasPendingPlacement)
        {
            message = "No ROOM_ORIGIN placement is active";
            return false;
        }

        string cancelledRoomId = pendingRoomId;
        ClearPendingPlacement();
        message = $"Room origin placement cancelled: {cancelledRoomId}";
        Debug.Log($"[RoomCoordinateSystem] {message}");
        PublishStatus(message, Color.white);
        return true;
    }

    /// <summary>
    /// Invalidates the Unity-session transform without deleting room-relative data.
    /// The next ROOM_ORIGIN placement rebuilds every detector in the new XR frame.
    /// </summary>
    public void InvalidateCalibration(string reason)
    {
        bool hadRoomLocalization = IsCalibrated;
        ClearPendingPlacement();
        IsCalibrated = false;
        RoomId = "";

        if (coordinateFrame != null)
            coordinateFrame.gameObject.SetActive(false);

        if (markerManager != null)
            markerManager.InvalidateRoomLocalization(reason);
        else
        {
            RadiationSourceEstimator estimator =
                FindFirstObjectByType<RadiationSourceEstimator>();
            if (estimator != null)
            {
                estimator.SetCoordinateFrame(null);
                estimator.ClearEstimate();
            }
        }

        if (!hadRoomLocalization)
            return;

        string message =
            "XR tracking resumed. Scan and place ROOM_ORIGIN again before using detectors";
        Debug.LogWarning($"[RoomCoordinateSystem] {message}. {reason}");
        PublishStatus(message, Color.yellow);
    }

    /// <summary>
    /// Called by the detector manager when a normal detector QR takes ownership of
    /// the shared Place/Cancel buttons. This cannot alter an already calibrated room.
    /// </summary>
    public void CancelPendingPlacementForDetectorScan()
    {
        if (HasPendingPlacement)
            ClearPendingPlacement();
    }

    public Vector3 WorldToRoomPoint(Vector3 worldPoint)
    {
        return IsCalibrated && coordinateFrame != null
            ? coordinateFrame.InverseTransformPoint(worldPoint)
            : worldPoint;
    }

    public Quaternion WorldToRoomRotation(Quaternion worldRotation)
    {
        return IsCalibrated && coordinateFrame != null
            ? Quaternion.Inverse(coordinateFrame.rotation) * worldRotation
            : worldRotation;
    }

    public Vector3 RoomToWorldPoint(Vector3 roomPoint)
    {
        return IsCalibrated && coordinateFrame != null
            ? coordinateFrame.TransformPoint(roomPoint)
            : roomPoint;
    }

    public Quaternion RoomToWorldRotation(Quaternion roomRotation)
    {
        return IsCalibrated && coordinateFrame != null
            ? coordinateFrame.rotation * roomRotation
            : roomRotation;
    }

    private void HandleScanStarted()
    {
        // A new scan supersedes an uncommitted room preview. Keep an already
        // calibrated room frame unchanged until another placement is confirmed.
        if (HasPendingPlacement)
            ClearPendingPlacement();
    }

    private void HandleQrDetected(
        string qrText,
        Vector2 imageCenter,
        int imageWidth,
        int imageHeight,
        float qrPixelSize)
    {
        if (!TryParseRoomOriginCode(qrText, out string parsedRoomId))
            return;

        ResolveReferences();
        markerManager?.CancelActivePlacementOnly();

        pendingRoomId = parsedRoomId;
        HasPendingPlacement = true;
        hasValidPendingPose = false;
        EnsurePreviewObject();
        RefreshPendingPose();

        Debug.Log(
            $"[RoomCoordinateSystem] ROOM_ORIGIN selected for {pendingRoomId}. " +
            "Aim the glasses center at the QR on its vertical wall, then press Place.");
        PublishStatus(
            $"ROOM_ORIGIN selected: {pendingRoomId}. Aim at the wall QR and press Place",
            Color.cyan);
    }

    private void RefreshPendingPose()
    {
        ResolveReferences();

        if (markerManager == null ||
            !markerManager.TryGetCurrentGazePlanePose(
                out Vector3 hitPosition,
                out _,
                out Vector3 wallNormal,
                out _,
                true,
                maximumWallNormalVerticalDot))
        {
            hasValidPendingPose = false;
            SetPreviewVisible(false);
            return;
        }

        Vector3 up = Vector3.up;
        Vector3 forward = Vector3.ProjectOnPlane(wallNormal, up);
        if (forward.sqrMagnitude < 0.0001f)
        {
            hasValidPendingPose = false;
            SetPreviewVisible(false);
            return;
        }

        forward.Normalize();

        Vector3 viewerOffset = glassesCamera != null
            ? Vector3.ProjectOnPlane(
                glassesCamera.transform.position - hitPosition,
                up)
            : Vector3.zero;

        if (viewerOffset.magnitude < minimumViewerOffsetMeters)
        {
            hasValidPendingPose = false;
            SetPreviewVisible(false);
            return;
        }

        // +Z always points from the wall into the room/scanner side, regardless of
        // which normal sign the plane provider reported in this session.
        if (Vector3.Dot(forward, viewerOffset) < 0f)
            forward = -forward;

        Vector3 stableUp = Vector3.ProjectOnPlane(up, forward).normalized;
        Vector3 right = Vector3.Cross(stableUp, forward).normalized;
        forward = Vector3.Cross(right, stableUp).normalized;

        pendingWorldPose = new Pose(
            hitPosition,
            Quaternion.LookRotation(forward, stableUp));
        hasValidPendingPose = true;

        EnsurePreviewObject();
        previewObject.transform.SetPositionAndRotation(
            pendingWorldPose.position,
            pendingWorldPose.rotation);
        previewObject.transform.localScale = Vector3.one * previewDiameterMeters;
        SetPreviewVisible(true);
    }

    private void ClearPendingPlacement()
    {
        HasPendingPlacement = false;
        hasValidPendingPose = false;
        pendingRoomId = "";
        SetPreviewVisible(false);
    }

    private void ResolveReferences()
    {
        if (markerManager == null)
            markerManager = FindFirstObjectByType<DetectorWorldMarkerManager>();

        if (coordinateDatabase == null)
        {
            coordinateDatabase = DetectorCoordinateDatabase.Instance != null
                ? DetectorCoordinateDatabase.Instance
                : FindFirstObjectByType<DetectorCoordinateDatabase>();
        }

        if (glassesCamera == null)
            glassesCamera = Camera.main;
    }

    private void SubscribeEvents()
    {
        if (eventsSubscribed)
            return;

        QRScanner.OnScanStarted += HandleScanStarted;
        QRScanner.OnQRDetectedDetailed += HandleQrDetected;
        eventsSubscribed = true;
    }

    private void UnsubscribeEvents()
    {
        if (!eventsSubscribed)
            return;

        QRScanner.OnScanStarted -= HandleScanStarted;
        QRScanner.OnQRDetectedDetailed -= HandleQrDetected;
        eventsSubscribed = false;
    }

    private void EnsureCoordinateFrame()
    {
        if (coordinateFrame != null)
            return;

        GameObject frameObject = new GameObject("RadVis_RoomCoordinateFrame");
        coordinateFrame = frameObject.transform;
        coordinateFrame.localScale = Vector3.one;
    }

    private void EnsurePreviewObject()
    {
        if (previewObject != null)
            return;

        previewObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        previewObject.name = "ROOM_ORIGIN_PlacementPreview";

        Collider previewCollider = previewObject.GetComponent<Collider>();
        if (previewCollider != null)
            Destroy(previewCollider);

        previewRenderer = previewObject.GetComponent<Renderer>();
        if (previewRenderer != null)
        {
            Shader shader = Resources.Load<Shader>("RadVisDetectorTransparent");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            if (shader != null)
            {
                previewMaterial = new Material(shader)
                {
                    name = "ROOM_ORIGIN_PreviewMaterial"
                };

                Color color = previewColor;
                color.a = previewAlpha;
                if (previewMaterial.HasProperty("_Color"))
                    previewMaterial.SetColor("_Color", color);
                if (previewMaterial.HasProperty("_BaseColor"))
                    previewMaterial.SetColor("_BaseColor", color);

                previewMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                previewRenderer.sharedMaterial = previewMaterial;
                previewRenderer.shadowCastingMode =
                    UnityEngine.Rendering.ShadowCastingMode.Off;
                previewRenderer.receiveShadows = false;
            }
        }

        previewObject.transform.localScale = Vector3.one * previewDiameterMeters;
        SetPreviewVisible(false);
    }

    private void SetPreviewVisible(bool visible)
    {
        if (previewObject != null && previewObject.activeSelf != visible)
            previewObject.SetActive(visible);
    }

    private void OnValidate()
    {
        maximumWallNormalVerticalDot =
            Mathf.Clamp(maximumWallNormalVerticalDot, 0.05f, 0.6f);
        minimumViewerOffsetMeters = Mathf.Max(0.02f, minimumViewerOffsetMeters);
        previewDiameterMeters = Mathf.Max(0.03f, previewDiameterMeters);
        previewAlpha = Mathf.Clamp(previewAlpha, 0.05f, 0.8f);

        if (previewMaterial != null)
        {
            Color color = previewColor;
            color.a = previewAlpha;
            if (previewMaterial.HasProperty("_Color"))
                previewMaterial.SetColor("_Color", color);
            if (previewMaterial.HasProperty("_BaseColor"))
                previewMaterial.SetColor("_BaseColor", color);
        }
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused)
        {
            applicationWasPaused = true;
            if (HasPendingPlacement)
                ClearPendingPlacement();
            return;
        }

        if (!applicationWasPaused)
            return;

        applicationWasPaused = false;
        if (invalidateCalibrationOnApplicationResume)
        {
            InvalidateCalibration(
                "Application resumed; the previous Unity world origin is no longer trusted.");
        }
    }
}
