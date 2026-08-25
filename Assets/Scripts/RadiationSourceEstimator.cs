using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Estimates one isotropic point source from placed detector positions and CPS.
///
/// The model is lambda_i = background_i + activity * calibration_i / r_i^2.
/// A coarse-to-fine grid search minimizes Poisson negative log likelihood. Search
/// work is split across frames so a room-sized grid does not stall the AR view.
/// A non-collinear planar array uses a 2D best-effort source projection on the
/// detector plane because the plane-normal side cannot be resolved reliably.
/// This is a relative localization aid, not a dose or safety certification model.
/// </summary>
[DisallowMultipleComponent]
public sealed class RadiationSourceEstimator : MonoBehaviour
{
    [Serializable]
    public sealed class DetectorCalibration
    {
        public string detectorId = "";
        [Min(0.0001f)] public float responseFactor = 1f;
        public bool overrideBackground;
        [Min(0f)] public float backgroundCps;
    }

    public enum EstimatorState
    {
        Disabled,
        WaitingForServer,
        StaleRadiationData,
        WaitingForRoomOrigin,
        WaitingForDetectors,
        InsufficientGeometry,
        Searching,
        OutOfSearchBounds,
        PoorFit,
        Ready
    }

    public readonly struct SourceEstimate
    {
        public readonly Vector3 worldPosition;
        public readonly Vector3 coordinatePosition;
        public readonly float relativeActivity;
        public readonly float fitQuality;
        public readonly float rmsResidualCps;
        public readonly int detectorCount;
        public readonly float finalGridSpacingMeters;

        public SourceEstimate(
            Vector3 worldPosition,
            Vector3 coordinatePosition,
            float relativeActivity,
            float fitQuality,
            float rmsResidualCps,
            int detectorCount,
            float finalGridSpacingMeters)
        {
            this.worldPosition = worldPosition;
            this.coordinatePosition = coordinatePosition;
            this.relativeActivity = relativeActivity;
            this.fitQuality = fitQuality;
            this.rmsResidualCps = rmsResidualCps;
            this.detectorCount = detectorCount;
            this.finalGridSpacingMeters = finalGridSpacingMeters;
        }
    }

    [Header("Inputs")]
    [SerializeField] private DetectorWorldMarkerManager markerManager;
    [SerializeField] private RadiationReceiver radiationReceiver;
    [SerializeField] private DetectorCoordinateDatabase coordinateDatabase;
    [SerializeField] private RoomCoordinateSystem roomCoordinateSystem;
    [Tooltip("Use stable DB room coordinates and require a matching ROOM_ORIGIN calibration. Keep this ON for the deployed app.")]
    [SerializeField] private bool useRoomCoordinateDatabase = true;
    [Tooltip("Optional manual unit-scale frame used only when Use Room Coordinate Database is OFF.")]
    [SerializeField] private Transform coordinateFrame;
    [SerializeField] private bool requireServerConnection = true;
    [Tooltip("Hide the estimate when the server stays connected but stops publishing CPS snapshots.")]
    [SerializeField] private bool requireFreshLiveData = true;
    [SerializeField, Min(0.5f)] private float maximumLiveDataAgeSeconds = 5f;

    [Header("Detector Model")]
    [SerializeField, Range(4, 64)] private int minimumDetectorCount = 4;
    [SerializeField, Min(0f)] private float globalBackgroundCps = 0f;
    [SerializeField, Min(0.01f)] private float minimumSourceDistanceMeters = 0.08f;
    [SerializeField, Min(0f)] private float minimumTotalExcessCps = 1f;
    [SerializeField, Min(0f)] private float minimumDetectorSpanMeters = 0.25f;
    [Tooltip("Minimum detector spread perpendicular to the longest detector-to-detector line. Smaller layouts are effectively collinear and cannot localize a source.")]
    [SerializeField, Min(0.001f)] private float minimumNonCollinearOffsetMeters = 0.03f;
    [Tooltip("Layouts no thicker than this are treated as one detector plane. The 10 cm default tolerates placement/anchor error in a physically wide square.")]
    [SerializeField, Min(0.01f)] private float maximumPlanarThicknessMeters = 0.10f;
    [SerializeField] private List<DetectorCalibration> detectorCalibrations =
        new List<DetectorCalibration>();

    [Header("Search Volume")]
    [Tooltip("OFF derives a volume from the detector bounds plus Search Padding.")]
    [SerializeField] private bool useConfiguredSearchBounds;
    [SerializeField] private Vector3 configuredBoundsCenter = new Vector3(0f, 1.5f, 0f);
    [SerializeField] private Vector3 configuredBoundsSize = new Vector3(5f, 3f, 5f);
    [SerializeField, Min(0f)] private float searchPaddingMeters = 1f;
    [SerializeField, Min(0.02f)] private float coarseGridSpacingMeters = 0.25f;
    [SerializeField, Range(0, 4)] private int refinementPasses = 2;
    [SerializeField, Range(2, 8)] private int refinementDivision = 4;
    [SerializeField, Range(1, 4)] private int refinementRadiusInPreviousCells = 2;
    [SerializeField, Min(1000)] private int maximumCoarseCandidates = 100000;

    [Header("Estimate Acceptance")]
    [Tooltip("Hide a solution on the initial search-volume edge; it usually means the real source lies outside the searched room volume.")]
    [SerializeField] private bool rejectSearchBoundarySolutions = true;
    [SerializeField, Range(0.5f, 2f)] private float boundaryMarginInCoarseCells = 0.75f;
    [Tooltip("Minimum improvement over a spatially uniform CPS model before showing the source sphere.")]
    [SerializeField, Range(0f, 1f)] private float minimumFitQuality = 0.05f;
    [Tooltip("A planar array can have a useful source projection even when improvement over a uniform model is small. Accept that projection only when RMS divided by mean CPS above background is below this value.")]
    [SerializeField, Range(0.05f, 1f)] private float maximumPlanarRelativeRms = 0.35f;

    [Header("Scheduling")]
    [SerializeField, Min(100)] private int candidatesPerFrame = 2000;
    [SerializeField, Min(0.1f)] private float detectorSnapshotIntervalSeconds = 0.5f;

    [Header("Estimated Source Visual")]
    [SerializeField] private bool showEstimatedSource = true;
    [SerializeField, Min(0.03f)] private float sourceSphereDiameterMeters = 0.18f;
    [SerializeField] private Color sourceSphereColor = new Color(1f, 0.08f, 0.02f, 1f);
    [SerializeField, Range(0.05f, 1f)] private float sourceSphereAlpha = 0.48f;
    [SerializeField] private bool pulseSourceSphere;
    [SerializeField, Min(0.05f)] private float pulseCyclesPerSecond = 0.8f;
    [SerializeField, Range(0f, 0.3f)] private float pulseScaleAmount = 0.06f;

    [Header("Diagnostics")]
    [SerializeField] private bool verboseLogging;

    public event Action<SourceEstimate> EstimateUpdated;

    public EstimatorState State { get; private set; } = EstimatorState.Disabled;
    public string StateMessage { get; private set; } = "disabled";
    public bool HasEstimate { get; private set; }
    public SourceEstimate LatestEstimate { get; private set; }
    public Transform CoordinateFrame => coordinateFrame;
    public Vector3 EstimatedWorldPosition =>
        coordinateFrame != null
            ? coordinateFrame.TransformPoint(LatestEstimate.coordinatePosition)
            : LatestEstimate.coordinatePosition;

    private readonly List<DetectorWorldMarkerManager.DetectorHudMarkerState> markerStates =
        new List<DetectorWorldMarkerManager.DetectorHudMarkerState>();
    private readonly List<DetectorSample> sampleBuffer = new List<DetectorSample>();
    private Coroutine estimationCoroutine;
    private GameObject sourceVisual;
    private Renderer sourceRenderer;
    private Material sourceMaterial;
    private float nextSnapshotTime;
    private int estimationGeneration;
    private int lastObservedSnapshotHash;
    private bool hasObservedSnapshot;
    private bool estimateQueued;
    private bool serverConnected;
    private bool hasReceivedLiveData;
    private float lastLiveDataTime = float.NegativeInfinity;
    private string activeRoomId = "";

    private void OnEnable()
    {
        hasReceivedLiveData = false;
        lastLiveDataTime = float.NegativeInfinity;
        RadiationReceiver.OnRadiationDataReceived += HandleRadiationDataReceived;
        RadiationReceiver.OnServerConnectionChanged += HandleServerConnectionChanged;
        EnsureReferences();
        serverConnected = radiationReceiver != null && radiationReceiver.IsConnected;
        hasObservedSnapshot = false;
        State = EstimatorState.WaitingForDetectors;
        StateMessage = "waiting for placed detectors";
        nextSnapshotTime = 0f;
    }

    private void Start()
    {
        EnsureReferences();
        serverConnected = radiationReceiver != null && radiationReceiver.IsConnected;
        RefreshDetectorSnapshot(true);
    }

    private void OnDisable()
    {
        RadiationReceiver.OnRadiationDataReceived -= HandleRadiationDataReceived;
        RadiationReceiver.OnServerConnectionChanged -= HandleServerConnectionChanged;
        CancelSearch();
        HasEstimate = false;
        hasObservedSnapshot = false;
        hasReceivedLiveData = false;
        lastLiveDataTime = float.NegativeInfinity;
        SetSourceVisualVisible(false);
        State = EstimatorState.Disabled;
        StateMessage = "disabled";
    }

    private void OnDestroy()
    {
        if (sourceMaterial != null)
            Destroy(sourceMaterial);
        if (sourceVisual != null)
            Destroy(sourceVisual);
    }

    private void Update()
    {
        if (Time.unscaledTime >= nextSnapshotTime)
        {
            nextSnapshotTime = Time.unscaledTime + detectorSnapshotIntervalSeconds;
            RefreshDetectorSnapshot(false);
        }
    }

    private void LateUpdate()
    {
        if (!HasEstimate || sourceVisual == null)
            return;

        Vector3 worldPosition = coordinateFrame != null
            ? coordinateFrame.TransformPoint(LatestEstimate.coordinatePosition)
            : LatestEstimate.coordinatePosition;
        sourceVisual.transform.position = worldPosition;

        float pulse = 1f;
        if (pulseSourceSphere)
        {
            pulse += Mathf.Sin(Time.unscaledTime * Mathf.PI * 2f * pulseCyclesPerSecond) *
                     pulseScaleAmount;
        }

        sourceVisual.transform.localScale = Vector3.one * sourceSphereDiameterMeters * pulse;
    }

    private void OnValidate()
    {
        minimumDetectorCount = Mathf.Max(4, minimumDetectorCount);
        minimumSourceDistanceMeters = Mathf.Max(0.01f, minimumSourceDistanceMeters);
        minimumNonCollinearOffsetMeters = Mathf.Max(
            0.001f,
            minimumNonCollinearOffsetMeters);
        maximumPlanarThicknessMeters = Mathf.Max(
            minimumNonCollinearOffsetMeters,
            maximumPlanarThicknessMeters);
        coarseGridSpacingMeters = Mathf.Max(0.02f, coarseGridSpacingMeters);
        configuredBoundsSize.x = Mathf.Max(0.02f, configuredBoundsSize.x);
        configuredBoundsSize.y = Mathf.Max(0.02f, configuredBoundsSize.y);
        configuredBoundsSize.z = Mathf.Max(0.02f, configuredBoundsSize.z);
        candidatesPerFrame = Mathf.Max(100, candidatesPerFrame);
        maximumCoarseCandidates = Mathf.Max(1000, maximumCoarseCandidates);
        detectorSnapshotIntervalSeconds = Mathf.Max(0.1f, detectorSnapshotIntervalSeconds);
        maximumLiveDataAgeSeconds = Mathf.Max(0.5f, maximumLiveDataAgeSeconds);
        boundaryMarginInCoarseCells = Mathf.Clamp(boundaryMarginInCoarseCells, 0.5f, 2f);
        minimumFitQuality = Mathf.Clamp01(minimumFitQuality);
        maximumPlanarRelativeRms = Mathf.Clamp(maximumPlanarRelativeRms, 0.05f, 1f);

        if (sourceMaterial != null)
            ApplySourceMaterialColor();
    }

    /// <summary>
    /// Supplies the current Room Coordinate System transform. Changing frames
    /// invalidates an in-flight solution and immediately requests a new estimate.
    /// </summary>
    public void SetCoordinateFrame(Transform roomFrame)
    {
        if (coordinateFrame == roomFrame)
            return;

        coordinateFrame = roomFrame;
        CancelSearch();
        HasEstimate = false;
        SetSourceVisualVisible(false);
        hasObservedSnapshot = false;
        nextSnapshotTime = 0f;
    }

    public void RequestEstimateNow()
    {
        hasObservedSnapshot = false;
        nextSnapshotTime = 0f;
        RefreshDetectorSnapshot(true);
    }

    public void ClearEstimate()
    {
        CancelSearch();
        HasEstimate = false;
        SetSourceVisualVisible(false);
        State = EstimatorState.WaitingForDetectors;
        StateMessage = "estimate cleared";
    }

    private void EnsureReferences()
    {
        if (markerManager == null)
            markerManager = FindFirstObjectByType<DetectorWorldMarkerManager>();
        if (radiationReceiver == null)
            radiationReceiver = FindFirstObjectByType<RadiationReceiver>();
        if (coordinateDatabase == null)
            coordinateDatabase = DetectorCoordinateDatabase.Instance != null
                ? DetectorCoordinateDatabase.Instance
                : FindFirstObjectByType<DetectorCoordinateDatabase>();
        if (roomCoordinateSystem == null)
            roomCoordinateSystem = FindFirstObjectByType<RoomCoordinateSystem>();
    }

    private void HandleRadiationDataReceived(Dictionary<string, float> data)
    {
        if (data == null)
            return;

        hasReceivedLiveData = true;
        lastLiveDataTime = Time.unscaledTime;
        nextSnapshotTime = 0f;
    }

    private void HandleServerConnectionChanged(bool isConnected)
    {
        serverConnected = isConnected;
        hasReceivedLiveData = false;
        lastLiveDataTime = float.NegativeInfinity;
        if (!isConnected && requireServerConnection)
        {
            CancelSearch();
            HasEstimate = false;
            SetSourceVisualVisible(false);
            State = EstimatorState.WaitingForServer;
            StateMessage = "waiting for server connection";
            return;
        }

        hasObservedSnapshot = false;
        nextSnapshotTime = 0f;
    }

    private void RefreshDetectorSnapshot(bool force)
    {
        EnsureReferences();

        if (requireServerConnection && !serverConnected)
        {
            CancelSearch();
            HasEstimate = false;
            SetSourceVisualVisible(false);
            State = EstimatorState.WaitingForServer;
            StateMessage = "waiting for server connection";
            return;
        }

        if (requireServerConnection && requireFreshLiveData)
        {
            // This component is normally added by DetectorWorldMarkerManager at
            // runtime. If the receiver published its first snapshot just before
            // that happens, the event was missed even though the receiver still
            // owns a valid fresh dictionary. Seed from the receiver so the source
            // estimate does not wait forever for a second server message.
            bool receiverHasFreshData = radiationReceiver != null &&
                                        radiationReceiver.HasFreshRadiationData;
            if (receiverHasFreshData && !hasReceivedLiveData)
            {
                hasReceivedLiveData = true;
                lastLiveDataTime = Time.unscaledTime;
            }

            float liveDataAge = Time.unscaledTime - lastLiveDataTime;
            if (!receiverHasFreshData ||
                !hasReceivedLiveData ||
                liveDataAge > maximumLiveDataAgeSeconds)
            {
                CancelSearch();
                HasEstimate = false;
                hasObservedSnapshot = false;
                SetSourceVisualVisible(false);
                State = EstimatorState.StaleRadiationData;
                StateMessage = hasReceivedLiveData
                    ? $"CPS data stale ({liveDataAge:F1}s old)"
                    : "waiting for first live CPS snapshot";
                return;
            }
        }

        bool roomInputReady = PrepareCoordinateInput(out bool inputChanged);
        if (!roomInputReady)
            return;

        force |= inputChanged;

        if (!useRoomCoordinateDatabase && markerManager == null)
        {
            State = EstimatorState.WaitingForDetectors;
            StateMessage = "DetectorWorldMarkerManager not found";
            return;
        }

        if (useRoomCoordinateDatabase)
            BuildRoomDatabaseSampleBuffer();
        else
        {
            markerManager.FillHudMarkerStates(markerStates);
            BuildWorldMarkerSampleBuffer();
        }
        int snapshotHash = ComputeSnapshotHash(sampleBuffer);

        if (!force && hasObservedSnapshot && snapshotHash == lastObservedSnapshotHash)
            return;

        hasObservedSnapshot = true;
        lastObservedSnapshotHash = snapshotHash;

        if (!ValidateSamples(
                sampleBuffer,
                out DetectorGeometry detectorGeometry,
                out string validationMessage))
        {
            CancelSearch();
            HasEstimate = false;
            SetSourceVisualVisible(false);
            StateMessage = validationMessage;
            return;
        }

        if (estimationCoroutine != null)
        {
            estimateQueued = true;
            return;
        }

        List<DetectorSample> snapshot = new List<DetectorSample>(sampleBuffer);
        int generation = ++estimationGeneration;
        estimationCoroutine = StartCoroutine(
            EstimateSourceCoroutine(snapshot, detectorGeometry, generation));
    }

    private bool PrepareCoordinateInput(out bool inputChanged)
    {
        inputChanged = false;
        if (!useRoomCoordinateDatabase)
            return true;

        if (roomCoordinateSystem == null ||
            !roomCoordinateSystem.IsCalibrated ||
            roomCoordinateSystem.CoordinateFrame == null ||
            string.IsNullOrWhiteSpace(roomCoordinateSystem.RoomId))
        {
            if (estimationCoroutine != null || HasEstimate)
            {
                CancelSearch();
                HasEstimate = false;
                SetSourceVisualVisible(false);
            }

            State = EstimatorState.WaitingForRoomOrigin;
            StateMessage = "scan and place ROOM_ORIGIN";
            return false;
        }

        if (coordinateDatabase == null)
        {
            if (estimationCoroutine != null || HasEstimate)
            {
                CancelSearch();
                HasEstimate = false;
                SetSourceVisualVisible(false);
            }

            State = EstimatorState.WaitingForDetectors;
            StateMessage = "DetectorCoordinateDatabase not found";
            return false;
        }

        string roomId = roomCoordinateSystem.RoomId ?? "";
        if (coordinateFrame != roomCoordinateSystem.CoordinateFrame ||
            !string.Equals(activeRoomId, roomId, StringComparison.OrdinalIgnoreCase))
        {
            CancelSearch();
            HasEstimate = false;
            SetSourceVisualVisible(false);
            coordinateFrame = roomCoordinateSystem.CoordinateFrame;
            activeRoomId = roomId;
            hasObservedSnapshot = false;
            inputChanged = true;
        }

        return true;
    }

    private void BuildRoomDatabaseSampleBuffer()
    {
        sampleBuffer.Clear();
        IReadOnlyList<DetectorCoordinateRecord> records = coordinateDatabase.GetAllRecords();
        IReadOnlyDictionary<string, float> liveData = radiationReceiver != null
            ? radiationReceiver.LatestDeviceData
            : null;

        for (int i = 0; i < records.Count; i++)
        {
            DetectorCoordinateRecord record = records[i];
            if (record == null ||
                string.IsNullOrWhiteSpace(record.detectorId) ||
                !record.HasRoomPose(activeRoomId))
            {
                continue;
            }

            float observedCps = -1f;
            bool hasLiveCps = liveData != null &&
                              liveData.TryGetValue(record.detectorId, out observedCps);
            if (!hasLiveCps)
            {
                if (requireServerConnection || record.lastRadiationValue < 0f)
                    continue;
                observedCps = record.lastRadiationValue;
            }

            if (!IsFinite(observedCps) || observedCps < 0f)
                continue;

            GetDetectorModel(
                record.detectorId,
                record.calibrationFactor,
                out float responseFactor,
                out float backgroundCps);

            Vector3 roomPosition = record.GetRoomPosition();
            if (!IsFinite(roomPosition) || !IsFinite(responseFactor) || responseFactor <= 0f)
                continue;

            sampleBuffer.Add(new DetectorSample
            {
                detectorId = record.detectorId.Trim(),
                coordinatePosition = roomPosition,
                observedCps = observedCps,
                responseFactor = responseFactor,
                backgroundCps = Mathf.Max(0f, backgroundCps)
            });
        }

        sampleBuffer.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.detectorId, right.detectorId));
    }

    private void BuildWorldMarkerSampleBuffer()
    {
        sampleBuffer.Clear();

        for (int i = 0; i < markerStates.Count; i++)
        {
            DetectorWorldMarkerManager.DetectorHudMarkerState state = markerStates[i];
            if (string.IsNullOrWhiteSpace(state.detectorId) ||
                !IsFinite(state.worldPosition) ||
                !IsFinite(state.radiationValue) ||
                state.radiationValue < 0f)
            {
                continue;
            }

            GetDetectorModel(
                state.detectorId,
                1f,
                out float responseFactor,
                out float backgroundCps);

            if (!IsFinite(responseFactor) || responseFactor <= 0f)
                continue;

            Vector3 coordinatePosition = coordinateFrame != null
                ? coordinateFrame.InverseTransformPoint(state.worldPosition)
                : state.worldPosition;

            sampleBuffer.Add(new DetectorSample
            {
                detectorId = state.detectorId,
                coordinatePosition = coordinatePosition,
                observedCps = state.radiationValue,
                responseFactor = responseFactor,
                backgroundCps = Mathf.Max(0f, backgroundCps)
            });
        }
    }

    private void GetDetectorModel(
        string detectorId,
        float storedResponseFactor,
        out float responseFactor,
        out float backgroundCps)
    {
        responseFactor = IsFinite(storedResponseFactor) && storedResponseFactor > 0f
            ? storedResponseFactor
            : 1f;
        backgroundCps = globalBackgroundCps;

        for (int i = 0; i < detectorCalibrations.Count; i++)
        {
            DetectorCalibration calibration = detectorCalibrations[i];
            if (calibration == null ||
                !string.Equals(calibration.detectorId, detectorId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            responseFactor = Mathf.Max(0.0001f, calibration.responseFactor);
            if (calibration.overrideBackground)
                backgroundCps = Mathf.Max(0f, calibration.backgroundCps);
            return;
        }
    }

    private bool ValidateSamples(
        List<DetectorSample> samples,
        out DetectorGeometry geometry,
        out string message)
    {
        geometry = default;

        if (samples.Count < minimumDetectorCount)
        {
            State = EstimatorState.WaitingForDetectors;
            message = $"need at least {minimumDetectorCount} placed detectors with CPS ({samples.Count} ready)";
            return false;
        }

        Vector3 min = samples[0].coordinatePosition;
        Vector3 max = min;
        float totalExcess = 0f;

        for (int i = 0; i < samples.Count; i++)
        {
            DetectorSample sample = samples[i];
            min = Vector3.Min(min, sample.coordinatePosition);
            max = Vector3.Max(max, sample.coordinatePosition);
            totalExcess += Mathf.Max(0f, sample.observedCps - sample.backgroundCps);
        }

        Vector3 extent = max - min;
        float largestSpan = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z));
        if (largestSpan < minimumDetectorSpanMeters)
        {
            State = EstimatorState.InsufficientGeometry;
            message = $"detectors span only {largestSpan:F2} m";
            return false;
        }

        if (!TryAnalyzeDetectorGeometry(samples, out geometry))
        {
            State = EstimatorState.InsufficientGeometry;
            message = "detector layout is nearly collinear";
            return false;
        }

        if (totalExcess < minimumTotalExcessCps)
        {
            State = EstimatorState.WaitingForDetectors;
            message = $"signal above background is only {totalExcess:F1} CPS";
            return false;
        }

        message = geometry.isPlanar
            ? $"ready for planar source projection ({geometry.outOfPlaneSpan:F3} m thickness)"
            : "ready";
        return true;
    }

    private IEnumerator EstimateSourceCoroutine(
        List<DetectorSample> samples,
        DetectorGeometry geometry,
        int generation)
    {
        State = EstimatorState.Searching;
        StateMessage = geometry.isPlanar
            ? $"searching detector plane with {samples.Count} detectors"
            : $"searching with {samples.Count} detectors";
        estimateQueued = false;

        Vector3 searchMin = default;
        Vector3 searchMax = default;
        Vector3 volumeMin = default;
        Vector3 volumeMax = default;
        Vector2 planarSearchMin = default;
        Vector2 planarSearchMax = default;
        Vector2 planarVolumeMin = default;
        Vector2 planarVolumeMax = default;
        float spacing;

        if (geometry.isPlanar)
        {
            GetInitialPlanarSearchBounds(
                samples,
                geometry,
                out planarSearchMin,
                out planarSearchMax);
            planarVolumeMin = planarSearchMin;
            planarVolumeMax = planarSearchMax;
            spacing = LimitPlanarCoarseSpacing(
                planarSearchMin,
                planarSearchMax,
                coarseGridSpacingMeters);
        }
        else
        {
            GetInitialSearchBounds(samples, out searchMin, out searchMax);
            volumeMin = searchMin;
            volumeMax = searchMax;
            spacing = LimitCoarseSpacing(searchMin, searchMax, coarseGridSpacingMeters);
        }

        float initialCoarseSpacing = spacing;
        Vector3 bestPosition = geometry.planeOrigin;
        double bestActivity = 0d;
        double bestNll = double.PositiveInfinity;
        int frameCandidateCount = 0;

        int passCount = 1 + refinementPasses;
        for (int pass = 0; pass < passCount; pass++)
        {
            if (pass > 0)
            {
                float radius = spacing * refinementRadiusInPreviousCells;
                if (geometry.isPlanar)
                {
                    Vector2 bestPlanePosition = ToPlaneCoordinates(geometry, bestPosition);
                    planarSearchMin = Vector2.Max(
                        planarVolumeMin,
                        bestPlanePosition - Vector2.one * radius);
                    planarSearchMax = Vector2.Min(
                        planarVolumeMax,
                        bestPlanePosition + Vector2.one * radius);
                }
                else
                {
                    searchMin = Vector3.Max(
                        volumeMin,
                        bestPosition - Vector3.one * radius);
                    searchMax = Vector3.Min(
                        volumeMax,
                        bestPosition + Vector3.one * radius);
                }

                spacing /= refinementDivision;
            }

            if (geometry.isPlanar)
            {
                int uCount = AxisPointCount(
                    planarSearchMin.x,
                    planarSearchMax.x,
                    spacing);
                int vCount = AxisPointCount(
                    planarSearchMin.y,
                    planarSearchMax.y,
                    spacing);

                for (int ui = 0; ui < uCount; ui++)
                {
                    float u = AxisCoordinate(
                        planarSearchMin.x,
                        planarSearchMax.x,
                        spacing,
                        ui,
                        uCount);
                    for (int vi = 0; vi < vCount; vi++)
                    {
                        if (generation != estimationGeneration)
                        {
                            estimationCoroutine = null;
                            yield break;
                        }

                        float v = AxisCoordinate(
                            planarSearchMin.y,
                            planarSearchMax.y,
                            spacing,
                            vi,
                            vCount);
                        Vector3 candidate = FromPlaneCoordinates(geometry, u, v);
                        EvaluateCandidate(samples, candidate, out double activity, out double nll);

                        if (IsBetterCandidate(
                                nll,
                                candidate,
                                bestNll,
                                bestPosition,
                                geometry.planeOrigin))
                        {
                            bestNll = nll;
                            bestPosition = candidate;
                            bestActivity = activity;
                        }

                        frameCandidateCount++;
                        if (frameCandidateCount >= candidatesPerFrame)
                        {
                            frameCandidateCount = 0;
                            yield return null;
                        }
                    }
                }
            }
            else
            {
                int xCount = AxisPointCount(searchMin.x, searchMax.x, spacing);
                int yCount = AxisPointCount(searchMin.y, searchMax.y, spacing);
                int zCount = AxisPointCount(searchMin.z, searchMax.z, spacing);

                for (int xi = 0; xi < xCount; xi++)
                {
                    float x = AxisCoordinate(
                        searchMin.x,
                        searchMax.x,
                        spacing,
                        xi,
                        xCount);
                    for (int yi = 0; yi < yCount; yi++)
                    {
                        float y = AxisCoordinate(
                            searchMin.y,
                            searchMax.y,
                            spacing,
                            yi,
                            yCount);
                        for (int zi = 0; zi < zCount; zi++)
                        {
                            if (generation != estimationGeneration)
                            {
                                estimationCoroutine = null;
                                yield break;
                            }

                            float z = AxisCoordinate(
                                searchMin.z,
                                searchMax.z,
                                spacing,
                                zi,
                                zCount);
                            Vector3 candidate = new Vector3(x, y, z);
                            EvaluateCandidate(
                                samples,
                                candidate,
                                out double activity,
                                out double nll);

                            if (IsBetterCandidate(
                                    nll,
                                    candidate,
                                    bestNll,
                                    bestPosition,
                                    geometry.planeOrigin))
                            {
                                bestNll = nll;
                                bestPosition = candidate;
                                bestActivity = activity;
                            }

                            frameCandidateCount++;
                            if (frameCandidateCount >= candidatesPerFrame)
                            {
                                frameCandidateCount = 0;
                                yield return null;
                            }
                        }
                    }
                }
            }
        }

        CalculateFitMetrics(
            samples,
            bestPosition,
            bestActivity,
            minimumSourceDistanceMeters * minimumSourceDistanceMeters,
            out float fitQuality,
            out float rmsResidualCps);

        float boundaryMargin = initialCoarseSpacing * boundaryMarginInCoarseCells;
        bool isNearSearchBoundary = geometry.isPlanar
            ? IsNearSearchBoundary(
                ToPlaneCoordinates(geometry, bestPosition),
                planarVolumeMin,
                planarVolumeMax,
                boundaryMargin)
            : IsNearSearchBoundary(
                bestPosition,
                volumeMin,
                volumeMax,
                boundaryMargin);

        if (rejectSearchBoundarySolutions && isNearSearchBoundary)
        {
            HasEstimate = false;
            SetSourceVisualVisible(false);
            State = EstimatorState.OutOfSearchBounds;
            StateMessage =
                "best source is on the search boundary; expand/configure room bounds";
            FinishSearchCycle();
            yield break;
        }

        float meanExcessCps = CalculateMeanExcessCps(samples);
        float relativeRms = rmsResidualCps / Mathf.Max(1f, meanExcessCps);
        bool planarResidualAccepted = geometry.isPlanar &&
                                      relativeRms <= maximumPlanarRelativeRms;

        if (fitQuality < minimumFitQuality && !planarResidualAccepted)
        {
            HasEstimate = false;
            SetSourceVisualVisible(false);
            State = EstimatorState.PoorFit;
            StateMessage = geometry.isPlanar
                ? $"planar source fit is too weak (fit {fitQuality:F2}, relative RMS {relativeRms:F2})"
                : $"single-source fit is too weak ({fitQuality:F2} < {minimumFitQuality:F2})";
            FinishSearchCycle();
            yield break;
        }

        Vector3 worldPosition = coordinateFrame != null
            ? coordinateFrame.TransformPoint(bestPosition)
            : bestPosition;

        LatestEstimate = new SourceEstimate(
            worldPosition,
            bestPosition,
            (float)bestActivity,
            fitQuality,
            rmsResidualCps,
            samples.Count,
            spacing);
        HasEstimate = true;
        State = EstimatorState.Ready;
        StateMessage = geometry.isPlanar
            ? $"source projection ({bestPosition.x:F2}, {bestPosition.y:F2}, {bestPosition.z:F2}) m, " +
              $"fit {fitQuality:F2}, RMS {rmsResidualCps:F1} CPS"
            : $"source ({bestPosition.x:F2}, {bestPosition.y:F2}, {bestPosition.z:F2}) m, " +
              $"fit {fitQuality:F2}, RMS {rmsResidualCps:F1} CPS";

        EnsureSourceVisual();
        SetSourceVisualVisible(showEstimatedSource);
        EstimateUpdated?.Invoke(LatestEstimate);

        if (verboseLogging)
        {
            Debug.Log(
                $"[RadiationSourceEstimator] {StateMessage}, " +
                $"relative activity {(float)bestActivity:F2}, detectors {samples.Count}.");
        }

        FinishSearchCycle();
    }

    private void FinishSearchCycle()
    {
        estimationCoroutine = null;
        if (estimateQueued)
        {
            estimateQueued = false;
            hasObservedSnapshot = false;
            nextSnapshotTime = 0f;
        }
    }

    private static bool IsNearSearchBoundary(
        Vector3 point,
        Vector3 minimum,
        Vector3 maximum,
        float margin)
    {
        margin = Mathf.Max(0.0001f, margin);
        return point.x <= minimum.x + margin || point.x >= maximum.x - margin ||
               point.y <= minimum.y + margin || point.y >= maximum.y - margin ||
               point.z <= minimum.z + margin || point.z >= maximum.z - margin;
    }

    private static bool IsNearSearchBoundary(
        Vector2 point,
        Vector2 minimum,
        Vector2 maximum,
        float margin)
    {
        margin = Mathf.Max(0.0001f, margin);
        return point.x <= minimum.x + margin || point.x >= maximum.x - margin ||
               point.y <= minimum.y + margin || point.y >= maximum.y - margin;
    }

    private void GetInitialPlanarSearchBounds(
        List<DetectorSample> samples,
        DetectorGeometry geometry,
        out Vector2 searchMin,
        out Vector2 searchMax)
    {
        if (useConfiguredSearchBounds)
        {
            Vector3 halfSize = configuredBoundsSize * 0.5f;
            searchMin = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            searchMax = new Vector2(float.NegativeInfinity, float.NegativeInfinity);

            for (int xSign = -1; xSign <= 1; xSign += 2)
            {
                for (int ySign = -1; ySign <= 1; ySign += 2)
                {
                    for (int zSign = -1; zSign <= 1; zSign += 2)
                    {
                        Vector3 corner = configuredBoundsCenter + Vector3.Scale(
                            halfSize,
                            new Vector3(xSign, ySign, zSign));
                        Vector2 planePoint = ToPlaneCoordinates(geometry, corner);
                        searchMin = Vector2.Min(searchMin, planePoint);
                        searchMax = Vector2.Max(searchMax, planePoint);
                    }
                }
            }
        }
        else
        {
            searchMin = ToPlaneCoordinates(geometry, samples[0].coordinatePosition);
            searchMax = searchMin;
            for (int i = 1; i < samples.Count; i++)
            {
                Vector2 planePoint = ToPlaneCoordinates(
                    geometry,
                    samples[i].coordinatePosition);
                searchMin = Vector2.Min(searchMin, planePoint);
                searchMax = Vector2.Max(searchMax, planePoint);
            }

            Vector2 padding = Vector2.one * searchPaddingMeters;
            searchMin -= padding;
            searchMax += padding;
        }

        EnsureMinimumAxisSize(
            ref searchMin.x,
            ref searchMax.x,
            coarseGridSpacingMeters * 2f);
        EnsureMinimumAxisSize(
            ref searchMin.y,
            ref searchMax.y,
            coarseGridSpacingMeters * 2f);
    }

    private void GetInitialSearchBounds(
        List<DetectorSample> samples,
        out Vector3 searchMin,
        out Vector3 searchMax)
    {
        if (useConfiguredSearchBounds)
        {
            Vector3 halfSize = configuredBoundsSize * 0.5f;
            searchMin = configuredBoundsCenter - halfSize;
            searchMax = configuredBoundsCenter + halfSize;
            return;
        }

        searchMin = samples[0].coordinatePosition;
        searchMax = searchMin;
        for (int i = 1; i < samples.Count; i++)
        {
            searchMin = Vector3.Min(searchMin, samples[i].coordinatePosition);
            searchMax = Vector3.Max(searchMax, samples[i].coordinatePosition);
        }

        Vector3 padding = Vector3.one * searchPaddingMeters;
        searchMin -= padding;
        searchMax += padding;

        // Avoid a zero-thickness search when detectors happen to be coplanar.
        EnsureMinimumAxisSize(ref searchMin.x, ref searchMax.x, coarseGridSpacingMeters * 2f);
        EnsureMinimumAxisSize(ref searchMin.y, ref searchMax.y, coarseGridSpacingMeters * 2f);
        EnsureMinimumAxisSize(ref searchMin.z, ref searchMax.z, coarseGridSpacingMeters * 2f);
    }

    private float LimitCoarseSpacing(Vector3 min, Vector3 max, float requestedSpacing)
    {
        double xCount = Math.Ceiling((max.x - min.x) / requestedSpacing) + 1d;
        double yCount = Math.Ceiling((max.y - min.y) / requestedSpacing) + 1d;
        double zCount = Math.Ceiling((max.z - min.z) / requestedSpacing) + 1d;
        double candidateCount = xCount * yCount * zCount;

        if (candidateCount <= maximumCoarseCandidates)
            return requestedSpacing;

        double scale = Math.Pow(candidateCount / maximumCoarseCandidates, 1d / 3d);
        return Mathf.Max(requestedSpacing, (float)(requestedSpacing * scale));
    }

    private float LimitPlanarCoarseSpacing(
        Vector2 min,
        Vector2 max,
        float requestedSpacing)
    {
        double xCount = Math.Ceiling((max.x - min.x) / requestedSpacing) + 1d;
        double yCount = Math.Ceiling((max.y - min.y) / requestedSpacing) + 1d;
        double candidateCount = xCount * yCount;

        if (candidateCount <= maximumCoarseCandidates)
            return requestedSpacing;

        double scale = Math.Sqrt(candidateCount / maximumCoarseCandidates);
        return Mathf.Max(requestedSpacing, (float)(requestedSpacing * scale));
    }

    private static Vector2 ToPlaneCoordinates(
        DetectorGeometry geometry,
        Vector3 position)
    {
        Vector3 relative = position - geometry.planeOrigin;
        return new Vector2(
            Vector3.Dot(relative, geometry.axisU),
            Vector3.Dot(relative, geometry.axisV));
    }

    private static Vector3 FromPlaneCoordinates(
        DetectorGeometry geometry,
        float u,
        float v)
    {
        return geometry.planeOrigin + geometry.axisU * u + geometry.axisV * v;
    }

    private static bool IsBetterCandidate(
        double candidateNll,
        Vector3 candidatePosition,
        double bestNll,
        Vector3 bestPosition,
        Vector3 detectorCentroid)
    {
        if (double.IsPositiveInfinity(bestNll))
            return true;

        double tolerance = Math.Max(1e-9d, Math.Abs(bestNll) * 1e-9d);
        if (candidateNll < bestNll - tolerance)
            return true;
        if (Math.Abs(candidateNll - bestNll) > tolerance)
            return false;

        float candidateDistanceSquared =
            (candidatePosition - detectorCentroid).sqrMagnitude;
        float bestDistanceSquared = (bestPosition - detectorCentroid).sqrMagnitude;
        return candidateDistanceSquared < bestDistanceSquared - 1e-8f;
    }

    private void EvaluateCandidate(
        List<DetectorSample> samples,
        Vector3 candidate,
        out double activity,
        out double nll)
    {
        double minimumDistanceSquared =
            minimumSourceDistanceMeters * minimumSourceDistanceMeters;
        double sumExcess = 0d;
        double sumResponse = 0d;
        bool hasBackground = false;

        for (int i = 0; i < samples.Count; i++)
        {
            DetectorSample sample = samples[i];
            double distanceSquared = Math.Max(
                (candidate - sample.coordinatePosition).sqrMagnitude,
                minimumDistanceSquared);
            double response = sample.responseFactor / distanceSquared;
            sumResponse += response;
            sumExcess += Math.Max(0d, sample.observedCps - sample.backgroundCps);
            hasBackground |= sample.backgroundCps > 1e-9f;
        }

        activity = Math.Max(0d, sumExcess / Math.Max(sumResponse, 1e-12d));
        nll = ComputeNegativeLogLikelihood(samples, candidate, activity, minimumDistanceSquared);

        // With zero background the Poisson optimum has the closed form
        // activity = sum(CPS) / sum(k/r^2), which is the value above.
        if (!hasBackground)
            return;

        // Activity is a convex one-dimensional subproblem at a fixed position.
        // Damped Newton iterations are much cheaper than adding a fourth grid axis.
        for (int iteration = 0; iteration < 8; iteration++)
        {
            ComputeActivityDerivatives(
                samples,
                candidate,
                activity,
                minimumDistanceSquared,
                out double gradient,
                out double hessian);

            if (hessian <= 1e-14d || Math.Abs(gradient) <= 1e-7d)
                break;

            double proposed = Math.Max(0d, activity - gradient / hessian);
            double proposedNll = ComputeNegativeLogLikelihood(
                samples,
                candidate,
                proposed,
                minimumDistanceSquared);

            int damping = 0;
            while (proposedNll > nll && damping < 8)
            {
                proposed = 0.5d * (activity + proposed);
                proposedNll = ComputeNegativeLogLikelihood(
                    samples,
                    candidate,
                    proposed,
                    minimumDistanceSquared);
                damping++;
            }

            if (Math.Abs(proposed - activity) <= Math.Max(1e-8d, activity * 1e-6d))
                break;

            activity = proposed;
            nll = proposedNll;
        }

        // The non-negative boundary can be optimal when all readings are at background.
        double zeroActivityNll = ComputeNegativeLogLikelihood(
            samples,
            candidate,
            0d,
            minimumDistanceSquared);
        if (zeroActivityNll < nll)
        {
            activity = 0d;
            nll = zeroActivityNll;
        }
    }

    private static double ComputeNegativeLogLikelihood(
        List<DetectorSample> samples,
        Vector3 candidate,
        double activity,
        double minimumDistanceSquared)
    {
        double nll = 0d;
        for (int i = 0; i < samples.Count; i++)
        {
            DetectorSample sample = samples[i];
            double distanceSquared = Math.Max(
                (candidate - sample.coordinatePosition).sqrMagnitude,
                minimumDistanceSquared);
            double lambda = sample.backgroundCps +
                            activity * sample.responseFactor / distanceSquared;
            lambda = Math.Max(lambda, 1e-9d);
            nll += lambda - sample.observedCps * Math.Log(lambda);
        }

        return nll;
    }

    private static void ComputeActivityDerivatives(
        List<DetectorSample> samples,
        Vector3 candidate,
        double activity,
        double minimumDistanceSquared,
        out double gradient,
        out double hessian)
    {
        gradient = 0d;
        hessian = 0d;

        for (int i = 0; i < samples.Count; i++)
        {
            DetectorSample sample = samples[i];
            double distanceSquared = Math.Max(
                (candidate - sample.coordinatePosition).sqrMagnitude,
                minimumDistanceSquared);
            double response = sample.responseFactor / distanceSquared;
            double lambda = Math.Max(
                sample.backgroundCps + activity * response,
                1e-9d);
            gradient += response * (1d - sample.observedCps / lambda);
            hessian += sample.observedCps * response * response / (lambda * lambda);
        }
    }

    private static void CalculateFitMetrics(
        List<DetectorSample> samples,
        Vector3 sourcePosition,
        double activity,
        double minimumDistanceSquared,
        out float fitQuality,
        out float rmsResidualCps)
    {
        double observedMean = 0d;
        for (int i = 0; i < samples.Count; i++)
            observedMean += samples[i].observedCps;
        observedMean /= samples.Count;

        double modelDeviance = 0d;
        double nullDeviance = 0d;
        double squaredResidualSum = 0d;

        for (int i = 0; i < samples.Count; i++)
        {
            DetectorSample sample = samples[i];
            double distanceSquared = Math.Max(
                (sourcePosition - sample.coordinatePosition).sqrMagnitude,
                minimumDistanceSquared);
            double predicted = Math.Max(
                sample.backgroundCps + activity * sample.responseFactor / distanceSquared,
                1e-9d);
            double observed = sample.observedCps;
            double residual = observed - predicted;
            squaredResidualSum += residual * residual;
            modelDeviance += PoissonDevianceTerm(observed, predicted);
            nullDeviance += PoissonDevianceTerm(observed, Math.Max(observedMean, 1e-9d));
        }

        double improvement = nullDeviance > 1e-9d
            ? 1d - modelDeviance / nullDeviance
            : 0d;
        fitQuality = Mathf.Clamp01((float)improvement);
        rmsResidualCps = (float)Math.Sqrt(squaredResidualSum / samples.Count);
    }

    private static float CalculateMeanExcessCps(List<DetectorSample> samples)
    {
        double sum = 0d;
        for (int i = 0; i < samples.Count; i++)
        {
            sum += Math.Max(
                0d,
                samples[i].observedCps - samples[i].backgroundCps);
        }

        return samples.Count > 0 ? (float)(sum / samples.Count) : 0f;
    }

    private static double PoissonDevianceTerm(double observed, double predicted)
    {
        if (observed <= 0d)
            return 2d * predicted;

        return 2d * (observed * Math.Log(observed / predicted) - (observed - predicted));
    }

    private void EnsureSourceVisual()
    {
        if (sourceVisual != null)
            return;

        sourceVisual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sourceVisual.name = "EstimatedRadiationSource";
        sourceVisual.transform.SetParent(null, true);

        Collider sourceCollider = sourceVisual.GetComponent<Collider>();
        if (sourceCollider != null)
            Destroy(sourceCollider);

        sourceRenderer = sourceVisual.GetComponent<Renderer>();
        if (sourceRenderer != null)
        {
            Shader shader = Shader.Find("RadVis/DetectorTransparent");
            if (shader == null)
                shader = Shader.Find("Standard");

            if (shader != null)
            {
                sourceMaterial = new Material(shader)
                {
                    name = "RadVis Estimated Source (Runtime)"
                };
                sourceRenderer.sharedMaterial = sourceMaterial;
                ApplySourceMaterialColor();
            }

            sourceRenderer.shadowCastingMode = ShadowCastingMode.Off;
            sourceRenderer.receiveShadows = false;
            sourceRenderer.lightProbeUsage = LightProbeUsage.Off;
            sourceRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        sourceVisual.transform.localScale = Vector3.one * sourceSphereDiameterMeters;
    }

    private void ApplySourceMaterialColor()
    {
        if (sourceMaterial == null)
            return;

        Color color = sourceSphereColor;
        color.a = sourceSphereAlpha;

        if (sourceMaterial.HasProperty("_Color"))
            sourceMaterial.SetColor("_Color", color);
        if (sourceMaterial.HasProperty("_BaseColor"))
            sourceMaterial.SetColor("_BaseColor", color);

        sourceMaterial.renderQueue = (int)RenderQueue.Transparent;
    }

    private void SetSourceVisualVisible(bool visible)
    {
        if (visible)
            EnsureSourceVisual();
        if (sourceVisual != null)
            sourceVisual.SetActive(visible);
    }

    private void CancelSearch()
    {
        estimationGeneration++;
        estimateQueued = false;
        if (estimationCoroutine != null)
        {
            StopCoroutine(estimationCoroutine);
            estimationCoroutine = null;
        }
    }

    private static int AxisPointCount(float min, float max, float spacing)
    {
        return Mathf.Max(2, Mathf.CeilToInt((max - min) / spacing) + 1);
    }

    private static float AxisCoordinate(
        float min,
        float max,
        float spacing,
        int index,
        int count)
    {
        return index == count - 1 ? max : Mathf.Min(max, min + index * spacing);
    }

    private static void EnsureMinimumAxisSize(ref float min, ref float max, float size)
    {
        if (max - min >= size)
            return;

        float center = 0.5f * (min + max);
        min = center - size * 0.5f;
        max = center + size * 0.5f;
    }

    private bool TryAnalyzeDetectorGeometry(
        List<DetectorSample> samples,
        out DetectorGeometry geometry)
    {
        geometry = default;

        int firstIndex = 0;
        int farthestIndex = 1;
        float farthestDistanceSquared = 0f;
        for (int i = 0; i < samples.Count - 1; i++)
        {
            for (int j = i + 1; j < samples.Count; j++)
            {
                float distanceSquared = (
                    samples[j].coordinatePosition -
                    samples[i].coordinatePosition).sqrMagnitude;
                if (distanceSquared > farthestDistanceSquared)
                {
                    farthestDistanceSquared = distanceSquared;
                    firstIndex = i;
                    farthestIndex = j;
                }
            }
        }

        Vector3 first = samples[firstIndex].coordinatePosition;
        Vector3 line = samples[farthestIndex].coordinatePosition - first;
        float lineLength = line.magnitude;
        if (lineLength < minimumDetectorSpanMeters)
            return false;

        Vector3 lineDirection = line / lineLength;
        int triangleIndex = -1;
        float greatestLineOffset = 0f;
        for (int i = 0; i < samples.Count; i++)
        {
            Vector3 relative = samples[i].coordinatePosition - first;
            float lineOffset = Vector3.Cross(lineDirection, relative).magnitude;
            if (lineOffset > greatestLineOffset)
            {
                greatestLineOffset = lineOffset;
                triangleIndex = i;
            }
        }

        if (triangleIndex < 0 || greatestLineOffset < minimumNonCollinearOffsetMeters)
            return false;

        Vector3 planeNormal = Vector3.Cross(
            lineDirection,
            samples[triangleIndex].coordinatePosition - first).normalized;
        Vector3 planeOrigin = Vector3.zero;
        for (int i = 0; i < samples.Count; i++)
            planeOrigin += samples[i].coordinatePosition;
        planeOrigin /= samples.Count;

        float minimumPlaneOffset = float.PositiveInfinity;
        float maximumPlaneOffset = float.NegativeInfinity;
        for (int i = 0; i < samples.Count; i++)
        {
            float planeOffset = Vector3.Dot(
                planeNormal,
                samples[i].coordinatePosition - planeOrigin);
            minimumPlaneOffset = Mathf.Min(minimumPlaneOffset, planeOffset);
            maximumPlaneOffset = Mathf.Max(maximumPlaneOffset, planeOffset);
        }

        float outOfPlaneSpan = maximumPlaneOffset - minimumPlaneOffset;
        Vector3 axisV = Vector3.Cross(planeNormal, lineDirection).normalized;
        geometry = new DetectorGeometry(
            outOfPlaneSpan <= maximumPlanarThicknessMeters,
            planeOrigin,
            lineDirection,
            axisV,
            planeNormal,
            outOfPlaneSpan);
        return true;
    }

    private static int ComputeSnapshotHash(List<DetectorSample> samples)
    {
        unchecked
        {
            int hash = 17;
            for (int i = 0; i < samples.Count; i++)
            {
                DetectorSample sample = samples[i];
                hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(sample.detectorId);
                hash = hash * 31 + Mathf.RoundToInt(sample.coordinatePosition.x * 1000f);
                hash = hash * 31 + Mathf.RoundToInt(sample.coordinatePosition.y * 1000f);
                hash = hash * 31 + Mathf.RoundToInt(sample.coordinatePosition.z * 1000f);
                hash = hash * 31 + Mathf.RoundToInt(sample.observedCps * 100f);
                hash = hash * 31 + Mathf.RoundToInt(sample.responseFactor * 10000f);
                hash = hash * 31 + Mathf.RoundToInt(sample.backgroundCps * 100f);
            }

            return hash;
        }
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private readonly struct DetectorGeometry
    {
        public readonly bool isPlanar;
        public readonly Vector3 planeOrigin;
        public readonly Vector3 axisU;
        public readonly Vector3 axisV;
        public readonly Vector3 planeNormal;
        public readonly float outOfPlaneSpan;

        public DetectorGeometry(
            bool isPlanar,
            Vector3 planeOrigin,
            Vector3 axisU,
            Vector3 axisV,
            Vector3 planeNormal,
            float outOfPlaneSpan)
        {
            this.isPlanar = isPlanar;
            this.planeOrigin = planeOrigin;
            this.axisU = axisU;
            this.axisV = axisV;
            this.planeNormal = planeNormal;
            this.outOfPlaneSpan = outOfPlaneSpan;
        }
    }

    private struct DetectorSample
    {
        public string detectorId;
        public Vector3 coordinatePosition;
        public float observedCps;
        public float responseFactor;
        public float backgroundCps;
    }
}
