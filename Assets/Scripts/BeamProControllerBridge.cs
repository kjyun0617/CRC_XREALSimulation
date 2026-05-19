using UnityEngine;

public class BeamProControllerBridge : MonoBehaviour
{
    private DetectorWorldMarkerManager markerManager;

    private void Awake()
    {
        markerManager = FindObjectOfType<DetectorWorldMarkerManager>();
    }

    public void PlaceDetector()
    {
        if (markerManager == null)
            markerManager = FindObjectOfType<DetectorWorldMarkerManager>();

        if (markerManager == null)
        {
            Debug.LogWarning("[BeamProControllerBridge] DetectorWorldMarkerManager not found.");
            return;
        }

        markerManager.PlaceDetector();
    }
}