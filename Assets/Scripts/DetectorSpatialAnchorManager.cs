using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Optional spatial-anchor layer.
/// It saves detector IDs to persistent anchor GUIDs and loads those anchors again on app start.
/// The detector still has JSON fallback coordinates in DetectorCoordinateDatabase.
/// </summary>
public class DetectorSpatialAnchorManager : MonoBehaviour
{
    public static DetectorSpatialAnchorManager Instance { get; private set; }

    [Header("References")]
    [Tooltip("Usually add AR Anchor Manager to XR Origin and drag it here.")]
    [SerializeField] private ARAnchorManager anchorManager;

    [SerializeField] private DetectorCoordinateDatabase coordinateDatabase;

    [Header("Saving")]
    [SerializeField] private float saveDelaySeconds = 0.5f;
    [SerializeField] private bool eraseOldAnchorOnRescan = true;

    [Header("Loading")]
    [SerializeField] private bool loadAnchorsOnStart = true;
    [SerializeField] private float loadDelaySeconds = 1.0f;

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

            LoadAnchorsFromDatabase();
        }
    }

    public bool IsReady()
    {
        EnsureReferences();
        return anchorManager != null;
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
        StartCoroutine(CreateAndSaveRoutine(detectorId, position, rotation, generation, previousAnchor));
    }

    private IEnumerator CreateAndSaveRoutine(
        string detectorId,
        Vector3 position,
        Quaternion rotation,
        int generation,
        ARAnchor previousAnchor)
    {
        GameObject anchorObject = new GameObject($"SpatialAnchor_{detectorId}");
        anchorObject.transform.SetParent(transform, true);
        anchorObject.transform.SetPositionAndRotation(position, rotation);

        ARAnchor anchor = anchorObject.AddComponent<ARAnchor>();
        // Keep an unsaved candidate out of the committed-anchor lookup. This also
        // prevents a later cancel/rescan from losing the previous persisted anchor.
        pendingAnchorsByDetectorId[detectorId] = anchor;

        if (saveDelaySeconds > 0f)
            yield return new WaitForSeconds(saveDelaySeconds);
        else
            yield return null;

        if (!IsCurrentOperation(detectorId, generation))
        {
            CleanupPendingAnchor(detectorId, anchor);
            yield break;
        }

        SaveAnchorAsync(detectorId, anchor, generation, previousAnchor);
    }

    private async void SaveAnchorAsync(
        string detectorId,
        ARAnchor anchor,
        int generation,
        ARAnchor previousAnchor)
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

            if (!IsCurrentOperation(detectorId, generation))
            {
                if (result.status.IsSuccess())
                    EraseAnchorByGuid(result.value.ToString());

                CleanupPendingAnchor(detectorId, anchor);
                return;
            }

            if (result.status.IsSuccess())
            {
                string guid = result.value.ToString();
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
                Debug.LogWarning($"[DetectorSpatialAnchorManager] Anchor save failed for {detectorId}: {message}");
                AnchorSaveFailed?.Invoke(detectorId, message);
                CleanupPendingAnchor(detectorId, anchor);
                RestorePreviousRuntimeAnchor(detectorId, previousAnchor, generation);
            }
        }
        catch (Exception e)
        {
            if (!IsCurrentOperation(detectorId, generation))
            {
                CleanupPendingAnchor(detectorId, anchor);
                return;
            }

            Debug.LogError($"[DetectorSpatialAnchorManager] Save exception for {detectorId}: {e.Message}");
            AnchorSaveFailed?.Invoke(detectorId, e.Message);
            CleanupPendingAnchor(detectorId, anchor);
            RestorePreviousRuntimeAnchor(detectorId, previousAnchor, generation);
        }
    }

    public void LoadAnchorsFromDatabase()
    {
        EnsureReferences();

        if (anchorManager == null)
        {
            Debug.LogWarning("[DetectorSpatialAnchorManager] Cannot load anchors. ARAnchorManager is missing.");
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
        if (anchorManager == null)
        {
            AnchorLoadFailed?.Invoke(detectorId, "ARAnchorManager is missing.");
            return;
        }

        if (!TryParseSerializableGuid(persistentGuid, out SerializableGuid serializableGuid))
        {
            string message = $"Invalid persistent anchor GUID: {persistentGuid}";
            Debug.LogWarning("[DetectorSpatialAnchorManager] " + message);
            AnchorLoadFailed?.Invoke(detectorId, message);
            return;
        }

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
                    anchorsByDetectorId[detectorId] = anchor;
                    anchorGuidByDetectorId[detectorId] = persistentGuid;
                    Debug.Log($"[DetectorSpatialAnchorManager] Anchor loaded: detector={detectorId}, guid={persistentGuid}");
                    AnchorLoaded?.Invoke(detectorId, anchor, persistentGuid);

                    if (previousAnchor != null && previousAnchor != anchor && previousAnchor.transform.childCount == 0)
                        Destroy(previousAnchor.gameObject);
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
            Debug.Log($"[DetectorSpatialAnchorManager] Erase anchor {persistentGuid}: {result}");
        }
        catch (Exception e)
        {
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

        if (!Guid.TryParse(guidText, out Guid systemGuid))
            return false;

        byte[] bytes = systemGuid.ToByteArray();
        ulong low = BitConverter.ToUInt64(bytes, 0);
        ulong high = BitConverter.ToUInt64(bytes, 8);
        serializableGuid = new SerializableGuid(low, high);
        return true;
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
