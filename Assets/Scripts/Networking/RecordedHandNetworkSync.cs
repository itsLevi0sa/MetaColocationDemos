using Leap;
using Leap.Encoding;
using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.Networking
{
    /// <summary>
    /// Networks recorded/played-back hand poses for a scene object like Lambertian - a shared environment
    /// prop rather than a per-client rig, so unlike LeapHandNetworkSync (owner-written, from a local
    /// LeapProvider) this is server-written, from whatever RecordManager feeds it during playback. Every
    /// client, including the server's own copy, decodes and interpolates identically via HandPoseApplier, so
    /// the recorded hands look the same to the VR user and every PC user watching.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class RecordedHandNetworkSync : NetworkBehaviour
    {
        // Matches RecordManager.SampleRateHz - recordings are captured at this rate, so played-back updates
        // arrive at roughly this cadence too (see RecordManager.PushHandsIfChanged).
        private const float UpdateRateHz = 30f;
        private const float UpdateInterval = 1f / UpdateRateHz;

        // See HumanoidPoseNetworkSync.MaxExtrapolationFactor for the reasoning - same bound applied here.
        private const float MaxExtrapolationFactor = 3f;

        [Tooltip("The left/right hand models (e.g. Lambertian's GhostHands HandBinder components) driven from " +
                 "recorded playback data on every client.")]
        [SerializeField] private HandModelBase leftHandModel;
        [SerializeField] private HandModelBase rightHandModel;

        private NetworkVariable<LeapHandsData> _networkedHands;

        private readonly VectorHand _leftVectorHand = new();
        private readonly VectorHand _rightVectorHand = new();
        private readonly Hand _leftHandBuffer = new();
        private readonly Hand _rightHandBuffer = new();

        // Same interpolation approach as LeapHandNetworkSync - see that class for the full reasoning.
        private readonly Hand _previousLeftHand = new();
        private readonly Hand _targetLeftHand = new();
        private readonly Hand _renderLeftHand = new();
        private readonly Hand _previousRightHand = new();
        private readonly Hand _targetRightHand = new();
        private readonly Hand _renderRightHand = new();
        private bool _previousLeftTracked;
        private bool _targetLeftTracked;
        private bool _previousRightTracked;
        private bool _targetRightTracked;
        private float _interpolationStartTime;
        private bool _hasReceivedFirstHands;

        private void Awake()
        {
            _networkedHands = new NetworkVariable<LeapHandsData>(
                new LeapHandsData
                {
                    LeftHandBytes = new byte[VectorHand.NUM_BYTES],
                    RightHandBytes = new byte[VectorHand.NUM_BYTES],
                },
                writePerm: NetworkVariableWritePermission.Server);
        }

        public override void OnNetworkSpawn()
        {
            _networkedHands.OnValueChanged += (_, newValue) => OnHandsReceived(newValue);
            ApplyHands(_networkedHands.Value);
        }

        // Called by RecordManager (server side only) during playback - not gated on IsServer here, since a
        // NetworkVariable with Server write permission already throws if a non-server caller tries to set
        // Value, and RecordManager is the one responsible for only driving playback on the server (see its
        // CanDrivePlayback).
        public void ServerSetHands(bool leftTracked, byte[] leftBytes, bool rightTracked, byte[] rightBytes)
        {
            _networkedHands.Value = new LeapHandsData
            {
                LeftTracked = leftTracked,
                RightTracked = rightTracked,
                LeftHandBytes = leftBytes ?? new byte[VectorHand.NUM_BYTES],
                RightHandBytes = rightBytes ?? new byte[VectorHand.NUM_BYTES],
            };
        }

        public void ServerHideHands()
        {
            ServerSetHands(false, null, false, null);
        }

        private void ApplyHands(LeapHandsData data)
        {
            HandPoseApplier.ApplySnapshot(leftHandModel, _leftVectorHand, _leftHandBuffer, data.LeftTracked, data.LeftHandBytes);
            HandPoseApplier.ApplySnapshot(rightHandModel, _rightVectorHand, _rightHandBuffer, data.RightTracked, data.RightHandBytes);
        }

        private void OnHandsReceived(LeapHandsData data)
        {
            _previousLeftTracked = _targetLeftTracked;
            _previousRightTracked = _targetRightTracked;
            if (_targetLeftTracked) _previousLeftHand.CopyFrom(_targetLeftHand);
            if (_targetRightTracked) _previousRightHand.CopyFrom(_targetRightHand);

            _targetLeftTracked = data.LeftTracked;
            _targetRightTracked = data.RightTracked;

            if (_targetLeftTracked && data.LeftHandBytes != null)
            {
                _leftVectorHand.ReadBytes(data.LeftHandBytes);
                _leftVectorHand.Decode(_targetLeftHand);
            }
            if (_targetRightTracked && data.RightHandBytes != null)
            {
                _rightVectorHand.ReadBytes(data.RightHandBytes);
                _rightVectorHand.Decode(_targetRightHand);
            }

            _interpolationStartTime = Time.time;
            _hasReceivedFirstHands = true;
        }

        private void Update()
        {
            if (!_hasReceivedFirstHands) return;

            var t = Mathf.Clamp((Time.time - _interpolationStartTime) / UpdateInterval, 0f, MaxExtrapolationFactor);
            HandPoseApplier.ApplyInterpolated(leftHandModel, _previousLeftHand, _targetLeftHand, _renderLeftHand,
                                               _previousLeftTracked, _targetLeftTracked, t);
            HandPoseApplier.ApplyInterpolated(rightHandModel, _previousRightHand, _targetRightHand, _renderRightHand,
                                               _previousRightTracked, _targetRightTracked, t);
        }
    }
}
