using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Stores detector ID, fallback Unity-world pose, stable room-relative pose,
/// QR scan metadata, detector calibration, last radiation value, and an optional
/// persistent spatial-anchor GUID in a JSON file.
/// </summary>
public class DetectorCoordinateDatabase : MonoBehaviour
{
    public static DetectorCoordinateDatabase Instance { get; private set; }

    [Header("Save File")]
    [SerializeField] private string saveFileName = "detector_coordinates.json";
    [SerializeField] private bool savePrettyJson = true;
    [SerializeField] private bool logSavePathOnStart = true;

    [Header("Auto Save")]
    [SerializeField] private bool autoSaveAfterEachChange = true;

    [Tooltip("Live CPS snapshots are batched and persisted no more often than this interval. Coordinate/anchor changes still save immediately.")]
    [SerializeField, Min(0.1f)] private float radiationSaveMinimumIntervalSeconds = 1f;

    [Tooltip("Wait before retrying a failed JSON write so a storage error cannot stall every frame.")]
    [SerializeField, Min(0.5f)] private float saveFailureRetryDelaySeconds = 2f;

    private DetectorCoordinateSaveRoot saveRoot = new DetectorCoordinateSaveRoot();
    private readonly Dictionary<string, DetectorCoordinateRecord> recordsById =
        new Dictionary<string, DetectorCoordinateRecord>(StringComparer.OrdinalIgnoreCase);
    private bool radiationSavePending;
    private float nextRadiationSaveTime;
    private bool lastSaveAttemptFailed;
    private float nextSaveRetryTime;

    public string SavePath => Path.Combine(Application.persistentDataPath, saveFileName);

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        LoadFromDisk();

        if (logSavePathOnStart)
            Debug.Log($"[DetectorCoordinateDatabase] JSON path: {SavePath}");
    }

    private void Update()
    {
        if (radiationSavePending &&
            autoSaveAfterEachChange &&
            Time.unscaledTime >= nextRadiationSaveTime)
        {
            SaveToDisk();
        }
    }

    public IReadOnlyList<DetectorCoordinateRecord> GetAllRecords()
    {
        return saveRoot.records;
    }

    public bool TryGetRecord(string detectorId, out DetectorCoordinateRecord record)
    {
        detectorId = NormalizeId(detectorId);
        return recordsById.TryGetValue(detectorId, out record);
    }

    public DetectorCoordinateRecord GetOrCreateRecord(string detectorId)
    {
        detectorId = NormalizeId(detectorId);
        if (string.IsNullOrEmpty(detectorId))
            return null;

        if (recordsById.TryGetValue(detectorId, out DetectorCoordinateRecord existing))
            return existing;

        DetectorCoordinateRecord record = new DetectorCoordinateRecord
        {
            detectorId = detectorId,
            createdUtc = DateTime.UtcNow.ToString("o")
        };

        saveRoot.records.Add(record);
        recordsById.Add(detectorId, record);
        return record;
    }

    public void SaveOrUpdateCoordinate(
        string detectorId,
        Vector3 worldPosition,
        Quaternion worldRotation,
        float estimatedDistanceMeters,
        float qrPixelSize,
        Vector2 qrImageCenter,
        int qrImageWidth,
        int qrImageHeight,
        string placementMethod = "PreviewCenterProjection")
    {
        DetectorCoordinateRecord record = GetOrCreateRecord(detectorId);
        if (record == null)
            return;

        record.updatedUtc = DateTime.UtcNow.ToString("o");

        record.positionX = worldPosition.x;
        record.positionY = worldPosition.y;
        record.positionZ = worldPosition.z;

        record.rotationX = worldRotation.x;
        record.rotationY = worldRotation.y;
        record.rotationZ = worldRotation.z;
        record.rotationW = worldRotation.w;

        record.estimatedDistanceMeters = estimatedDistanceMeters;
        record.qrPixelSize = qrPixelSize;
        record.qrImageCenterX = qrImageCenter.x;
        record.qrImageCenterY = qrImageCenter.y;
        record.qrImageWidth = qrImageWidth;
        record.qrImageHeight = qrImageHeight;
        record.placementMethod = placementMethod;

        if (autoSaveAfterEachChange)
            SaveToDisk();
    }

    public void SaveOrUpdateAnchorGuid(string detectorId, string persistentAnchorGuid)
    {
        DetectorCoordinateRecord record = GetOrCreateRecord(detectorId);
        if (record == null)
            return;

        record.anchorPersistentGuid = persistentAnchorGuid ?? "";
        record.anchorSaved = !string.IsNullOrWhiteSpace(record.anchorPersistentGuid);
        record.anchorSavedUtc = DateTime.UtcNow.ToString("o");
        record.updatedUtc = DateTime.UtcNow.ToString("o");

        if (autoSaveAfterEachChange)
            SaveToDisk();
    }

    /// <summary>
    /// Saves a detector pose in the stable coordinate frame established by a
    /// ROOM_ORIGIN QR. This is intentionally separate from the Unity-world pose:
    /// Unity's session origin is not guaranteed to survive an app restart.
    /// </summary>
    public void SaveOrUpdateRoomCoordinate(
        string detectorId,
        string roomId,
        Vector3 roomPosition,
        Quaternion roomRotation,
        float calibrationFactor = 1f)
    {
        DetectorCoordinateRecord record = GetOrCreateRecord(detectorId);
        roomId = NormalizeId(roomId);

        if (record == null || string.IsNullOrEmpty(roomId))
            return;

        record.roomId = roomId;
        record.hasRoomPose = true;

        record.roomPositionX = roomPosition.x;
        record.roomPositionY = roomPosition.y;
        record.roomPositionZ = roomPosition.z;

        Quaternion normalizedRotation = NormalizeQuaternion(roomRotation);
        record.roomRotationX = normalizedRotation.x;
        record.roomRotationY = normalizedRotation.y;
        record.roomRotationZ = normalizedRotation.z;
        record.roomRotationW = normalizedRotation.w;

        record.calibrationFactor = SanitizeCalibrationFactor(calibrationFactor);
        record.roomPoseUpdatedUtc = DateTime.UtcNow.ToString("o");
        record.updatedUtc = record.roomPoseUpdatedUtc;

        if (autoSaveAfterEachChange)
            SaveToDisk();
    }

    public bool TryGetRoomCoordinate(
        string detectorId,
        string roomId,
        out Vector3 roomPosition,
        out Quaternion roomRotation,
        out float calibrationFactor)
    {
        roomPosition = Vector3.zero;
        roomRotation = Quaternion.identity;
        calibrationFactor = 1f;

        detectorId = NormalizeId(detectorId);
        roomId = NormalizeId(roomId);

        if (!recordsById.TryGetValue(detectorId, out DetectorCoordinateRecord record) ||
            record == null ||
            !record.HasRoomPose(roomId))
        {
            return false;
        }

        roomPosition = record.GetRoomPosition();
        roomRotation = record.GetRoomRotation();
        calibrationFactor = SanitizeCalibrationFactor(record.calibrationFactor);
        return true;
    }

    public void UpdateCalibrationFactor(string detectorId, float calibrationFactor)
    {
        detectorId = NormalizeId(detectorId);
        if (!recordsById.TryGetValue(detectorId, out DetectorCoordinateRecord record) ||
            record == null)
        {
            return;
        }

        record.calibrationFactor = SanitizeCalibrationFactor(calibrationFactor);
        record.updatedUtc = DateTime.UtcNow.ToString("o");

        if (autoSaveAfterEachChange)
            SaveToDisk();
    }

    public void ClearAnchorGuid(string detectorId)
    {
        detectorId = NormalizeId(detectorId);
        if (!recordsById.TryGetValue(detectorId, out DetectorCoordinateRecord record))
            return;

        record.anchorPersistentGuid = "";
        record.anchorSaved = false;
        record.updatedUtc = DateTime.UtcNow.ToString("o");

        if (autoSaveAfterEachChange)
            SaveToDisk();
    }

    public void UpdateRadiationValue(string detectorId, float radiationValue)
    {
        detectorId = NormalizeId(detectorId);
        if (string.IsNullOrEmpty(detectorId) ||
            radiationValue < 0f ||
            float.IsNaN(radiationValue) ||
            float.IsInfinity(radiationValue))
            return;

        if (!recordsById.TryGetValue(detectorId, out DetectorCoordinateRecord record))
            return;

        if (Mathf.Approximately(record.lastRadiationValue, radiationValue))
            return;

        record.lastRadiationValue = radiationValue;
        record.lastRadiationUpdatedUtc = DateTime.UtcNow.ToString("o");

        ScheduleRadiationSave();
    }

    /// <summary>
    /// Applies one complete server snapshot and writes the JSON file at most once.
    /// This avoids one full-file write per detector on every WebSocket update.
    /// </summary>
    public void UpdateRadiationValues(IReadOnlyDictionary<string, float> radiationValues)
    {
        if (radiationValues == null || radiationValues.Count == 0)
            return;

        string updatedUtc = DateTime.UtcNow.ToString("o");
        bool changed = false;

        foreach (var pair in radiationValues)
        {
            string detectorId = NormalizeId(pair.Key);
            if (string.IsNullOrEmpty(detectorId) ||
                pair.Value < 0f ||
                float.IsNaN(pair.Value) ||
                float.IsInfinity(pair.Value) ||
                !recordsById.TryGetValue(detectorId, out DetectorCoordinateRecord record) ||
                record == null)
            {
                continue;
            }

            if (Mathf.Approximately(record.lastRadiationValue, pair.Value))
                continue;

            record.lastRadiationValue = pair.Value;
            record.lastRadiationUpdatedUtc = updatedUtc;
            changed = true;
        }

        if (changed)
            ScheduleRadiationSave();
    }

    public void MarkDetectorPlaced(string detectorId)
    {
        DetectorCoordinateRecord record = GetOrCreateRecord(detectorId);
        if (record == null)
            return;

        long nextSequence = Math.Max(1L, saveRoot.nextPlacementSequence);
        record.lastPlacedSequence = nextSequence;
        record.lastPlacedUtc = DateTime.UtcNow.ToString("o");
        record.updatedUtc = record.lastPlacedUtc;
        saveRoot.nextPlacementSequence = nextSequence + 1L;

        if (autoSaveAfterEachChange)
            SaveToDisk();
    }

    public bool RemoveCoordinate(string detectorId)
    {
        detectorId = NormalizeId(detectorId);
        if (string.IsNullOrEmpty(detectorId))
            return false;

        bool removed = recordsById.Remove(detectorId);

        for (int i = saveRoot.records.Count - 1; i >= 0; i--)
        {
            DetectorCoordinateRecord record = saveRoot.records[i];
            if (record != null &&
                string.Equals(NormalizeId(record.detectorId), detectorId, StringComparison.OrdinalIgnoreCase))
            {
                saveRoot.records.RemoveAt(i);
                removed = true;
            }
        }

        if (removed && autoSaveAfterEachChange)
            SaveToDisk();

        return removed;
    }

    public void SaveToDisk()
    {
        SaveToDisk(false);
    }

    private void SaveToDisk(bool ignoreRetryBackoff)
    {
        if (!ignoreRetryBackoff &&
            lastSaveAttemptFailed &&
            Time.unscaledTime < nextSaveRetryTime)
        {
            radiationSavePending = true;
            nextRadiationSaveTime = nextSaveRetryTime;
            return;
        }

        try
        {
            string directory = Path.GetDirectoryName(SavePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string json = JsonUtility.ToJson(saveRoot, savePrettyJson);
            string temporaryPath = SavePath + ".tmp";
            string backupPath = SavePath + ".bak";

            // The fully-written temp file remains recoverable if the process dies
            // while the canonical path is being replaced.
            File.WriteAllText(temporaryPath, json);

            if (File.Exists(SavePath))
            {
                try
                {
                    File.Replace(temporaryPath, SavePath, backupPath, true);
                }
                catch (PlatformNotSupportedException)
                {
                    ReplaceSaveFileWithPortableFallback(
                        temporaryPath,
                        backupPath);
                }
                catch (IOException)
                {
                    ReplaceSaveFileWithPortableFallback(
                        temporaryPath,
                        backupPath);
                }
            }
            else
            {
                File.Move(temporaryPath, SavePath);
            }

            radiationSavePending = false;
            lastSaveAttemptFailed = false;
            Debug.Log($"[DetectorCoordinateDatabase] Saved: {SavePath}");
        }
        catch (Exception e)
        {
            lastSaveAttemptFailed = true;
            radiationSavePending = true;
            nextSaveRetryTime =
                Time.unscaledTime + Mathf.Max(0.5f, saveFailureRetryDelaySeconds);
            nextRadiationSaveTime = nextSaveRetryTime;
            Debug.LogError($"[DetectorCoordinateDatabase] Failed to save: {e.Message}");
        }
    }

    public void LoadFromDisk()
    {
        recordsById.Clear();
        saveRoot = new DetectorCoordinateSaveRoot();

        try
        {
            bool hasSaveCandidate = File.Exists(SavePath) ||
                                    File.Exists(SavePath + ".tmp") ||
                                    File.Exists(SavePath + ".bak");
            if (!hasSaveCandidate)
            {
                Debug.Log($"[DetectorCoordinateDatabase] No coordinate file yet: {SavePath}");
                return;
            }

            if (!TryFindLatestReadableSave(
                    out string loadPath,
                    out DetectorCoordinateSaveRoot loaded))
            {
                throw new InvalidDataException(
                    "Coordinate JSON and its recovery files are invalid.");
            }

            bool recoveredSave = !string.Equals(
                loadPath,
                SavePath,
                StringComparison.OrdinalIgnoreCase);
            saveRoot = loaded;
            bool sanitizedRecords = false;
            long largestPlacementSequence = 0L;

            for (int i = saveRoot.records.Count - 1; i >= 0; i--)
            {
                DetectorCoordinateRecord record = saveRoot.records[i];
                if (record == null)
                {
                    saveRoot.records.RemoveAt(i);
                    sanitizedRecords = true;
                    continue;
                }

                string normalizedDetectorId = NormalizeId(record.detectorId);
                if (!string.Equals(record.detectorId, normalizedDetectorId, StringComparison.Ordinal))
                    sanitizedRecords = true;

                record.detectorId = normalizedDetectorId;

                if (record.calibrationFactor <= 0f ||
                    float.IsNaN(record.calibrationFactor) ||
                    float.IsInfinity(record.calibrationFactor))
                {
                    record.calibrationFactor = 1f;
                    sanitizedRecords = true;
                }

                if (record.hasRoomPose && string.IsNullOrWhiteSpace(record.roomId))
                {
                    record.hasRoomPose = false;
                    sanitizedRecords = true;
                }

                if (string.IsNullOrEmpty(record.detectorId))
                {
                    saveRoot.records.RemoveAt(i);
                    sanitizedRecords = true;
                    continue;
                }

                if (!recordsById.ContainsKey(record.detectorId))
                    recordsById.Add(record.detectorId, record);
                else
                {
                    saveRoot.records.RemoveAt(i);
                    sanitizedRecords = true;
                }


                if (record.lastPlacedSequence > largestPlacementSequence)
                    largestPlacementSequence = record.lastPlacedSequence;
            }

            if (saveRoot.schemaVersion < 2)
            {
                saveRoot.schemaVersion = 2;
                sanitizedRecords = true;
            }

            long requiredNextSequence = Math.Max(1L, largestPlacementSequence + 1L);
            if (saveRoot.nextPlacementSequence < requiredNextSequence)
            {
                saveRoot.nextPlacementSequence = requiredNextSequence;
                sanitizedRecords = true;
            }

            if ((sanitizedRecords || recoveredSave) && autoSaveAfterEachChange)
                SaveToDisk();

            if (recoveredSave)
            {
                Debug.LogWarning(
                    $"[DetectorCoordinateDatabase] Recovered coordinates from: {loadPath}");
            }

            Debug.Log($"[DetectorCoordinateDatabase] Loaded records: {recordsById.Count}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[DetectorCoordinateDatabase] Failed to load: {e.Message}");
            saveRoot = new DetectorCoordinateSaveRoot();
            recordsById.Clear();
        }
    }

    public void ClearAllCoordinates()
    {
        saveRoot.records.Clear();
        recordsById.Clear();
        radiationSavePending = false;
        lastSaveAttemptFailed = false;

        try
        {
            if (File.Exists(SavePath))
                File.Delete(SavePath);
            if (File.Exists(SavePath + ".tmp"))
                File.Delete(SavePath + ".tmp");
            if (File.Exists(SavePath + ".bak"))
                File.Delete(SavePath + ".bak");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[DetectorCoordinateDatabase] Failed to delete coordinate file: {e.Message}");
        }
    }

    public void LogAllCoordinates()
    {
        Debug.Log($"[DetectorCoordinateDatabase] Coordinate count: {saveRoot.records.Count}");

        foreach (DetectorCoordinateRecord record in saveRoot.records)
        {
            Debug.Log(
                $"[{record.detectorId}] " +
                $"method={record.placementMethod}, " +
                $"anchor={record.anchorPersistentGuid}, " +
                $"pos=({record.positionX:F3}, {record.positionY:F3}, {record.positionZ:F3}), " +
                $"room={record.roomId}, roomPos=({record.roomPositionX:F3}, {record.roomPositionY:F3}, {record.roomPositionZ:F3}), " +
                $"distance={record.estimatedDistanceMeters:F2}m, " +
                $"qrPixelSize={record.qrPixelSize:F1}, " +
                $"lastRadiation={record.lastRadiationValue:F3}, " +
                $"updated={record.updatedUtc}"
            );
        }
    }

    private string NormalizeId(string raw)
    {
        return string.IsNullOrWhiteSpace(raw) ? "" : raw.Trim();
    }

    private void ScheduleRadiationSave()
    {
        if (!autoSaveAfterEachChange)
            return;

        if (!radiationSavePending)
        {
            radiationSavePending = true;
            nextRadiationSaveTime =
                Time.unscaledTime + Mathf.Max(0.1f, radiationSaveMinimumIntervalSeconds);
        }
    }

    private void ReplaceSaveFileWithPortableFallback(
        string temporaryPath,
        string backupPath)
    {
        if (File.Exists(SavePath))
            File.Copy(SavePath, backupPath, true);

        if (File.Exists(SavePath))
            File.Delete(SavePath);

        File.Move(temporaryPath, SavePath);
    }

    private bool TryFindLatestReadableSave(
        out string selectedPath,
        out DetectorCoordinateSaveRoot selectedRoot)
    {
        selectedPath = "";
        selectedRoot = null;
        DateTime selectedWriteTime = DateTime.MinValue;

        // Temp is first so equal/coarse filesystem timestamps favor the completed
        // transaction that had not yet reached the replace step when the app died.
        string[] candidates =
        {
            SavePath + ".tmp",
            SavePath,
            SavePath + ".bak"
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            string candidatePath = candidates[i];
            if (!File.Exists(candidatePath) ||
                !TryReadSaveRoot(candidatePath, out DetectorCoordinateSaveRoot candidateRoot))
            {
                continue;
            }

            DateTime candidateWriteTime;
            try
            {
                candidateWriteTime = File.GetLastWriteTimeUtc(candidatePath);
            }
            catch (Exception timeException)
            {
                Debug.LogWarning(
                    $"[DetectorCoordinateDatabase] Could not read save timestamp " +
                    $"for {candidatePath}: {timeException.Message}");
                candidateWriteTime = DateTime.MinValue;
            }

            if (selectedRoot != null && candidateWriteTime <= selectedWriteTime)
                continue;

            selectedPath = candidatePath;
            selectedRoot = candidateRoot;
            selectedWriteTime = candidateWriteTime;
        }

        return selectedRoot != null;
    }

    private bool TryReadSaveRoot(
        string path,
        out DetectorCoordinateSaveRoot loaded)
    {
        loaded = null;

        try
        {
            string json = File.ReadAllText(path);
            loaded = JsonUtility.FromJson<DetectorCoordinateSaveRoot>(json);
            return loaded != null && loaded.records != null;
        }
        catch (Exception readException)
        {
            Debug.LogWarning(
                $"[DetectorCoordinateDatabase] Invalid save candidate {path}: " +
                readException.Message);
            loaded = null;
            return false;
        }
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused && radiationSavePending && autoSaveAfterEachChange)
            SaveToDisk(true);
    }

    private void OnApplicationQuit()
    {
        if (radiationSavePending && autoSaveAfterEachChange)
            SaveToDisk(true);
    }

    private void OnDestroy()
    {
        if (radiationSavePending && autoSaveAfterEachChange)
            SaveToDisk(true);

        if (Instance == this)
            Instance = null;
    }

    private float SanitizeCalibrationFactor(float value)
    {
        return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value)
            ? value
            : 1f;
    }

    private Quaternion NormalizeQuaternion(Quaternion value)
    {
        float magnitude = Mathf.Sqrt(
            value.x * value.x + value.y * value.y +
            value.z * value.z + value.w * value.w);

        if (magnitude < 0.0001f || float.IsNaN(magnitude) || float.IsInfinity(magnitude))
            return Quaternion.identity;

        float inverseMagnitude = 1f / magnitude;
        return new Quaternion(
            value.x * inverseMagnitude,
            value.y * inverseMagnitude,
            value.z * inverseMagnitude,
            value.w * inverseMagnitude);
    }
}

[Serializable]
public class DetectorCoordinateSaveRoot
{
    public int schemaVersion = 2;
    public long nextPlacementSequence = 1L;
    public List<DetectorCoordinateRecord> records = new List<DetectorCoordinateRecord>();
}

[Serializable]
public class DetectorCoordinateRecord
{
    public string detectorId;

    // Persistent spatial-anchor key. World pose below is still kept as fallback.
    public string anchorPersistentGuid = "";
    public bool anchorSaved = false;
    public string anchorSavedUtc;

    public float positionX;
    public float positionY;
    public float positionZ;

    public float rotationX;
    public float rotationY;
    public float rotationZ;
    public float rotationW = 1f;

    // Stable room-relative pose. This is valid only for the matching roomId and
    // is populated after the user calibrates a ROOM_ORIGIN QR reference frame.
    public string roomId = "";
    public bool hasRoomPose = false;

    public float roomPositionX;
    public float roomPositionY;
    public float roomPositionZ;

    public float roomRotationX;
    public float roomRotationY;
    public float roomRotationZ;
    public float roomRotationW = 1f;

    // Per-detector relative sensitivity used by the inverse-square estimator.
    public float calibrationFactor = 1f;
    public string roomPoseUpdatedUtc;

    public float estimatedDistanceMeters;
    public float qrPixelSize;

    public float qrImageCenterX;
    public float qrImageCenterY;
    public int qrImageWidth;
    public int qrImageHeight;

    public string placementMethod = "PreviewCenterProjection";

    public float lastRadiationValue = -1f;

    public string createdUtc;
    public string updatedUtc;
    public string lastRadiationUpdatedUtc;
    public long lastPlacedSequence;
    public string lastPlacedUtc;

    public Vector3 GetPosition()
    {
        return new Vector3(positionX, positionY, positionZ);
    }

    public Quaternion GetRotation()
    {
        return new Quaternion(rotationX, rotationY, rotationZ, rotationW);
    }

    public Vector3 GetRoomPosition()
    {
        return new Vector3(roomPositionX, roomPositionY, roomPositionZ);
    }

    public Quaternion GetRoomRotation()
    {
        Quaternion rotation = new Quaternion(
            roomRotationX,
            roomRotationY,
            roomRotationZ,
            roomRotationW);

        float magnitude = Mathf.Sqrt(
            rotation.x * rotation.x + rotation.y * rotation.y +
            rotation.z * rotation.z + rotation.w * rotation.w);

        if (magnitude < 0.0001f || float.IsNaN(magnitude) || float.IsInfinity(magnitude))
            return Quaternion.identity;

        float inverseMagnitude = 1f / magnitude;
        return new Quaternion(
            rotation.x * inverseMagnitude,
            rotation.y * inverseMagnitude,
            rotation.z * inverseMagnitude,
            rotation.w * inverseMagnitude);
    }

    public Vector2 GetQrImageCenter()
    {
        return new Vector2(qrImageCenterX, qrImageCenterY);
    }

    public bool HasSavedAnchor()
    {
        return anchorSaved && !string.IsNullOrWhiteSpace(anchorPersistentGuid);
    }

    public bool HasRoomPose(string expectedRoomId = null)
    {
        if (!hasRoomPose || string.IsNullOrWhiteSpace(roomId))
            return false;

        return string.IsNullOrWhiteSpace(expectedRoomId) ||
               string.Equals(
                   roomId.Trim(),
                   expectedRoomId.Trim(),
                   StringComparison.OrdinalIgnoreCase);
    }
}
