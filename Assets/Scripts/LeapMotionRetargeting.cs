using UnityEngine;

/// <summary>
/// Locks each Leap ghost-hand model's position/rotation to the XR rig's own hand anchors every frame, so
/// the ghost hands track wherever the rig thinks the user's hands are (controller pose or hand tracking)
/// rather than wherever the Leap sensor's own coordinate frame would otherwise place them.
///
/// This only touches each hand model's root transform, never its finger bones. Leap's hand model script
/// (HandModelBase.UpdateHand, driven by LeapProvider.OnUpdateFrame during Update()) already positioned
/// every finger bone for this frame as children of that root, with their local rotations encoding curl
/// relative to the root. Because Unity's transform hierarchy is rigid, overriding just the root's world
/// position/rotation afterwards - in LateUpdate, after that Update() pass has run - carries the whole
/// finger chain along with it unchanged relative to the root. The result is the rig's hand position and
/// orientation with Leap's tracked finger curl overlaid on top.
/// </summary>
public class LeapMotionRetargeting : MonoBehaviour
{
    [Tooltip("Root of the Leap ghost-hand model driven by Leap tracking (the object referenced as 'Left' in HandModelManager's Hand Model Pairs).")]
    [SerializeField] private Transform leftGhostHandRoot;

    [Tooltip("Root of the Leap ghost-hand model driven by Leap tracking (the object referenced as 'Right' in HandModelManager's Hand Model Pairs).")]
    [SerializeField] private Transform rightGhostHandRoot;

    [Tooltip("XR rig's hand anchors to glue the ghost hands to. Auto-resolved from the scene's OVRCameraRig if left empty.")]
    [SerializeField] private Transform leftHandAnchor;
    [SerializeField] private Transform rightHandAnchor;

    private void LateUpdate()
    {
        if (leftHandAnchor == null || rightHandAnchor == null)
        {
            ResolveHandAnchors();
        }

        if (leftGhostHandRoot != null && leftHandAnchor != null)
        {
            leftGhostHandRoot.SetPositionAndRotation(leftHandAnchor.position, leftHandAnchor.rotation);
        }

        if (rightGhostHandRoot != null && rightHandAnchor != null)
        {
            rightGhostHandRoot.SetPositionAndRotation(rightHandAnchor.position, rightHandAnchor.rotation);
        }
    }

    // Runs lazily rather than once in Awake() - the VR rig (VRUser) is loaded additively at
    // runtime by SessionManager, often after this component's Awake() has already run, and re-tries every
    // frame until it succeeds.
    private void ResolveHandAnchors()
    {
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig == null) return;

        leftHandAnchor = cameraRig.leftHandAnchor;
        rightHandAnchor = cameraRig.rightHandAnchor;
    }
}
