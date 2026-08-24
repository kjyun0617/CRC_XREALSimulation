using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Optional spatial-anchor layer.
/// It saves detector IDs to persistent anchor GUIDs and loads those anchors again on app start.
/// JSON coordinates remain as placement metadata, but are not treated as a physical-space
/// fallback after restart because Unity world coordinates are session-relative.
/// </summary>
public class DetectorSpatialAnchorManager : MonoBehaviour
{
    private static readonly Vector3 LoadedAnchorWaitingPosition = new Vector3(9999f, 9999f, 9999f);

    public static DetectorSpatialAnchorManager Instance { get; private set; }

    [Header("References")]
    [Tooltip("Usually add AR Anchor Manager to XR Origin and drag it here.")]
    [SerializeField] private ARAnchorManager anchorManager;

    [SerializeField] private DetectorCoordinateDatabase coordinateDatabase;

    [Header("Saving")]
    [SerializeField] private float saveDelaySeconds = 0.5f;
    [SerializeField, Min(1)] private int saveAttemptCount = 3;
    [SerializeField, Min(0.1f)] private float saveRetryDelaySeconds = 2.0f;
    [SerializeField] private bool eraseOldAnchorOnRescan = true;

    [Header("Loading")]
    [SerializeField] private bool loadAnchorsOnStart = true;
    [SerializeField] private float loadDelaySeconds = 1.0f;
    [SerializeField] private float anchorSubsystemReadyTimeoutSeconds = 8.0f;
    [SerializeField] private float loadedAnchorTrackingTimeoutSeconds = 30.0f;

    public event Action<string, ARAnchor, string> AnchorSaved;
    public event Action<string, ARAnchor, string> AnchorLoaded;
    public event Action<string, string> AnchorSaveFailed;
    public event Action<string, string> AnchorLoadFailed;

    private readonly Dictionary<string, ARAnchor> anchorsByDetectorId =
        new Dictionary<string, ARAnchor>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ARAnchor> pendingAnchorsByDetectorId =
        new Dictionary<string, ARAnchor>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> anchorGuidByDetectorId =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> operationGenerationByDetectorId =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        EnsureReferences();
    }

    private IEnumerator Start()
    {
        EnsureReferences();

        if (loadAnchorsOnStart)
        {
            if (loadDelaySeconds > 0f)
                yield return new WaitForSeconds(loadDelaySeconds);

            float deadline = Time.realtimeSinceStartup + Mathf.Max(0f, anchorSubsystemReadyTimeoutSeconds);
            while (!IsReady() && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (IsReady())
            {
                LoadAnchorsFromDatabase();
            }
            else
            {
                Debug.LogWarning(
                    "[DetectorSpatialAnchorManager] Persistent anchors were not loaded because " +
                    "the AR anchor subsystem did not become ready.");
            }
        }
    }

    public bool IsReady()
    {
        EnsureReferences();
        return anchorManager != null &&
               anchorManager.isActiveAndEnabled &&
               anchorManager.subsystem != null;
    }

    public bool TryGetLoadedAnchor(string detectorId, out ARAnchor anchor)
    {
        detectorId = NormalizeId(detectorId);
        return anchorsByDetectorId.TryGetValue(detectorId, out anchor) && anchor != null;
    }

    public void CreateAndSaveAnchorForDetector(string detectorId, Vector3 position, Quaternion rotation)
    {
        detectorId = NormalizeId(detectorId);
        if (string.IsNullOrEmpty(detectorId))
            return;

        EnsureReferences();
        if (anchorManager == null)
        {
            string message = "ARAnchorManager is missing. Add AR Anchor Manager to XR Origin first.";
            Debug.LogWarning("[DetectorSpatialAnchorManager] " + message);
            AnchorSaveFailed?.Invoke(detectorId, message);
            return;
        }

        anchorsByDetectorId.TryGetValue(detectorId, out ARAnchor previousAnchor);
        int generation = AdvanceOperationGeneration(detectorId);
        CreateAndSaveAnchorCandidate(detectorId, position, rotation, generation, previousAnchor);
    }

    private void CreateAndSaveAnchorCandidate(
        string detectorId,
        Vector3 position,
        Quaternion rotation,
        int generation,
        ARAnchor previousAnchor)
    {
        GameObject anchorObject = null;
        ARAnchor anchor = null;

        try
        {
            if (!IsReady())
            {
                string message = "AR anchor subsystem is not ready.";
                AnchorSaveFailed?.Invoke(detectorId, message);
                RestorePreviousRuntimeAnchor(detectorId, previousAnchor, generation);
                return;
            }

            // XREAL's own Anchors sample uses the synchronous ARAnchor-component
            // path. Its Unity 6 TryAddAnchorAsync provider completes from Task.Run
            // without switching back to Unity's main thread, so creating the
            // trackable synchronously here avoids touching Unity objects off-thread.
            anchorObject = new GameObject($"SpatialAnchor_{detectorId}");
            anchorObject.transform.SetParent(transform, true);
            anchorObject.transform.SetPositionAndRotation(position, rotation);
            anchor = anchorObject.AddComponent<ARAnchor>();

            if (!IsCurrentOperation(detectorId, generation))
            {
                if (anchor != null)
                    Destroy(anchor.gameObject);
                else if (anchorObject != null)
                    Destroy(anchorObject);

                return;
            }

            if (anchor == null ||
                !anchor.enabled ||
                anchor.trackableId == TrackableId.invalidId)
            {
                string message = "XREAL failed to add the runtime AR anchor.";
                Debug.LogWarning($"[DetectorSpatialAnchorManager] Anchor creation failed for {detectorId}: {message}");
                AnchorSaveFailed?.Invoke(detectorId, message);

                if (anchor != null)
                    Destroy(anchor.gameObject);
                else if (anchorObject != null)
                    Destroy(anchorObject);

                RestorePreviousRuntimeAnchor(detectorId, previousAnchor, generation);
                return;
            }

            anchor.name = $"SpatialAnchor_{detectorId}";

            // Keep an unsaved candidate out of the committed-anchor lookup. This
            // prevents a late cancel/rescan from losing the previous saved anchor.
            pendingAnchorsByDetectorId[detectorId] = anchor;

            if (saveDelaySeconds > 0f)
                StartCoroutine(SaveAnchorAfterDelayRoutine(
                    detectorId, anchor, generation, previousAnchor));
            else
                SaveAnchorAsync(detectorId, anchor, generation, previousAnchor);
        }
        catch (Exception e)
        {
            if (anchor != null)
                CleanupPendingAnchor(detectorId, anchor);
            else if (anchorObject != null)
                Destroy(anchorObject);

            if (!IsCurrentOperation(detectorId, generation))
                return;

            Debug.LogError($"[DetectorSpatialAnchorManager] Anchor creation exception for {detectorId}: {e.Message}");
            AnchorSaveFailed?.Invoke(detectorId, e.Message);
            RestorePreviousRuntimeAnchor(detectorId, previousAnchor, generation);
        }
    }

    private IEnumerator SaveAnchorAfterDelayRoutine(
        string detectorId,
        ARAnchor anchor,
        int generation,
        ARAnchor previousAnchor)
    {
        yield return new WaitForSeconds(saveDelaySeconds);

        if (IsCurrentOperation(detectorId, generation))
            SaveAnchorAsync(detectorId, anchor, generation, previousAnchor);
        else
            CleanupPendingAnchor(detectorId, anchor);
    }

    private async void SaveAnchorAsync(
        string detectorId,
        ARAnchor anchor,
        int generation,
        ARAnchor previousAnchor,
        int attemptNumber = 1)
    {
        try
        {
            if (!IsCurrentOperation(detectorId, generation))
            {
                CleanupPendingAnchor(detectorId, anchor);
                return;
            }

            if (anchor == null || anchorManager == null)
            {
                AnchorSaveFailed?.Invoke(detectorId, "Anchor or ARAnchorManager is null.");
                CleanupPendingAnchor(detectorId, anchor);
                RestorePreviousRuntimeAnchor(detectorId, previousAnchor, generation);
                return;
            }

            string oldGuid = "";
            if (coordinateDatabase != null && coordinateDatabase.TryGetRecord(detectorId, out DetectorCoordinateRecord oldRecord))
                oldGuid = oldRecord.anchorPersistentGuid;

            var result = await anchorManager.TrySaveAnchorAsync(anchor);
            // XREAL 3.1.0 completes save from Task.Run. Explicitly marshal back
            // before reading Unity state, mutating dictionaries/DB, or publishing
            // events that update marker GameObjects.
            await Awaitable.MainThreadAsync();

            if (!IsCurrentOperation(detectorId, generation))
            {
                if (result.status.IsSuccess())
                    EraseAnchorByGuid(result.value.guid.ToString("D"));

                CleanupPendingAnchor(detectorId, anchor);
                return;
            }

            if (result.status.IsSuccess())
            {
                // SerializableGuid.ToString() is "16 hex-16 hex", not a standard
                // System.Guid string. Persist the canonical Guid so it round-trips
                // across application launches and keep legacy parsing below.
                string guid = result.value.guid.ToString("D");
                RemovePendingAnchorRegistration(detectorId, anchor);
                anchorsByDetectorId[detectorId] = anchor;
                anchorGuidByDetectorId[detectorId] = guid;

                if (coordinateDatabase != null)
                    coordinateDatabase.SaveOrUpdateAnchorGuid(detectorId, guid);

                Debug.Log($"[DetectorSpatialAnchorManager] Anchor saved: detector={detectorId}, guid={guid}");
                AnchorSaved?.Invoke(detectorId, anchor, guid);

                if (previousAnchor != null && previousAnchor != anchor && previousAnchor.transform.childCount == 0)
                    Destroy(previousAnchor.gameObject);

                if (eraseOldAnchorOnRescan && !string.IsNullOrWhiteSpace(oldGuid) && oldGuid != guid)
                    EraseAnchorByGuid(oldGuid);
            }
            else
            {
                string message = $"TrySaveAnchorAsync failed: {result.status}";

                if (ScheduleSaveRetryIfAvailable(
                    detectorId, anchor, generation, previousAnchor, attemptNumber, message))
                {
                    return;
                }

                Debug.LogWarning($"[DetectorSpatialAnchorManager] Anchor save failed for {detectorId}: {message}");
                AnchorSaveFailed?.Invoke(detectorId, message);
                CleanupPendingAnchor(detectorId, anchor);
                RestorePreviousRuntimeAnchor(detectorId, previousAnchor, generation);
            }
        }
        catch (Exception e)
        {
            await Awaitable.MainThreadAsync();

            if (!IsCurrentOperation(detectorId, generation))
            {
                CleanupPendingAnchor(detectorId, anchor);
                return;
            }

            if (ScheduleSaveRetryIfAvailable(
                detectorId, anchor, generation, previousAnchor, attemptNumber, e.Message))
            {
                return;
            }

            Debug.LogError($"[DetectorSpatialAnchorManager] Save exception for {detectorId}: {e.Message}");
            AnchorSaveFailed?.Invoke(detectorId, e.Message);
            CleanupPendingAnchor(detectorId, anchor);
            RestorePreviousRuntimeAnchor(detectorId, previousAnchor, generation);
        }
    }

    private bool ScheduleSaveRetryIfAvailable(
        string detectorId,
        ARAnchor anchor,
        int generation,
        ARAnchor previousAnchor,
        int attemptNumber,
        string reason)
    {
        int maxAttempts = Mathf.Max(1, saveAttemptCount);
        if (!IsCurrentOperation(detectorId, generation) ||
            anchor == null ||
            attemptNumber >= maxAttempts)
        {
            return false;
        }

        int nextAttempt = attemptNumber + 1;
        Debug.LogWarning(
            $"[DetectorSpatialAnchorManager] Anchor save attempt {attemptNumber}/{maxAttempts} " +
            $"failed for {detectorId}; retrying in {saveRetryDelaySeconds:F1}s. {reason}");

        StartCoroutine(RetrySaveAnchorRoutine(
            detectorId, anchor, generation, previousAnchor, nextAttempt));
        return true;
    }

    private IEnumerator RetrySaveAnchorRoutine(
        string detectorId,
        ARAnchor anchor,
        int generation,
        ARAnchor previousAnchor,
        int attemptNumber)
    {
        yield return new WaitForSeconds(Mathf.Max(0.1f, saveRetryDelaySeconds));

        if (IsCurrentOperation(detectorId, generation) && anchor != null)
            SaveAnchorAsync(detectorId, anchor, generation, previousAnchor, attemptNumber);
        else
            CleanupPendingAnchor(detectorId, anchor);
    }

    public void LoadAnchorsFromDatabase()
    {
        EnsureReferences();

        if (!IsReady())
        {
            Debug.LogWarning("[DetectorSpatialAnchorManager] Cannot load anchors. AR anchor subsystem is not ready.");
            return;
        }

        if (coordinateDatabase == null)
        {
            Debug.LogWarning("[DetectorSpatialAnchorManager] Cannot load anchors. DetectorCoordinateDatabase is missing.");
            return;
        }

        IReadOnlyList<DetectorCoordinateRecord> records = coordinateDatabase.GetAllRecords();
        for (int i = 0; i < records.Count; i++)
        {
            DetectorCoordinateRecord record = records[i];
            if (record != null && record.HasSavedAnchor())
                LoadAnchorForDetector(record.detectorId, record.anchorPersistentGuid);
        }
    }

    public async void LoadAnchorForDetector(string detectorId, string persistentGuid)
    {
        detectorId = NormalizeId(detectorId);
        persistentGuid = persistentGuid == null ? "" : persistentGuid.Trim();

        if (string.IsNullOrEmpty(detectorId) || string.IsNullOrEmpty(persistentGuid))
            return;

        EnsureReferences();
        if (!IsReady())
        {
            AnchorLoadFailed?.Invoke(detectorId, "AR anchor subsystem is not ready.");
            return;
        }

        if (!TryParseSerializableGuid(persistentGuid, out SerializableGuid serializableGuid))
        {
            string message = $"Invalid persistent anchor GUID: {persistentGuid}";
            Debug.LogWarning("[DetectorSpatialAnchorManager] " + message);
            AnchorLoadFailed?.Invoke(detectorId, message);
            return;
        }

        string canonicalGuid = serializableGuid.guid.ToString("D");
        anchorsByDetectorId.TryGetValue(detectorId, out ARAnchor previousAnchor);
        int generation = AdvanceOperationGeneration(detectorId);

        try
        {
            var result = await anchorManager.TryLoadAnchorAsync(serializableGuid);

            if (!IsCurrentOperation(detectorId, generation))
            {
                if (result.status.IsSuccess() && result.value != null)
                    Destroy(result.value.gameObject);

                return;
            }

            if (result.status.IsSuccess())
            {
                ARAnchor anchor = result.value;
                if (anchor != null)
                {
                    anchor.name = $"LoadedSpatialAnchor_{detectorId}";
                    pendingAnchorsByDetectorId[detectorId] = anchor;
                    StartCoroutine(WaitForLoadedAnchorTrackingRoutine(
                        detectorId,
                        anchor,
                        canonicalGuid,
                        persistentGuid,
                        generation,
                        previousAnchor));
                }
                else
                {
                    AnchorLoadFailed?.Invoke(detectorId, "Loaded ARAnchor is null.");
                }
            }
            else
            {
                string message = $"TryLoadAnchorAsync failed: {result.status}";
                Debug.LogWarning($"[DetectorSpatialAnchorManager] Anchor load failed for {detectorId}: {message}");
                AnchorLoadFailed?.Invoke(detectorId, message);
            }
        }
        catch (Exception e)
        {
            if (!IsCurrentOperation(detectorId, generation))
                return;

            Debug.LogError($"[DetectorSpatialAnchorManager] Load exception for {detectorId}: {e.Message}");
            AnchorLoadFailed?.Invoke(detectorId, e.Message);
        }
    }

    private IEnumerator WaitForLoadedAnchorTrackingRoutine(
        string detectorId,
        ARAnchor anchor,
        string canonicalGuid,
        string originalGuid,
        int generation,
        ARAnchor previousAnchor)
    {
        float deadline = Time.realtimeSinceStartup + Mathf.Max(0.1f, loadedAnchorTrackingTimeoutSeconds);

        // Follow the XREAL Anchors sample: a successful load returns an ARAnchor
        // before the mapping system has necessarily located it. Moving the hidden
        // anchor to a sentinel lets us distinguish the later provider pose update
        // from the provisional load result without ever creating a visible marker.
        if (anchor != null)
            anchor.transform.position = LoadedAnchorWaitingPosition;

        // XREAL returns the persisted anchor before its physical map has necessarily
        // relocalized. Publishing its provisional pose causes a visible origin/old-
        // coordinate flash, so wait for a real Tracking state first.
        while (IsCurrentOperation(detectorId, generation) &&
               anchor != null &&
               (anchor.trackingState != TrackingState.Tracking ||
                !HasLocatedLoadedAnchorPose(anchor)) &&
               Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        if (!IsCurrentOperation(detectorId, generation))
        {
            CleanupPendingAnchor(detectorId, anchor);
            yield break;
        }

        if (anchor == null ||
            anchor.trackingState != TrackingState.Tracking ||
            !HasLocatedLoadedAnchorPose(anchor))
        {
            string message =
                $"Loaded anchor did not reach Tracking within {loadedAnchorTrackingTimeoutSeconds:F1}s.";
            Debug.LogWarning($"[DetectorSpatialAnchorManager] {message} detector={detectorId}");
            AnchorLoadFailed?.Invoke(detectorId, message);
            CleanupPendingAnchor(detectorId, anchor);
            RestorePreviousRuntimeAnchor(detectorId, previousAnchor, generation);
            yield break;
        }

        RemovePendingAnchorRegistration(detectorId, anchor);
        anchorsByDetectorId[detectorId] = anchor;
        anchorGuidByDetectorId[detectorId] = canonicalGuid;

        // Upgrade GUIDs written by older builds that used
        // SerializableGuid.ToString().
        if (coordinateDatabase != null &&
            !string.Equals(originalGuid, canonicalGuid, StringComparison.OrdinalIgnoreCase))
        {
            coordinateDatabase.SaveOrUpdateAnchorGuid(detectorId, canonicalGuid);
        }

        Debug.Log($"[DetectorSpatialAnchorManager] Anchor located: detector={detectorId}, guid={canonicalGuid}");
        AnchorLoaded?.Invoke(detectorId, anchor, canonicalGuid);

        if (previousAnchor != null && previousAnchor != anchor && previousAnchor.transform.childCount == 0)
            Destroy(previousAnchor.gameObject);
    }

    private bool HasLocatedLoadedAnchorPose(ARAnchor anchor)
    {
        return anchor != null &&
               (anchor.transform.position - LoadedAnchorWaitingPosition).sqrMagnitude > 1f;
    }

    public void EraseAnchorForDetector(string detectorId)
    {
        detectorId = NormalizeId(detectorId);
        if (string.IsNullOrEmpty(detectorId))
            return;

        // Invalidate an in-flight save/load before clearing runtime and persisted
        // state. A late completion is then cleaned up instead of recreating the ID.
        AdvanceOperationGeneration(detectorId);

        if (coordinateDatabase != null && coordinateDatabase.TryGetRecord(detectorId, out DetectorCoordinateRecord record))
        {
            if (!string.IsNullOrWhiteSpace(record.anchorPersistentGuid))
                EraseAnchorByGuid(record.anchorPersistentGuid);

            coordinateDatabase.ClearAnchorGuid(detectorId);
        }

        if (anchorsByDetectorId.TryGetValue(detectorId, out ARAnchor anchor) && anchor != null)
            Destroy(anchor.gameObject);

        anchorsByDetectorId.Remove(detectorId);
        // An in-flight save cleans its own native result when the invalidated await
        // completes. Do not destroy that candidate early while ARFoundation uses it.
        anchorGuidByDetectorId.Remove(detectorId);
    }

    public void InvalidatePendingOperationForDetector(string detectorId)
    {
        detectorId = NormalizeId(detectorId);
        if (!string.IsNullOrEmpty(detectorId))
            AdvanceOperationGeneration(detectorId);
    }

    private async void EraseAnchorByGuid(string persistentGuid)
    {
        EnsureReferences();
        if (anchorManager == null)
            return;

        if (!TryParseSerializableGuid(persistentGuid, out SerializableGuid serializableGuid))
            return;

        try
        {
            var result = await anchorManager.TryEraseAnchorAsync(serializableGuid);
            // XREAL 3.1.0 also completes erase from Task.Run.
            await Awaitable.MainThreadAsync();
            Debug.Log($"[DetectorSpatialAnchorManager] Erase anchor {persistentGuid}: {result}");
        }
        catch (Exception e)
        {
            await Awaitable.MainThreadAsync();
            Debug.LogWarning($"[DetectorSpatialAnchorManager] Erase failed: {e.Message}");
        }
    }

    private int AdvanceOperationGeneration(string detectorId)
    {
        operationGenerationByDetectorId.TryGetValue(detectorId, out int currentGeneration);
        int nextGeneration = currentGeneration == int.MaxValue ? 1 : currentGeneration + 1;
        operationGenerationByDetectorId[detectorId] = nextGeneration;
        return nextGeneration;
    }

    private bool IsCurrentOperation(string detectorId, int generation)
    {
        return operationGenerationByDetectorId.TryGetValue(detectorId, out int currentGeneration) &&
               currentGeneration == generation;
    }

    private void CleanupPendingAnchor(string detectorId, ARAnchor anchor)
    {
        RemovePendingAnchorRegistration(detectorId, anchor);

        if (anchor != null)
            Destroy(anchor.gameObject);
    }

    private void RemovePendingAnchorRegistration(string detectorId, ARAnchor anchor)
    {
        if (pendingAnchorsByDetectorId.TryGetValue(detectorId, out ARAnchor pendingAnchor) &&
            pendingAnchor == anchor)
        {
            pendingAnchorsByDetectorId.Remove(detectorId);
        }
    }

    private void RestorePreviousRuntimeAnchor(
        string detectorId,
        ARAnchor previousAnchor,
        int generation)
    {
        if (!IsCurrentOperation(detectorId, generation))
            return;

        if (previousAnchor != null)
            anchorsByDetectorId[detectorId] = previousAnchor;
        else
            anchorsByDetectorId.Remove(detectorId);
    }

    private bool TryParseSerializableGuid(string guidText, out SerializableGuid serializableGuid)
    {
        serializableGuid = default;

        if (Guid.TryParse(guidText, out Guid systemGuid))
        {
            serializableGuid = new SerializableGuid(systemGuid);
            return true;
        }

        // AR Foundation 6 SerializableGuid.ToString() uses two 16-digit hex
        // numbers separated by a dash. Previous app builds stored that value.
        string[] legacyParts = guidText.Split('-');
        if (legacyParts.Length == 2 &&
            ulong.TryParse(legacyParts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong low) &&
            ulong.TryParse(legacyParts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong high))
        {
            serializableGuid = new SerializableGuid(low, high);
            return true;
        }

        return false;
    }

    private void EnsureReferences()
    {
        if (anchorManager == null)
            anchorManager = FindObjectOfType<ARAnchorManager>();

        if (coordinateDatabase == null)
            coordinateDatabase = FindObjectOfType<DetectorCoordinateDatabase>();
    }

    private string NormalizeId(string raw)
    {
        return string.IsNullOrWhiteSpace(raw) ? "" : raw.Trim();
    }
}
