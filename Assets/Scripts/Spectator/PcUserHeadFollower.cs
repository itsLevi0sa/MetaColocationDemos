using MetaColocationDemos.Networking;
using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.Spectator
{
    /// <summary>
    /// Owner-only, and only when SessionManager.ActiveLeapRetargetingMode isn't Off: positions this
    /// NetworkedPCUser's container relative to whichever VR user's VrHeadPoseNetworkSync HeadAnchor is
    /// currently tracked, instead of the normal SpectatorFlyCamera free-fly. Position always tracks the
    /// anchor live, every frame, in both modes - the container follows the VR wearer's head height/location
    /// wherever it goes (e.g. sitting vs. standing), and this is never a one-time snap, so it can't get stuck
    /// reading a stale HeadAnchor before VrHeadPoseNetworkSync's own OnNetworkSpawn has positioned it for the
    /// first time (see TryTrackAvatar). Rotation is what differs between the two modes:
    /// DynamicRetargetingToMovingVrHead copies the anchor's rotation every frame right along with its
    /// position, like a camera clipped to the VR wearer's own head. StaticRetargetingToVrHead instead
    /// computes a leveled yaw-only rotation once - the first LateUpdate after tracking begins - and holds
    /// onto that forever after: only the anchor's yaw (facing direction) is used, with pitch/roll zeroed out,
    /// so the spectator ends up looking forward on a level horizon rather than wherever the VR wearer's head
    /// happened to be tilted at that moment, and never turns again afterwards no matter how much the VR
    /// wearer turns their own head. SpectatorFlyCamera (sibling component on the
    /// same GameObject) is disabled for as long as a VR user is being tracked, and re-enabled the moment
    /// there isn't one, so the spectator falls back to manual flying rather than freezing in place if the
    /// VR user they were tracking disconnects.
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
        private const string RetargeterChildName = "LeapMotionRetargeter";

        private SpectatorFlyCamera _flyCamera;
        private HumanoidPoseNetworkSync _trackedVrAvatar;
        private Transform _trackedVrHeadAnchor;
        private bool _staticRotationSet;
        private Quaternion _staticRotation;
        private Transform _retargeterTransform;
        private Vector3 _retargeterBaseLocalPosition;

        public override void OnNetworkSpawn()
        {
            if (!IsOwner || SessionManager.ActiveLeapRetargetingMode == SessionManager.LeapRetargetingMode.Off) return;

            _flyCamera = GetComponent<SpectatorFlyCamera>();

            // Base position captured once, here, before SessionManager.LeapMotionRetargeterOffset is ever
            // applied to it - LateUpdate below adds that offset on top of this every frame, rather than
            // overwriting localPosition outright, so the prefab-authored placement (whatever it is) is
            // preserved and the Inspector value is purely an adjustable delta, defaulting to no change.
            _retargeterTransform = transform.Find(RetargeterChildName);
            if (_retargeterTransform != null)
            {
                _retargeterBaseLocalPosition = _retargeterTransform.localPosition;
            }
            else
            {
                Debug.LogWarning($"{nameof(PcUserHeadFollower)}: no child named '{RetargeterChildName}' " +
                                  $"found under {name}, so SessionManager.LeapMotionRetargeterOffset won't apply.");
            }

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

            // Deliberately not read here: this runs from the AvatarSpawned event, fired by the VR avatar's
            // HumanoidPoseNetworkSync.OnNetworkSpawn - a sibling component to VrHeadPoseNetworkSync on the
            // same avatar, with no guaranteed ordering between the two. If VrHeadPoseNetworkSync's own
            // OnNetworkSpawn (which is what moves HeadAnchor off its default prefab-authored pose to the real
            // synced head position) hasn't run yet at this exact instant, reading headAnchor here would see a
            // stale/default pose - typically near the avatar's floor-level origin, not head height.
            // LateUpdate below reads it instead: position every frame anyway, so this is a non-issue there,
            // and StaticRetargetingToVrHead's one-time rotation snapshot is deferred to LateUpdate too, since
            // it's guaranteed to run only after every OnNetworkSpawn callback for this frame's spawn batch -
            // including that sibling's - has already completed.
            _staticRotationSet = false;

            // TEMP DIAGNOSTIC: confirms tracking actually engaged, and what position it's about to snap to -
            // remove once head-follow is confirmed working end to end.
            Debug.Log($"{nameof(PcUserHeadFollower)}: now tracking {avatar.name}'s head anchor at world " +
                      $"position {headAnchor.position}.");
        }

        // Discards the head anchor's pitch and roll and keeps only its yaw, so a VR wearer who happened to
        // be looking up, down, or with their head tilted at the moment tracking began doesn't leave the
        // static spectator stuck staring at the ceiling/floor or on a slant. Falls back to the anchor's up
        // vector projected onto the ground plane for the rare case where forward is itself near-vertical
        // (the VR wearer looking almost straight up or down), since flattening a vertical forward vector
        // would otherwise collapse to zero and produce an undefined look direction.
        private static Quaternion GetLeveledYawRotation(Transform headAnchor)
        {
            var forward = headAnchor.forward;
            forward.y = 0f;

            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = headAnchor.up;
                forward.y = 0f;
            }

            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;

            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        private void UpdateFlyCameraState()
        {
            if (_flyCamera != null) _flyCamera.enabled = _trackedVrHeadAnchor == null;
        }

        private void LateUpdate()
        {
            if (!IsOwner || _trackedVrHeadAnchor == null) return;

            // Position always tracks the anchor live, in both modes, so the container follows the VR
            // wearer's head wherever it actually is (e.g. standing up or sitting down mid-session) instead
            // of freezing at wherever they happened to be the instant tracking began. SessionManager.
            // PcUserHeadOffset is read fresh every frame too, so nudging it in the Inspector during Play
            // takes effect immediately.
            var isDynamic = SessionManager.ActiveLeapRetargetingMode == SessionManager.LeapRetargetingMode.DynamicRetargetingToMovingVrHead;
            var rotation = isDynamic ? _trackedVrHeadAnchor.rotation : GetOrComputeStaticRotation();

            transform.SetPositionAndRotation(_trackedVrHeadAnchor.position + SessionManager.PcUserHeadOffset, rotation);

            if (_retargeterTransform != null)
            {
                _retargeterTransform.localPosition = _retargeterBaseLocalPosition + SessionManager.LeapMotionRetargeterOffset;
            }
        }

        // Computed once per tracked avatar and cached, rather than every frame - StaticRetargetingToVrHead's
        // whole point is a facing direction that never changes once set, no matter how much the VR wearer
        // goes on to turn their own head afterwards.
        private Quaternion GetOrComputeStaticRotation()
        {
            if (!_staticRotationSet)
            {
                _staticRotation = GetLeveledYawRotation(_trackedVrHeadAnchor);
                _staticRotationSet = true;
            }

            return _staticRotation;
        }
    }
}
