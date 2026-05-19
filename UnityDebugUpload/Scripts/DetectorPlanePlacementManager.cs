using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Plane-based detector placement.
/// Clean fixed version:
/// - Keeps ARRaycastManager support.
/// - Adds a reliable manual ray-plane fallback for XREAL cases where ARRaycast(ray, ...) fails.
/// - Uses ARPlane.center and ARPlane.boundary instead of assuming the plane center is local zero.
/// - Recomputes the hit every frame and again on Place, so stale edge hits do not get reused.
/// </summary>
public class DetectorPlanePlacementManager : MonoBehaviour
{
    [Header("Required References")]
    [SerializeField] private DetectorWorldMarkerManager markerManager;
    [SerializeField] private ARRaycastManager raycastManager;

    [Header("Optional AR References")]
    [SerializeField] private ARPlaneManager planeManager;
    [SerializeField] private Camera xrCamera;

    [Header("UI")]
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private Button placeButton;
    [SerializeField] private Button cancelButton;

    [Header("Raycast Mode")]
    [Tooltip("Uses ARRaycastManager screen-center raycast when true. Uses Camera center Ray when false.")]
    [SerializeField] private bool useScreenCenterRaycast = false;

    [Tooltip("Used for visualizing the ray. Also used when Use Screen Center Raycast is off.")]
    [SerializeField] private bool useCenterViewRay = true;

    [SerializeField] private TrackableType primaryRaycastTypes = TrackableType.PlaneWithinPolygon;
    [SerializeField] private bool allowLoosePlaneFallback = true;
    [SerializeField] private TrackableType fallbackRaycastTypes = TrackableType.PlaneWithinBounds;

    [Header("Manual Ray-Plane Fallback")]
    [Tooltip("Recommended for XREAL if ARRaycastManager.Raycast(ray, ...) fails even though planes are tracked.")]
    [SerializeField] private bool useManualRayPlaneFallback = true;

    [Tooltip("If true and Use Screen Center Raycast is off, manual plane hit is tried before ARRaycastManager Raycast.")]
    [SerializeField] private bool preferManualRayPlaneHit = true;

    [Tooltip("Use ARPlane.boundary polygon when available. This is more accurate than only using plane.size.")]
    [SerializeField] private bool manualUsePlaneBoundaryPolygon = true;

    [Tooltip("Small tolerance around plane boundary. Set near 0 for stricter placement.")]
    [SerializeField] private float manualPlaneEdgePaddingMeters = 0.01f;

    [SerializeField] private float maxManualHitDistanceMeters = 10f;
    [SerializeField] private bool allowHorizontalPlanes = true;
    [SerializeField] private bool allowVerticalPlanes = true;
    [SerializeField] private bool allowNotAxisAlignedPlanes = true;

    [Tooltip("Only for debugging placement flow. Keep this off for final accurate plane placement.")]
    [SerializeField] private bool allowFixedDistanceFallback = false;
    [SerializeField] private float fixedFallbackDistanceMeters = 1.2f;

    [Header("Debug Visual")]
    [SerializeField] private bool showDebugRay = true;
    [SerializeField] private bool showRayEvenWithoutPendingQr = true;
    [SerializeField] private bool autoCreateLineRenderer = true;
    [SerializeField] private LineRenderer centerRayLine;
    [SerializeField] private float centerRayVisualLengthMeters = 2.0f;
    [SerializeField] private float rayWidth = 0.01f;

    [Header("Debug Hit Marker")]
    [SerializeField] private bool showHitPreviewSphere = true;
    [SerializeField] private GameObject hitPreviewSphere;
    [SerializeField] private float hitPreviewSphereSize = 0.06f;

    [Header("Debug Log")]
    [SerializeField] private bool verboseDebugLog = false;

    [Header("Marker Position Adjustment")]
    [Tooltip("Push marker slightly away from the plane. Useful for wall-mounted QR so the ball is not hidden inside the wall.")]
    [SerializeField] private float normalOffsetMeters = 0.03f;

    [Tooltip("If on, marker rotation faces the user instead of using the plane pose rotation.")]
    [SerializeField] private bool faceMarkerToUser = true;

    private readonly List<ARRaycastHit> hits = new List<ARRaycastHit>();

    private string pendingDetectorId;
    private Vector2 pendingQrImageCenter;
    private int pendingQrImageWidth;
    private int pendingQrImageHeight;
    private float pendingQrPixelSize;
    private bool hasPendingDetector;

    private bool hasLastPlacementHit;
    private PlacementHit lastPlacementHit;

    private struct PlacementHit
    {
        public bool isValid;
        public Pose pose;
        public float distance;
        public string trackableId;
        public Vector3 normal;
        public string method;
    }

    private void Awake()
    {
        EnsureReferences();
        EnsureDebugObjects();
        SetPlaceButtonEnabled(false);
    }

    private void OnEnable()
    {
        QRScanner.OnQRDetectedDetailed += HandleQrDetected;
        QRScanner.OnQRDetected += HandleQrDetectedLegacy;

        if (placeButton != null)
            placeButton.onClick.AddListener(PlacePendingDetectorOnPlane);

        if (cancelButton != null)
            cancelButton.onClick.AddListener(CancelPendingPlacement);
    }

    private void OnDisable()
    {
        QRScanner.OnQRDetectedDetailed -= HandleQrDetected;
        QRScanner.OnQRDetected -= HandleQrDetectedLegacy;

        if (placeButton != null)
            placeButton.onClick.RemoveListener(PlacePendingDetectorOnPlane);

        if (cancelButton != null)
            cancelButton.onClick.RemoveListener(CancelPendingPlacement);
    }

    private void Update()
    {
        EnsureReferences();

        Ray ray = CreateCenterRay();
        bool shouldShowRay = showDebugRay && (showRayEvenWithoutPendingQr || hasPendingDetector);

        hasLastPlacementHit = TryGetPlacementHit(out lastPlacementHit);

        if (hasLastPlacementHit)
        {
            UpdateCenterRayVisual(shouldShowRay, ray, lastPlacementHit.pose.position, true);
            UpdateHitPreview(true, lastPlacementHit.pose.position, lastPlacementHit.normal);
        }
        else
        {
            // Important: clear stale hit every failed frame.
            lastPlacementHit = default;
            UpdateCenterRayVisual(shouldShowRay, ray, ray.origin + ray.direction.normalized * centerRayVisualLengthMeters, false);
            UpdateHitPreview(false, Vector3.zero, Vector3.up);
        }

        if (!hasPendingDetector)
            return;

        if (raycastManager == null || !raycastManager.enabled)
        {
            UpdateStatus($"{pendingDetectorId}\nARRaycastManager is missing or disabled.");
            return;
        }

        if (hasLastPlacementHit)
        {
            UpdateStatus($"{pendingDetectorId}\nPlane HIT ({lastPlacementHit.distance:F2}m)\n{lastPlacementHit.method}\nPress Place");
        }
        else
        {
            int planeCount = CountTrackedPlanes();
            UpdateStatus($"{pendingDetectorId}\nNo plane hit at center yet.\nTracked planes: {planeCount}\nScan surface slowly, aim center at QR/device.");
        }
    }

    private void HandleQrDetectedLegacy(string qrText)
    {
        HandleQrDetected(qrText, new Vector2(0.5f, 0.5f), 0, 0, -1f);
    }

    private void HandleQrDetected(string qrText, Vector2 imageCenter, int imageWidth, int imageHeight, float qrPixelSize)
    {
        string detectorId = NormalizeDetectorId(qrText);
        if (string.IsNullOrEmpty(detectorId))
            return;

        pendingDetectorId = detectorId;
        pendingQrImageCenter = imageCenter;
        pendingQrImageWidth = imageWidth;
        pendingQrImageHeight = imageHeight;
        pendingQrPixelSize = qrPixelSize;
        hasPendingDetector = true;

        SetPlaceButtonEnabled(true);
        UpdateStatus($"QR detected: {pendingDetectorId}\nLook at the real QR/device through glasses, then press Place.");
        Debug.Log($"[PlanePlacement] Pending detector placement: {pendingDetectorId}");
    }

    public void PlacePendingDetectorOnPlane()
    {
        EnsureReferences();

        if (!hasPendingDetector || string.IsNullOrEmpty(pendingDetectorId))
        {
            UpdateStatus("No pending detector. Scan QR first.");
            Debug.LogWarning("[PlanePlacement] Place pressed, but there is no pending detector.");
            return;
        }

        Pose pose;
        string placementMethod;
        bool placedByPlane;
        string planeTrackableId;
        float hitDistance;
        Vector3 planeNormal;

        // Recompute on click. Do not trust a previous frame's edge hit.
        if (TryGetPlacementHit(out PlacementHit hit))
        {
            pose = hit.pose;
            placementMethod = hit.method;
            placedByPlane = true;
            planeTrackableId = hit.trackableId;
            hitDistance = hit.distance;
            planeNormal = hit.normal;
            pose.position += planeNormal * normalOffsetMeters;
        }
        else if (allowFixedDistanceFallback && xrCamera != null)
        {
            Ray ray = CreateCenterRay();
            Vector3 position = ray.origin + ray.direction.normalized * fixedFallbackDistanceMeters;
            Quaternion rotation = Quaternion.LookRotation(ray.direction.normalized, Vector3.up);
            pose = new Pose(position, rotation);
            placementMethod = "HeadRayFixedDistanceFallback";
            placedByPlane = false;
            planeTrackableId = "";
            hitDistance = fixedFallbackDistanceMeters;
            planeNormal = Vector3.zero;
        }
        else
        {
            int planeCount = CountTrackedPlanes();
            UpdateStatus($"Place failed.\nNo valid plane hit at center.\nTracked planes: {planeCount}");
            Debug.LogWarning($"[PlanePlacement] Place failed. No valid placement hit. trackedPlanes={planeCount}, raycastManager={(raycastManager != null)}, camera={(xrCamera != null ? xrCamera.name : "null")}");
            return;
        }

        Quaternion markerRotation = pose.rotation;
        if (faceMarkerToUser && xrCamera != null)
        {
            Vector3 toUser = xrCamera.transform.position - pose.position;
            if (toUser.sqrMagnitude > 0.0001f)
                markerRotation = Quaternion.LookRotation(-toUser.normalized, Vector3.up);
        }

        if (markerManager == null)
        {
            UpdateStatus("Place failed. Marker Manager is missing.");
            Debug.LogError("[PlanePlacement] Marker Manager is missing.");
            return;
        }

        markerManager.PlaceDetectorAtWorldPose(
            pendingDetectorId,
            pose.position,
            markerRotation,
            hitDistance,
            pendingQrPixelSize,
            pendingQrImageCenter,
            pendingQrImageWidth,
            pendingQrImageHeight,
            placementMethod,
            placedByPlane,
            planeTrackableId,
            hitDistance,
            planeNormal
        );

        UpdateStatus($"Placed: {pendingDetectorId}\n{placementMethod}\n{hitDistance:F2}m");
        Debug.Log($"[PlanePlacement] Detector placed: {pendingDetectorId}, method={placementMethod}, pos={pose.position}, trackable={planeTrackableId}, distance={hitDistance:F2}");

        hasPendingDetector = false;
        pendingDetectorId = "";
        SetPlaceButtonEnabled(false);
    }

    public void CancelPendingPlacement()
    {
        hasPendingDetector = false;
        pendingDetectorId = "";
        hasLastPlacementHit = false;
        lastPlacementHit = default;
        SetPlaceButtonEnabled(false);
        UpdateStatus("Placement cancelled. Scan QR again when ready.");
    }

    private bool TryGetPlacementHit(out PlacementHit placementHit)
    {
        placementHit = default;
        EnsureReferences();

        Ray ray = CreateCenterRay();

        if (!useScreenCenterRaycast && preferManualRayPlaneHit && useManualRayPlaneFallback)
        {
            if (TryManualRayPlaneHit(ray, out placementHit))
                return true;
        }

        if (TryARRaycastHit(out placementHit))
            return true;

        if (useManualRayPlaneFallback)
        {
            if (TryManualRayPlaneHit(ray, out placementHit))
                return true;
        }

        return false;
    }

    private bool TryARRaycastHit(out PlacementHit placementHit)
    {
        placementHit = default;
        EnsureReferences();

        if (raycastManager == null || xrCamera == null)
            return false;

        hits.Clear();

        if (useScreenCenterRaycast)
        {
            Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            if (raycastManager.Raycast(center, hits, primaryRaycastTypes) && hits.Count > 0)
            {
                placementHit = ConvertARRaycastHit(hits[0], "HeadRayPlane");
                return true;
            }

            if (allowLoosePlaneFallback)
            {
                hits.Clear();
                if (raycastManager.Raycast(center, hits, fallbackRaycastTypes) && hits.Count > 0)
                {
                    placementHit = ConvertARRaycastHit(hits[0], "HeadRayPlaneLoose");
                    return true;
                }
            }
        }
        else
        {
            Ray ray = CreateCenterRay();

            if (raycastManager.Raycast(ray, hits, primaryRaycastTypes) && hits.Count > 0)
            {
                placementHit = ConvertARRaycastHit(hits[0], "HeadRayPlane");
                return true;
            }

            if (allowLoosePlaneFallback)
            {
                hits.Clear();
                if (raycastManager.Raycast(ray, hits, fallbackRaycastTypes) && hits.Count > 0)
                {
                    placementHit = ConvertARRaycastHit(hits[0], "HeadRayPlaneLoose");
                    return true;
                }
            }
        }

        return false;
    }

    private PlacementHit ConvertARRaycastHit(ARRaycastHit hit, string method)
    {
        Vector3 normal = GetPlaneNormalFromHit(hit);
        return new PlacementHit
        {
            isValid = true,
            pose = hit.pose,
            distance = hit.distance,
            trackableId = hit.trackableId.ToString(),
            normal = normal,
            method = method
        };
    }

    private bool TryManualRayPlaneHit(Ray inputRay, out PlacementHit placementHit)
    {
        placementHit = default;

        if (planeManager == null || xrCamera == null)
            return false;

        Ray ray = new Ray(inputRay.origin, inputRay.direction.normalized);
        bool found = false;
        float bestDistance = float.MaxValue;

        foreach (ARPlane plane in planeManager.trackables)
        {
            if (plane == null)
                continue;

            if (plane.trackingState != TrackingState.Tracking)
                continue;

            if (!IsPlaneAlignmentAllowed(plane.alignment))
                continue;

            // Use the actual local center of the ARPlane, not local zero.
            Vector3 planeCenterWorld = plane.transform.TransformPoint(plane.center);
            Vector3 planeNormal = plane.transform.up.normalized;

            float denominator = Vector3.Dot(ray.direction, planeNormal);
            if (Mathf.Abs(denominator) < 0.0001f)
                continue;

            float distance = Vector3.Dot(planeCenterWorld - ray.origin, planeNormal) / denominator;
            if (distance <= 0f || distance > maxManualHitDistanceMeters)
                continue;

            Vector3 intersection = ray.origin + ray.direction * distance;
            Vector3 local = plane.transform.InverseTransformPoint(intersection);

            if (!IsManualHitInsidePlane(plane, local))
                continue;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                found = true;

                Quaternion rotation = Quaternion.LookRotation(ray.direction, planeNormal);
                placementHit = new PlacementHit
                {
                    isValid = true,
                    pose = new Pose(intersection, rotation),
                    distance = distance,
                    trackableId = plane.trackableId.ToString(),
                    normal = planeNormal,
                    method = "ManualHeadRayPlane"
                };
            }
        }

        if (verboseDebugLog)
        {
            if (found)
                Debug.Log($"[PlanePlacement][ManualFallback] SUCCESS pos={placementHit.pose.position}, distance={placementHit.distance:F2}, trackable={placementHit.trackableId}");
            else
                Debug.LogWarning("[PlanePlacement][ManualFallback] No valid manual plane hit.");
        }

        return found;
    }

    private bool IsPlaneAlignmentAllowed(PlaneAlignment alignment)
    {
        if ((alignment == PlaneAlignment.HorizontalUp || alignment == PlaneAlignment.HorizontalDown) && allowHorizontalPlanes)
            return true;

        if (alignment == PlaneAlignment.Vertical && allowVerticalPlanes)
            return true;

        if (alignment == PlaneAlignment.NotAxisAligned && allowNotAxisAlignedPlanes)
            return true;

        // Some providers may report None/Unknown-like states. Keep them allowed only when NotAxisAligned is allowed.
        if (alignment == PlaneAlignment.None && allowNotAxisAlignedPlanes)
            return true;

        return false;
    }

    private bool IsManualHitInsidePlane(ARPlane plane, Vector3 localPoint)
    {
        if (manualUsePlaneBoundaryPolygon)
        {
            var boundary = plane.boundary;
            if (boundary.IsCreated && boundary.Length >= 3)
            {
                Vector2 point2D = new Vector2(localPoint.x, localPoint.z);
                bool insidePolygon = IsPointInsidePolygon(point2D, boundary);
                if (insidePolygon)
                    return true;

                if (manualPlaneEdgePaddingMeters > 0f)
                {
                    float minDistance = float.MaxValue;
                    for (int i = 0, j = boundary.Length - 1; i < boundary.Length; j = i++)
                    {
                        float d = DistancePointToSegment(point2D, boundary[j], boundary[i]);
                        if (d < minDistance)
                            minDistance = d;
                    }

                    return minDistance <= manualPlaneEdgePaddingMeters;
                }

                return false;
            }
        }

        // Fallback when boundary is unavailable: use center + size, not local zero + size.
        Vector3 center = plane.center;
        Vector2 size = plane.size;

        return
            Mathf.Abs(localPoint.x - center.x) <= size.x * 0.5f + manualPlaneEdgePaddingMeters &&
            Mathf.Abs(localPoint.z - center.z) <= size.y * 0.5f + manualPlaneEdgePaddingMeters;
    }

    private bool IsPointInsidePolygon(Vector2 point, Unity.Collections.NativeArray<Vector2> polygon)
    {
        bool inside = false;

        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            Vector2 pi = polygon[i];
            Vector2 pj = polygon[j];

            bool intersects =
                ((pi.y > point.y) != (pj.y > point.y)) &&
                (point.x < (pj.x - pi.x) * (point.y - pi.y) / ((pj.y - pi.y) + 0.000001f) + pi.x);

            if (intersects)
                inside = !inside;
        }

        return inside;
    }

    private float DistancePointToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float denominator = Mathf.Max(ab.sqrMagnitude, 0.000001f);
        float t = Vector2.Dot(point - a, ab) / denominator;
        t = Mathf.Clamp01(t);
        Vector2 closest = a + ab * t;
        return Vector2.Distance(point, closest);
    }

    private Ray CreateCenterRay()
    {
        if (xrCamera == null)
            xrCamera = Camera.main;

        if (xrCamera == null)
            return new Ray(transform.position, transform.forward);

        if (useCenterViewRay)
            return xrCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));

        return new Ray(xrCamera.transform.position, xrCamera.transform.forward);
    }

    private Vector3 GetPlaneNormalFromHit(ARRaycastHit hit)
    {
        if (planeManager != null)
        {
            ARPlane plane = planeManager.GetPlane(hit.trackableId);
            if (plane != null)
                return plane.transform.up.normalized;
        }

        return hit.pose.up.normalized;
    }

    private int CountTrackedPlanes()
    {
        if (planeManager == null)
            return -1;

        int count = 0;
        foreach (ARPlane plane in planeManager.trackables)
        {
            if (plane != null && plane.trackingState == TrackingState.Tracking)
                count++;
        }
        return count;
    }

    private void EnsureReferences()
    {
        if (xrCamera == null)
            xrCamera = Camera.main;

        if (markerManager == null)
            markerManager = FindObjectOfType<DetectorWorldMarkerManager>();

        if (raycastManager == null)
            raycastManager = FindObjectOfType<ARRaycastManager>();

        if (planeManager == null)
            planeManager = FindObjectOfType<ARPlaneManager>();
    }

    private void EnsureDebugObjects()
    {
        if (showDebugRay && autoCreateLineRenderer && centerRayLine == null)
        {
            GameObject lineObject = new GameObject("CenterRayDebugLine");
            lineObject.transform.SetParent(transform);
            centerRayLine = lineObject.AddComponent<LineRenderer>();
            centerRayLine.positionCount = 2;
            centerRayLine.startWidth = rayWidth;
            centerRayLine.endWidth = rayWidth;
            centerRayLine.useWorldSpace = true;

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader != null)
                centerRayLine.material = new Material(shader);
        }

        if (showHitPreviewSphere && hitPreviewSphere == null)
        {
            hitPreviewSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            hitPreviewSphere.name = "PlaneHitPreviewSphere";
            hitPreviewSphere.transform.SetParent(transform);
            hitPreviewSphere.transform.localScale = Vector3.one * hitPreviewSphereSize;

            Collider collider = hitPreviewSphere.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            Renderer renderer = hitPreviewSphere.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                    shader = Shader.Find("Standard");
                if (shader != null)
                    renderer.material = new Material(shader);
            }

            hitPreviewSphere.SetActive(false);
        }
    }

    private void UpdateCenterRayVisual(bool visible, Ray ray, Vector3 endPoint, bool hit)
    {
        if (!showDebugRay || centerRayLine == null)
            return;

        centerRayLine.enabled = visible;
        if (!visible)
            return;

        centerRayLine.positionCount = 2;
        centerRayLine.SetPosition(0, ray.origin);
        centerRayLine.SetPosition(1, endPoint);

        if (centerRayLine.material != null)
        {
            Color color = hit ? Color.green : Color.red;
            if (centerRayLine.material.HasProperty("_Color"))
                centerRayLine.material.SetColor("_Color", color);
            if (centerRayLine.material.HasProperty("_BaseColor"))
                centerRayLine.material.SetColor("_BaseColor", color);
        }
    }

    private void UpdateHitPreview(bool visible, Vector3 position, Vector3 normal)
    {
        if (!showHitPreviewSphere || hitPreviewSphere == null)
            return;

        hitPreviewSphere.SetActive(visible);
        if (!visible)
            return;

        hitPreviewSphere.transform.position = position + normal.normalized * normalOffsetMeters;
        hitPreviewSphere.transform.localScale = Vector3.one * hitPreviewSphereSize;
    }

    private void SetPlaceButtonEnabled(bool enabled)
    {
        if (placeButton != null)
            placeButton.interactable = enabled;
    }

    private void UpdateStatus(string message)
    {
        if (statusText != null)
        {
            if (!statusText.gameObject.activeSelf)
                statusText.gameObject.SetActive(true);
            statusText.text = message;
        }

        Debug.Log($"[PlanePlacement] {message}");
    }

    private string NormalizeDetectorId(string rawQrText)
    {
        return string.IsNullOrWhiteSpace(rawQrText) ? "" : rawQrText.Trim();
    }
}
