using UnityEngine;

/// <summary>
/// Keeps a World Space Canvas fixed in front of the XREAL/Unity camera.
/// Attach this script to the Canvas object.
/// </summary>
[RequireComponent(typeof(Canvas))]
public class HeadLockedCanvas : MonoBehaviour
{
    [Header("Target Camera")]
    [Tooltip("Usually leave this empty. The script will use Camera.main automatically.")]
    [SerializeField] private Transform targetHead;

    [Header("Screen Position")]
    [Tooltip("Distance from the camera in meters.")]
    [SerializeField] private float distanceFromCamera = 1.5f;

    [Tooltip("X = right/left, Y = up/down. Unit is meters, not pixels.")]
    [SerializeField] private Vector2 screenOffset = new Vector2(0f, -0.15f);

    [Header("Follow Option")]
    [Tooltip("Turn this off if you want the UI to be perfectly fixed to the glasses screen.")]
    [SerializeField] private bool useSmoothFollow = false;

    [Tooltip("Only used when Use Smooth Follow is on.")]
    [SerializeField, Range(1f, 30f)] private float followSpeed = 15f;

    private Canvas canvas;

    private void Awake()
    {
        canvas = GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        TryFindMainCamera();
    }

    private void LateUpdate()
    {
        if (targetHead == null)
        {
            TryFindMainCamera();
            if (targetHead == null) return;
        }

        Vector3 targetPosition =
            targetHead.position +
            targetHead.forward * distanceFromCamera +
            targetHead.right * screenOffset.x +
            targetHead.up * screenOffset.y;

        Quaternion targetRotation = targetHead.rotation;

        if (useSmoothFollow)
        {
            float t = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, targetPosition, t);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, t);
        }
        else
        {
            transform.position = targetPosition;
            transform.rotation = targetRotation;
        }
    }

    private void TryFindMainCamera()
    {
        Camera mainCamera = Camera.main;
        if (mainCamera == null) return;

        targetHead = mainCamera.transform;

        if (canvas == null)
            canvas = GetComponent<Canvas>();

        if (canvas.worldCamera == null)
            canvas.worldCamera = mainCamera;
    }

    public void SetDistance(float distance)
    {
        distanceFromCamera = Mathf.Max(0.1f, distance);
    }

    public void SetScreenOffset(Vector2 offset)
    {
        screenOffset = offset;
    }
}
