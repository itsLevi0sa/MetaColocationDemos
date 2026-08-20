using Leap;
using Leap.Encoding;
using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.Networking
{
    /// <summary>
    /// Networks the Ultraleap "ghost hand" pose under NetworkedPCUser. The Leap device only exists on the
    /// owning spectator's machine, so every other client's local LeapProvider has no hands to report -
    /// without this, GhostHands only ever animates on the owner's own screen.
    ///
    /// The owner encodes its tracked hands with Ultraleap's VectorHand (an 86-byte-per-hand compressed
    /// encoding built for exactly this purpose) into a NetworkVariable; every other client decodes it and
    /// feeds it straight into the same HandBinder/CapsuleHand components that would otherwise be driven by
    /// a local LeapProvider - see leapProvider = null below for why the normal auto-driven path has to be
    /// turned off on non-owners first, same reasoning as HumanoidPoseNetworkSync disabling OVRBody.
    ///
    /// Decode/interpolate/show/hide lifecycle lives in HandPoseApplier, shared with RecordedHandNetworkSync
    /// (recorded playback, server-written instead of owner-written) so both stay in sync with each other.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class LeapHandNetworkSync : NetworkBehaviour
    {
        // Hand pose doesn't need a fresh sample every render frame to look smooth - see
        // HumanoidPoseNetworkSync for the same reasoning applied to body pose.
        private const float SendRateHz = 30f;
        private const float SendInterval = 1f / SendRateHz;

        // See HumanoidPoseNetworkSync.MaxExtrapolationFactor for the reasoning - same bound applied here.
        private const float MaxExtrapolationFactor = 3f;

        // Untracked here only ever means a genuine live tracking dropout (see HandPoseApplier.ApplyInterpolated's
        // graceSeconds doc) - never an intentional hide - so never give up on it: keep showing the last known
        // pose (settling at a small clamped drift offset, not sliding away) for as long as tracking stays lost,
        // and resolve straight back to live tracking the moment it returns.
        private const float TrackingGraceSeconds = Mathf.Infinity;

        [Tooltip("The LeapProvider to read local tracking data from on the owner (e.g. the LeapServiceProvider " +
                 "on 'Service Provider Desktop'). Unused on non-owners.")]
        [SerializeField] private LeapProvider localLeapProvider;

        [Tooltip("The left/right hand models under GhostHands (e.g. the HandBinder components) that should be " +
                 "driven from network data on non-owners, and left alone (owner's own LeapProvider keeps " +
                 "driving them) on the owner.")]
        [SerializeField] private HandModelBase leftHandModel;
        [SerializeField] private HandModelBase rightHandModel;

        // Exposed for RecordManager: it samples these models' current Leap.Hand via GetLeapHand() rather than
        // reading localLeapProvider directly, so recording works the same way regardless of whether it runs
        // on this PC user's own machine (owner, live tracking) or anywhere else in the session (non-owner,
        // already-decoded network data) - either way these are the models actually showing this PC user's hands.
        public HandModelBase LeftHandModel => leftHandModel;
        public HandModelBase RightHandModel => rightHandModel;

        // Byte arrays must never be null when this gets serialized - Netcode's array writer has no
        // null-check, same gotcha as HumanoidPoseNetworkSync.Muscles. Built in Awake(), not a field
        // initializer, to match that file's convention.
        private NetworkVariable<LeapHandsData> _networkedHands;

        // Reused across frames so encoding/decoding doesn't allocate every send/receive.
        private readonly VectorHand _leftVectorHand = new VectorHand();
        private readonly VectorHand _rightVectorHand = new VectorHand();
        private readonly Hand _leftHandBuffer = new Hand();
        private readonly Hand _rightHandBuffer = new Hand();

        private float _timeSinceLastSend;

        // Non-owner side only: NetworkVariable has no built-in interpolation, so without this the hand would
        // snap to each newly-decoded pose the instant it arrives - see HumanoidPoseNetworkSync for the same
        // reasoning applied to body pose. Each side keeps the last two DECODED hands (not raw bytes - VectorHand's
        // encoding is quantized/compressed, not something that can be blended directly) and Lerps/Slerps
        // between them; _renderLeftHand/_renderRightHand are the blended result actually fed to the hand models.
        private readonly Hand _previousLeftHand = new Hand();
        private readonly Hand _targetLeftHand = new Hand();
        private readonly Hand _renderLeftHand = new Hand();
        private readonly Hand _previousRightHand = new Hand();
        private readonly Hand _targetRightHand = new Hand();
        private readonly Hand _renderRightHand = new Hand();
        private bool _previousLeftTracked;
        private bool _targetLeftTracked;
        private bool _previousRightTracked;
        private bool _targetRightTracked;
        private float _interpolationStartTime;
        private bool _hasReceivedFirstHands;

        // Time.time of the last true->false transition of _targetLeftTracked/_targetRightTracked - stays
        // NegativeInfinity until tracking is actually lost once, so a hand that's simply never been tracked
        // yet still hides immediately in ApplyInterpolatedHands rather than getting an undeserved grace
        // period. See HandPoseApplier.ApplyInterpolated for how this is used.
        private float _leftLostTrackingTime = float.NegativeInfinity;
        private float _rightLostTrackingTime = float.NegativeInfinity;

        private void Awake()
        {
            _networkedHands = new NetworkVariable<LeapHandsData>(
                new LeapHandsData
                {
                    LeftHandBytes = new byte[VectorHand.NUM_BYTES],
                    RightHandBytes = new byte[VectorHand.NUM_BYTES],
                },
                writePerm: NetworkVariableWritePermission.Owner);
        }

        public override void OnNetworkSpawn()
        {
            if (!IsOwner)
            {
                // The remote copy of Service Provider Desktop has no physical Leap device to talk to on this
                // machine (e.g. it's just noise on a Quest, spamming "Leap Service not connected; attempting
                // to reconnect" every few seconds forever) - it was never providing anything to a non-owner
                // copy anyway, since the two lines below already disconnect the hand models from it.
                if (localLeapProvider != null) localLeapProvider.enabled = false;

                // A remote copy must not let its own (device-less) LeapProvider drive these hand models -
                // left enabled, it would call SetLeapHand(null) every frame and fight the pose applied below.
                if (leftHandModel != null) leftHandModel.leapProvider = null;
                if (rightHandModel != null) rightHandModel.leapProvider = null;

                // HandEnableDisable (sibling of HandBinder/CapsuleHand) starts every hand disabled and only
                // re-enables the GameObject via its OWN LeapProvider's OnHandFound event - a separate
                // provider reference from the one above, and one that never fires on a remote client with no
                // physical device. Left alone, the hand GameObject would stay inactive forever regardless of
                // the pose ApplyHand feeds into HandBinder. Disable it and take over SetActive ourselves.
                DisableHandEnableDisable(leftHandModel);
                DisableHandEnableDisable(rightHandModel);

                _networkedHands.OnValueChanged += (_, newValue) => OnHandsReceived(newValue);
                ApplyHands(_networkedHands.Value);
            }
            // If IsOwner, there's nothing else to do here - the owner's own LeapProvider keeps driving
            // these hand models locally exactly as it would without networking.
        }

        private static void DisableHandEnableDisable(HandModelBase model)
        {
            if (model == null) return;
            var handEnableDisable = model.GetComponent<HandEnableDisable>();
            if (handEnableDisable == null) handEnableDisable = model.GetComponentInParent<HandEnableDisable>();
            if (handEnableDisable != null) handEnableDisable.enabled = false;
        }

        private void LateUpdate()
        {
            if (IsOwner)
            {
                SendHandsIfDue();
            }
            else
            {
                ApplyInterpolatedHands();
            }
        }

        private void SendHandsIfDue()
        {
            if (localLeapProvider == null) return;

            _timeSinceLastSend += Time.deltaTime;
            if (_timeSinceLastSend < SendInterval) return;
            _timeSinceLastSend -= SendInterval;

            var left = localLeapProvider.GetHand(Chirality.Left);
            var right = localLeapProvider.GetHand(Chirality.Right);

            var data = new LeapHandsData
            {
                LeftTracked = left != null,
                RightTracked = right != null,
                // New arrays each send, same reason as HumanoidPoseNetworkSync.Muscles.Clone() - the
                // NetworkVariable's dirty check compares references, so reusing a buffer here would mean
                // the value never appears to change past the first send.
                LeftHandBytes = new byte[VectorHand.NUM_BYTES],
                RightHandBytes = new byte[VectorHand.NUM_BYTES],
            };

            if (left != null)
            {
                _leftVectorHand.Encode(left);
                _leftVectorHand.FillBytes(data.LeftHandBytes);
            }
            if (right != null)
            {
                _rightVectorHand.Encode(right);
                _rightVectorHand.FillBytes(data.RightHandBytes);
            }

            _networkedHands.Value = data;
        }

        // Initial snap on spawn only - nothing to interpolate from yet, and _hasReceivedFirstHands stays
        // false until OnHandsReceived fires for the first real update, so ApplyInterpolatedHands leaves
        // this alone until then.
        private void ApplyHands(LeapHandsData data)
        {
            HandPoseApplier.ApplySnapshot(leftHandModel, _leftVectorHand, _leftHandBuffer, data.LeftTracked, data.LeftHandBytes);
            HandPoseApplier.ApplySnapshot(rightHandModel, _rightVectorHand, _rightHandBuffer, data.RightTracked, data.RightHandBytes);
        }

        private void OnHandsReceived(LeapHandsData data)
        {
            // Shift target -> previous before decoding the new target in place, so interpolation always
            // blends from whatever was last actually shown rather than the pose before that.
            _previousLeftTracked = _targetLeftTracked;
            _previousRightTracked = _targetRightTracked;
            if (_targetLeftTracked) _previousLeftHand.CopyFrom(_targetLeftHand);
            if (_targetRightTracked) _previousRightHand.CopyFrom(_targetRightHand);

            if (_previousLeftTracked && !data.LeftTracked) _leftLostTrackingTime = Time.time;
            if (_previousRightTracked && !data.RightTracked) _rightLostTrackingTime = Time.time;

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

        private void ApplyInterpolatedHands()
        {
            if (!_hasReceivedFirstHands) return;

            // Unclamped past t=1 - see HumanoidPoseNetworkSync.ApplyInterpolatedPose for why (predicts
            // forward on a late update instead of freezing dead on target and visibly catching up).
            var t = Mathf.Clamp((Time.time - _interpolationStartTime) / SendInterval, 0f, MaxExtrapolationFactor);
            HandPoseApplier.ApplyInterpolated(leftHandModel, _previousLeftHand, _targetLeftHand, _renderLeftHand,
                                               _previousLeftTracked, _targetLeftTracked, t, Time.time - _leftLostTrackingTime,
                                               TrackingGraceSeconds);
            HandPoseApplier.ApplyInterpolated(rightHandModel, _previousRightHand, _targetRightHand, _renderRightHand,
                                               _previousRightTracked, _targetRightTracked, t, Time.time - _rightLostTrackingTime,
                                               TrackingGraceSeconds);
        }
    }
}
