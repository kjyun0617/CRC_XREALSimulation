using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Preview-center detector placement flow.
/// QR scan selects detectorId, then marker preview follows the glasses/camera center
/// until PlaceDetector() is called. The placed marker keeps the same fixed size and
/// updates the center color by CPS and draws faint inverse-square falloff shells.
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
    [SerializeField] private bool loadSavedCoordinatesOnStart = true;

    [Tooltip("Create a ROOM_ORIGIN QR reference frame for stable detector coordinates across Unity sessions.")]
    [SerializeField] private bool enableRoomCoordinateSystem = true;

    [Tooltip("Optional. Created on this GameObject automatically when empty.")]
    [SerializeField] private RoomCoordinateSystem roomCoordinateSystem;

    [Tooltip("Require ROOM_ORIGIN calibration before accepting detector QR placement. This prevents session-space coordinates from entering the room database.")]
    [SerializeField] private bool requireRoomCalibrationBeforeDetectorPlacement = true;

    [Header("Source Localization")]
    [Tooltip("Run the Phase-1 inverse-square/Poisson single-source estimator after ROOM_ORIGIN calibration.")]
    [SerializeField] private bool enableSingleSourceEstimator = true;

    [Tooltip("Optional. Created on this GameObject automatically when empty.")]
    [SerializeField] private RadiationSourceEstimator radiationSourceEstimator;

    [Header("Spatial Anchor Storage")]
    [SerializeField] private bool useSpatialAnchors = false;
    [SerializeField] private DetectorSpatialAnchorManager spatialAnchorManager;
    [SerializeField] private bool createSpatialAnchorOnQr = false;
    [SerializeField] private bool parentMarkerToAnchor = true;

    [Header("Server Visibility")]
    [Tooltip("Keep every restored and placed detector hidden until the WebSocket server is connected.")]
    [SerializeField] private bool hideMarkersUntilServerConnected = true;

    [Tooltip("Hide a placed detector until its ID exists in a recent complete CPS snapshot. This prevents saved values from looking live after reconnects or per-device dropouts.")]
    [SerializeField] private bool hideMarkersWithoutFreshRadiationData = true;

    [SerializeField, Min(0.5f)] private float maximumRadiationSnapshotAgeSeconds = 5f;

    [Tooltip("Optional. Found automatically when empty.")]
    [SerializeField] private RadiationReceiver radiationReceiver;

    [Tooltip("With multiple placed detectors, Cancel removes only the marker closest to the gaze center within this angle.")]
    [SerializeField, Range(1f, 45f)] private float cancelSelectionMaxAngleDegrees = 12f;

    [Header("Preview-Center Placement")]
    [Tooltip("ON = use camera/glasses center. OFF = use ZXing QR image center.")]
    [SerializeField] private bool usePreviewCenterPlacement = true;

    [Tooltip("Distance from glasses/camera to detector preview and final placed detector. Try 1.5 ~ 2.5m if too close.")]
    [SerializeField] private float defaultPlacementDistanceMeters = 2.0f;

    [Tooltip("ON = after QR scan, the marker preview follows the glasses/camera center until PlaceDetector() is called.")]
    [SerializeField] private bool followPreviewCenterUntilPlaced = true;

    [Tooltip("Label text shown while the detector preview is following the glasses/camera center.")]
    [SerializeField] private string followingStateLabel = "following gaze";

    [Tooltip("Label text shown after PlaceDetector() fixes the detector in world space.")]
    [SerializeField] private string placedStateLabel = "placed";

    [Tooltip("Optional vertical offset while previewing/placing. Usually 0.")]
    [SerializeField] private float markerVerticalOffsetMeters = 0f;

    [Header("Placement Preview Visual")]
    [Tooltip("ON = the detector preview always renders as a neutral gray sphere until PlaceDetector() fixes it, whatever the CPS reading is. Risk colors start only after placement.")]
    [SerializeField] private bool showGrayPreviewSphere = true;

    [Tooltip("Color of the preview sphere shown before placement.")]
    [SerializeField] private Color previewSphereColor = new Color(0.75f, 0.75f, 0.75f, 1f);

    [Tooltip("Opacity of the preview sphere. Keep it above Marker Alpha so the preview stays easy to see while aiming.")]
    [SerializeField, Range(0.05f, 1f)] private float previewSphereAlpha = 0.35f;

    [Tooltip("ON = the preview sphere stays visible before the WebSocket server is connected, even while placed detectors are still hidden.")]
    [SerializeField] private bool showPreviewBeforeServerConnected = true;

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

    [Tooltip("Opacity of the line-free center detector sphere.")]
    [SerializeField, Range(0.02f, 0.8f)] private float markerAlpha = 0.18f;

    [Tooltip("Use the included line-free transparent volume shader for consistent XREAL rendering.")]
    [SerializeField] private bool forceDedicatedTransparentShader = true;

    [Header("Inverse-Square Falloff Visualization")]
    [Tooltip("Show very faint inverse-square approach zones. This is a relative visual guide; source geometry, shielding, and detector calibration still determine the real safe distance.")]
    [SerializeField] private bool showFalloffShells = true;

    [Tooltip("Calibrated reference distance for I(r) = I0 * (reference/r)^2. At 8 cm, a 351 CPS reading produces roughly 0.47 m yellow and 1.06 m green boundaries.")]
    [SerializeField, Min(0.01f)] private float falloffReferenceDistanceMeters = 0.08f;

    [Tooltip("Maximum radius visualized around one detector.")]
    [SerializeField, Min(0.5f)] private float falloffMaxRadiusMeters = 5.0f;

    [Tooltip("Opacity of outer falloff spheres. Keep this very low so they do not obstruct the glasses view.")]
    [SerializeField, Range(0.002f, 0.12f)] private float falloffShellAlpha = 0.012f;

    [Tooltip("Maximum number of transition/boundary shells per detector.")]
    [SerializeField, Range(1, 3)] private int maxFalloffShells = 3;

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
    [Tooltip("CPS values at or below this value hide the center sphere completely.")]
    [SerializeField, Min(0f)] private float hiddenMaxCps = 2f;

    [Tooltip("CPS values above Hidden Max and at or below this value are green.")]
    [SerializeField, Min(0f)] private float greenMaxCps = 10f;

    [Tooltip("CPS values strictly above this value are red. Exactly 350 CPS remains yellow.")]
    [SerializeField, Min(0f)] private float dangerThresholdCps = 350f;

    [Header("Controller Detector Reposition")]
    [Tooltip("Allow a placed, currently visible detector to be highlighted and repositioned with the Beam Pro controller ray and center pad.")]
    [SerializeField] private bool enableControllerDetectorReposition = true;

    [Tooltip("Optional controller input bridge. It is created on this GameObject automatically when empty.")]
    [SerializeField] private DetectorControllerInteractor detectorControllerInteractor;

    [Tooltip("Maximum controller-ray distance at which a detector can be selected.")]
    [SerializeField, Min(0.5f)] private float controllerSelectionMaxDistanceMeters = 10f;

    [Tooltip("Small angular allowance around the controller ray, useful for distant 20 cm detector spheres.")]
    [SerializeField, Range(0.25f, 8f)] private float controllerSelectionAngleDegrees = 1.5f;

    [Tooltip("Multiplier applied to the visible center-sphere radius for controller hit testing.")]
    [SerializeField, Range(1f, 2f)] private float controllerSelectionRadiusMultiplier = 1.25f;

    [Tooltip("How much a hovered detector center color blends toward white.")]
    [SerializeField, Range(0f, 0.6f)] private float controllerHoverHighlightBlend = 0.20f;

    [Tooltip("Additional center-sphere opacity while a detector is hovered.")]
    [SerializeField, Range(0f, 0.5f)] private float controllerHoverAlphaBoost = 0.08f;

    [Tooltip("How much a detector being moved blends toward white.")]
    [SerializeField, Range(0f, 0.8f)] private float controllerMoveHighlightBlend = 0.32f;

    [Tooltip("Additional center-sphere opacity while a detector is being moved.")]
    [SerializeField, Range(0f, 0.6f)] private float controllerMoveAlphaBoost = 0.14f;

    private readonly Dictionary<string, MarkerInfo> markers =
        new Dictionary<string, MarkerInfo>(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> sortedHudDetectorIds = new List<string>();
    private readonly List<string> placedDetectorOrder = new List<string>();
    private readonly HashSet<string> liveRadiationDetectorIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool spatialEventsSubscribed = false;
    private bool warnedAboutMissingPlaneManager = false;
    private string currentFollowingDetectorId = "";
    private PlacementSession activePlacementSession;
    // PlaceDetector clears currentFollowingDetectorId after the preview is fixed.
    // Keep the most recently committed detector so Cancel Place can also remove
    // that exact sphere immediately after it has been placed.
    private string lastInteractedDetectorId = "";
    private Shader cachedDetectorTransparentShader;
    private bool serverConnected;
    private bool hasReceivedRadiationSnapshot;
    private bool lastSnapshotFreshnessState;
    private float lastRadiationSnapshotTime = float.NegativeInfinity;
    private MarkerInfo controllerHoveredMarker;
    private DetectorMoveSession activeDetectorMoveSession;

    private enum RadiationRiskBand
    {
        Unknown,
        Hidden,
        Green,
        Yellow,
        Red
    }

    private void OnEnable()
    {
        hasReceivedRadiationSnapshot = false;
        lastSnapshotFreshnessState = false;
        lastRadiationSnapshotTime = float.NegativeInfinity;
        liveRadiationDetectorIds.Clear();
        QRScanner.OnScanStarted += NotifyQrScanStarted;
        QRScanner.OnQRDetectedDetailed += HandleQrDetected;
        RadiationReceiver.OnRadiationDataReceived += HandleRadiationDataReceived;
        RadiationReceiver.OnServerConnectionChanged += HandleServerConnectionChanged;
        EnsureSpatialAnchorManager();
        SubscribeSpatialEvents();
    }

    private void OnDisable()
    {
        AbortControllerDetectorInteraction(true, false);
        QRScanner.OnScanStarted -= NotifyQrScanStarted;
        QRScanner.OnQRDetectedDetailed -= HandleQrDetected;
        RadiationReceiver.OnRadiationDataReceived -= HandleRadiationDataReceived;
        RadiationReceiver.OnServerConnectionChanged -= HandleServerConnectionChanged;
        UnsubscribeSpatialEvents();
    }

    private void OnDestroy()
    {
        AbortControllerDetectorInteraction(false, false);
        foreach (var pair in markers)
            DestroyMarkerVisualResources(pair.Value);
    }

    public bool HasActivePlacement =>
        activePlacementSession != null || !string.IsNullOrEmpty(currentFollowingDetectorId);

    public bool HasActiveDetectorMove => activeDetectorMoveSession != null;

    public string HoveredDetectorId =>
        controllerHoveredMarker != null ? controllerHoveredMarker.detectorId : "";

    public string ActiveMovedDetectorId =>
        activeDetectorMoveSession != null && activeDetectorMoveSession.marker != null
            ? activeDetectorMoveSession.marker.detectorId
            : "";

    /// <summary>
    /// Detector currently owned by the shared Place/Cancel controls.
    /// Exposed for the Beam Pro workflow guide only; placement remains owned here.
    /// </summary>
    public string ActivePlacementDetectorId =>
        activePlacementSession != null
            ? activePlacementSession.detectorId
            : currentFollowingDetectorId;

    /// <summary>
    /// True when the active detector preview can be committed at its current pose.
    /// In plane-intersection mode this becomes true only while the center gaze ray
    /// hits a tracked plane.
    /// </summary>
    public bool HasValidActivePlacementPose
    {
        get
        {
            string detectorId = NormalizeDetectorId(ActivePlacementDetectorId);
            if (string.IsNullOrEmpty(detectorId) ||
                !markers.TryGetValue(detectorId, out MarkerInfo marker) ||
                marker == null || marker.root == null)
            {
                return false;
            }

            return !usePlaneIntersectionPlacement || marker.hasValidPlaneHit;
        }
    }

    /// <summary>
    /// Number of committed detector visuals materialized in the currently
    /// localized room. Hidden-by-CPS/server markers are still counted.
    /// </summary>
    public int CurrentRoomPlacedDetectorCount
    {
        get
        {
            int count = 0;
            foreach (var pair in markers)
            {
                MarkerInfo marker = pair.Value;
                if (marker != null && marker.root != null && marker.isPlaced)
                    count++;
            }

            return count;
        }
    }

    /// <summary>
    /// Peeks the same LIFO detector that Cancel Place will remove without changing
    /// any marker or saved coordinate state.
    /// </summary>
    public bool TryPeekLastPlacedDetector(
        out string detectorId,
        out bool waitingForRestore)
    {
        bool found = TryGetMostRecentlyPlacedDetector(
            out detectorId,
            out string pendingDetectorId);

        waitingForRestore =
            !found && !string.IsNullOrEmpty(pendingDetectorId);
        if (waitingForRestore)
            detectorId = pendingDetectorId;

        return found || waitingForRestore;
    }

    /// <summary>
    /// Updates the controller-ray hover without relying on colliders. Detector
    /// falloff shells intentionally have no colliders, so the center ray is tested
    /// directly against each visible center sphere.
    /// </summary>
    public bool TryUpdateDetectorHover(Ray pointerRay, out string detectorId)
    {
        detectorId = "";

        if (!enableControllerDetectorReposition)
        {
            SetControllerHoveredMarker(null);
            return false;
        }

        if (activeDetectorMoveSession != null)
        {
            MarkerInfo movingMarker = activeDetectorMoveSession.marker;
            if (movingMarker != null && movingMarker.root != null)
            {
                SetControllerHoveredMarker(movingMarker);
                detectorId = movingMarker.detectorId;
                return true;
            }

            AbortControllerDetectorInteraction(false, false);
            return false;
        }

        if (HasActivePlacement ||
            (roomCoordinateSystem != null && roomCoordinateSystem.HasPendingPlacement))
        {
            SetControllerHoveredMarker(null);
            return false;
        }

        Vector3 rayDirection = pointerRay.direction;
        if (!IsFiniteVector(rayDirection) || rayDirection.sqrMagnitude < 0.0001f)
        {
            SetControllerHoveredMarker(null);
            return false;
        }

        rayDirection.Normalize();
        float maximumDistance = Mathf.Max(0.5f, controllerSelectionMaxDistanceMeters);
        float angularAllowance = Mathf.Tan(
            Mathf.Clamp(controllerSelectionAngleDegrees, 0.25f, 8f) * Mathf.Deg2Rad);
        bool selectedDirectHit = false;
        float closestEntryDepth = float.PositiveInfinity;
        float closestNormalizedMiss = float.PositiveInfinity;
        MarkerInfo selectedMarker = null;

        foreach (var pair in markers)
        {
            MarkerInfo marker = pair.Value;
            if (!IsMarkerSelectableByController(marker))
                continue;

            Vector3 toMarker = marker.root.transform.position - pointerRay.origin;
            float depth = Vector3.Dot(toMarker, rayDirection);
            if (depth <= 0.02f || depth > maximumDistance)
                continue;

            float perpendicularSquared =
                Mathf.Max(0f, toMarker.sqrMagnitude - depth * depth);
            float visibleRadius = Mathf.Max(0.01f, fixedMarkerSize * 0.5f);
            if (marker.renderer != null)
            {
                Vector3 extents = marker.renderer.bounds.extents;
                visibleRadius = Mathf.Max(
                    visibleRadius,
                    Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z)));
            }

            float hitRadius =
                visibleRadius * Mathf.Max(1f, controllerSelectionRadiusMultiplier);
            bool directHit = perpendicularSquared <= hitRadius * hitRadius;
            float allowedRadius = Mathf.Max(
                hitRadius,
                depth * angularAllowance);
            if (perpendicularSquared > allowedRadius * allowedRadius)
                continue;

            float normalizedMiss =
                Mathf.Sqrt(perpendicularSquared) / Mathf.Max(0.001f, depth);
            float entryDepth = directHit
                ? depth - Mathf.Sqrt(
                    Mathf.Max(0f, hitRadius * hitRadius - perpendicularSquared))
                : depth;
            bool shouldSelect =
                selectedMarker == null ||
                (directHit && !selectedDirectHit) ||
                (directHit == selectedDirectHit &&
                 (entryDepth < closestEntryDepth ||
                  (Mathf.Approximately(entryDepth, closestEntryDepth) &&
                   normalizedMiss < closestNormalizedMiss)));
            if (shouldSelect)
            {
                selectedDirectHit = directHit;
                closestEntryDepth = entryDepth;
                closestNormalizedMiss = normalizedMiss;
                selectedMarker = marker;
            }
        }

        SetControllerHoveredMarker(selectedMarker);
        if (selectedMarker == null)
            return false;

        detectorId = selectedMarker.detectorId;
        return true;
    }

    public void ClearDetectorHover()
    {
        if (activeDetectorMoveSession == null)
            SetControllerHoveredMarker(null);
    }

    public bool TryBeginPointedDetectorMove(Ray pointerRay, out string resultMessage)
    {
        resultMessage = "No detector is pointed at";

        if (!enableControllerDetectorReposition)
        {
            resultMessage = "Controller detector movement is disabled";
            return false;
        }

        if (activeDetectorMoveSession != null)
        {
            resultMessage = $"Detector already moving: {ActiveMovedDetectorId}";
            return false;
        }

        if (HasActivePlacement)
        {
            resultMessage = "Place or cancel the detector preview first";
            return false;
        }

        if (roomCoordinateSystem != null && roomCoordinateSystem.HasPendingPlacement)
        {
            resultMessage = "Place or cancel ROOM_ORIGIN first";
            return false;
        }

        if (!TryUpdateDetectorHover(pointerRay, out string detectorId) ||
            controllerHoveredMarker == null ||
            controllerHoveredMarker.root == null)
        {
            return false;
        }

        Vector3 initialDirection = pointerRay.direction;
        if (!IsFiniteVector(initialDirection) || initialDirection.sqrMagnitude < 0.0001f)
        {
            resultMessage = "Controller pointing direction is unavailable";
            return false;
        }

        initialDirection.Normalize();
        MarkerInfo marker = controllerHoveredMarker;
        Transform markerTransform = marker.root.transform;
        activeDetectorMoveSession = new DetectorMoveSession
        {
            marker = marker,
            parent = markerTransform.parent,
            worldPosition = markerTransform.position,
            worldRotation = markerTransform.rotation,
            localPosition = markerTransform.localPosition,
            localRotation = markerTransform.localRotation,
            localScale = markerTransform.localScale,
            visibilityRequested = marker.visibilityRequested,
            savedPosition = marker.savedPosition,
            lastEstimatedDistance = marker.lastEstimatedDistance,
            lastPlacementMethod = marker.lastPlacementMethod,
            anchor = marker.anchor,
            anchorGuid = marker.anchorGuid,
            anchorState = marker.anchorState,
            initialPointerDirection = initialDirection,
            initialOffsetFromRayOrigin = markerTransform.position - pointerRay.origin
        };

        // A marker parented to a legacy detector anchor must be detached while it
        // moves. Its original hierarchy is retained in the transaction for rollback.
        markerTransform.SetParent(transform, true);
        if (useSpatialAnchors && spatialAnchorManager != null)
            spatialAnchorManager.InvalidatePendingOperationForDetector(detectorId);

        marker.isControllerMoving = true;
        marker.isControllerHovered = true;
        if (enableSingleSourceEstimator && radiationSourceEstimator != null)
            radiationSourceEstimator.ClearEstimate();

        UpdateMarkerVisual(marker, marker.lastRadiationValue);
        resultMessage = $"Moving detector: {detectorId}";
        Debug.Log($"[DetectorWorldMarkerManager] Controller move started: {detectorId}");
        return true;
    }

    public bool UpdateActiveDetectorMove(Ray pointerRay)
    {
        if (activeDetectorMoveSession == null ||
            activeDetectorMoveSession.marker == null ||
            activeDetectorMoveSession.marker.root == null)
        {
            return false;
        }

        Vector3 currentDirection = pointerRay.direction;
        if (!IsFiniteVector(pointerRay.origin) ||
            !IsFiniteVector(currentDirection) ||
            currentDirection.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        currentDirection.Normalize();
        Quaternion directionDelta = Quaternion.FromToRotation(
            activeDetectorMoveSession.initialPointerDirection,
            currentDirection);
        Vector3 newPosition =
            pointerRay.origin +
            directionDelta * activeDetectorMoveSession.initialOffsetFromRayOrigin;
        if (!IsFiniteVector(newPosition))
            return false;

        MarkerInfo marker = activeDetectorMoveSession.marker;
        marker.root.transform.SetPositionAndRotation(
            newPosition,
            activeDetectorMoveSession.worldRotation);
        UpdateLabel(marker, marker.lastRadiationValue, true);
        return true;
    }

    public bool TryEndActiveDetectorMove(bool commit, out string resultMessage)
    {
        return EndActiveDetectorMove(commit, true, out resultMessage);
    }

    public bool TryEndActiveDetectorMove(
        bool commit,
        bool refreshEstimator,
        out string resultMessage)
    {
        return EndActiveDetectorMove(
            commit,
            refreshEstimator,
            out resultMessage);
    }

    private bool EndActiveDetectorMove(
        bool commit,
        bool refreshEstimator,
        out string resultMessage)
    {
        if (activeDetectorMoveSession == null)
        {
            resultMessage = "No detector is being moved";
            return false;
        }

        DetectorMoveSession session = activeDetectorMoveSession;
        activeDetectorMoveSession = null;
        MarkerInfo marker = session.marker;
        if (marker == null || marker.root == null)
        {
            resultMessage = "The moving detector is no longer available";
            return false;
        }

        marker.isControllerMoving = false;
        if (!commit)
        {
            RestoreDetectorMoveSession(session);
            if (refreshEstimator)
                RefreshRadiationSourceAfterDetectorMove();

            resultMessage = $"Detector move cancelled: {marker.detectorId}";
            Debug.Log($"[DetectorWorldMarkerManager] Controller move cancelled: {marker.detectorId}");
            return true;
        }

        Vector3 committedPosition = marker.root.transform.position;
        Quaternion committedRotation = session.worldRotation;
        marker.root.transform.SetPositionAndRotation(committedPosition, committedRotation);
        marker.savedPosition = committedPosition;
        marker.lastPlacementMethod = "ControllerRayRepositioned";

        SaveCoordinate(
            marker.detectorId,
            committedPosition,
            committedRotation,
            marker.lastEstimatedDistance,
            marker.lastQrPixelSize,
            marker.lastPlacementImagePoint,
            marker.lastImageWidth,
            marker.lastImageHeight,
            marker.lastPlacementMethod);

        // ROOM_ORIGIN remains authoritative when calibrated. In the legacy anchor
        // path this creates a replacement anchor and preserves restart persistence.
        FinalizeSpatialBinding(
            marker.detectorId,
            marker,
            committedPosition,
            committedRotation,
            true);

        ForceMarkerVisible(marker);
        UpdateMarkerVisual(marker, marker.lastRadiationValue);
        if (refreshEstimator)
            RefreshRadiationSourceAfterDetectorMove();

        resultMessage = $"Detector moved: {marker.detectorId}";
        Debug.Log(
            $"[DetectorWorldMarkerManager] Controller move committed: " +
            $"{marker.detectorId}, pos={committedPosition}");
        return true;
    }

    private void RestoreDetectorMoveSession(DetectorMoveSession session)
    {
        if (session == null || session.marker == null || session.marker.root == null)
            return;

        MarkerInfo marker = session.marker;
        Transform markerTransform = marker.root.transform;
        markerTransform.SetParent(session.parent, false);
        if (session.parent != null)
        {
            markerTransform.localPosition = session.localPosition;
            markerTransform.localRotation = session.localRotation;
        }
        else
        {
            markerTransform.SetPositionAndRotation(
                session.worldPosition,
                session.worldRotation);
        }

        markerTransform.localScale = session.localScale;
        marker.savedPosition = session.savedPosition;
        marker.lastEstimatedDistance = session.lastEstimatedDistance;
        marker.lastPlacementMethod = session.lastPlacementMethod;
        marker.visibilityRequested = session.visibilityRequested;
        marker.anchor = session.anchor;
        marker.anchorGuid = session.anchorGuid;
        marker.anchorState = session.anchorState;
        marker.isControllerMoving = false;

        UpdateMarkerVisual(marker, marker.lastRadiationValue);
        if (session.parent != null)
        {
            markerTransform.localPosition = session.localPosition;
            markerTransform.localRotation = session.localRotation;
        }
        else
        {
            markerTransform.SetPositionAndRotation(
                session.worldPosition,
                session.worldRotation);
        }

        markerTransform.localScale = session.localScale;
        marker.visibilityRequested = session.visibilityRequested;
        ApplyMarkerVisibility(marker);
        UpdateLabel(marker, marker.lastRadiationValue, false);
    }

    private void RefreshRadiationSourceAfterDetectorMove()
    {
        if (!enableSingleSourceEstimator)
            return;

        EnsureRadiationSourceEstimator();
        if (radiationSourceEstimator == null)
            return;

        radiationSourceEstimator.ClearEstimate();
        radiationSourceEstimator.RequestEstimateNow();
    }

    private void AbortControllerDetectorInteraction(
        bool restoreActiveMove,
        bool refreshEstimator)
    {
        if (activeDetectorMoveSession != null)
        {
            if (restoreActiveMove)
            {
                EndActiveDetectorMove(false, refreshEstimator, out _);
            }
            else
            {
                if (activeDetectorMoveSession.marker != null)
                {
                    activeDetectorMoveSession.marker.isControllerMoving = false;
                    activeDetectorMoveSession.marker.isControllerHovered = false;
                }

                activeDetectorMoveSession = null;
            }
        }

        SetControllerHoveredMarker(null);
    }

    private void SetControllerHoveredMarker(MarkerInfo marker)
    {
        if (ReferenceEquals(controllerHoveredMarker, marker))
            return;

        MarkerInfo previous = controllerHoveredMarker;
        controllerHoveredMarker = marker;

        if (previous != null)
        {
            previous.isControllerHovered = false;
            if (previous.root != null)
                UpdateMarkerVisual(previous, previous.lastRadiationValue);
        }

        if (controllerHoveredMarker != null)
        {
            controllerHoveredMarker.isControllerHovered = true;
            if (controllerHoveredMarker.root != null)
            {
                UpdateMarkerVisual(
                    controllerHoveredMarker,
                    controllerHoveredMarker.lastRadiationValue);
            }
        }
    }

    private bool IsMarkerSelectableByController(MarkerInfo marker)
    {
        return marker != null &&
               marker.root != null &&
               marker.root.activeInHierarchy &&
               marker.isPlaced &&
               !marker.isFollowingPlacementOrigin &&
               marker.centerVisualRequested &&
               marker.renderer != null &&
               marker.renderer.enabled;
    }

    private bool IsFiniteVector(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
               !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }

    public void NotifyQrScanStarted()
    {
        AbortControllerDetectorInteraction(true, true);
        // Starting a new scan invalidates the old one-step delete token. Cancel
        // during camera startup must stop the scan, not delete a previous detector.
        lastInteractedDetectorId = "";
    }

    private void Start()
    {
        EnsurePlacementOrigin();
        EnsurePlaneDetectionManager();
        EnsureCoordinateDatabase();
        EnsureRoomCoordinateSystem();
        EnsureRadiationSourceEstimator();
        EnsureSpatialAnchorManager();
        EnsureRadiationReceiver();
        EnsureDetectorControllerInteractor();
        SubscribeSpatialEvents();
        EnsureArGlassesHud();
        InitializePlacedDetectorOrderFromDatabase();

        SetServerConnectionState(radiationReceiver != null && radiationReceiver.IsConnected);

        if (loadSavedCoordinatesOnStart)
            LoadSavedCoordinatesWithoutAnchors();
    }

    private void LateUpdate()
    {
        RefreshRadiationSnapshotVisibilityIfExpired();
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
        AbortControllerDetectorInteraction(true, true);

        // ROOM_ORIGIN is a coordinate-frame command, not a radiation detector ID.
        // RoomCoordinateSystem owns its preview/confirmation transaction.
        if (RoomCoordinateSystem.IsRoomOriginCode(qrText))
        {
            CancelActivePlacementOnly();
            return;
        }

        if (roomCoordinateSystem != null)
            roomCoordinateSystem.CancelPendingPlacementForDetectorScan();

        if (enableRoomCoordinateSystem &&
            requireRoomCalibrationBeforeDetectorPlacement &&
            (roomCoordinateSystem == null || !roomCoordinateSystem.IsCalibrated))
        {
            CancelActivePlacementOnly();
            Debug.LogWarning(
                "[DetectorWorldMarkerManager] Detector QR ignored until ROOM_ORIGIN " +
                "is scanned and placed on its vertical wall.");
            RoomCoordinateSystem.PublishStatus(
                "Place ROOM_ORIGIN before scanning detectors",
                Color.yellow);
            return;
        }

        string detectorId = NormalizeDetectorId(qrText);
        if (string.IsNullOrEmpty(detectorId))
            return;

        // A new scan starts a new transaction; a later Cancel must never fall
        // through to a detector committed before this scan.
        lastInteractedDetectorId = "";

        if (!updateExistingMarkerOnRescan &&
            markers.TryGetValue(detectorId, out MarkerInfo existingMarker) &&
            existingMarker != null && existingMarker.root != null && existingMarker.isPlaced)
        {
            // The QR scanner has already switched out of scan mode. End any other
            // pending transaction too, otherwise Place would act on the wrong ID.
            RollbackActivePlacementSession();
            Debug.Log($"[DetectorWorldMarkerManager] Rescan ignored because updating existing markers is disabled: {existingMarker.detectorId}");
            return;
        }

        if (followPreviewCenterUntilPlaced)
        {
            detectorId = BeginPlacementSession(detectorId);
        }
        else
        {
            // Immediate-placement mode cannot share state with a pending preview.
            // Restore/remove that preview before touching the incoming detector.
            RollbackActivePlacementSession();
        }

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
            currentFollowingDetectorId = marker.detectorId;
            marker.isFollowingPlacementOrigin = true;
            marker.isPlaced = false;
            marker.anchorState = followingStateLabel;
            marker.lastEstimatedDistance = defaultPlacementDistanceMeters;

            if (marker.root != null)
            {
                SetMarkerRequestedVisibility(marker, true);
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
        lastInteractedDetectorId = marker.detectorId;
        UpdateMarkerVisual(marker, marker.lastRadiationValue);

        // This branch commits immediately, so it is already a placed detector.
        // Persist it regardless of whether the button-driven flow is configured to
        // defer saves until its Place action.
        SaveCoordinate(detectorId, worldPosition, worldRotation, estimatedDistance, qrPixelSize, placementImagePoint, imageWidth, imageHeight, placementMethod);
        RecordPlacedDetector(marker.detectorId, true);

        FinalizeSpatialBinding(detectorId, marker, worldPosition, worldRotation);

        Debug.Log($"[DetectorWorldMarkerManager] Detector placed from projection: {detectorId}, method={placementMethod}, pos={worldPosition}, distance={estimatedDistance:F2}m, qrPixelSize={qrPixelSize:F1}px");
    }

    /// <summary>
    /// Starts one isolated placement transaction. A new detector automatically rolls
    /// back the previous transaction so an unreachable, unplaced sphere cannot remain.
    /// Re-scanning an already placed detector snapshots its committed state first.
    /// </summary>
    private string BeginPlacementSession(string detectorId)
    {
        detectorId = NormalizeDetectorId(detectorId);
        if (string.IsNullOrEmpty(detectorId))
            return "";

        if (activePlacementSession != null &&
            DetectorIdsEqual(activePlacementSession.detectorId, detectorId) &&
            markers.TryGetValue(activePlacementSession.detectorId, out MarkerInfo activeMarker) &&
            activeMarker != null && activeMarker.root != null)
        {
            currentFollowingDetectorId = activePlacementSession.detectorId;
            return activePlacementSession.detectorId;
        }

        RollbackActivePlacementSession();

        MarkerInfo existing = null;
        if (markers.TryGetValue(detectorId, out existing) &&
            (existing == null || existing.root == null))
        {
            markers.Remove(detectorId);
            existing = null;
        }

        string canonicalDetectorId = existing != null
            ? existing.detectorId
            : detectorId;

        if (useSpatialAnchors)
        {
            EnsureSpatialAnchorManager();
            if (spatialAnchorManager != null)
                spatialAnchorManager.InvalidatePendingOperationForDetector(canonicalDetectorId);
        }

        activePlacementSession = CapturePlacementSession(canonicalDetectorId, existing);
        if (activePlacementSession.restoresCommittedMarker &&
            existing.anchor == null &&
            string.Equals(existing.anchorState, "anchor saving...", StringComparison.OrdinalIgnoreCase))
        {
            // The generation invalidation above cancelled that save. If this rescan
            // is cancelled too, restore an honest coordinate-fallback state.
            activePlacementSession.anchorState = $"{placedStateLabel} (coordinate)";
        }

        currentFollowingDetectorId = canonicalDetectorId;
        return canonicalDetectorId;
    }

    private PlacementSession CapturePlacementSession(string detectorId, MarkerInfo marker)
    {
        PlacementSession session = new PlacementSession
        {
            detectorId = detectorId,
            restoresCommittedMarker = marker != null && marker.root != null && marker.isPlaced
        };

        if (!session.restoresCommittedMarker)
            return session;

        session.parent = marker.root.transform.parent;
        session.worldPosition = marker.root.transform.position;
        session.worldRotation = marker.root.transform.rotation;
        session.localPosition = marker.root.transform.localPosition;
        session.localRotation = marker.root.transform.localRotation;
        session.localScale = marker.root.transform.localScale;
        session.visibilityRequested = marker.visibilityRequested;
        session.savedPosition = marker.savedPosition;
        session.lastEstimatedDistance = marker.lastEstimatedDistance;
        session.lastQrPixelSize = marker.lastQrPixelSize;
        session.lastPlacementImagePoint = marker.lastPlacementImagePoint;
        session.lastImageWidth = marker.lastImageWidth;
        session.lastImageHeight = marker.lastImageHeight;
        session.lastPlacementMethod = marker.lastPlacementMethod;
        session.hasValidPlaneHit = marker.hasValidPlaneHit;
        session.anchorState = marker.anchorState;
        return session;
    }

    /// <summary>
    /// Restores a detector that was already committed, or removes a brand-new preview.
    /// Persistent coordinate/anchor data is deliberately left untouched.
    /// </summary>
    private bool RollbackActivePlacementSession()
    {
        PlacementSession session = activePlacementSession;
        string detectorId = session != null
            ? session.detectorId
            : currentFollowingDetectorId;

        activePlacementSession = null;
        currentFollowingDetectorId = "";

        detectorId = NormalizeDetectorId(detectorId);
        if (string.IsNullOrEmpty(detectorId) ||
            !markers.TryGetValue(detectorId, out MarkerInfo marker))
            return false;

        if (marker == null)
        {
            markers.Remove(detectorId);
            return false;
        }

        if (session != null && session.restoresCommittedMarker && marker.root != null)
        {
            if (session.parent != null)
            {
                // Restore in the anchor's local coordinate system so any anchor
                // relocalization that happened during the preview is respected.
                marker.root.transform.SetParent(session.parent, false);
                marker.root.transform.localPosition = session.localPosition;
                marker.root.transform.localRotation = session.localRotation;
            }
            else
            {
                marker.root.transform.SetParent(transform, true);
                marker.root.transform.SetPositionAndRotation(session.worldPosition, session.worldRotation);
            }

            marker.root.transform.localScale = session.localScale;

            marker.savedPosition = session.savedPosition;
            marker.lastEstimatedDistance = session.lastEstimatedDistance;
            marker.lastQrPixelSize = session.lastQrPixelSize;
            marker.lastPlacementImagePoint = session.lastPlacementImagePoint;
            marker.lastImageWidth = session.lastImageWidth;
            marker.lastImageHeight = session.lastImageHeight;
            marker.lastPlacementMethod = session.lastPlacementMethod;
            marker.isFollowingPlacementOrigin = false;
            marker.isPlaced = true;
            marker.hasValidPlaneHit = session.hasValidPlaneHit;
            marker.anchorState = session.anchorState;

            UpdateMarkerVisual(marker, marker.lastRadiationValue);

            // UpdateMarkerVisual enforces the preview scale/visibility, so restore
            // the exact committed transform and visibility afterwards.
            if (session.parent != null)
            {
                marker.root.transform.localPosition = session.localPosition;
                marker.root.transform.localRotation = session.localRotation;
            }
            else
            {
                marker.root.transform.SetPositionAndRotation(session.worldPosition, session.worldRotation);
            }

            marker.root.transform.localScale = session.localScale;
            marker.visibilityRequested = session.visibilityRequested;
            ApplyMarkerVisibility(marker);

            UpdateLabel(marker, marker.lastRadiationValue, false);
            return true;
        }

        markers.Remove(detectorId);
        marker.isFollowingPlacementOrigin = false;
        marker.isPlaced = false;
        DestroyMarkerVisualResources(marker);

        if (marker.root != null)
        {
            SetMarkerRequestedVisibility(marker, false);
            Destroy(marker.root);
        }

        return false;
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

        SetMarkerRequestedVisibility(marker, true);
        marker.root.transform.position = worldPosition;
        marker.root.transform.rotation = Quaternion.LookRotation(direction, placementOrigin.up);
        marker.root.transform.localScale = Vector3.one * fixedMarkerSize;

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

            if (hidePreviewWithoutPlaneHit)
            {
                if (marker.root != null)
                    SetMarkerRequestedVisibility(marker, false);

                return;
            }

            // Keep the preview attached to the gaze center at the default distance
            // instead of leaving it frozen at a stale pose while plane tracking
            // catches up. PlaceDetector still refuses to commit without a plane hit.
            Vector3 fallbackDirection = placementOrigin.forward.sqrMagnitude > 0.0001f
                ? placementOrigin.forward.normalized
                : Vector3.forward;

            Vector3 fallbackPosition =
                placementOrigin.position +
                fallbackDirection * defaultPlacementDistanceMeters +
                placementOrigin.up * markerVerticalOffsetMeters;

            SetMarkerRequestedVisibility(marker, true);
            marker.root.transform.position = fallbackPosition;
            marker.root.transform.rotation =
                Quaternion.LookRotation(fallbackDirection, placementOrigin.up);
            marker.root.transform.localScale = Vector3.one * fixedMarkerSize;

            marker.savedPosition = fallbackPosition;
            marker.lastEstimatedDistance = defaultPlacementDistanceMeters;
            return;
        }

        marker.hasValidPlaneHit = true;
        SetMarkerRequestedVisibility(marker, true);
        marker.root.transform.position = hitPosition;
        marker.root.transform.rotation = hitRotation;
        marker.root.transform.localScale = Vector3.one * fixedMarkerSize;

        marker.savedPosition = hitPosition;
        marker.lastEstimatedDistance = hitDistance;
        marker.lastPlacementMethod = "GazeCenterPlaneIntersection";
        marker.anchorState = $"{followingStateLabel} (plane)";
    }

    private bool TryGetGazePlaneIntersection(
        out Vector3 hitPosition,
        out Quaternion hitRotation,
        out float hitDistance,
        bool requireVerticalSurface = false,
        float maximumVerticalNormalDot = 0.35f)
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

            // ROOM_ORIGIN must use a wall. Filter candidates before choosing the
            // nearest intersection; otherwise a nearer floor can permanently mask
            // the valid wall behind it.
            if (requireVerticalSurface &&
                Mathf.Abs(Vector3.Dot(planeNormal.normalized, Vector3.up)) >
                Mathf.Clamp01(maximumVerticalNormalDot))
            {
                continue;
            }

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

    public bool TryPlaceCurrentDetector(out string resultMessage)
    {
        string detectorId = NormalizeDetectorId(ActivePlacementDetectorId);
        if (string.IsNullOrEmpty(detectorId))
        {
            resultMessage = "Scan a detector QR before placing";
            return false;
        }

        if (!markers.TryGetValue(detectorId, out MarkerInfo marker) ||
            marker == null || marker.root == null)
        {
            resultMessage = $"Detector preview not found: {detectorId}";
            return false;
        }

        if (marker.isFollowingPlacementOrigin && !marker.isPlaced)
            UpdateFollowingMarkerPosition(marker);

        if (usePlaneIntersectionPlacement && !marker.hasValidPlaneHit)
        {
            resultMessage =
                $"Aim the glasses center at a detected surface for {marker.detectorId}";
            return false;
        }

        PlaceDetector(marker.detectorId);
        bool placed = marker.isPlaced && !marker.isFollowingPlacementOrigin;
        resultMessage = placed
            ? $"Detector placed: {marker.detectorId}"
            : $"Detector could not be placed: {marker.detectorId}";
        return placed;
    }

    public void PlaceDetector()
    {
        if (!TryPlaceCurrentDetector(out string resultMessage))
        {
            Debug.LogWarning($"[DetectorWorldMarkerManager] {resultMessage}");
        }
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

        detectorId = marker.detectorId;

        if (activePlacementSession != null &&
            !DetectorIdsEqual(activePlacementSession.detectorId, detectorId))
        {
            Debug.LogWarning($"[DetectorWorldMarkerManager] PlaceDetector ignored. Active preview belongs to {activePlacementSession.detectorId}, not {detectorId}.");
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
        lastInteractedDetectorId = detectorId;

        if (DetectorIdsEqual(currentFollowingDetectorId, detectorId))
            currentFollowingDetectorId = "";

        if (activePlacementSession != null &&
            DetectorIdsEqual(activePlacementSession.detectorId, detectorId))
        {
            activePlacementSession = null;
        }

        // Preserve the AR plane orientation captured by the placement preview.
        // Local Y remains the plane normal, so the detector pattern does not turn
        // toward the viewer after it has been placed.
        Quaternion worldRotation = marker.root.transform.rotation;
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
        RecordPlacedDetector(detectorId, true);

        FinalizeSpatialBinding(detectorId, marker, marker.savedPosition, worldRotation);

        ForceMarkerVisible(marker);
        UpdateMarkerVisual(marker, marker.lastRadiationValue);
        Debug.Log($"[DetectorWorldMarkerManager] Detector fixed: {detectorId}, pos={marker.savedPosition}, distance={marker.lastEstimatedDistance:F2}m, method={placementMethod}");
    }

    public void CancelCurrentFollowingDetector()
    {
        TryCancelCurrentDetector(out _);
    }

    public bool TryCancelCurrentDetector(out string resultMessage)
    {
        if (activePlacementSession != null || !string.IsNullOrEmpty(currentFollowingDetectorId))
        {
            string previewDetectorId = activePlacementSession != null
                ? activePlacementSession.detectorId
                : currentFollowingDetectorId;

            bool restoredCommittedMarker = RollbackActivePlacementSession();
            lastInteractedDetectorId = "";

            string result = restoredCommittedMarker
                ? "restored to its previous placed pose"
                : "new preview removed";
            Debug.Log($"[DetectorWorldMarkerManager] Detector placement cancelled: {previewDetectorId}, {result}.");
            resultMessage = restoredCommittedMarker
                ? $"Placement cancelled: {previewDetectorId} restored"
                : $"Placement cancelled: {previewDetectorId} preview removed";
            return true;
        }

        // Remove committed detectors in reverse placement order. Starting/cancelling
        // a later QR scan does not erase this stack, so repeated Cancel Place presses
        // remove C, then B, then A without requiring gaze selection.
        string detectorId = "";
        TryGetMostRecentlyPlacedDetector(
            out detectorId,
            out string pendingDetectorId);

        if (string.IsNullOrEmpty(detectorId) &&
            !string.IsNullOrEmpty(pendingDetectorId))
        {
            resultMessage =
                $"Latest detector is waiting for ROOM_ORIGIN/anchor restore: {pendingDetectorId}";
            return false;
        }

        if (string.IsNullOrEmpty(detectorId))
            detectorId = NormalizeDetectorId(lastInteractedDetectorId);

        lastInteractedDetectorId = "";

        if (string.IsNullOrEmpty(detectorId))
        {
            if (!TrySelectPlacedDetectorForCancel(out detectorId, out resultMessage))
            {
                Debug.LogWarning($"[DetectorWorldMarkerManager] Cancel did not select a detector. {resultMessage}");
                return false;
            }
        }

        if (RemoveDetectorAndSavedData(detectorId))
        {
            resultMessage = $"Detector removed: {detectorId}";
            return true;
        }

        resultMessage = $"Detector not found: {detectorId}";
        return false;
    }

    private bool TrySelectPlacedDetectorForCancel(
        out string detectorId,
        out string resultMessage)
    {
        detectorId = "";
        resultMessage = "Nothing to cancel";

        int placedCount = 0;
        MarkerInfo onlyMarker = null;

        foreach (var pair in markers)
        {
            MarkerInfo marker = pair.Value;
            if (marker == null || marker.root == null || !marker.isPlaced)
                continue;

            placedCount++;
            onlyMarker = marker;
        }

        if (placedCount == 0)
            return false;

        // This fixes the restored-single-marker case: after an app restart there
        // is deliberately no "last interacted" token, but Cancel is unambiguous.
        if (placedCount == 1)
        {
            detectorId = onlyMarker.detectorId;
            return true;
        }

        if (hideMarkersUntilServerConnected && !serverConnected)
        {
            resultMessage = "Connect to the server, then aim at the detector to remove";
            return false;
        }

        EnsurePlacementOrigin();
        if (placementOrigin == null)
        {
            resultMessage = "Cannot select a detector: glasses pose is unavailable";
            return false;
        }

        Vector3 gazeForward = placementOrigin.forward;
        if (gazeForward.sqrMagnitude < 0.0001f)
        {
            resultMessage = "Cannot select a detector: glasses direction is unavailable";
            return false;
        }

        gazeForward.Normalize();
        float closestAngle = float.PositiveInfinity;
        float closestDistance = float.PositiveInfinity;
        MarkerInfo selected = null;

        foreach (var pair in markers)
        {
            MarkerInfo marker = pair.Value;
            if (marker == null || marker.root == null || !marker.isPlaced)
                continue;

            Vector3 toMarker = marker.root.transform.position - placementOrigin.position;
            float distance = toMarker.magnitude;
            if (distance < 0.0001f)
                continue;

            float angle = Vector3.Angle(gazeForward, toMarker / distance);
            if (angle < closestAngle ||
                (Mathf.Approximately(angle, closestAngle) && distance < closestDistance))
            {
                closestAngle = angle;
                closestDistance = distance;
                selected = marker;
            }
        }

        if (selected == null || closestAngle > Mathf.Max(1f, cancelSelectionMaxAngleDegrees))
        {
            resultMessage = "Aim at the detector to remove, then press Cancel again";
            return false;
        }

        detectorId = selected.detectorId;
        return true;
    }

    /// <summary>
    /// Cancels only an active preview transaction. It never falls through to the
    /// committed-detector LIFO removal path.
    /// </summary>
    public bool CancelActivePlacementOnly()
    {
        if (activePlacementSession == null &&
            string.IsNullOrEmpty(currentFollowingDetectorId))
        {
            return false;
        }

        RollbackActivePlacementSession();
        lastInteractedDetectorId = "";
        return true;
    }

    /// <summary>
    /// Exposes the same center-gaze/AR-plane pose used by detector placement so a
    /// ROOM_ORIGIN calibration cannot drift from the detector placement method.
    /// </summary>
    public bool TryGetCurrentGazePlanePose(
        out Vector3 hitPosition,
        out Quaternion hitRotation,
        out Vector3 surfaceNormal,
        out float hitDistance,
        bool requireVerticalSurface = false,
        float maximumVerticalNormalDot = 0.35f)
    {
        bool found = TryGetGazePlaneIntersection(
            out hitPosition,
            out hitRotation,
            out hitDistance,
            requireVerticalSurface,
            maximumVerticalNormalDot);

        surfaceNormal = found
            ? (hitRotation * Vector3.up).normalized
            : Vector3.zero;
        return found;
    }

    private void InitializePlacedDetectorOrderFromDatabase()
    {
        placedDetectorOrder.Clear();

        if (!useCoordinateDatabase || coordinateDatabase == null)
            return;

        IReadOnlyList<DetectorCoordinateRecord> records = coordinateDatabase.GetAllRecords();
        List<DetectorCoordinateRecord> sequencedRecords =
            new List<DetectorCoordinateRecord>();

        // Legacy JSON has no sequence. Preserve its existing order, then append
        // records that have an explicit persisted placement sequence.
        for (int i = 0; i < records.Count; i++)
        {
            DetectorCoordinateRecord record = records[i];
            if (record == null || string.IsNullOrWhiteSpace(record.detectorId))
                continue;

            if (record.lastPlacedSequence > 0L)
                sequencedRecords.Add(record);
            else
                RecordPlacedDetector(record.detectorId);
        }

        sequencedRecords.Sort((left, right) =>
            left.lastPlacedSequence.CompareTo(right.lastPlacedSequence));

        for (int i = 0; i < sequencedRecords.Count; i++)
            RecordPlacedDetector(sequencedRecords[i].detectorId);
    }

    private void RecordPlacedDetector(string detectorId, bool persistOrder = false)
    {
        detectorId = NormalizeDetectorId(detectorId);
        if (string.IsNullOrEmpty(detectorId))
            return;

        RemoveFromPlacedDetectorOrder(detectorId);
        placedDetectorOrder.Add(detectorId);

        if (persistOrder && useCoordinateDatabase && coordinateDatabase != null)
            coordinateDatabase.MarkDetectorPlaced(detectorId);
    }

    private bool TryGetMostRecentlyPlacedDetector(
        out string detectorId,
        out string pendingDetectorId)
    {
        detectorId = "";
        pendingDetectorId = "";

        for (int i = placedDetectorOrder.Count - 1; i >= 0; i--)
        {
            string candidateId = NormalizeDetectorId(placedDetectorOrder[i]);
            if (markers.TryGetValue(candidateId, out MarkerInfo marker) &&
                marker != null && marker.root != null && marker.isPlaced)
            {
                detectorId = marker.detectorId;
                return true;
            }

            if (coordinateDatabase != null &&
                coordinateDatabase.TryGetRecord(
                    candidateId,
                    out DetectorCoordinateRecord pendingRecord) &&
                pendingRecord != null)
            {
                // Records from another named room do not participate in this
                // room's LIFO stack.
                if (roomCoordinateSystem != null &&
                    roomCoordinateSystem.IsCalibrated &&
                    pendingRecord.HasRoomPose() &&
                    !pendingRecord.HasRoomPose(roomCoordinateSystem.RoomId))
                {
                    continue;
                }

                // Do not skip over the newest detector while its asynchronous
                // anchor/room restoration is pending and delete an older one.
                pendingDetectorId = pendingRecord.detectorId;
                return false;
            }

            placedDetectorOrder.RemoveAt(i);
        }

        return false;
    }

    private void RemoveFromPlacedDetectorOrder(string detectorId)
    {
        detectorId = NormalizeDetectorId(detectorId);
        for (int i = placedDetectorOrder.Count - 1; i >= 0; i--)
        {
            if (DetectorIdsEqual(placedDetectorOrder[i], detectorId))
                placedDetectorOrder.RemoveAt(i);
        }
    }

    private bool RemoveDetectorAndSavedData(string detectorId)
    {
        AbortControllerDetectorInteraction(true, false);

        detectorId = NormalizeDetectorId(detectorId);
        if (string.IsNullOrEmpty(detectorId) ||
            !markers.TryGetValue(detectorId, out MarkerInfo marker) ||
            marker == null)
        {
            Debug.LogWarning($"[DetectorWorldMarkerManager] Detector removal failed. Marker not found: {detectorId}");
            return false;
        }

        detectorId = marker.detectorId;
        marker.isFollowingPlacementOrigin = false;
        marker.isPlaced = false;
        markers.Remove(detectorId);
        RemoveFromPlacedDetectorOrder(detectorId);

        if (marker.root != null)
            SetMarkerRequestedVisibility(marker, false);

        // Erase the persistent anchor before removing the coordinate record,
        // because the anchor manager reads its saved GUID from that record.
        if (useSpatialAnchors && spatialAnchorManager != null)
            spatialAnchorManager.EraseAnchorForDetector(detectorId);

        if (coordinateDatabase != null)
            coordinateDatabase.RemoveCoordinate(detectorId);

        DestroyMarkerVisualResources(marker);
        if (marker.root != null)
            Destroy(marker.root);

        RefreshRadiationSourceAfterDetectorMove();

        Debug.Log($"[DetectorWorldMarkerManager] Detector removed from the scene and saved data: {detectorId}");
        return true;
    }

    private string NormalizeDetectorId(string rawQrText)
    {
        return string.IsNullOrWhiteSpace(rawQrText) ? "" : rawQrText.Trim();
    }

    private bool DetectorIdsEqual(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
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

    private void EnsureRoomCoordinateSystem()
    {
        if (!enableRoomCoordinateSystem)
            return;

        if (roomCoordinateSystem == null)
            roomCoordinateSystem = GetComponent<RoomCoordinateSystem>();

        if (roomCoordinateSystem == null)
            roomCoordinateSystem = gameObject.AddComponent<RoomCoordinateSystem>();

        roomCoordinateSystem.Initialize(this, coordinateDatabase, fallbackCamera);
    }

    private void EnsureRadiationSourceEstimator()
    {
        if (!enableSingleSourceEstimator)
            return;

        if (radiationSourceEstimator == null)
            radiationSourceEstimator = GetComponent<RadiationSourceEstimator>();

        if (radiationSourceEstimator == null)
            radiationSourceEstimator = gameObject.AddComponent<RadiationSourceEstimator>();

        if (roomCoordinateSystem != null && roomCoordinateSystem.IsCalibrated)
            radiationSourceEstimator.SetCoordinateFrame(roomCoordinateSystem.CoordinateFrame);
    }

    private void EnsureRadiationReceiver()
    {
        if (radiationReceiver == null)
            radiationReceiver = FindFirstObjectByType<RadiationReceiver>();
    }

    private void EnsureDetectorControllerInteractor()
    {
        if (!enableControllerDetectorReposition)
            return;

        if (detectorControllerInteractor == null)
            detectorControllerInteractor = GetComponent<DetectorControllerInteractor>();

        if (detectorControllerInteractor == null)
            detectorControllerInteractor = gameObject.AddComponent<DetectorControllerInteractor>();

        detectorControllerInteractor.Initialize(this);
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

    private bool CreateAnchorForMarker(string detectorId, Vector3 worldPosition, Quaternion worldRotation)
    {
        EnsureSpatialAnchorManager();
        SubscribeSpatialEvents();

        if (spatialAnchorManager != null && spatialAnchorManager.IsReady())
        {
            spatialAnchorManager.CreateAndSaveAnchorForDetector(detectorId, worldPosition, worldRotation);
            return true;
        }

        Debug.LogWarning("[DetectorWorldMarkerManager] Spatial anchor not created. Manager or ARAnchorManager missing; using coordinate fallback.");
        return false;
    }

    private void FinalizeSpatialBinding(
        string detectorId,
        MarkerInfo marker,
        Vector3 worldPosition,
        Quaternion worldRotation,
        bool forceCreateSpatialAnchor = false)
    {
        if (marker == null)
            return;


        // Once a stable room pose exists, ROOM_ORIGIN is the single authoritative
        // reference frame. Do not create a second detector-local pose source.
        if (roomCoordinateSystem != null && roomCoordinateSystem.IsCalibrated)
        {
            if (marker.root != null)
                marker.root.transform.SetParent(transform, true);

            if (spatialAnchorManager != null)
                spatialAnchorManager.InvalidatePendingOperationForDetector(detectorId);

            marker.anchor = null;
            marker.anchorState = "room coordinate saved";
            return;
        }

        if (!useSpatialAnchors)
            return;

        // Detach the marker while the replacement anchor is being created. The
        // spatial manager keeps the previous persisted anchor until the new save
        // succeeds, so a transient mapping failure cannot destroy the last known
        // physical-space restoration point.
        if (marker.root != null)
            marker.root.transform.SetParent(transform, true);

        EnsureSpatialAnchorManager();
        if (spatialAnchorManager != null)
            spatialAnchorManager.InvalidatePendingOperationForDetector(detectorId);

        marker.anchor = null;

        if ((createSpatialAnchorOnQr || forceCreateSpatialAnchor) &&
            CreateAnchorForMarker(detectorId, worldPosition, worldRotation))
        {
            marker.anchorState = "anchor saving...";
        }
        else
        {
            marker.anchorState = $"{placedStateLabel} (anchor unavailable)";
        }
    }

    private void HandleAnchorCreatedAndSaved(string detectorId, ARAnchor anchor, string persistentGuid)
    {
        detectorId = NormalizeDetectorId(detectorId);
        if (anchor == null || IsActivePlacementForDetector(detectorId))
            return;

        if (HasStoredRoomPose(detectorId))
        {
            if (markers.TryGetValue(detectorId, out MarkerInfo roomMarker) &&
                roomMarker != null)
            {
                roomMarker.anchor = null;
                roomMarker.anchorState = "room coordinate saved";
            }

            return;
        }

        if (!markers.TryGetValue(detectorId, out MarkerInfo marker) || marker == null)
            return;

        marker.anchor = anchor;
        marker.anchorGuid = persistentGuid;
        marker.anchorState = "anchor saved";
        marker.savedPosition = anchor.transform.position;
        marker.isFollowingPlacementOrigin = false;
        marker.isPlaced = true;

        if (marker.root != null)
        {
            marker.root.transform.SetPositionAndRotation(anchor.transform.position, anchor.transform.rotation);
            if (parentMarkerToAnchor)
            {
                marker.root.transform.SetParent(anchor.transform, false);
                marker.root.transform.localPosition = Vector3.zero;
                marker.root.transform.localRotation = Quaternion.identity;
            }
        }

        UpdateMarkerVisual(marker, marker.lastRadiationValue);
        Debug.Log($"[DetectorWorldMarkerManager] Anchor created and saved: {detectorId}, {persistentGuid}");
    }

    private void HandleAnchorLoaded(string detectorId, ARAnchor anchor, string persistentGuid)
    {
        detectorId = NormalizeDetectorId(detectorId);
        if (anchor == null || IsActivePlacementForDetector(detectorId))
            return;

        // A room-relative record is restored only after ROOM_ORIGIN is localized.
        // Loading its older detector-local anchor here would create a startup ghost
        // and later fight the authoritative room transform.
        if (HasStoredRoomPose(detectorId))
            return;

        MarkerInfo marker = CreateOrMoveMarker(
            detectorId,
            anchor.transform.position,
            0f,
            0f,
            anchor.transform,
            true);
        if (marker == null)
            return;

        marker.anchor = anchor;
        marker.anchorGuid = persistentGuid;
        marker.anchorState = "anchor loaded";
        marker.savedPosition = anchor.transform.position;
        marker.isFollowingPlacementOrigin = false;
        marker.isPlaced = true;

        if (marker.root != null)
        {
            marker.root.transform.SetPositionAndRotation(anchor.transform.position, anchor.transform.rotation);
            if (parentMarkerToAnchor)
            {
                marker.root.transform.SetParent(anchor.transform, false);
                marker.root.transform.localPosition = Vector3.zero;
                marker.root.transform.localRotation = Quaternion.identity;
            }
        }

        float restoredRadiationValue = marker.lastRadiationValue;
        if (coordinateDatabase != null &&
            coordinateDatabase.TryGetRecord(detectorId, out DetectorCoordinateRecord record) &&
            record.lastRadiationValue >= 0f)
        {
            restoredRadiationValue = record.lastRadiationValue;
        }

        // The room may have been calibrated while this asynchronous legacy anchor
        // was still loading. Capture it now so it is not omitted from the estimator
        // or the next room-relative restoration, then detach the visual from the
        // legacy per-detector pose source.
        if (roomCoordinateSystem != null && roomCoordinateSystem.IsCalibrated)
        {
            SaveRoomCoordinateIfCalibrated(
                detectorId,
                anchor.transform.position,
                anchor.transform.rotation);

            if (marker.root != null)
                marker.root.transform.SetParent(transform, true);

            marker.anchor = null;
            marker.anchorState = "room coordinate captured";
        }

        UpdateMarkerVisual(
            marker,
            GetLatestRadiationValue(detectorId, restoredRadiationValue));
    }

    private void HandleAnchorSaveFailed(string detectorId, string message)
    {
        detectorId = NormalizeDetectorId(detectorId);
        if (IsActivePlacementForDetector(detectorId))
            return;

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

        if (IsActivePlacementForDetector(detectorId))
            return;

        if (HasStoredRoomPose(detectorId))
        {
            Debug.LogWarning(
                $"[DetectorWorldMarkerManager] Legacy anchor load failed for {detectorId}; " +
                $"ROOM_ORIGIN calibration will restore its room-relative pose. {message}");
            return;
        }

        // A serialized Unity world pose is session-relative and is not the same
        // physical location after an app restart. Do not show that coordinate as a
        // fallback; it creates the startup "ghost" sphere and can visibly jump once
        // relocalization completes.
        Debug.LogWarning(
            $"[DetectorWorldMarkerManager] Anchor load failed for {detectorId}; " +
            $"the detector stays hidden until it is placed again. {message}");
    }

    private bool IsActivePlacementForDetector(string detectorId)
    {
        bool activePlacement =
            activePlacementSession != null &&
            DetectorIdsEqual(activePlacementSession.detectorId, detectorId);
        bool activeControllerMove =
            activeDetectorMoveSession != null &&
            activeDetectorMoveSession.marker != null &&
            DetectorIdsEqual(
                activeDetectorMoveSession.marker.detectorId,
                detectorId);
        return activePlacement || activeControllerMove;
    }

    private void HandleServerConnectionChanged(bool connected)
    {
        SetServerConnectionState(connected);
    }

    private void SetServerConnectionState(bool connected)
    {
        if (!connected)
            AbortControllerDetectorInteraction(true, false);

        bool changed = serverConnected != connected;
        serverConnected = connected;

        if (changed)
        {
            // A snapshot from the previous WebSocket generation must never make
            // restored detector values look live on the replacement connection.
            hasReceivedRadiationSnapshot = false;
            lastSnapshotFreshnessState = false;
            lastRadiationSnapshotTime = float.NegativeInfinity;
            liveRadiationDetectorIds.Clear();
        }

        foreach (var pair in markers)
            ApplyMarkerVisibility(pair.Value);

        if (changed)
        {
            Debug.Log(
                connected
                    ? "[DetectorWorldMarkerManager] Server connected; detector markers may now be shown."
                    : "[DetectorWorldMarkerManager] Server disconnected; all detector markers are hidden.");
        }
    }

    private void HandleRadiationDataReceived(Dictionary<string, float> data)
    {
        if (data == null)
            return;

        hasReceivedRadiationSnapshot = true;
        lastRadiationSnapshotTime = Time.unscaledTime;
        liveRadiationDetectorIds.Clear();

        foreach (var kvp in data)
        {
            string detectorId = NormalizeDetectorId(kvp.Key);

            if (string.IsNullOrEmpty(detectorId) ||
                kvp.Value < 0f ||
                float.IsNaN(kvp.Value) ||
                float.IsInfinity(kvp.Value))
            {
                continue;
            }

            liveRadiationDetectorIds.Add(detectorId);

            if (markers.TryGetValue(detectorId, out MarkerInfo marker))
                UpdateMarkerVisual(marker, kvp.Value);
        }

        lastSnapshotFreshnessState = IsRadiationSnapshotFresh();

        // A complete server snapshot can omit a detector that was present in the
        // preceding snapshot. Re-evaluate every marker so that detector is hidden.
        foreach (var pair in markers)
            ApplyMarkerVisibility(pair.Value);

        if (useCoordinateDatabase && coordinateDatabase != null)
            coordinateDatabase.UpdateRadiationValues(data);
    }

    private MarkerInfo CreateOrMoveMarker(
        string detectorId,
        Vector3 worldPosition,
        float estimatedDistance,
        float qrPixelSize,
        Transform parent,
        bool forceMoveExisting = false)
    {
        detectorId = NormalizeDetectorId(detectorId);
        if (string.IsNullOrEmpty(detectorId))
            return null;

        if (markers.TryGetValue(detectorId, out MarkerInfo existing))
        {
            if (existing == null || existing.root == null)
            {
                if (existing != null)
                    DestroyMarkerVisualResources(existing);
                markers.Remove(detectorId);
            }
            else
            {
                if (!updateExistingMarkerOnRescan && !forceMoveExisting)
                    return existing;

                Vector3 finalPosition = worldPosition;
                if (!forceMoveExisting &&
                    smoothPositionOnRescan &&
                    existing.anchor == null &&
                    !existing.isFollowingPlacementOrigin)
                {
                    finalPosition = Vector3.Lerp(existing.savedPosition, worldPosition, rescanPositionBlend);
                }

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
            lastRadiationValue = GetLatestRadiationValue(detectorId, -1f),
            lastEstimatedDistance = estimatedDistance,
            lastQrPixelSize = qrPixelSize,
            lastPlacementImagePoint = new Vector2(0.5f, 0.5f),
            lastImageWidth = 1,
            lastImageHeight = 1,
            lastPlacementMethod = "preview projection",
            isFollowingPlacementOrigin = false,
            isPlaced = false,
            centerVisualRequested = true,
            anchorState = useSpatialAnchors ? "no anchor yet" : "preview projection"
        };

        markers.Add(detectorId, info);
        ForceMarkerVisible(info);
        UpdateMarkerVisual(info, info.lastRadiationValue);
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
        {
            collider.enabled = false;
            Destroy(collider);
        }

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

        RadiationRiskBand riskBand = GetRiskBand(radiationValue);
        bool useGrayPreview = showGrayPreviewSphere && IsPreviewMarker(marker);

        Color color = useGrayPreview ? previewSphereColor : GetRiskColor(radiationValue);
        color.a = useGrayPreview ? Mathf.Clamp01(previewSphereAlpha) : markerAlpha;

        // Highlight only the center material. Scaling the marker root would also
        // scale every inverse-square falloff shell and falsify its real-world radius.
        if (marker.isControllerMoving)
        {
            float originalAlpha = color.a;
            color = Color.Lerp(
                color,
                Color.white,
                Mathf.Clamp01(controllerMoveHighlightBlend));
            color.a = Mathf.Clamp01(originalAlpha + controllerMoveAlphaBoost);
        }
        else if (marker.isControllerHovered)
        {
            float originalAlpha = color.a;
            color = Color.Lerp(
                color,
                Color.white,
                Mathf.Clamp01(controllerHoverHighlightBlend));
            color.a = Mathf.Clamp01(originalAlpha + controllerHoverAlphaBoost);
        }

        marker.centerMaterial =
            SetRendererTransparentColor(marker.renderer, color, 10, false);

        // Keep the logical marker root alive so HUD distance, gaze selection,
        // Cancel, and anchor state continue to work even when 0-2 CPS hides the
        // center sphere. An unknown preview stays visible for placement; an
        // already placed detector with no valid reading stays visually quiet.
        // A gray preview is an aiming aid, so it ignores the CPS bands entirely
        // and stays visible even at a hidden 0-2 CPS reading.
        marker.centerVisualRequested =
            useGrayPreview ||
            marker.isControllerMoving ||
            (riskBand != RadiationRiskBand.Hidden &&
             (riskBand != RadiationRiskBand.Unknown || !marker.isPlaced));

        UpdateFalloffShellVisuals(marker, radiationValue, riskBand);

        // Radiation value affects visibility/color only. Center size stays fixed.
        marker.root.transform.localScale = Vector3.one * fixedMarkerSize;

        bool waitingForPlaneHit =
            usePlaneIntersectionPlacement &&
            marker.isFollowingPlacementOrigin &&
            !marker.hasValidPlaneHit;

        if (waitingForPlaneHit && hidePreviewWithoutPlaneHit)
            SetMarkerRequestedVisibility(marker, false);
        else
            ForceMarkerVisible(marker);

        UpdateLabel(marker, radiationValue, false);
    }

    private Color GetRiskColor(float radiationValue)
    {
        switch (GetRiskBand(radiationValue))
        {
            case RadiationRiskBand.Green:
            case RadiationRiskBand.Hidden:
                return new Color(0.0f, 1.0f, 0.0f, markerAlpha);
            case RadiationRiskBand.Yellow:
                return new Color(1.0f, 1.0f, 0.0f, markerAlpha);
            case RadiationRiskBand.Red:
                return new Color(1.0f, 0.0f, 0.0f, markerAlpha);
            default:
                return new Color(0.65f, 0.65f, 0.65f, markerAlpha);
        }
    }

    private RadiationRiskBand GetRiskBand(float radiationValue)
    {
        if (float.IsNaN(radiationValue) ||
            float.IsInfinity(radiationValue) ||
            radiationValue < 0f)
        {
            return RadiationRiskBand.Unknown;
        }

        float hiddenThreshold = Mathf.Max(0f, hiddenMaxCps);
        float greenThreshold = Mathf.Max(hiddenThreshold, greenMaxCps);
        float redThreshold = Mathf.Max(greenThreshold, dangerThresholdCps);

        if (radiationValue <= hiddenThreshold)
            return RadiationRiskBand.Hidden;
        if (radiationValue <= greenThreshold)
            return RadiationRiskBand.Green;
        if (radiationValue <= redThreshold)
            return RadiationRiskBand.Yellow;
        return RadiationRiskBand.Red;
    }

    private void UpdateFalloffShellVisuals(
        MarkerInfo marker,
        float centerCps,
        RadiationRiskBand centerBand)
    {
        HideFalloffShells(marker);

        if (!showFalloffShells ||
            marker == null ||
            !marker.isPlaced ||
            (centerBand != RadiationRiskBand.Red && centerBand != RadiationRiskBand.Yellow))
        {
            return;
        }

        EnsureFalloffShellPool(marker);
        if (marker.falloffShells == null || marker.falloffShells.Count == 0)
            return;

        float referenceDistance = Mathf.Max(0.01f, falloffReferenceDistanceMeters);
        float centerRadius = Mathf.Max(0.01f, fixedMarkerSize * 0.5f);
        float maximumRadius = Mathf.Max(centerRadius, falloffMaxRadiusMeters);
        int availableShells = Mathf.Min(
            Mathf.Clamp(maxFalloffShells, 1, 3),
            marker.falloffShells.Count);
        int shellIndex = 0;
        float lastConfiguredRadius = centerRadius;

        float hiddenThreshold = Mathf.Max(0.001f, hiddenMaxCps);
        float greenThreshold = Mathf.Max(hiddenThreshold, greenMaxCps);
        float redThreshold = Mathf.Max(greenThreshold, dangerThresholdCps);
        float redBoundaryRadius =
            referenceDistance * Mathf.Sqrt(centerCps / redThreshold);
        float yellowBoundaryRadius =
            referenceDistance * Mathf.Sqrt(centerCps / greenThreshold);
        float greenBoundaryRadius =
            referenceDistance * Mathf.Sqrt(centerCps / hiddenThreshold);

        // Each shell marks the outer edge of the like-colored approach zone.
        // A barely-red reading keeps its red boundary inside the fixed center;
        // a much stronger reading expands that red zone correctly.
        if (centerBand == RadiationRiskBand.Red)
        {
            TryConfigureFalloffBoundary(
                marker,
                ref shellIndex,
                ref lastConfiguredRadius,
                availableShells,
                redBoundaryRadius,
                maximumRadius,
                RadiationRiskBand.Red);
        }

        TryConfigureFalloffBoundary(
            marker,
            ref shellIndex,
            ref lastConfiguredRadius,
            availableShells,
            yellowBoundaryRadius,
            maximumRadius,
            RadiationRiskBand.Yellow);

        TryConfigureFalloffBoundary(
            marker,
            ref shellIndex,
            ref lastConfiguredRadius,
            availableShells,
            greenBoundaryRadius,
            maximumRadius,
            RadiationRiskBand.Green);

        // If the true green boundary is beyond the configured view range, show
        // the actually estimated band at the endpoint instead of a false safe
        // boundary. This is most relevant for exceptionally high readings.
        if (greenBoundaryRadius > maximumRadius && shellIndex < availableShells)
        {
            float ratio = referenceDistance / Mathf.Max(maximumRadius, referenceDistance);
            RadiationRiskBand endpointBand = GetRiskBand(centerCps * ratio * ratio);
            TryConfigureFalloffBoundary(
                marker,
                ref shellIndex,
                ref lastConfiguredRadius,
                availableShells,
                maximumRadius,
                maximumRadius,
                endpointBand);
        }
    }

    private bool TryConfigureFalloffBoundary(
        MarkerInfo marker,
        ref int shellIndex,
        ref float lastConfiguredRadius,
        int availableShells,
        float radiusMeters,
        float maximumRadius,
        RadiationRiskBand band)
    {
        if (shellIndex >= availableShells ||
            radiusMeters > maximumRadius ||
            radiusMeters <= lastConfiguredRadius * 1.05f ||
            band == RadiationRiskBand.Hidden ||
            band == RadiationRiskBand.Unknown)
        {
            return false;
        }

        ConfigureFalloffShell(marker, shellIndex, radiusMeters, band);
        shellIndex++;
        lastConfiguredRadius = radiusMeters;
        return true;
    }

    private void EnsureFalloffShellPool(MarkerInfo marker)
    {
        if (marker == null || marker.root == null)
            return;

        if (marker.falloffShells == null)
            marker.falloffShells = new List<FalloffShellInfo>();

        int desiredCount = Mathf.Clamp(maxFalloffShells, 1, 3);
        while (marker.falloffShells.Count < desiredCount)
        {
            int shellIndex = marker.falloffShells.Count;
            GameObject shellObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            shellObject.name = $"FalloffShell_{shellIndex + 1}";
            shellObject.layer = marker.root.layer;
            shellObject.transform.SetParent(marker.root.transform, false);
            shellObject.transform.localPosition = Vector3.zero;
            shellObject.transform.localRotation = Quaternion.identity;
            shellObject.transform.localScale = Vector3.one;

            Collider collider = shellObject.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                Destroy(collider);
            }

            Renderer shellRenderer = shellObject.GetComponent<Renderer>();
            Material shellMaterial = null;
            if (shellRenderer != null)
            {
                Shader shader = GetDetectorTransparentShader();
                if (shader == null)
                    shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                    shader = Shader.Find("Standard");
                if (shader != null)
                {
                    shellMaterial = new Material(shader);
                    shellRenderer.material = shellMaterial;
                }

                shellRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                shellRenderer.receiveShadows = false;
                shellRenderer.enabled = false;
            }

            marker.falloffShells.Add(new FalloffShellInfo
            {
                root = shellObject,
                renderer = shellRenderer,
                visualRequested = false,
                material = shellMaterial
            });
        }
    }

    private void ConfigureFalloffShell(
        MarkerInfo marker,
        int shellIndex,
        float radiusMeters,
        RadiationRiskBand band)
    {
        if (marker == null ||
            marker.falloffShells == null ||
            shellIndex < 0 ||
            shellIndex >= marker.falloffShells.Count)
        {
            return;
        }

        FalloffShellInfo shell = marker.falloffShells[shellIndex];
        if (shell == null || shell.root == null)
            return;

        float centerDiameter = Mathf.Max(0.001f, fixedMarkerSize);
        shell.root.transform.localPosition = Vector3.zero;
        shell.root.transform.localRotation = Quaternion.identity;
        shell.root.transform.localScale =
            Vector3.one * ((radiusMeters * 2f) / centerDiameter);

        Color color = GetRiskColorForBand(band);
        color.a = falloffShellAlpha;

        // Inner shells render after outer shells; the center sphere renders last.
        int queueOffset = Mathf.Clamp(maxFalloffShells, 1, 3) - shellIndex;
        shell.material =
            SetRendererTransparentColor(shell.renderer, color, queueOffset, true);
        shell.visualRequested = true;
    }

    private Color GetRiskColorForBand(RadiationRiskBand band)
    {
        switch (band)
        {
            case RadiationRiskBand.Red:
                return Color.red;
            case RadiationRiskBand.Yellow:
                return Color.yellow;
            default:
                return Color.green;
        }
    }

    private float GetLatestRadiationValue(string detectorId, float fallbackValue)
    {
        detectorId = NormalizeDetectorId(detectorId);
        if (radiationReceiver != null &&
            radiationReceiver.LatestDeviceData != null &&
            radiationReceiver.LatestDeviceData.TryGetValue(detectorId, out float latestValue))
        {
            return latestValue;
        }

        return fallbackValue;
    }

    private void HideFalloffShells(MarkerInfo marker)
    {
        if (marker == null || marker.falloffShells == null)
            return;

        for (int i = 0; i < marker.falloffShells.Count; i++)
        {
            FalloffShellInfo shell = marker.falloffShells[i];
            if (shell != null)
                shell.visualRequested = false;
        }
    }

    private bool IsPreviewMarker(MarkerInfo marker)
    {
        return marker != null &&
               marker.isFollowingPlacementOrigin &&
               !marker.isPlaced;
    }

    private void ForceMarkerVisible(MarkerInfo marker)
    {
        if (marker == null)
            return;

        SetMarkerRequestedVisibility(marker, true);
    }

    private void SetMarkerRequestedVisibility(MarkerInfo marker, bool visible)
    {
        if (marker == null)
            return;

        marker.visibilityRequested = visible;
        ApplyMarkerVisibility(marker);
    }

    private void ApplyMarkerVisibility(MarkerInfo marker)
    {
        if (marker == null)
            return;

        bool isPreview = IsPreviewMarker(marker);

        // The preview is a placement aid, not a measurement, so it does not have to
        // wait for the radiation server. Otherwise the sphere the user is aiming with
        // is invisible during an offline or not-yet-connected placement.
        bool previewIgnoresServerGate =
            showPreviewBeforeServerConnected && isPreview;

        bool serverReady = !hideMarkersUntilServerConnected || serverConnected;
        bool roomReady = isPreview ||
                         !enableRoomCoordinateSystem ||
                         !requireRoomCalibrationBeforeDetectorPlacement ||
                         (roomCoordinateSystem != null && roomCoordinateSystem.IsCalibrated);
        bool radiationReady = isPreview ||
                              !hideMarkersWithoutFreshRadiationData ||
                              (IsRadiationSnapshotFresh() &&
                               liveRadiationDetectorIds.Contains(marker.detectorId));

        bool visible = marker.visibilityRequested &&
                       roomReady &&
                       radiationReady &&
                       (previewIgnoresServerGate || serverReady);

        if (marker.root != null)
            marker.root.SetActive(visible);

        if (marker.renderer != null)
        {
            bool centerVisible = marker.centerVisualRequested ||
                                 (showGrayPreviewSphere && IsPreviewMarker(marker));
            marker.renderer.enabled = visible && centerVisible;
        }

        if (marker.falloffShells != null)
        {
            for (int i = 0; i < marker.falloffShells.Count; i++)
            {
                FalloffShellInfo shell = marker.falloffShells[i];
                if (shell != null && shell.renderer != null)
                    shell.renderer.enabled = visible && shell.visualRequested;
            }
        }

        if (showLabel && marker.label != null)
            marker.label.gameObject.SetActive(visible);
    }

    private bool IsRadiationSnapshotFresh()
    {
        return serverConnected &&
               hasReceivedRadiationSnapshot &&
               Time.unscaledTime - lastRadiationSnapshotTime <=
               Mathf.Max(0.5f, maximumRadiationSnapshotAgeSeconds);
    }

    private void RefreshRadiationSnapshotVisibilityIfExpired()
    {
        bool snapshotIsFresh = IsRadiationSnapshotFresh();
        if (snapshotIsFresh == lastSnapshotFreshnessState)
            return;

        lastSnapshotFreshnessState = snapshotIsFresh;
        foreach (var pair in markers)
            ApplyMarkerVisibility(pair.Value);
    }

    private Material SetRendererTransparentColor(
        Renderer renderer,
        Color color,
        int renderQueueOffset,
        bool renderInside)
    {
        if (renderer == null || renderer.material == null)
            return null;

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

        if (material.HasProperty("_Cull"))
        {
            material.SetFloat(
                "_Cull",
                renderInside
                    ? (float)UnityEngine.Rendering.CullMode.Off
                    : (float)UnityEngine.Rendering.CullMode.Back);
        }

        material.renderQueue =
            (int)UnityEngine.Rendering.RenderQueue.Transparent + renderQueueOffset;

        return material;
    }

    private void DestroyMarkerVisualResources(MarkerInfo marker)
    {
        if (marker == null)
            return;

        if (marker.centerMaterial != null)
        {
            Destroy(marker.centerMaterial);
            marker.centerMaterial = null;
        }

        if (marker.falloffShells == null)
            return;

        for (int i = 0; i < marker.falloffShells.Count; i++)
        {
            FalloffShellInfo shell = marker.falloffShells[i];
            if (shell == null || shell.material == null)
                continue;

            Destroy(shell.material);
            shell.material = null;
        }
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

            SaveRoomCoordinateIfCalibrated(
                detectorId,
                worldPosition,
                worldRotation);

            // A server snapshot may have arrived before this detector's first
            // coordinate record existed. Persist the value used by the marker now
            // so an app restart does not fall back to an unknown/hidden reading.
            string normalizedDetectorId = NormalizeDetectorId(detectorId);
            if (markers.TryGetValue(normalizedDetectorId, out MarkerInfo marker) &&
                marker != null &&
                !float.IsNaN(marker.lastRadiationValue) &&
                !float.IsInfinity(marker.lastRadiationValue) &&
                marker.lastRadiationValue >= 0f)
            {
                coordinateDatabase.UpdateRadiationValue(
                    normalizedDetectorId,
                    marker.lastRadiationValue);
            }
        }
    }

    private bool HasStoredRoomPose(string detectorId)
    {
        return coordinateDatabase != null &&
               coordinateDatabase.TryGetRecord(detectorId, out DetectorCoordinateRecord record) &&
               record != null &&
               record.HasRoomPose();
    }

    private void SaveRoomCoordinateIfCalibrated(
        string detectorId,
        Vector3 worldPosition,
        Quaternion worldRotation)
    {
        if (!useCoordinateDatabase ||
            coordinateDatabase == null ||
            roomCoordinateSystem == null ||
            !roomCoordinateSystem.IsCalibrated)
        {
            return;
        }

        float calibrationFactor = 1f;
        if (coordinateDatabase.TryGetRecord(detectorId, out DetectorCoordinateRecord record) &&
            record != null &&
            record.calibrationFactor > 0f &&
            !float.IsNaN(record.calibrationFactor) &&
            !float.IsInfinity(record.calibrationFactor))
        {
            calibrationFactor = record.calibrationFactor;
        }

        coordinateDatabase.SaveOrUpdateRoomCoordinate(
            detectorId,
            roomCoordinateSystem.RoomId,
            roomCoordinateSystem.WorldToRoomPoint(worldPosition),
            roomCoordinateSystem.WorldToRoomRotation(worldRotation),
            calibrationFactor);
    }

    private void LoadSavedCoordinatesWithoutAnchors()
    {
        if (useCoordinateDatabase && coordinateDatabase != null)
        {
            IReadOnlyList<DetectorCoordinateRecord> records = coordinateDatabase.GetAllRecords();

            if (useSpatialAnchors)
            {
                int legacyCoordinateCount = 0;
                for (int i = 0; i < records.Count; i++)
                {
                    DetectorCoordinateRecord record = records[i];
                    if (record != null &&
                        !string.IsNullOrWhiteSpace(record.detectorId) &&
                        !record.HasSavedAnchor())
                    {
                        legacyCoordinateCount++;
                    }
                }

                if (legacyCoordinateCount > 0)
                {
                    Debug.LogWarning(
                        $"[DetectorWorldMarkerManager] {legacyCoordinateCount} saved detector coordinate(s) " +
                        "have no persistent spatial anchor and will not be restored at a misleading session-space pose. " +
                        "Scan and place them once to create anchors.");
                }

                // Records with GUIDs are materialized only by HandleAnchorLoaded.
                // This avoids a coordinate-fallback blink or jump before XREAL
                // finishes relocalizing the persisted local anchor.
                return;
            }

            for (int i = 0; i < records.Count; i++)
            {
                DetectorCoordinateRecord record = records[i];
                if (record == null || string.IsNullOrWhiteSpace(record.detectorId))
                    continue;

                if (record.HasRoomPose())
                {
                    // A room-local coordinate has no valid Unity-world pose until
                    // the user localizes ROOM_ORIGIN in this session.
                    continue;
                }

                MarkerInfo marker = CreateOrMoveMarker(record.detectorId, record.GetPosition(), record.estimatedDistanceMeters, record.qrPixelSize, null);

                if (marker != null)
                {
                    marker.root.transform.rotation = record.GetRotation();
                    marker.anchorState = record.placementMethod;
                    marker.isFollowingPlacementOrigin = false;
                    marker.isPlaced = true;

                    float restoredRadiationValue = record.lastRadiationValue >= 0f
                        ? record.lastRadiationValue
                        : marker.lastRadiationValue;
                    UpdateMarkerVisual(
                        marker,
                        GetLatestRadiationValue(record.detectorId, restoredRadiationValue));
                }
            }

            Debug.Log($"[DetectorWorldMarkerManager] Loaded fallback markers from coordinate database: {records.Count}");
        }
    }

    /// <summary>
    /// Writes every currently materialized detector into the calibrated room frame.
    /// Hidden-by-server markers are included because visibility is unrelated to pose.
    /// </summary>
    public int CapturePlacedDetectorRoomCoordinates(RoomCoordinateSystem roomFrame)
    {
        if (roomFrame == null ||
            !roomFrame.IsCalibrated ||
            coordinateDatabase == null)
        {
            return 0;
        }

        int savedCount = 0;
        foreach (var pair in markers)
        {
            MarkerInfo marker = pair.Value;
            if (marker == null || marker.root == null || !marker.isPlaced)
                continue;

            // Never reinterpret a detector already assigned to a room. A room
            // re-scan refines the frame, not the detector's stored local pose.
            if (coordinateDatabase.TryGetRecord(
                    marker.detectorId,
                    out DetectorCoordinateRecord existingRecord) &&
                existingRecord != null &&
                existingRecord.HasRoomPose())
            {
                continue;
            }

            SaveRoomCoordinateIfCalibrated(
                marker.detectorId,
                marker.root.transform.position,
                marker.root.transform.rotation);
            savedCount++;
        }

        return savedCount;
    }

    /// <summary>
    /// Materializes detectors that could not be restored by their persistent XREAL
    /// anchor, using the newly calibrated ROOM_ORIGIN frame. Existing anchored
    /// markers are deliberately left untouched; the room pose is a safe fallback,
    /// not an offset applied on top of a working anchor.
    /// </summary>
    public int RestoreMissingMarkersFromRoomCoordinates(RoomCoordinateSystem roomFrame)
    {
        AbortControllerDetectorInteraction(true, false);

        if (roomFrame == null ||
            !roomFrame.IsCalibrated ||
            coordinateDatabase == null)
        {
            return 0;
        }

        IReadOnlyList<DetectorCoordinateRecord> records = coordinateDatabase.GetAllRecords();
        int restoredCount = 0;

        // Markers belonging to another calibrated room must not leak into the
        // active room. Remove only their runtime visuals; saved DB/anchor data is
        // kept so switching back to that room remains possible.
        List<string> markersOutsideRoom = new List<string>();
        foreach (var pair in markers)
        {
            if (!coordinateDatabase.TryGetRecord(
                    pair.Key,
                    out DetectorCoordinateRecord markerRecord) ||
                markerRecord == null ||
                !markerRecord.HasRoomPose() ||
                markerRecord.HasRoomPose(roomFrame.RoomId))
            {
                continue;
            }

            markersOutsideRoom.Add(pair.Key);
        }

        for (int i = 0; i < markersOutsideRoom.Count; i++)
        {
            string detectorId = markersOutsideRoom[i];
            if (!markers.TryGetValue(detectorId, out MarkerInfo marker) || marker == null)
                continue;

            markers.Remove(detectorId);
            DestroyMarkerVisualResources(marker);
            if (marker.root != null)
                Destroy(marker.root);
        }

        for (int i = 0; i < records.Count; i++)
        {
            DetectorCoordinateRecord record = records[i];
            if (record == null ||
                string.IsNullOrWhiteSpace(record.detectorId) ||
                !record.HasRoomPose(roomFrame.RoomId))
            {
                continue;
            }

            string detectorId = NormalizeDetectorId(record.detectorId);
            markers.TryGetValue(detectorId, out MarkerInfo existing);
            if (existing != null && existing.root == null)
            {
                DestroyMarkerVisualResources(existing);
                markers.Remove(detectorId);
                existing = null;
            }

            Vector3 worldPosition = roomFrame.RoomToWorldPoint(record.GetRoomPosition());
            Quaternion worldRotation =
                roomFrame.RoomToWorldRotation(record.GetRoomRotation());

            MarkerInfo marker = existing;
            bool newlyRestored = marker == null;
            if (newlyRestored)
            {
                marker = CreateOrMoveMarker(
                    detectorId,
                    worldPosition,
                    record.estimatedDistanceMeters,
                    record.qrPixelSize,
                    null,
                    true);
            }

            if (marker == null || marker.root == null)
                continue;

            // Room coordinates are authoritative after ROOM_ORIGIN calibration.
            // Detach from an old detector anchor so the two pose sources cannot
            // apply offsets on top of one another.
            marker.root.transform.SetParent(transform, true);
            marker.root.transform.SetPositionAndRotation(worldPosition, worldRotation);
            marker.savedPosition = worldPosition;
            marker.lastPlacementImagePoint = record.GetQrImageCenter();
            marker.lastImageWidth = record.qrImageWidth;
            marker.lastImageHeight = record.qrImageHeight;
            marker.lastPlacementMethod = "RoomCoordinateFallback";
            marker.anchorState = "room coordinate restored";
            marker.anchor = null;
            marker.isFollowingPlacementOrigin = false;
            marker.isPlaced = true;

            float restoredRadiationValue = record.lastRadiationValue >= 0f
                ? record.lastRadiationValue
                : marker.lastRadiationValue;
            UpdateMarkerVisual(
                marker,
                GetLatestRadiationValue(detectorId, restoredRadiationValue));
            if (newlyRestored)
                restoredCount++;
        }

        if (restoredCount > 0)
        {
            Debug.Log(
                $"[DetectorWorldMarkerManager] Restored {restoredCount} detector(s) " +
                $"from room coordinates for {roomFrame.RoomId}.");
        }

        return restoredCount;
    }

    /// <summary>
    /// Drops only session-world visuals after an XR tracking reset. Persistent room
    /// coordinates and LIFO placement order remain intact and are materialized again
    /// after ROOM_ORIGIN is calibrated in the new session frame.
    /// </summary>
    public void InvalidateRoomLocalization(string reason)
    {
        AbortControllerDetectorInteraction(true, false);
        RollbackActivePlacementSession();

        foreach (var pair in markers)
        {
            MarkerInfo marker = pair.Value;
            if (marker == null)
                continue;

            SetMarkerRequestedVisibility(marker, false);
            DestroyMarkerVisualResources(marker);
            if (marker.root != null)
                Destroy(marker.root);
        }

        markers.Clear();
        currentFollowingDetectorId = "";
        activePlacementSession = null;
        lastInteractedDetectorId = "";

        hasReceivedRadiationSnapshot = false;
        lastSnapshotFreshnessState = false;
        lastRadiationSnapshotTime = float.NegativeInfinity;
        liveRadiationDetectorIds.Clear();

        if (radiationSourceEstimator != null)
        {
            radiationSourceEstimator.SetCoordinateFrame(null);
            radiationSourceEstimator.ClearEstimate();
        }

        Debug.LogWarning(
            $"[DetectorWorldMarkerManager] Room localization invalidated; " +
            $"runtime detector visuals were cleared. {reason}");
    }

    public void ClearSavedMarkers()
    {
        AbortControllerDetectorInteraction(true, false);

        HashSet<string> detectorIds =
            new HashSet<string>(markers.Keys, StringComparer.OrdinalIgnoreCase);

        // Include records whose anchors are still loading or failed to relocalize;
        // those IDs intentionally have no visible marker but their native XREAL map
        // files still need to be erased by a clear-all action.
        if (coordinateDatabase != null)
        {
            IReadOnlyList<DetectorCoordinateRecord> records = coordinateDatabase.GetAllRecords();
            for (int i = 0; i < records.Count; i++)
            {
                DetectorCoordinateRecord record = records[i];
                if (record != null && !string.IsNullOrWhiteSpace(record.detectorId))
                    detectorIds.Add(record.detectorId.Trim());
            }
        }

        foreach (string detectorId in detectorIds)
        {
            if (useSpatialAnchors && spatialAnchorManager != null)
                spatialAnchorManager.EraseAnchorForDetector(detectorId);
        }

        foreach (var kvp in markers)
        {
            DestroyMarkerVisualResources(kvp.Value);
            if (kvp.Value.root != null)
                Destroy(kvp.Value.root);
        }

        markers.Clear();
        placedDetectorOrder.Clear();
        currentFollowingDetectorId = "";
        activePlacementSession = null;
        lastInteractedDetectorId = "";

        if (coordinateDatabase != null)
            coordinateDatabase.ClearAllCoordinates();

        if (radiationSourceEstimator != null)
            radiationSourceEstimator.ClearEstimate();
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
        sortedHudDetectorIds.Clear();

        foreach (var pair in markers)
        {
            MarkerInfo marker = pair.Value;
            if (marker == null || marker.root == null || !marker.isPlaced || !marker.root.activeInHierarchy)
                continue;

            sortedHudDetectorIds.Add(marker.detectorId);
        }

        sortedHudDetectorIds.Sort(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < sortedHudDetectorIds.Count; i++)
        {
            if (!markers.TryGetValue(sortedHudDetectorIds[i], out MarkerInfo marker) ||
                marker == null || marker.root == null)
            {
                continue;
            }

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

    private void OnValidate()
    {
        maximumRadiationSnapshotAgeSeconds =
            Mathf.Max(0.5f, maximumRadiationSnapshotAgeSeconds);
        controllerSelectionMaxDistanceMeters =
            Mathf.Max(0.5f, controllerSelectionMaxDistanceMeters);
        controllerSelectionAngleDegrees =
            Mathf.Clamp(controllerSelectionAngleDegrees, 0.25f, 8f);
        controllerSelectionRadiusMultiplier =
            Mathf.Clamp(controllerSelectionRadiusMultiplier, 1f, 2f);
    }

    public struct DetectorHudMarkerState
    {
        public string detectorId;
        public Vector3 worldPosition;
        public float radiationValue;
        public Color color;
    }

    private class PlacementSession
    {
        public string detectorId;
        public bool restoresCommittedMarker;
        public Transform parent;
        public Vector3 worldPosition;
        public Quaternion worldRotation;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
        public bool visibilityRequested;
        public Vector3 savedPosition;
        public float lastEstimatedDistance;
        public float lastQrPixelSize;
        public Vector2 lastPlacementImagePoint;
        public int lastImageWidth;
        public int lastImageHeight;
        public string lastPlacementMethod;
        public bool hasValidPlaneHit;
        public string anchorState;
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
        public bool visibilityRequested;
        public bool centerVisualRequested;
        public bool isControllerHovered;
        public bool isControllerMoving;
        public List<FalloffShellInfo> falloffShells;
        public Material centerMaterial;
        public ARAnchor anchor;
        public string anchorGuid;
        public string anchorState;
    }

    private class DetectorMoveSession
    {
        public MarkerInfo marker;
        public Transform parent;
        public Vector3 worldPosition;
        public Quaternion worldRotation;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
        public bool visibilityRequested;
        public Vector3 savedPosition;
        public float lastEstimatedDistance;
        public string lastPlacementMethod;
        public ARAnchor anchor;
        public string anchorGuid;
        public string anchorState;
        public Vector3 initialPointerDirection;
        public Vector3 initialOffsetFromRayOrigin;
    }

    private class FalloffShellInfo
    {
        public GameObject root;
        public Renderer renderer;
        public bool visualRequested;
        public Material material;
    }
}
