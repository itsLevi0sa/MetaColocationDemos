using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.Networking
{
    /// <summary>
    /// Networks this VR user's real HMD head pose (OVRCameraRig.centerEyeAnchor), independent of the
    /// Humanoid IK pipeline (RigBuilder/OVRBody/RetargetingLayer). PcUserHeadFollower reads this - not the
    /// Animator's Head bone - specifically so it keeps an accurate, live head position to attach to even
    /// when SessionManager.IsHumanoidIkEnabled is off and that pipeline is disabled on the owner (see
    /// AvatarBodyVisibility). Head position doesn't depend on body IK at all, so there's no reason it should
    /// share a data path with it.
    ///
    /// Exposes the result as HeadAnchor, a plain local child Transform this component positions every frame
    /// on every copy (owner: straight from the real rig; non-owner: from network data) - other local
    /// components just read it like any other Transform, no networking awareness needed on their end.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class VrHeadPoseNetworkSync : NetworkBehaviour
    {
        // Same send rate/extrapolation bound as HumanoidPoseNetworkSync/LeapHandNetworkSync, for the same
        // reasons - see those for the full explanation.
        private const float SendRateHz = 30f;
        private const float SendInterval = 1f / SendRateHz;
        private const float MaxExtrapolationFactor = 3f;

        [Tooltip("Local child Transform this component positions every frame - the single source of truth " +
                 "for 'where is this VR user's real head', on owner and non-owner copies alike.")]
        [SerializeField] private Transform headAnchor;

        public Transform HeadAnchor => headAnchor;

        private struct HeadPoseData : INetworkSerializable
        {
            public Vector3 Position;
            public Quaternion Rotation;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref Position);
                serializer.SerializeValue(ref Rotation);
            }
        }

        // Explicit identity rotation, not the struct default - a default(Quaternion) is (0,0,0,0), a
        // degenerate/zero-length quaternion, not identity. Matters here specifically because a non-owner
        // reads this as its very first pose before the owner's first real send has necessarily landed.
        private readonly NetworkVariable<HeadPoseData> _networkedHeadPose = new(
            new HeadPoseData { Position = Vector3.zero, Rotation = Quaternion.identity },
            writePerm: NetworkVariableWritePermission.Owner);

        private Transform _centerEyeAnchor;
        private float _timeSinceLastSend;

        private HeadPoseData _previousPose;
        private HeadPoseData _targetPose;
        private float _interpolationStartTime;
        private bool _hasReceivedFirstPose;

        public override void OnNetworkSpawn()
        {
            if (headAnchor == null)
            {
                Debug.LogError($"{nameof(VrHeadPoseNetworkSync)}: headAnchor not assigned on {name}.");
                enabled = false;
                return;
            }

            if (!IsOwner)
            {
                _previousPose = _targetPose = _networkedHeadPose.Value;
                _networkedHeadPose.OnValueChanged += (_, newPose) => OnPoseReceived(newPose);
                headAnchor.SetPositionAndRotation(_networkedHeadPose.Value.Position, _networkedHeadPose.Value.Rotation);
            }
        }

        private void LateUpdate()
        {
            if (IsOwner)
            {
                UpdateAndSend();
            }
            else
            {
                ApplyInterpolatedPose();
            }
        }

        private void UpdateAndSend()
        {
            if (_centerEyeAnchor == null)
            {
                ResolveCenterEyeAnchor();
                if (_centerEyeAnchor == null) return;
            }

            // Every frame, regardless of send throttling below - anything local on this same machine reading
            // HeadAnchor (there isn't one today, but nothing should have to care about network send timing).
            headAnchor.SetPositionAndRotation(_centerEyeAnchor.position, _centerEyeAnchor.rotation);

            _timeSinceLastSend += Time.deltaTime;
            if (_timeSinceLastSend < SendInterval) return;
            _timeSinceLastSend -= SendInterval;

            _networkedHeadPose.Value = new HeadPoseData
            {
                Position = _centerEyeAnchor.position,
                Rotation = _centerEyeAnchor.rotation,
            };
        }

        // Retried every LateUpdate rather than looked up once in OnNetworkSpawn and given up on failure -
        // includeInactive: true matches LocalCameraRigAnchor's own lookup, but even with that, the rig can
        // still momentarily not exist yet while VRUser.unity is mid-load (see SessionManager), so this needs
        // to keep trying rather than permanently disabling itself on one early failed attempt.
        private void ResolveCenterEyeAnchor()
        {
            var cameraRig = FindObjectOfType<OVRCameraRig>(includeInactive: true);
            if (cameraRig == null) return;

            _centerEyeAnchor = cameraRig.centerEyeAnchor;

            // TEMP DIAGNOSTIC: confirms the owner actually resolved a real anchor, and what it read as the
            // very first position - remove once head-follow is confirmed working end to end.
            Debug.Log($"{nameof(VrHeadPoseNetworkSync)}: resolved centerEyeAnchor '{_centerEyeAnchor.name}' " +
                      $"at world position {_centerEyeAnchor.position}.");
        }

        private void OnPoseReceived(HeadPoseData newPose)
        {
            _previousPose = _targetPose;
            _targetPose = newPose;
            _interpolationStartTime = Time.time;
            _hasReceivedFirstPose = true;

            // TEMP DIAGNOSTIC: confirms the non-owner is actually receiving updates, and what position they
            // carry - remove once head-follow is confirmed working end to end.
            Debug.Log($"{nameof(VrHeadPoseNetworkSync)}: received pose - position {newPose.Position}, " +
                      $"rotation {newPose.Rotation}.");
        }

        private void ApplyInterpolatedPose()
        {
            if (!_hasReceivedFirstPose) return;

            // Unclamped past t=1 - see HumanoidPoseNetworkSync.ApplyInterpolatedPose for why (predicts
            // forward on a late update instead of freezing dead on target and visibly catching up).
            var t = Mathf.Clamp((Time.time - _interpolationStartTime) / SendInterval, 0f, MaxExtrapolationFactor);
            var position = Vector3.LerpUnclamped(_previousPose.Position, _targetPose.Position, t);
            var rotation = Quaternion.SlerpUnclamped(_previousPose.Rotation, _targetPose.Rotation, t);
            headAnchor.SetPositionAndRotation(position, rotation);
        }
    }
}
