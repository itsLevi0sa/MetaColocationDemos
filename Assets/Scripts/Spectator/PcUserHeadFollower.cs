using MetaColocationDemos.Networking;
using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.Spectator
{
    /// <summary>
    /// Owner-only, and only when SessionManager.IsLeapRetargetingEnabled is checked: rigidly attaches this
    /// NetworkedPCUser's container to whichever VR user's VrHeadPoseNetworkSync HeadAnchor is currently
    /// tracked - copying its position/rotation every frame, like a camera clipped to the VR wearer's own
    /// head - instead of the normal SpectatorFlyCamera free-fly. SpectatorFlyCamera (sibling component on
    /// the same GameObject) is disabled for as long as a VR user is being followed, and re-enabled the
    /// moment there isn't one, so the spectator falls back to manual flying rather than freezing in place if
    /// the VR user they were riding along with disconnects.
    ///
    /// Deliberately reads VrHeadPoseNetworkSync.HeadAnchor rather than the avatar's Animator Head bone: that
    /// bone is only driven while the Humanoid IK pipeline (RigBuilder/OVRBody/RetargetingLayer) is running,
    /// which SessionManager.IsHumanoidIkEnabled can turn off on the VR user's own copy for performance (see
    /// AvatarBodyVisibility) - reading the bone would freeze head-follow the moment that happens.
    /// HeadAnchor is sourced straight from the real HMD (OVRCameraRig.centerEyeAnchor) independent of that
    /// pipeline, so it keeps tracking correctly either way.
    ///
    /// Reads HumanoidPoseNetworkSync.AvatarSpawned/AvatarDespawned to notice VR avatars as they come and
    /// go, plus does an initial scan on spawn for any that already exist - a VR user who connected before
    /// this spectator did would otherwise have already fired AvatarSpawned before this component existed
    /// to hear it.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class PcUserHeadFollower : NetworkBehaviour
    {
        private SpectatorFlyCamera _flyCamera;
        private HumanoidPoseNetworkSync _trackedVrAvatar;
        private Transform _trackedVrHeadAnchor;

        public override void OnNetworkSpawn()
        {
            if (!IsOwner || !SessionManager.IsLeapRetargetingEnabled) return;

            _flyCamera = GetComponent<SpectatorFlyCamera>();

            HumanoidPoseNetworkSync.AvatarSpawned += OnVrAvatarSpawned;
            HumanoidPoseNetworkSync.AvatarDespawned += OnVrAvatarDespawned;

            foreach (var avatar in FindObjectsOfType<HumanoidPoseNetworkSync>())
            {
                TryTrackAvatar(avatar);
                if (_trackedVrAvatar != null) break;
            }

            UpdateFlyCameraState();
        }

        public override void OnNetworkDespawn()
        {
            HumanoidPoseNetworkSync.AvatarSpawned -= OnVrAvatarSpawned;
            HumanoidPoseNetworkSync.AvatarDespawned -= OnVrAvatarDespawned;
        }

        private void OnVrAvatarSpawned(HumanoidPoseNetworkSync avatar)
        {
            if (_trackedVrAvatar != null) return; // Already following one - first VR user wins.

            TryTrackAvatar(avatar);
            UpdateFlyCameraState();
        }

        private void OnVrAvatarDespawned(HumanoidPoseNetworkSync avatar)
        {
            if (avatar != _trackedVrAvatar) return;

            _trackedVrAvatar = null;
            _trackedVrHeadAnchor = null;

            // Fall back to whichever other VR avatar is still around, if any, rather than staying attached
            // to a now-despawned head.
            foreach (var other in FindObjectsOfType<HumanoidPoseNetworkSync>())
            {
                if (other == avatar) continue;
                TryTrackAvatar(other);
                if (_trackedVrAvatar != null) break;
            }

            UpdateFlyCameraState();
        }

        private void TryTrackAvatar(HumanoidPoseNetworkSync avatar)
        {
            var headSync = avatar.GetComponent<VrHeadPoseNetworkSync>();
            var headAnchor = headSync != null ? headSync.HeadAnchor : null;
            if (headAnchor == null)
            {
                Debug.LogWarning($"{nameof(PcUserHeadFollower)}: no VrHeadPoseNetworkSync/HeadAnchor found " +
                                  $"on {avatar.name}, skipping it.");
                return;
            }

            _trackedVrAvatar = avatar;
            _trackedVrHeadAnchor = headAnchor;

            // TEMP DIAGNOSTIC: confirms tracking actually engaged, and what position it's about to snap to -
            // remove once head-follow is confirmed working end to end.
            Debug.Log($"{nameof(PcUserHeadFollower)}: now tracking {avatar.name}'s head anchor at world " +
                      $"position {headAnchor.position}.");
        }

        private void UpdateFlyCameraState()
        {
            if (_flyCamera != null) _flyCamera.enabled = _trackedVrHeadAnchor == null;
        }

        private void LateUpdate()
        {
            if (!IsOwner || _trackedVrHeadAnchor == null) return;

            transform.SetPositionAndRotation(_trackedVrHeadAnchor.position, _trackedVrHeadAnchor.rotation);
        }
    }
}
