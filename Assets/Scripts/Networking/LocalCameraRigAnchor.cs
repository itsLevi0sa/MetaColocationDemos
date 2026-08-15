using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.Networking
{
    /// <summary>
    /// Owner-only: keeps this avatar's container locked to the LOCAL device's own [BuildingBlock] Camera
    /// Rig transform, so the container's world position includes whatever alignment correction Colocation's
    /// AlignCameraToAnchor is applying for this specific device. OwnerNetworkTransform (also on this
    /// GameObject) then replicates that corrected value to every other client as an absolute world
    /// position - which is safe specifically because colocation guarantees "world position X" means the
    /// same physical point on every device, so once the owner computes the right value, everyone else can
    /// just apply it directly rather than recomputing anything themselves.
    ///
    /// Deliberately owner-only: a non-owner's own Camera Rig sits at a DIFFERENT physical point in the room
    /// (wherever that viewer happens to be standing), not the owner's. Running this for every copy -
    /// substituting the local viewer's own rig position when positioning someone ELSE's avatar - silently
    /// mixes two different players' reference frames and produces exactly the offset-avatar bug this
    /// component exists to fix. OVRSkeleton (which the Movement SDK retargeting sits on top of) writes body
    /// tracking straight into each bone's local position with no awareness of the Camera Rig at all, so the
    /// container is the only place that correction can be applied.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class LocalCameraRigAnchor : NetworkBehaviour
    {
        private Transform _cameraRig;

        public override void OnNetworkSpawn()
        {
            if (!IsOwner)
            {
                enabled = false;
                return;
            }

            // includeInactive: true - a spectator client never loads Colocation_VR_Rig at all (see
            // SessionManager), so there's no Camera Rig object here to find regardless; this just guards
            // against finding nothing and falling through to the warning below.
            var ovrCameraRig = FindObjectOfType<OVRCameraRig>(includeInactive: true);
            if (ovrCameraRig == null)
            {
                Debug.LogWarning($"{nameof(LocalCameraRigAnchor)}: no OVRCameraRig found in the scene, " +
                                  $"{name} will stay wherever it was spawned.");
                enabled = false;
                return;
            }

            _cameraRig = ovrCameraRig.transform;
        }

        private void LateUpdate()
        {
            transform.SetPositionAndRotation(_cameraRig.position, _cameraRig.rotation);
        }
    }
}
