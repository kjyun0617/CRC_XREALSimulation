using Unity.XR.XREAL;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Connects the Beam Pro center pad and the active right-controller pointer to
/// DetectorWorldMarkerManager. XREAL maps the Beam Pro center pad to TriggerButton;
/// XRI Select is GripButton in the current rig and therefore is not used here.
/// </summary>
[DisallowMultipleComponent]
public class DetectorControllerInteractor : MonoBehaviour
{
    [Tooltip("Optional explicit ray origin. When empty, Right Controller/Ray Interactor or Near-Far Interactor is found automatically.")]
    [SerializeField] private Transform controllerRayOrigin;

    [SerializeField, Min(0.1f)] private float referenceResolveIntervalSeconds = 0.5f;

    private DetectorWorldMarkerManager markerManager;
    private XREALVirtualController boundVirtualController;
    private InputAction triggerAction;
    private bool virtualPadHeld;
    private bool pointerDownQueued;
    private bool pointerUpQueued;
    private bool wasPressed;
    private bool suppressPressUntilRelease;
    private float nextReferenceResolveTime;

    public void Initialize(DetectorWorldMarkerManager manager)
    {
        markerManager = manager;
        EnsureTriggerAction();
        TryBindVirtualController();
        TryResolveControllerRayOrigin(true);
    }

    private void Awake()
    {
        if (markerManager == null)
            markerManager = GetComponent<DetectorWorldMarkerManager>();

        EnsureTriggerAction();
    }

    private void OnEnable()
    {
        EnsureTriggerAction();
        triggerAction?.Enable();
        TryBindVirtualController();
        nextReferenceResolveTime = 0f;
    }

    private void OnDisable()
    {
        triggerAction?.Disable();
        UnbindVirtualController();
        RollbackActiveInteraction(
            markerManager != null && markerManager.isActiveAndEnabled);
    }

    private void OnDestroy()
    {
        UnbindVirtualController();
        if (triggerAction != null)
        {
            triggerAction.Dispose();
            triggerAction = null;
        }
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused)
            RollbackActiveInteraction(true);
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
            RollbackActiveInteraction(true);
    }

    private void RollbackActiveInteraction(bool refreshEstimator)
    {
        virtualPadHeld = false;
        pointerDownQueued = false;
        pointerUpQueued = false;
        wasPressed = false;
        suppressPressUntilRelease = true;

        if (markerManager == null)
            return;

        if (markerManager.HasActiveDetectorMove)
        {
            markerManager.TryEndActiveDetectorMove(
                false,
                refreshEstimator && markerManager.isActiveAndEnabled,
                out _);
        }

        markerManager.ClearDetectorHover();
    }

    private void Update()
    {
        if (markerManager == null)
            markerManager = GetComponent<DetectorWorldMarkerManager>();

        if (markerManager == null)
            return;

        if (!markerManager.isActiveAndEnabled)
        {
            pointerDownQueued = false;
            pointerUpQueued = false;
            wasPressed = virtualPadHeld ||
                         (triggerAction != null && triggerAction.enabled &&
                          triggerAction.ReadValue<float>() >= 0.5f);
            return;
        }

        TryBindVirtualController();
        TryResolveControllerRayOrigin(false);

        bool actionPressed =
            triggerAction != null && triggerAction.enabled &&
            triggerAction.ReadValue<float>() >= 0.5f;
        bool isPressed = virtualPadHeld || actionPressed;
        bool pressedThisFrame =
            pointerDownQueued ||
            (triggerAction != null && triggerAction.enabled &&
             triggerAction.WasPressedThisFrame()) ||
            (isPressed && !wasPressed);
        bool releasedThisFrame =
            pointerUpQueued ||
            (triggerAction != null && triggerAction.enabled &&
             triggerAction.WasReleasedThisFrame()) ||
            (!isPressed && wasPressed);

        if (suppressPressUntilRelease)
        {
            pressedThisFrame = false;
            releasedThisFrame = false;
            if (!isPressed)
                suppressPressUntilRelease = false;
        }

        pointerDownQueued = false;
        pointerUpQueued = false;
        wasPressed = isPressed;

        bool hasPointerRay = TryGetPointerRay(out Ray pointerRay);
        if (!hasPointerRay)
        {
            markerManager.ClearDetectorHover();
            if (releasedThisFrame && markerManager.HasActiveDetectorMove)
                markerManager.TryEndActiveDetectorMove(true, out _);
            return;
        }

        if (!markerManager.HasActiveDetectorMove)
        {
            markerManager.TryUpdateDetectorHover(pointerRay, out _);
            if (pressedThisFrame)
                markerManager.TryBeginPointedDetectorMove(pointerRay, out _);
        }

        // Apply the final controller pose before committing on the release frame.
        if (markerManager.HasActiveDetectorMove)
            markerManager.UpdateActiveDetectorMove(pointerRay);

        if (releasedThisFrame && markerManager.HasActiveDetectorMove)
            markerManager.TryEndActiveDetectorMove(true, out _);
    }

    private void EnsureTriggerAction()
    {
        if (triggerAction != null)
            return;

        triggerAction = new InputAction(
            "Move Detector With Beam Pro Pad",
            InputActionType.Button);

        // Exact XREAL runtime binding plus editor/standard XR fallbacks.
        triggerAction.AddBinding("<XREALController>/TriggerButton");
        triggerAction.AddBinding("<XRController>{RightHand}/{TriggerButton}");
        triggerAction.AddBinding("<XRSimulatedController>/triggerButton");

        if (isActiveAndEnabled)
            triggerAction.Enable();
    }

    private void TryBindVirtualController()
    {
        XREALVirtualController current = XREALVirtualController.Singleton;
        if (boundVirtualController == current)
            return;

        UnbindVirtualController();
        boundVirtualController = current;
        if (boundVirtualController == null)
            return;

        // This direct Beam Pro UI path removes any dependency on InputSystem update
        // latency. The InputAction above remains as an editor/physical fallback.
        boundVirtualController.pointerDown += HandleVirtualControllerPointerDown;
        boundVirtualController.pointerUp += HandleVirtualControllerPointerUp;
    }

    private void UnbindVirtualController()
    {
        if (boundVirtualController != null)
        {
            boundVirtualController.pointerDown -= HandleVirtualControllerPointerDown;
            boundVirtualController.pointerUp -= HandleVirtualControllerPointerUp;
        }

        boundVirtualController = null;
    }

    private void HandleVirtualControllerPointerDown(
        XREALButtonType buttonType,
        GameObject target,
        PointerEventData eventData)
    {
        if (buttonType != XREALButtonType.TriggerButton)
            return;

        virtualPadHeld = true;
        pointerDownQueued = true;
    }

    private void HandleVirtualControllerPointerUp(
        XREALButtonType buttonType,
        GameObject target,
        PointerEventData eventData)
    {
        if (buttonType != XREALButtonType.TriggerButton)
            return;

        virtualPadHeld = false;
        pointerUpQueued = true;
    }

    private bool TryGetPointerRay(out Ray pointerRay)
    {
        pointerRay = default;
        if (controllerRayOrigin == null ||
            !controllerRayOrigin.gameObject.activeInHierarchy)
        {
            return false;
        }

        Vector3 direction = controllerRayOrigin.forward;
        if (direction.sqrMagnitude < 0.0001f)
            return false;

        pointerRay = new Ray(controllerRayOrigin.position, direction.normalized);
        return true;
    }

    private void TryResolveControllerRayOrigin(bool force)
    {
        if (controllerRayOrigin != null &&
            controllerRayOrigin.gameObject.activeInHierarchy)
        {
            return;
        }

        if (!force && Time.unscaledTime < nextReferenceResolveTime)
            return;

        nextReferenceResolveTime =
            Time.unscaledTime + Mathf.Max(0.1f, referenceResolveIntervalSeconds);

        // Prefer XRI's semantic handedness over scene object names. The current
        // HelloMR sample can use either a Right Hand or Right Controller hierarchy.
        NearFarInteractor[] nearFarInteractors =
            FindObjectsByType<NearFarInteractor>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
        for (int i = 0; i < nearFarInteractors.Length; i++)
        {
            NearFarInteractor interactor = nearFarInteractors[i];
            if (interactor == null ||
                !interactor.isActiveAndEnabled ||
                interactor.handedness != InteractorHandedness.Right)
            {
                continue;
            }

            if (TrySetControllerRayOrigin(interactor.transform))
                return;
        }

        GameObject rightController = GameObject.Find("Right Controller");
        if (TryResolveNamedRightRoot(rightController))
            return;

        GameObject rightHand = GameObject.Find("Right Hand");
        if (TryResolveNamedRightRoot(rightHand))
            return;

        // Handles renamed/cloned XR Origin roots while still rejecting the left ray.
        Transform[] transforms = FindObjectsByType<Transform>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform candidate = transforms[i];
            if (candidate == null ||
                (candidate.name != "Ray Interactor" &&
                 candidate.name != "Near-Far Interactor") ||
                (!HasAncestorNamed(candidate, "Right Controller") &&
                 !HasAncestorNamed(candidate, "Right Hand")))
            {
                continue;
            }

            if (TrySetControllerRayOrigin(candidate))
                return;
        }
    }

    private bool TryResolveNamedRightRoot(GameObject rightRoot)
    {
        if (rightRoot == null)
            return false;

        return
            TrySetControllerRayOrigin(
                rightRoot.transform.Find("Ray Interactor")) ||
            TrySetControllerRayOrigin(
                rightRoot.transform.Find("Near-Far Interactor")) ||
            TrySetControllerRayOrigin(
                FindDescendantByName(rightRoot.transform, "Ray Interactor")) ||
            TrySetControllerRayOrigin(
                FindDescendantByName(rightRoot.transform, "Near-Far Interactor"));
    }

    private bool TrySetControllerRayOrigin(Transform interactorTransform)
    {
        Transform effectiveRayOrigin = GetEffectiveRayOrigin(interactorTransform);
        if (effectiveRayOrigin == null ||
            !effectiveRayOrigin.gameObject.activeInHierarchy)
        {
            return false;
        }

        controllerRayOrigin = effectiveRayOrigin;
        return true;
    }

    private Transform GetEffectiveRayOrigin(Transform interactorTransform)
    {
        if (interactorTransform == null)
            return null;

        NearFarInteractor nearFarInteractor =
            interactorTransform.GetComponent<NearFarInteractor>();
        if (nearFarInteractor != null && nearFarInteractor.curveOrigin != null)
            return nearFarInteractor.curveOrigin;

        return interactorTransform;
    }

    private Transform FindDescendantByName(Transform root, string objectName)
    {
        if (root == null)
            return null;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            if (child.name == objectName)
                return child;

            Transform nested = FindDescendantByName(child, objectName);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private bool HasAncestorNamed(Transform transformToCheck, string objectName)
    {
        Transform current = transformToCheck;
        while (current != null)
        {
            if (current.name == objectName)
                return true;

            current = current.parent;
        }

        return false;
    }
}
