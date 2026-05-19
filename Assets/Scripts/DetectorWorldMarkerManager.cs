using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Previous preview-center detector placement flow.
/// QR scan selects detectorId, then marker is placed from camera preview center direction
/// at default/estimated distance. It saves JSON fallback coordinates and optionally
/// creates persistent spatial anchors.
/// </summary>
public class DetectorWorldMarkerManager : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Usually empty. Uses Main Camera automatically.")]
    [SerializeField] private Transform placementOrigin;

    [Tooltip("Used only when Placement Origin is empty.")]
    [SerializeField] private Camera fallbackCamera;

    [Tooltip("Optional. If empty, the script creates a sphere automatically.")]
    [SerializeField] private GameObject markerPrefab;

    [Header("Coordinate Storage")]
    [SerializeField] private bool useCoordinateDatabase = true;
    [SerializeField] private DetectorCoordinateDatabase coordinateDatabase;
    [SerializeField] private bool loadSavedCoordinatesOnStart = true;

    [Header("Spatial Anchor Storage")]
    [SerializeField] private bool useSpatialAnchors = false;
    [SerializeField] private DetectorSpatialAnchorManager spatialAnchorManager;
    [SerializeField] private bool createSpatialAnchorOnQr = false;
    [SerializeField] private bool parentMarkerToAnchor = true;

    [Header("Preview-Center Placement")]
    [Tooltip("ON = place using camera preview center. OFF = place using ZXing QR image center.")]
    [SerializeField] private bool usePreviewCenterPlacement = true;

    [Tooltip("Used when QR-size distance estimation is off, or when QR size cannot be read.")]
    [SerializeField] private float defaultPlacementDistanceMeters = 0.5f;

    [Tooltip("Approximate Beam Pro rear camera horizontal FOV. Tune if left/right feels wrong.")]
    [SerializeField] private float cameraHorizontalFovDegrees = 70f;

    [Tooltip("Approximate Beam Pro rear camera vertical FOV. Tune if up/down feels wrong.")]
    [SerializeField] private float cameraVerticalFovDegrees = 50f;

    [Tooltip("If on, distance is estimated from visible QR size.")]
    [SerializeField] private bool useQrSizeToEstimateDistance = false;

    [Tooltip("Physical side length of printed QR code in meters. Example: 8cm = 0.08")]
    [SerializeField] private float realQrSizeMeters = 0.08f;

    [Tooltip("ZXing result points can be inside the QR, not full printed square.")]
    [SerializeField, Range(0.4f, 1.2f)] private float qrEffectiveSizeRatio = 0.70f;

    [SerializeField, Range(0.3f, 3.0f)] private float distanceCalibrationMultiplier = 1.0f;
    [SerializeField] private float minEstimatedDistanceMeters = 0.3f;
    [SerializeField] private float maxEstimatedDistanceMeters = 5.0f;
    [SerializeField] private float markerVerticalOffsetMeters = 0f;

    [Header("Gaze Follow Placement")]
    [Tooltip("ON = after QR scan, the detector follows the glasses/camera center until PlaceDetector() is called by a UI button.")]
    [SerializeField] private bool followPreviewCenterUntilPlaced = true;

    [Tooltip("If true, detector coordinates are saved only when the Place Detector button is pressed.")]
    [SerializeField] private bool saveCoordinateOnlyWhenPlaced = true;

    [Tooltip("Text shown in the marker label while it is following the view center.")]
    [SerializeField] private string followingStateLabel = "following gaze";

    [Tooltip("Text shown in the marker label after Place Detector is pressed.")]
    [SerializeField] private string placedStateLabel = "placed";

    [Header("Rescan Behavior")]
    [SerializeField] private bool updateExistingMarkerOnRescan = true;
    [SerializeField] private bool smoothPositionOnRescan = true;
    [SerializeField, Range(0.05f, 1.0f)] private float rescanPositionBlend = 0.55f;

    [Header("Marker Visual")]
    [SerializeField] private float baseMarkerSize = 0.16f;
    [SerializeField] private float maxSizeMultiplier = 3.0f;
    [SerializeField] private bool showLabel = true;
    [SerializeField] private bool showDistanceInLabel = true;
    [SerializeField] private bool showAnchorStateInLabel = true;

    [Header("Radiation Thresholds")]
    [SerializeField] private float safeMax = 0.3f;
    [SerializeField] private float warningMax = 1.0f;

    private readonly Dictionary<string, MarkerInfo> markers = new Dictionary<string, MarkerInfo>();
    private bool spatialEventsSubscribed = false;
    private string currentFollowingDetectorId = "";

    private void OnEnable()
    {
        QRScanner.OnQRDetectedDetailed += HandleQrDetected;
        RadiationReceiver.OnRadiationDataReceived += HandleRadiationDataReceived;
    }

    private void OnDisable()
    {
        QRScanner.OnQRDetectedDetailed -= HandleQrDetected;
        RadiationReceiver.OnRadiationDataReceived -= HandleRadiationDataReceived;
        UnsubscribeSpatialEvents();
    }

    private void Start()
    {
        EnsurePlacementOrigin();
        EnsureCoordinateDatabase();
        EnsureSpatialAnchorManager();
        SubscribeSpatialEvents();

        if (loadSavedCoordinatesOnStart)
            LoadSavedCoordinatesWithoutAnchors();
    }

    private void LateUpdate()
    {
        UpdateFollowingMarkerPosition();

        if (!showLabel || fallbackCamera == null)
            return;

        foreach (var kvp in markers)
        {
            if (kvp.Value.label != null)
                kvp.Value.label.transform.rotation = fallbackCamera.transform.rotation;
        }
    }

    private void HandleQrDetected(string qrText, Vector2 imageCenter, int imageWidth, int imageHeight, float qrPixelSize)
    {
        string detectorId = NormalizeDetectorId(qrText);
        if (string.IsNullOrEmpty(detectorId))
            return;

        Vector2 placementImagePoint = imageCenter;
        string placementMethod = "QrImageCenterProjection";

        if (usePreviewCenterPlacement)
        {
            placementImagePoint = new Vector2(imageWidth * 0.5f, imageHeight * 0.5f);
            placementMethod = "PreviewCenterProjection";
        }

        float estimatedDistance = GetPlacementDistance(imageWidth, qrPixelSize);
        Vector3 worldPosition = CalculateWorldPosition(placementImagePoint, imageWidth, imageHeight, estimatedDistance);
        Quaternion worldRotation = CalculateMarkerRotation(worldPosition);

        MarkerInfo marker = CreateOrMoveMarker(detectorId, worldPosition, estimatedDistance, qrPixelSize, null);
        if (marker == null)
            return;

        marker.lastPlacementImagePoint = placementImagePoint;
        marker.lastImageWidth = imageWidth;
        marker.lastImageHeight = imageHeight;
        marker.lastPlacementMethod = placementMethod;

        if (followPreviewCenterUntilPlaced)
        {
            StopPreviousFollowingMarker(detectorId);

            currentFollowingDetectorId = detectorId;
            marker.isFollowingPlacementOrigin = true;
            marker.isPlaced = false;
            marker.anchorState = followingStateLabel;

            UpdateFollowingMarkerPosition(marker);
            UpdateLabel(marker, marker.lastRadiationValue, false);

            Debug.Log($"[DetectorWorldMarkerManager] Detector is following preview center: {detectorId}, distance={marker.lastEstimatedDistance:F2}m");
            return;
        }

        worldPosition = marker.savedPosition;
        marker.isFollowingPlacementOrigin = false;
        marker.isPlaced = true;
        marker.anchorState = useSpatialAnchors ? "anchor saving..." : placementMethod;
        UpdateLabel(marker, marker.lastRadiationValue, false);

        if (!saveCoordinateOnlyWhenPlaced)
        {
            SaveCoordinate(
                detectorId,
                worldPosition,
                worldRotation,
                estimatedDistance,
                qrPixelSize,
                placementImagePoint,
                imageWidth,
                imageHeight,
                placementMethod
            );
        }

        if (useSpatialAnchors && createSpatialAnchorOnQr)
        {
            EnsureSpatialAnchorManager();
            SubscribeSpatialEvents();

            if (spatialAnchorManager != null && spatialAnchorManager.IsReady())
                spatialAnchorManager.CreateAndSaveAnchorForDetector(detectorId, worldPosition, worldRotation);
            else
                Debug.LogWarning("[DetectorWorldMarkerManager] Spatial anchor not created. Manager or ARAnchorManager missing.");
        }

        Debug.Log($"[DetectorWorldMarkerManager] Detector placed from preview projection: {detectorId}, method={placementMethod}, pos={worldPosition}, distance={estimatedDistance:F2}m, qrPixelSize={qrPixelSize:F1}px");
    }

    private void StopPreviousFollowingMarker(string newDetectorId)
    {
        if (string.IsNullOrEmpty(currentFollowingDetectorId))
            return;

        if (currentFollowingDetectorId == newDetectorId)
            return;

        if (markers.TryGetValue(currentFollowingDetectorId, out MarkerInfo previous) && previous != null)
        {
            previous.isFollowingPlacementOrigin = false;
            previous.anchorState = "not placed";
            UpdateLabel(previous, previous.lastRadiationValue, false);
        }
    }

    private void UpdateFollowingMarkerPosition()
    {
        if (string.IsNullOrEmpty(currentFollowingDetectorId))
            return;

        if (!markers.TryGetValue(currentFollowingDetectorId, out MarkerInfo marker) || marker == null)
            return;

        if (!marker.isFollowingPlacementOrigin)
            return;

        UpdateFollowingMarkerPosition(marker);
    }

    private void UpdateFollowingMarkerPosition(MarkerInfo marker)
    {
        if (marker == null || marker.root == null)
            return;

        float distance = marker.lastEstimatedDistance > 0f
            ? marker.lastEstimatedDistance
            : defaultPlacementDistanceMeters;

        Vector3 worldPosition = CalculateWorldPosition(new Vector2(0.5f, 0.5f), 1, 1, distance);
        marker.root.transform.position = worldPosition;
        marker.savedPosition = worldPosition;
        marker.anchorState = followingStateLabel;
    }

    public void PlaceDetector()
    {
        if (string.IsNullOrEmpty(currentFollowingDetectorId))
        {
            Debug.LogWarning("[DetectorWorldMarkerManager] PlaceDetector called, but no detector is currently following the view center.");
            return;
        }

        PlaceDetector(currentFollowingDetectorId);
    }

    public void PlaceCurrentDetector()
    {
        PlaceDetector();
    }

    public void ConfirmCurrentDetectorPlacement()
    {
        PlaceDetector();
    }

    public void StopFollowingAndPlaceDetector()
    {
        PlaceDetector();
    }

    public void PlaceDetector(string detectorId)
    {
        detectorId = NormalizeDetectorId(detectorId);
        if (string.IsNullOrEmpty(detectorId))
            return;

        if (!markers.TryGetValue(detectorId, out MarkerInfo marker) || marker == null || marker.root == null)
        {
            Debug.LogWarning($"[DetectorWorldMarkerManager] PlaceDetector failed. Marker not found: {detectorId}");
            return;
        }

        marker.isFollowingPlacementOrigin = false;
        marker.isPlaced = true;
        marker.savedPosition = marker.root.transform.position;
        marker.anchorState = placedStateLabel;

        if (currentFollowingDetectorId == detectorId)
            currentFollowingDetectorId = "";

        Quaternion worldRotation = CalculateMarkerRotation(marker.savedPosition);
        string placementMethod = string.IsNullOrEmpty(marker.lastPlacementMethod)
            ? "PreviewCenterPlacedByButton"
            : marker.lastPlacementMethod + "+ButtonPlaced";

        SaveCoordinate(
            detectorId,
            marker.savedPosition,
            worldRotation,
            marker.lastEstimatedDistance > 0f ? marker.lastEstimatedDistance : defaultPlacementDistanceMeters,
            marker.lastQrPixelSize,
            marker.lastPlacementImagePoint,
            marker.lastImageWidth,
            marker.lastImageHeight,
            placementMethod
        );

        if (useSpatialAnchors && createSpatialAnchorOnQr)
        {
            EnsureSpatialAnchorManager();
            SubscribeSpatialEvents();

            if (spatialAnchorManager != null && spatialAnchorManager.IsReady())
            {
                marker.anchorState = "anchor saving...";
                spatialAnchorManager.CreateAndSaveAnchorForDetector(detectorId, marker.savedPosition, worldRotation);
            }
            else
            {
                Debug.LogWarning("[DetectorWorldMarkerManager] Spatial anchor not created. Manager or ARAnchorManager missing.");
            }
        }

        UpdateLabel(marker, marker.lastRadiationValue, false);
        Debug.Log($"[DetectorWorldMarkerManager] Detector fixed at current preview-center position: {detectorId}, pos={marker.savedPosition}");
    }

    public void CancelCurrentFollowingDetector()
    {
        if (string.IsNullOrEmpty(currentFollowingDetectorId))
            return;

        if (markers.TryGetValue(currentFollowingDetectorId, out MarkerInfo marker) && marker != null)
        {
            marker.isFollowingPlacementOrigin = false;
            marker.anchorState = "cancelled";
            UpdateLabel(marker, marker.lastRadiationValue, false);
        }

        currentFollowingDetectorId = "";
    }

    private string NormalizeDetectorId(string rawQrText)
    {
        return string.IsNullOrWhiteSpace(rawQrText) ? "" : rawQrText.Trim();
    }

    private Vector3 CalculateWorldPosition(Vector2 imagePoint, int imageWidth, int imageHeight, float distance)
    {
        EnsurePlacementOrigin();

        if (placementOrigin == null)
            return transform.position;

        float viewportX = imageWidth > 0 ? Mathf.Clamp01(imagePoint.x / imageWidth) : 0.5f;
        float viewportY = imageHeight > 0 ? Mathf.Clamp01(1f - (imagePoint.y / imageHeight)) : 0.5f;

        float xFromCenter = viewportX - 0.5f;
        float yFromCenter = viewportY - 0.5f;

        float tanX = Mathf.Tan(cameraHorizontalFovDegrees * Mathf.Deg2Rad * 0.5f);
        float tanY = Mathf.Tan(cameraVerticalFovDegrees * Mathf.Deg2Rad * 0.5f);

        Vector3 direction =
            placementOrigin.forward +
            placementOrigin.right * (xFromCenter * 2f * tanX) +
            placementOrigin.up * (yFromCenter * 2f * tanY);

        direction.Normalize();

        Vector3 worldPosition = placementOrigin.position + direction * distance;
        worldPosition += Vector3.up * markerVerticalOffsetMeters;
        return worldPosition;
    }

    private Quaternion CalculateMarkerRotation(Vector3 worldPosition)
    {
        EnsurePlacementOrigin();

        if (placementOrigin == null)
            return Quaternion.identity;

        Vector3 direction = worldPosition - placementOrigin.position;
        if (direction.sqrMagnitude < 0.0001f)
            return Quaternion.identity;

        return Quaternion.LookRotation(direction.normalized, Vector3.up);
    }

    private float GetPlacementDistance(int imageWidth, float qrPixelSize)
    {
        if (!useQrSizeToEstimateDistance || imageWidth <= 0 || qrPixelSize <= 1f || realQrSizeMeters <= 0f)
            return defaultPlacementDistanceMeters;

        float horizontalFovRad = cameraHorizontalFovDegrees * Mathf.Deg2Rad;
        float focalLengthPixels = (imageWidth * 0.5f) / Mathf.Tan(horizontalFovRad * 0.5f);

        float effectiveQrSizeMeters = realQrSizeMeters * qrEffectiveSizeRatio;
        float estimatedDistance = (effectiveQrSizeMeters * focalLengthPixels) / qrPixelSize;
        estimatedDistance *= distanceCalibrationMultiplier;

        return Mathf.Clamp(estimatedDistance, minEstimatedDistanceMeters, maxEstimatedDistanceMeters);
    }

    private void EnsurePlacementOrigin()
    {
        if (fallbackCamera == null)
            fallbackCamera = Camera.main;

        if (placementOrigin == null && fallbackCamera != null)
            placementOrigin = fallbackCamera.transform;
    }

    private void EnsureCoordinateDatabase()
    {
        if (!useCoordinateDatabase)
            return;

        if (coordinateDatabase != null)
            return;

        coordinateDatabase = FindObjectOfType<DetectorCoordinateDatabase>();

        if (coordinateDatabase == null)
        {
            GameObject dbObject = new GameObject("DetectorCoordinateDatabase");
            coordinateDatabase = dbObject.AddComponent<DetectorCoordinateDatabase>();
            Debug.Log("[DetectorWorldMarkerManager] DetectorCoordinateDatabase was created automatically.");
        }
    }

    private void EnsureSpatialAnchorManager()
    {
        if (!useSpatialAnchors)
            return;

        if (spatialAnchorManager != null)
            return;

        spatialAnchorManager = FindObjectOfType<DetectorSpatialAnchorManager>();

        if (spatialAnchorManager == null)
        {
            GameObject anchorObject = new GameObject("DetectorSpatialAnchorManager");
            spatialAnchorManager = anchorObject.AddComponent<DetectorSpatialAnchorManager>();
            Debug.Log("[DetectorWorldMarkerManager] DetectorSpatialAnchorManager was created automatically.");
        }
    }

    private void SubscribeSpatialEvents()
    {
        if (!useSpatialAnchors || spatialAnchorManager == null || spatialEventsSubscribed)
            return;

        spatialAnchorManager.AnchorSaved += HandleAnchorSaved;
        spatialAnchorManager.AnchorLoaded += HandleAnchorLoaded;
        spatialAnchorManager.AnchorSaveFailed += HandleAnchorSaveFailed;
        spatialAnchorManager.AnchorLoadFailed += HandleAnchorLoadFailed;
        spatialEventsSubscribed = true;
    }

    private void UnsubscribeSpatialEvents()
    {
        if (spatialAnchorManager == null || !spatialEventsSubscribed)
            return;

        spatialAnchorManager.AnchorSaved -= HandleAnchorSaved;
        spatialAnchorManager.AnchorLoaded -= HandleAnchorLoaded;
        spatialAnchorManager.AnchorSaveFailed -= HandleAnchorSaveFailed;
        spatialAnchorManager.AnchorLoadFailed -= HandleAnchorLoadFailed;
        spatialEventsSubscribed = false;
    }

    private void HandleAnchorSaved(string detectorId, ARAnchor anchor, string persistentGuid)
    {
        detectorId = NormalizeDetectorId(detectorId);
        if (string.IsNullOrEmpty(detectorId) || anchor == null)
            return;

        MarkerInfo marker = CreateOrMoveMarker(detectorId, anchor.transform.position, 0f, 0f, anchor.transform);
        if (marker == null)
            return;

        marker.anchor = anchor;
        marker.anchorGuid = persistentGuid;
        marker.anchorState = "anchor saved";
        marker.savedPosition = anchor.transform.position;
        marker.isFollowingPlacementOrigin = false;
        marker.isPlaced = true;

        if (parentMarkerToAnchor)
            marker.root.transform.SetParent(anchor.transform, true);

        UpdateLabel(marker, marker.lastRadiationValue, false);
    }

    private void HandleAnchorLoaded(string detectorId, ARAnchor anchor, string persistentGuid)
    {
        detectorId = NormalizeDetectorId(detectorId);
        if (string.IsNullOrEmpty(detectorId) || anchor == null)
            return;

        MarkerInfo marker = CreateOrMoveMarker(detectorId, anchor.transform.position, 0f, 0f, anchor.transform);
        if (marker == null)
            return;

        marker.anchor = anchor;
        marker.anchorGuid = persistentGuid;
        marker.anchorState = "anchor loaded";
        marker.savedPosition = anchor.transform.position;
        marker.isFollowingPlacementOrigin = false;
        marker.isPlaced = true;

        if (parentMarkerToAnchor)
            marker.root.transform.SetParent(anchor.transform, true);

        if (coordinateDatabase != null && coordinateDatabase.TryGetRecord(detectorId, out DetectorCoordinateRecord record) && record.lastRadiationValue >= 0f)
            UpdateMarkerVisual(marker, record.lastRadiationValue);
        else
            UpdateLabel(marker, marker.lastRadiationValue, false);
    }

    private void HandleAnchorSaveFailed(string detectorId, string message)
    {
        detectorId = NormalizeDetectorId(detectorId);
        if (markers.TryGetValue(detectorId, out MarkerInfo marker))
        {
            marker.anchorState = "anchor save failed";
            UpdateLabel(marker, marker.lastRadiationValue, false);
        }

        Debug.LogWarning($"[DetectorWorldMarkerManager] Anchor save failed for {detectorId}: {message}");
    }

    private void HandleAnchorLoadFailed(string detectorId, string message)
    {
        detectorId = NormalizeDetectorId(detectorId);

        if (coordinateDatabase != null && coordinateDatabase.TryGetRecord(detectorId, out DetectorCoordinateRecord record))
        {
            MarkerInfo marker = CreateOrMoveMarker(record.detectorId, record.GetPosition(), record.estimatedDistanceMeters, record.qrPixelSize, null);
            if (marker != null)
            {
                marker.anchorState = "anchor load failed; fallback coord";
                if (record.lastRadiationValue >= 0f)
                    UpdateMarkerVisual(marker, record.lastRadiationValue);
                else
                    UpdateLabel(marker, marker.lastRadiationValue, false);
            }
        }

        Debug.LogWarning($"[DetectorWorldMarkerManager] Anchor load failed for {detectorId}: {message}");
    }

    private void HandleRadiationDataReceived(Dictionary<string, float> data)
    {
        if (data == null)
            return;

        foreach (var kvp in data)
        {
            string detectorId = NormalizeDetectorId(kvp.Key);

            if (markers.TryGetValue(detectorId, out MarkerInfo marker))
                UpdateMarkerVisual(marker, kvp.Value);

            if (useCoordinateDatabase && coordinateDatabase != null)
                coordinateDatabase.UpdateRadiationValue(detectorId, kvp.Value);
        }
    }

    private MarkerInfo CreateOrMoveMarker(string detectorId, Vector3 worldPosition, float estimatedDistance, float qrPixelSize, Transform parent)
    {
        detectorId = NormalizeDetectorId(detectorId);
        if (string.IsNullOrEmpty(detectorId))
            return null;

        if (markers.TryGetValue(detectorId, out MarkerInfo existing))
        {
            if (!updateExistingMarkerOnRescan)
                return existing;

            Vector3 finalPosition = worldPosition;
            if (smoothPositionOnRescan && existing.anchor == null)
                finalPosition = Vector3.Lerp(existing.savedPosition, worldPosition, rescanPositionBlend);

            existing.root.transform.SetParent(parent != null && parentMarkerToAnchor ? parent : transform, true);
            existing.root.transform.position = finalPosition;
            existing.savedPosition = finalPosition;

            if (estimatedDistance > 0f)
                existing.lastEstimatedDistance = estimatedDistance;
            if (qrPixelSize > 0f)
                existing.lastQrPixelSize = qrPixelSize;

            UpdateLabel(existing, existing.lastRadiationValue, true);
            return existing;
        }

        Transform markerParent = parent != null && parentMarkerToAnchor ? parent : transform;

        GameObject root = markerPrefab != null
            ? Instantiate(markerPrefab, worldPosition, Quaternion.identity, markerParent)
            : CreateDefaultSphere(worldPosition, markerParent);

        root.name = $"DetectorMarker_{detectorId}";
        root.transform.position = worldPosition;
        root.transform.localScale = Vector3.one * baseMarkerSize;

        Renderer renderer = root.GetComponentInChildren<Renderer>();
        TMP_Text label = null;

        if (showLabel)
            label = CreateLabel(root.transform, detectorId);

        MarkerInfo info = new MarkerInfo
        {
            detectorId = detectorId,
            root = root,
            renderer = renderer,
            label = label,
            savedPosition = worldPosition,
            lastRadiationValue = -1f,
            lastEstimatedDistance = estimatedDistance,
            lastQrPixelSize = qrPixelSize,
            lastPlacementImagePoint = new Vector2(0.5f, 0.5f),
            lastImageWidth = 1,
            lastImageHeight = 1,
            lastPlacementMethod = "preview projection",
            isFollowingPlacementOrigin = false,
            isPlaced = false,
            anchorState = useSpatialAnchors ? "no anchor yet" : "preview projection"
        };

        markers.Add(detectorId, info);
        UpdateMarkerVisual(info, -1f);
        return info;
    }

    private GameObject CreateDefaultSphere(Vector3 position, Transform parent)
    {
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.transform.SetParent(parent != null ? parent : transform, true);
        sphere.transform.position = position;

        Renderer renderer = sphere.GetComponent<Renderer>();
        if (renderer != null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                shader = Shader.Find("Standard");

            if (shader != null)
                renderer.material = new Material(shader);
        }

        Collider collider = sphere.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        return sphere;
    }

    private TMP_Text CreateLabel(Transform parent, string detectorId)
    {
        GameObject labelObject = new GameObject($"Label_{detectorId}");
        labelObject.transform.SetParent(parent);
        labelObject.transform.localPosition = new Vector3(0f, 1.1f, 0f);
        labelObject.transform.localRotation = Quaternion.identity;
        labelObject.transform.localScale = Vector3.one * 0.08f;

        TMP_Text label = labelObject.AddComponent<TextMeshPro>();
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = 2.5f;
        label.text = detectorId;
        label.color = Color.white;

        return label;
    }

    private void UpdateMarkerVisual(MarkerInfo marker, float radiationValue)
    {
        marker.lastRadiationValue = radiationValue;

        Color color = GetRiskColor(radiationValue);
        SetRendererColor(marker.renderer, color);

        float sizeMultiplier = 1f;
        if (radiationValue > 0f && warningMax > 0f)
            sizeMultiplier = Mathf.Lerp(1f, maxSizeMultiplier, Mathf.Clamp01(radiationValue / (warningMax * 2f)));

        marker.root.transform.localScale = Vector3.one * baseMarkerSize * sizeMultiplier;
        UpdateLabel(marker, radiationValue, false);
    }

    private Color GetRiskColor(float radiationValue)
    {
        if (radiationValue < 0f)
            return Color.gray;

        if (radiationValue <= safeMax)
            return Color.green;

        if (radiationValue <= warningMax)
            return Color.yellow;

        return Color.red;
    }

    private void SetRendererColor(Renderer renderer, Color color)
    {
        if (renderer == null || renderer.material == null)
            return;

        if (renderer.material.HasProperty("_BaseColor"))
            renderer.material.SetColor("_BaseColor", color);

        if (renderer.material.HasProperty("_Color"))
            renderer.material.SetColor("_Color", color);
    }

    private void UpdateLabel(MarkerInfo marker, float radiationValue, bool moved)
    {
        if (marker.label == null)
            return;

        string text = marker.detectorId;

        if (radiationValue >= 0f)
            text += $"\n{radiationValue:F3}";

        if (showDistanceInLabel && marker.lastEstimatedDistance > 0f)
            text += $"\n{marker.lastEstimatedDistance:F2}m";

        if (showAnchorStateInLabel && !string.IsNullOrEmpty(marker.anchorState))
            text += $"\n{marker.anchorState}";

        if (moved)
            text += "\nposition updated";

        marker.label.text = text;
    }

    private void SaveCoordinate(
        string detectorId,
        Vector3 worldPosition,
        Quaternion worldRotation,
        float estimatedDistance,
        float qrPixelSize,
        Vector2 imageCenter,
        int imageWidth,
        int imageHeight,
        string placementMethod)
    {
        if (useCoordinateDatabase && coordinateDatabase != null)
        {
            coordinateDatabase.SaveOrUpdateCoordinate(
                detectorId,
                worldPosition,
                worldRotation,
                estimatedDistance,
                qrPixelSize,
                imageCenter,
                imageWidth,
                imageHeight,
                placementMethod
            );
        }
    }

    private void LoadSavedCoordinatesWithoutAnchors()
    {
        if (useCoordinateDatabase && coordinateDatabase != null)
        {
            IReadOnlyList<DetectorCoordinateRecord> records = coordinateDatabase.GetAllRecords();
            for (int i = 0; i < records.Count; i++)
            {
                DetectorCoordinateRecord record = records[i];
                if (record == null || string.IsNullOrWhiteSpace(record.detectorId))
                    continue;

                if (useSpatialAnchors && record.HasSavedAnchor())
                    continue;

                MarkerInfo marker = CreateOrMoveMarker(
                    record.detectorId,
                    record.GetPosition(),
                    record.estimatedDistanceMeters,
                    record.qrPixelSize,
                    null
                );

                if (marker != null)
                {
                    marker.anchorState = useSpatialAnchors ? "fallback coord" : record.placementMethod;
                    marker.isFollowingPlacementOrigin = false;
                    marker.isPlaced = true;

                    if (record.lastRadiationValue >= 0f)
                        UpdateMarkerVisual(marker, record.lastRadiationValue);
                    else
                        UpdateLabel(marker, marker.lastRadiationValue, false);
                }
            }

            Debug.Log($"[DetectorWorldMarkerManager] Loaded fallback markers from coordinate database: {records.Count}");
        }
    }

    public void ClearSavedMarkers()
    {
        List<string> detectorIds = new List<string>(markers.Keys);

        foreach (string detectorId in detectorIds)
        {
            if (useSpatialAnchors && spatialAnchorManager != null)
                spatialAnchorManager.EraseAnchorForDetector(detectorId);
        }

        foreach (var kvp in markers)
        {
            if (kvp.Value.root != null)
                Destroy(kvp.Value.root);
        }

        markers.Clear();

        if (coordinateDatabase != null)
            coordinateDatabase.ClearAllCoordinates();
    }

    public void PrintSavedCoordinatesToLog()
    {
        if (coordinateDatabase != null)
            coordinateDatabase.LogAllCoordinates();
        else
            Debug.LogWarning("[DetectorWorldMarkerManager] No DetectorCoordinateDatabase found.");
    }

    private class MarkerInfo
    {
        public string detectorId;
        public GameObject root;
        public Renderer renderer;
        public TMP_Text label;
        public Vector3 savedPosition;
        public float lastRadiationValue;
        public float lastEstimatedDistance;
        public float lastQrPixelSize;
        public Vector2 lastPlacementImagePoint;
        public int lastImageWidth;
        public int lastImageHeight;
        public string lastPlacementMethod;
        public bool isFollowingPlacementOrigin;
        public bool isPlaced;
        public ARAnchor anchor;
        public string anchorGuid;
        public string anchorState;
    }
}
