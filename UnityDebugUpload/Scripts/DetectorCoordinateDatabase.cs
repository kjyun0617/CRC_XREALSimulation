using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Stores detector ID, fallback world pose, QR scan metadata, last radiation value,
/// and optional persistent spatial-anchor GUID in a JSON file.
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

    private DetectorCoordinateSaveRoot saveRoot = new DetectorCoordinateSaveRoot();
    private readonly Dictionary<string, DetectorCoordinateRecord> recordsById = new Dictionary<string, DetectorCoordinateRecord>();

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
        if (string.IsNullOrEmpty(detectorId))
            return;

        if (!recordsById.TryGetValue(detectorId, out DetectorCoordinateRecord record))
            return;

        record.lastRadiationValue = radiationValue;
        record.lastRadiationUpdatedUtc = DateTime.UtcNow.ToString("o");

        if (autoSaveAfterEachChange)
            SaveToDisk();
    }

    public void SaveToDisk()
    {
        try
        {
            string directory = Path.GetDirectoryName(SavePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string json = JsonUtility.ToJson(saveRoot, savePrettyJson);
            File.WriteAllText(SavePath, json);
            Debug.Log($"[DetectorCoordinateDatabase] Saved: {SavePath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[DetectorCoordinateDatabase] Failed to save: {e.Message}");
        }
    }

    public void LoadFromDisk()
    {
        recordsById.Clear();
        saveRoot = new DetectorCoordinateSaveRoot();

        try
        {
            if (!File.Exists(SavePath))
            {
                Debug.Log($"[DetectorCoordinateDatabase] No coordinate file yet: {SavePath}");
                return;
            }

            string json = File.ReadAllText(SavePath);
            DetectorCoordinateSaveRoot loaded = JsonUtility.FromJson<DetectorCoordinateSaveRoot>(json);
            if (loaded == null || loaded.records == null)
                return;

            saveRoot = loaded;

            for (int i = saveRoot.records.Count - 1; i >= 0; i--)
            {
                DetectorCoordinateRecord record = saveRoot.records[i];
                record.detectorId = NormalizeId(record.detectorId);

                if (string.IsNullOrEmpty(record.detectorId))
                {
                    saveRoot.records.RemoveAt(i);
                    continue;
                }

                if (!recordsById.ContainsKey(record.detectorId))
                    recordsById.Add(record.detectorId, record);
                else
                    saveRoot.records.RemoveAt(i);
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

        try
        {
            if (File.Exists(SavePath))
                File.Delete(SavePath);
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
}

[Serializable]
public class DetectorCoordinateSaveRoot
{
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

    public Vector3 GetPosition()
    {
        return new Vector3(positionX, positionY, positionZ);
    }

    public Quaternion GetRotation()
    {
        return new Quaternion(rotationX, rotationY, rotationZ, rotationW);
    }

    public Vector2 GetQrImageCenter()
    {
        return new Vector2(qrImageCenterX, qrImageCenterY);
    }

    public bool HasSavedAnchor()
    {
        return anchorSaved && !string.IsNullOrWhiteSpace(anchorPersistentGuid);
    }
}
