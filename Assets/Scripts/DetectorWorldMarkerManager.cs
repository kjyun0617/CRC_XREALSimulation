using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Preview-center detector placement flow.
/// QR scan selects detectorId, then marker preview follows the glasses/camera center
/// until PlaceDetector() is called. The placed marker keeps the same fixed size and
/// updates color by radiation value with a transparent material.
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

    [Header("Plane Intersection Placement")]
    [Tooltip("ON = place the preview at the intersection between the glasses' center gaze ray and a detected AR plane.")]
    [SerializeField] private bool usePlaneIntersectionPlacement = true;

    [Tooltip("ARPlaneManager on the XR Origin. It is found and enabled automatically when empty.")]
    [SerializeField] private ARPlaneManager planeManager;

    [Tooltip("Plane types to detect. XREAL supports horizontal and vertical planes.")]
    [SerializeField] private PlaneDetectionMode planeDetectionMode =
        PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;

    [Tooltip("Minimum gaze-ray distance accepted as a placement point.")]
    [SerializeField, Min(0f)] private float minPlaneHitDistanceMeters = 0.15f;

    [Tooltip("Maximum gaze-ray distance searched for a placement point.")]
    [SerializeField, Min(0.1f)] private float maxPlaneHitDistanceMeters = 10f;

    [Tooltip("Hide the detector preview until the center gaze ray intersects a detected plane polygon.")]
    [SerializeField] private bool hidePreviewWithoutPlaneHit = true;

    [SerializeField] private string waitingForPlaneStateLabel = "aim at a detected plane";

    [Header("Coordinate Storage")]
    [SerializeField] private bool useCoordinateDatabase = true;
    [SerializeField] private DetectorCoordinateDatabase coordinateDatabase;
    [SerializeField] private bool loadSavedCoordinatesOnStart = false;

    [Header("Spatial Anchor Storage")]
    [SerializeField] private bool useSpatialAnchors = false;
    [SerializeField] private DetectorSpatialAnchorManager spatialAnchorManager;
    [SerializeField] private bool createSpatialAnchorOnQr = false;
    [SerializeField] private bool parentMarkerToAnchor = true;

    [Header("Preview-Center Placement")]
    [Tooltip("ON = use camera/glasses center. OFF = use ZXing QR image center.")]
    [SerializeField] private bool usePreviewCenterPlacement = true;

    [Tooltip("Distance from glasses/camera to detector preview and final placed detector. Try 1.5 ~ 2.5m if too close.")]
    [SerializeField] private float defaultPlacementDistanceMeters = 2.0f;

    [Tooltip("ON = after QR scan, the marker preview follows the glasses/camera center until PlaceDetector() is called.")]
    [SerializeField] private bool followPreviewCenterUntilPlaced = true;

    [Tooltip("ON = save detector coordinate only when PlaceDetector() is called.")]
    [SerializeField] private bool saveCoordinateOnlyWhenPlaced = true;

    [Tooltip("Label text shown while the detector preview is following the glasses/camera center.")]
    [SerializeField] private string followingStateLabel = "following gaze";

    [Tooltip("Label text shown after PlaceDetector() fixes the detector in world space.")]
    [SerializeField] private string placedStateLabel = "placed";

    [Tooltip("Optional vertical offset while previewing/placing. Usually 0.")]
    [SerializeField] private float markerVerticalOffsetMeters = 0f;

    [Tooltip("Approximate Beam Pro rear camera horizontal FOV. Used only when not using pure center placement.")]
    [SerializeField] private float cameraHorizontalFovDegrees = 70f;

    [Tooltip("Approximate Beam Pro rear camera vertical FOV. Used only when not using pure center placement.")]
    [SerializeField] private float cameraVerticalFovDegrees = 50f;

    [Tooltip("OFF recommended for current preview-center flow. If ON, QR size can override default distance.")]
    [SerializeField] private bool useQrSizeToEstimateDistance = false;

    [Tooltip("Physical side length of printed QR code in meters. Example: 8cm = 0.08")]
    [SerializeField] private float realQrSizeMeters = 0.08f;

    [Tooltip("ZXing result points can be inside the QR, not full printed square.")]
    [SerializeField, Range(0.4f, 1.2f)] private float qrEffectiveSizeRatio = 0.70f;

    [SerializeField, Range(0.3f, 3.0f)] private float distanceCalibrationMultiplier = 1.0f;
    [SerializeField] private float minEstimatedDistanceMeters = 0.3f;
    [SerializeField] private float maxEstimatedDistanceMeters = 5.0f;

    [Header("Rescan Behavior")]
    [SerializeField] private bool updateExistingMarkerOnRescan = true;
    [SerializeField] private bool smoothPositionOnRescan = false;
    [SerializeField, Range(0.05f, 1.0f)] private float rescanPositionBlend = 0.55f;

    [Header("Marker Visual")]
    [Tooltip("Fixed world scale of every detector sphere. Radiation value changes color only, not size.")]
    [SerializeField] private float fixedMarkerSize = 0.20f;

    [Tooltip("Visible pixel coverage of the detector sphere. 0.35 keeps 35% of pixels and opens 65% as real see-through holes on XREAL.")]
    [SerializeField, Range(0.05f, 1.0f)] private float markerAlpha = 0.35f;

    [Tooltip("Use the included RadVis transparent shader so XREAL builds cannot fall back to an opaque material mode.")]
    [SerializeField] private bool forceDedicatedTransparentShader = true;

    [SerializeField] private bool showLabel = false;
    [SerializeField] private bool showDistanceInLabel = true;
    [SerializeField] private bool showAnchorStateInLabel = true;

    [Header("AR Glasses HUD")]
    [Tooltip("Creates a head-locked server/device HUD and off-screen detector arrows automatically.")]
    [SerializeField] private bool enableArGlassesHud = true;

    [Tooltip("Optional existing HUD. When empty, one is created automatically at runtime.")]
    [SerializeField] private ARDetectorHud arGlassesHud;

    [Header("Marker Label")]
    [Tooltip("Camera-relative world offset from the sphere center. Positive X is screen-right and negative Y is screen-down.")]
    [SerializeField] private Vector3 labelCameraOffsetMeters = new Vector3(0.16f, -0.13f, -0.01f);

    [Tooltip("World-space scale of the label. This is kept independent of Fixed Marker Size.")]
    [SerializeField, Min(0.001f)] private float labelWorldScale = 0.04f;

    [SerializeField, Min(0.1f)] private float labelFontSize = 4.5f;
    [SerializeField, Range(0f, 1f)] private float labelOutlineWidth = 0.2f;
    [SerializeField] private Color labelOutlineColor = Color.black;

    [Header("Radiation Thresholds")]
    [SerializeField] private float safeMax = 0.3f;
    [SerializeField] private float warningMax = 1.0f;

    private readonly Dictionary<string, MarkerInfo> markers = new Dictionary<string, MarkerInfo>();
    private bool spatialEventsSubscribed = false;
    private bool warnedAboutMissingPlaneManager = false;
    private string currentFollowingDetectorId = "";
    private Shader cachedDetectorTransparentShader;

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
        EnsurePlaneDetectionManager();
        EnsureCoordinateDatabase();
        EnsureSpatialAnchorManager();
        SubscribeSpatialEvents();
        EnsureArGlassesHud();

        if (loadSavedCoordinatesOnStart)
            LoadSavedCoordinatesWithoutAnchors();
    }

    private void LateUpdate()
    {
        EnsurePlacementOrigin();
        UpdateFollowingMarkerPosition();

        if (!showLabel || fallbackCamera == null)
            return;

        foreach (var kvp in markers)
        {
            UpdateLabelTransform(kvp.Value);
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
        marker.lastEstimatedDistance = estimatedDistance;

        if (followPreviewCenterUntilPlaced)
        {
            StopPreviousFollowingMarker(detectorId);

            currentFollowingDetectorId = detectorId;
            marker.isFollowingPlacementOrigin = true;
            marker.isPlaced = false;
            marker.anchorState = followingStateLabel;
            marker.lastEstimatedDistance = defaultPlacementDistanceMeters;

            if (marker.root != null)
            {
                marker.root.SetActive(true);

                if (marker.renderer != null)
                    marker.renderer.enabled = true;
            }

            UpdateMarkerVisual(marker, marker.lastRadiationValue);
            UpdateFollowingMarkerPosition(marker);

            string previewMode = usePlaneIntersectionPlacement
                ? "gaze-center plane intersection"
                : $"fixed distance ({defaultPlacementDistanceMeters:F2}m)";
            Debug.Log($"[DetectorWorldMarkerManager] Detector preview started: {detectorId}, mode={previewMode}");
            return;
        }

        marker.isFollowingPlacementOrigin = false;
        marker.isPlaced = true;
        marker.anchorState = useSpatialAnchors ? "anchor saving..." : placementMethod;
        UpdateMarkerVisual(marker, marker.lastRadiationValue);

        if (!saveCoordinateOnlyWhenPlaced)
        {
            SaveCoordinate(detectorId, worldPosition, worldRotation, estimatedDistance, qrPixelSize, placementImagePoint, imageWidth, imageHeight, placementMethod);
        }

        if (useSpatialAnchors && createSpatialAnchorOnQr)
            CreateAnchorForMarker(detectorId, worldPosition, worldRotation);

        Debug.Log($"[DetectorWorldMarkerManager] Detector placed from projection: {detectorId}, method={placementMethod}, pos={worldPosition}, distance={estimatedDistance:F2}m, qrPixelSize={qrPixelSize:F1}px");
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

        if (!marker.isFollowingPlacementOrigin || marker.isPlaced)
            return;

        UpdateFollowingMarkerPosition(marker);
    }

    private void UpdateFollowingMarkerPosition(MarkerInfo marker)
    {
        if (marker == null || marker.root == null)
            return;

        EnsurePlacementOrigin();
        if (placementOrigin == null)
        {
            Debug.LogWarning("[DetectorWorldMarkerManager] Cannot update detector preview. Placement Origin is missing.");
            return;
        }

        if (usePlaneIntersectionPlacement)
        {
            UpdateFollowingMarkerFromPlaneIntersection(marker);
            return;
        }

        float distance = defaultPlacementDistanceMeters;

        Vector3 direction = placementOrigin.forward.sqrMagnitude > 0.0001f
            ? placementOrigin.forward.normalized
            : Vector3.forward;

        Vector3 worldPosition = placementOrigin.position + direction * distance;
        worldPosition += placementOrigin.up * markerVerticalOffsetMeters;

        marker.root.SetActive(true);
        marker.root.transform.position = worldPosition;
        marker.root.transform.rotation = Quaternion.LookRotation(direction, placementOrigin.up);
        marker.root.transform.localScale = Vector3.one * fixedMarkerSize;

        if (marker.renderer != null)
            marker.renderer.enabled = true;

        marker.savedPosition = worldPosition;
        marker.lastEstimatedDistance = distance;
        marker.anchorState = followingStateLabel;
        marker.hasValidPlaneHit = false;
    }

    private void UpdateFollowingMarkerFromPlaneIntersection(MarkerInfo marker)
    {
        if (!TryGetGazePlaneIntersection(out Vector3 hitPosition, out Quaternion hitRotation, out float hitDistance))
        {
            marker.hasValidPlaneHit = false;
            marker.anchorState = waitingForPlaneStateLabel;

            if (hidePreviewWithoutPlaneHit && marker.root != null)
                marker.root.SetActive(false);

            return;
        }

        marker.hasValidPlaneHit = true;
        marker.root.SetActive(true);
        marker.root.transform.position = hitPosition;
        marker.root.transform.rotation = hitRotation;
        marker.root.transform.localScale = Vector3.one * fixedMarkerSize;

        if (marker.renderer != null)
            marker.renderer.enabled = true;

        marker.savedPosition = hitPosition;
        marker.lastEstimatedDistance = hitDistance;
        marker.lastPlacementMethod = "GazeCenterPlaneIntersection";
        marker.anchorState = $"{followingStateLabel} (plane)";
    }

    private bool TryGetGazePlaneIntersection(
        out Vector3 hitPosition,
        out Quaternion hitRotation,
        out float hitDistance)
    {
        hitPosition = Vector3.zero;
        hitRotation = Quaternion.identity;
        hitDistance = 0f;

        EnsurePlacementOrigin();
        EnsurePlaneDetectionManager();

        if (placementOrigin == null || planeManager == null || !planeManager.enabled)
            return false;

        Vector3 gazeDirection = placementOrigin.forward.normalized;
        if (gazeDirection.sqrMagnitude < 0.0001f)
            return false;

        Ray gazeRay = new Ray(placementOrigin.position, gazeDirection);
        float closestDistance = float.PositiveInfinity;
        ARPlane closestPlane = null;
        Vector3 closestPosition = Vector3.zero;

        foreach (ARPlane plane in planeManager.trackables)
        {
            if (plane == null || !plane.gameObject.activeInHierarchy || plane.trackingState == TrackingState.None)
                continue;

            Vector3 planeNormal = plane.transform.up;
            float denominator = Vector3.Dot(gazeRay.direction, planeNormal);
            if (Mathf.Abs(denominator) < 0.0001f)
                continue;

            float distance = Vector3.Dot(plane.transform.position - gazeRay.origin, planeNormal) / denominator;
            if (distance < minPlaneHitDistanceMeters ||
                distance > maxPlaneHitDistanceMeters ||
                distance >= closestDistance)
            {
                continue;
            }

            Vector3 candidatePosition = gazeRay.GetPoint(distance);
            if (!IsPointInsidePlaneBoundary(plane, candidatePosition))
                continue;

            closestDistance = distance;
            closestPosition = candidatePosition;
            closestPlane = plane;
        }

        if (closestPlane == null)
            return false;

        hitPosition = closestPosition;
        hitRotation = closestPlane.transform.rotation;
        hitDistance = closestDistance;
        return true;
    }

    private bool IsPointInsidePlaneBoundary(ARPlane plane, Vector3 worldPoint)
    {
        Vector3 localPoint3D = plane.transform.InverseTransformPoint(worldPoint);
        Vector2 localPoint = new Vector2(localPoint3D.x, localPoint3D.z);
        var boundary = plane.boundary;

        if (boundary.IsCreated && boundary.Length >= 3)
        {
            bool inside = false;
            int previous = boundary.Length - 1;

            for (int current = 0; current < boundary.Length; current++)
            {
                Vector2 a = boundary[current];
                Vector2 b = boundary[previous];

                bool crossesY = (a.y > localPoint.y) != (b.y > localPoint.y);
                if (crossesY)
                {
                    float intersectionX =
                        (b.x - a.x) * (localPoint.y - a.y) /
                        (b.y - a.y) + a.x;

                    if (localPoint.x < intersectionX)
                        inside = !inside;
                }

                previous = current;
            }

            return inside;
        }

        // Boundary data can be briefly unavailable on the first tracking frame.
        // Fall back to the plane's rectangular size until its polygon arrives.
        Vector2 halfSize = plane.size * 0.5f;
        Vector3 planeCenter3D = plane.center;
        Vector2 planeCenter = new Vector2(planeCenter3D.x, planeCenter3D.z);
        Vector2 pointFromCenter = localPoint - planeCenter;
        return Mathf.Abs(pointFromCenter.x) <= halfSize.x &&
               Mathf.Abs(pointFromCenter.y) <= halfSize.y;
    }

    public void PlaceDetector()
    {
        if (string.IsNullOrEmpty(currentFollowingDetectorId))
        {
            Debug.LogWarning("[DetectorWorldMarkerManager] PlaceDetector called, but no detector preview is following the view center.");
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

        if (marker.isFollowingPlacementOrigin && !marker.isPlaced)
            UpdateFollowingMarkerPosition(marker);

        if (usePlaneIntersectionPlacement && !marker.hasValidPlaneHit)
        {
            Debug.LogWarning($"[DetectorWorldMarkerManager] PlaceDetector blocked. The center gaze ray is not intersecting a detected plane: {detectorId}");
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
            ? "PreviewCenterButtonPlaced"
            : marker.lastPlacementMethod + "+ButtonPlaced";

        SaveCoordinate(
            detectorId,
            marker.savedPosition,
            worldRotation,
            marker.lastEstimatedDistance,
            marker.lastQrPixelSize,
            marker.lastPlacementImagePoint,
            marker.lastImageWidth,
            marker.lastImageHeight,
            placementMethod
        );

        if (useSpatialAnchors && createSpatialAnchorOnQr)
            CreateAnchorForMarker(detectorId, marker.savedPosition, worldRotation);

        ForceMarkerVisible(marker);
        UpdateMarkerVisual(marker, marker.lastRadiationValue);
        Debug.Log($"[DetectorWorldMarkerManager] Detector fixed: {detectorId}, pos={marker.savedPosition}, distance={marker.lastEstimatedDistance:F2}m, method={placementMethod}");
    }

    public void CancelCurrentFollowingDetector()
    {
        if (string.IsNullOrEmpty(currentFollowingDetectorId))
        {
            Debug.LogWarning("[DetectorWorldMarkerManager] Cancel called, but no detector preview is currently following the view center.");
            return;
        }

        string detectorId = currentFollowingDetectorId;
        currentFollowingDetectorId = "";

        if (!markers.TryGetValue(detectorId, out MarkerInfo marker) || marker == null)
        {
            Debug.LogWarning($"[DetectorWorldMarkerManager] Cancel failed. Marker not found: {detectorId}");
            return;
        }

        marker.isFollowingPlacementOrigin = false;
        marker.isPlaced = false;
        markers.Remove(detectorId);

        // Hide immediately, then destroy the cancelled preview. Removing it from
        // the dictionary also prevents later radiation updates from showing it again.
        if (marker.root != null)
        {
            marker.root.SetActive(false);
            Destroy(marker.root);
        }

        Debug.Log($"[DetectorWorldMarkerManager] Detector placement cancelled and preview removed: {detectorId}");
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

        if (usePreviewCenterPlacement)
        {
            Vector3 direct = placementOrigin.position + placementOrigin.forward.normalized * distance;
            direct += placementOrigin.up * markerVerticalOffsetMeters;
            return direct;
        }

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
        worldPosition += placementOrigin.up * markerVerticalOffsetMeters;
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

    private void EnsurePlaneDetectionManager()
    {
        if (!usePlaneIntersectionPlacement)
            return;

        if (planeManager == null)
            planeManager = FindFirstObjectByType<ARPlaneManager>(FindObjectsInactive.Include);

        if (planeManager == null)
        {
            if (!warnedAboutMissingPlaneManager)
            {
                Debug.LogError("[DetectorWorldMarkerManager] Plane intersection placement requires an ARPlaneManager on the XR Origin.");
                warnedAboutMissingPlaneManager = true;
            }

            return;
        }

        warnedAboutMissingPlaneManager = false;
        planeManager.requestedDetectionMode = planeDetectionMode;

        if (!planeManager.enabled)
            planeManager.enabled = true;
    }

    private void EnsureArGlassesHud()
    {
        if (!enableArGlassesHud)
            return;

        if (arGlassesHud == null)
            arGlassesHud = GetComponent<ARDetectorHud>();

        if (arGlassesHud == null)
            arGlassesHud = gameObject.AddComponent<ARDetectorHud>();

        arGlassesHud.Initialize(this, fallbackCamera);
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
            Debug.Log("[DetectorWorldMarkerManager] Created DetectorCoordinateDatabase automatically.");
        }
    }

    private void EnsureSpatialAnchorManager()
    {
        if (!useSpatialAnchors)
            return;

        if (spatialAnchorManager != null)
            return;

        spatialAnchorManager = FindObjectOfType<DetectorSpatialAnchorManager>();
    }

    private void SubscribeSpatialEvents()
    {
        if (!useSpatialAnchors || spatialAnchorManager == null || spatialEventsSubscribed)
            return;

        spatialAnchorManager.AnchorSaved += HandleAnchorCreatedAndSaved;
        spatialAnchorManager.AnchorLoaded += HandleAnchorLoaded;
        spatialAnchorManager.AnchorSaveFailed += HandleAnchorSaveFailed;
        spatialAnchorManager.AnchorLoadFailed += HandleAnchorLoadFailed;
        spatialEventsSubscribed = true;
    }

    private void UnsubscribeSpatialEvents()
    {
        if (spatialAnchorManager == null || !spatialEventsSubscribed)
            return;

        spatialAnchorManager.AnchorSaved -= HandleAnchorCreatedAndSaved;
        spatialAnchorManager.AnchorLoaded -= HandleAnchorLoaded;
        spatialAnchorManager.AnchorSaveFailed -= HandleAnchorSaveFailed;
        spatialAnchorManager.AnchorLoadFailed -= HandleAnchorLoadFailed;
        spatialEventsSubscribed = false;
    }

    private void CreateAnchorForMarker(string detectorId, Vector3 worldPosition, Quaternion worldRotation)
    {
        EnsureSpatialAnchorManager();
        SubscribeSpatialEvents();

        if (spatialAnchorManager != null && spatialAnchorManager.IsReady())
            spatialAnchorManager.CreateAndSaveAnchorForDetector(detectorId, worldPosition, worldRotation);
        else
            Debug.LogWarning("[DetectorWorldMarkerManager] Spatial anchor not created. Manager or ARAnchorManager missing.");
    }

    private void HandleAnchorCreatedAndSaved(string detectorId, ARAnchor anchor, string persistentGuid)
    {
        detectorId = NormalizeDetectorId(detectorId);
        if (!markers.TryGetValue(detectorId, out MarkerInfo marker) || marker == null)
            return;

        marker.anchor = anchor;
        marker.anchorGuid = persistentGuid;
        marker.anchorState = "anchor saved";
        marker.savedPosition = anchor.transform.position;
        marker.isFollowingPlacementOrigin = false;
        marker.isPlaced = true;

        if (parentMarkerToAnchor && anchor != null && marker.root != null)
            marker.root.transform.SetParent(anchor.transform, true);

        UpdateMarkerVisual(marker, marker.lastRadiationValue);
        Debug.Log($"[DetectorWorldMarkerManager] Anchor created and saved: {detectorId}, {persistentGuid}");
    }

    private void HandleAnchorLoaded(string detectorId, ARAnchor anchor, string persistentGuid)
    {
        detectorId = NormalizeDetectorId(detectorId);
        if (anchor == null)
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

        if (parentMarkerToAnchor && marker.root != null)
            marker.root.transform.SetParent(anchor.transform, true);

        if (coordinateDatabase != null && coordinateDatabase.TryGetRecord(detectorId, out DetectorCoordinateRecord record) && record.lastRadiationValue >= 0f)
            UpdateMarkerVisual(marker, record.lastRadiationValue);
        else
            UpdateMarkerVisual(marker, marker.lastRadiationValue);
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
                    UpdateMarkerVisual(marker, marker.lastRadiationValue);
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
            if (smoothPositionOnRescan && existing.anchor == null && !existing.isFollowingPlacementOrigin)
                finalPosition = Vector3.Lerp(existing.savedPosition, worldPosition, rescanPositionBlend);

            existing.root.transform.SetParent(parent != null && parentMarkerToAnchor ? parent : transform, true);
            existing.root.transform.position = finalPosition;
            existing.root.transform.localScale = Vector3.one * fixedMarkerSize;
            existing.savedPosition = finalPosition;

            if (estimatedDistance > 0f)
                existing.lastEstimatedDistance = estimatedDistance;
            if (qrPixelSize > 0f)
                existing.lastQrPixelSize = qrPixelSize;

            ForceMarkerVisible(existing);
            UpdateMarkerVisual(existing, existing.lastRadiationValue);
            return existing;
        }

        Transform markerParent = parent != null && parentMarkerToAnchor ? parent : transform;

        GameObject root = markerPrefab != null
            ? Instantiate(markerPrefab, worldPosition, Quaternion.identity, markerParent)
            : CreateDefaultSphere(worldPosition, markerParent);

        root.name = $"DetectorMarker_{detectorId}";
        root.transform.position = worldPosition;
        root.transform.localScale = Vector3.one * fixedMarkerSize;

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
        ForceMarkerVisible(info);
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
            Shader shader = GetDetectorTransparentShader();
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                shader = Shader.Find("Standard");

            if (shader != null)
                renderer.material = new Material(shader);

            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        Collider collider = sphere.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        return sphere;
    }

    private TMP_Text CreateLabel(Transform parent, string detectorId)
    {
        GameObject labelObject = new GameObject($"Label_{detectorId}");
        labelObject.transform.SetParent(parent, false);
        labelObject.transform.localPosition = Vector3.zero;
        labelObject.transform.localRotation = Quaternion.identity;

        TMP_Text label = labelObject.AddComponent<TextMeshPro>();
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = labelFontSize;
        label.text = detectorId;
        label.color = Color.white;
        label.outlineWidth = labelOutlineWidth;
        label.outlineColor = labelOutlineColor;

        SetLabelWorldScale(label.transform);

        return label;
    }

    private void UpdateLabelTransform(MarkerInfo marker)
    {
        if (marker == null || marker.root == null || marker.label == null || fallbackCamera == null)
            return;

        Transform cameraTransform = fallbackCamera.transform;
        Transform labelTransform = marker.label.transform;

        labelTransform.position =
            marker.root.transform.position +
            cameraTransform.right * labelCameraOffsetMeters.x +
            cameraTransform.up * labelCameraOffsetMeters.y +
            cameraTransform.forward * labelCameraOffsetMeters.z;

        labelTransform.rotation = cameraTransform.rotation;
        SetLabelWorldScale(labelTransform);
    }

    private void SetLabelWorldScale(Transform labelTransform)
    {
        if (labelTransform == null)
            return;

        Transform parent = labelTransform.parent;
        if (parent == null)
        {
            labelTransform.localScale = Vector3.one * labelWorldScale;
            return;
        }

        Vector3 parentScale = parent.lossyScale;
        labelTransform.localScale = new Vector3(
            SafeScaleDivision(labelWorldScale, parentScale.x),
            SafeScaleDivision(labelWorldScale, parentScale.y),
            SafeScaleDivision(labelWorldScale, parentScale.z)
        );
    }

    private float SafeScaleDivision(float desiredWorldScale, float parentScale)
    {
        return Mathf.Abs(parentScale) > 0.0001f
            ? desiredWorldScale / Mathf.Abs(parentScale)
            : desiredWorldScale;
    }

    private void UpdateMarkerVisual(MarkerInfo marker, float radiationValue)
    {
        if (marker == null || marker.root == null)
            return;

        marker.lastRadiationValue = radiationValue;

        Color color = GetRiskColor(radiationValue);
        color.a = markerAlpha;
        SetRendererTransparentColor(marker.renderer, color);

        // Radiation value affects color only. Size always stays fixed.
        marker.root.transform.localScale = Vector3.one * fixedMarkerSize;

        bool waitingForPlaneHit =
            usePlaneIntersectionPlacement &&
            marker.isFollowingPlacementOrigin &&
            !marker.hasValidPlaneHit;

        if (waitingForPlaneHit && hidePreviewWithoutPlaneHit)
            marker.root.SetActive(false);
        else
            ForceMarkerVisible(marker);

        UpdateLabel(marker, radiationValue, false);
    }

    private Color GetRiskColor(float radiationValue)
    {
        if (radiationValue < 0f)
            return new Color(0.65f, 0.65f, 0.65f, markerAlpha);

        if (radiationValue <= safeMax)
            return new Color(0.0f, 1.0f, 0.25f, markerAlpha);

        if (radiationValue <= warningMax)
            return new Color(1.0f, 0.85f, 0.0f, markerAlpha);

        return new Color(1.0f, 0.0f, 0.0f, markerAlpha);
    }

    private void ForceMarkerVisible(MarkerInfo marker)
    {
        if (marker == null)
            return;

        if (marker.root != null)
            marker.root.SetActive(true);

        if (marker.renderer != null)
            marker.renderer.enabled = true;

        if (showLabel && marker.label != null)
            marker.label.gameObject.SetActive(true);
    }

    private void SetRendererTransparentColor(Renderer renderer, Color color)
    {
        if (renderer == null || renderer.material == null)
            return;

        Material material = renderer.material;

        if (forceDedicatedTransparentShader)
        {
            Shader transparentShader = GetDetectorTransparentShader();
            if (transparentShader != null && material.shader != transparentShader)
                material.shader = transparentShader;
        }

        ConfigureTransparentMaterial(material);

        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);

        if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);
    }

    private void ConfigureTransparentMaterial(Material material)
    {
        if (material == null)
            return;

        // URP/Lit transparent setup.
        if (material.HasProperty("_Surface"))
            material.SetFloat("_Surface", 1f);
        if (material.HasProperty("_Blend"))
            material.SetFloat("_Blend", 0f);
        if (material.HasProperty("_AlphaClip"))
            material.SetFloat("_AlphaClip", 0f);
        if (material.HasProperty("_SrcBlend"))
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (material.HasProperty("_DstBlend"))
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (material.HasProperty("_ZWrite"))
            material.SetInt("_ZWrite", 0);

        material.SetOverrideTag("RenderType", "Transparent");
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHATEST_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.DisableKeyword("_ALPHAMODULATE_ON");
        material.SetShaderPassEnabled("ShadowCaster", false);
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

        // Built-in Standard shader Fade mode matches SrcAlpha/OneMinusSrcAlpha.
        if (material.HasProperty("_Mode"))
            material.SetFloat("_Mode", 2f);
    }

    private Shader GetDetectorTransparentShader()
    {
        if (cachedDetectorTransparentShader == null)
            cachedDetectorTransparentShader = Resources.Load<Shader>("RadVisDetectorTransparent");

        if (cachedDetectorTransparentShader == null)
            cachedDetectorTransparentShader = Shader.Find("RadVis/DetectorTransparent");

        return cachedDetectorTransparentShader;
    }

    private void UpdateLabel(MarkerInfo marker, float radiationValue, bool moved)
    {
        if (marker == null || marker.label == null)
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

                MarkerInfo marker = CreateOrMoveMarker(record.detectorId, record.GetPosition(), record.estimatedDistanceMeters, record.qrPixelSize, null);

                if (marker != null)
                {
                    marker.anchorState = useSpatialAnchors ? "fallback coord" : record.placementMethod;
                    marker.isFollowingPlacementOrigin = false;
                    marker.isPlaced = true;

                    if (record.lastRadiationValue >= 0f)
                        UpdateMarkerVisual(marker, record.lastRadiationValue);
                    else
                        UpdateMarkerVisual(marker, marker.lastRadiationValue);
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
        currentFollowingDetectorId = "";

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

    /// <summary>
    /// Copies placed detector positions and colors for the head-locked AR HUD.
    /// The caller owns and reuses the list to avoid per-frame allocations.
    /// </summary>
    public void FillHudMarkerStates(List<DetectorHudMarkerState> output)
    {
        if (output == null)
            return;

        output.Clear();

        foreach (var pair in markers)
        {
            MarkerInfo marker = pair.Value;
            if (marker == null || marker.root == null || !marker.isPlaced || !marker.root.activeInHierarchy)
                continue;

            Color color = GetRiskColor(marker.lastRadiationValue);
            color.a = 1f;

            output.Add(new DetectorHudMarkerState
            {
                detectorId = marker.detectorId,
                worldPosition = marker.root.transform.position,
                radiationValue = marker.lastRadiationValue,
                color = color
            });
        }
    }

    public struct DetectorHudMarkerState
    {
        public string detectorId;
        public Vector3 worldPosition;
        public float radiationValue;
        public Color color;
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
        public bool hasValidPlaneHit;
        public ARAnchor anchor;
        public string anchorGuid;
        public string anchorState;
    }
}
