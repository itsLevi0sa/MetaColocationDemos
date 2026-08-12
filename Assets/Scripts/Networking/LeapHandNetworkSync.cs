using Leap;
using Leap.Encoding;
using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.Networking
{
    /// <summary>
    /// Networks the Ultraleap "ghost hand" pose under NetworkedPCUser/Spectator_LeapMotion. The Leap
    /// device only exists on the owning spectator's machine, so every other client's local LeapProvider
    /// has no hands to report - without this, GhostHands only ever animates on the owner's own screen.
    ///
    /// The owner encodes its tracked hands with Ultraleap's VectorHand (an 86-byte-per-hand compressed
    /// encoding built for exactly this purpose) into a NetworkVariable; every other client decodes it and
    /// feeds it straight into the same HandBinder/CapsuleHand components that would otherwise be driven by
    /// a local LeapProvider - see leapProvider = null below for why the normal auto-driven path has to be
    /// turned off on non-owners first, same reasoning as HumanoidPoseNetworkSync disabling OVRBody.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class LeapHandNetworkSync : NetworkBehaviour
    {
        // Hand pose doesn't need a fresh sample every render frame to look smooth - see
        // HumanoidPoseNetworkSync for the same reasoning applied to body pose.
        private const float SendRateHz = 30f;
        private const float SendInterval = 1f / SendRateHz;

        [Tooltip("The LeapProvider to read local tracking data from on the owner (e.g. the LeapServiceProvider " +
                 "on 'Service Provider Desktop'). Unused on non-owners.")]
        [SerializeField] private LeapProvider localLeapProvider;

        [Tooltip("The left/right hand models under GhostHands (e.g. the HandBinder components) that should be " +
                 "driven from network data on non-owners, and left alone (owner's own LeapProvider keeps " +
                 "driving them) on the owner.")]
        [SerializeField] private HandModelBase leftHandModel;
        [SerializeField] private HandModelBase rightHandModel;

        private struct LeapHandsData : INetworkSerializable
        {
            public bool LeftTracked;
            public bool RightTracked;
            public byte[] LeftHandBytes;
            public byte[] RightHandBytes;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref LeftTracked);
                serializer.SerializeValue(ref RightTracked);
                serializer.SerializeValue(ref LeftHandBytes);
                serializer.SerializeValue(ref RightHandBytes);
            }
        }

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

        // TODO(debug): temporary tracked-state change logging - remove once ghost hands are confirmed working.
        private bool _loggedLeftTrackedOwner;
        private bool _loggedRightTrackedOwner;
        private bool _loggedLeftTrackedRemote;
        private bool _loggedRightTrackedRemote;

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
            // TODO(debug): temporary - remove once ghost hands are confirmed working on non-owners.
            Debug.Log($"{nameof(LeapHandNetworkSync)}: OnNetworkSpawn on '{gameObject.name}', IsOwner={IsOwner}, " +
                      $"localLeapProvider={(localLeapProvider != null ? localLeapProvider.name : "null")}, " +
                      $"leftHandModel={(leftHandModel != null ? leftHandModel.name : "null")}, " +
                      $"rightHandModel={(rightHandModel != null ? rightHandModel.name : "null")}.");

            if (!IsOwner)
            {
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

                _networkedHands.OnValueChanged += (_, newValue) => ApplyHands(newValue);
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
            if (!IsOwner || localLeapProvider == null) return;

            _timeSinceLastSend += Time.deltaTime;
            if (_timeSinceLastSend < SendInterval) return;
            _timeSinceLastSend -= SendInterval;

            var left = localLeapProvider.GetHand(Chirality.Left);
            var right = localLeapProvider.GetHand(Chirality.Right);

            // TODO(debug): temporary - remove once ghost hands are confirmed working on non-owners.
            if ((left != null) != _loggedLeftTrackedOwner)
            {
                _loggedLeftTrackedOwner = left != null;
                Debug.Log($"{nameof(LeapHandNetworkSync)} (owner): left hand tracked={_loggedLeftTrackedOwner} " +
                          $"(provider={localLeapProvider.name}).");
            }
            if ((right != null) != _loggedRightTrackedOwner)
            {
                _loggedRightTrackedOwner = right != null;
                Debug.Log($"{nameof(LeapHandNetworkSync)} (owner): right hand tracked={_loggedRightTrackedOwner} " +
                          $"(provider={localLeapProvider.name}).");
            }

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

        private void ApplyHands(LeapHandsData data)
        {
            // TODO(debug): temporary - remove once ghost hands are confirmed working on non-owners.
            if (data.LeftTracked != _loggedLeftTrackedRemote)
            {
                _loggedLeftTrackedRemote = data.LeftTracked;
                Debug.Log($"{nameof(LeapHandNetworkSync)} (remote): received left hand tracked={_loggedLeftTrackedRemote}.");
            }
            if (data.RightTracked != _loggedRightTrackedRemote)
            {
                _loggedRightTrackedRemote = data.RightTracked;
                Debug.Log($"{nameof(LeapHandNetworkSync)} (remote): received right hand tracked={_loggedRightTrackedRemote}.");
            }

            ApplyHand(leftHandModel, _leftVectorHand, _leftHandBuffer, data.LeftTracked, data.LeftHandBytes);
            ApplyHand(rightHandModel, _rightVectorHand, _rightHandBuffer, data.RightTracked, data.RightHandBytes);
        }

        private static void ApplyHand(HandModelBase model, VectorHand vectorHand, Hand handBuffer, bool tracked, byte[] bytes)
        {
            if (model == null) return;

            if (!tracked || bytes == null)
            {
                model.SetLeapHand(null);
                if (model.IsTracked) model.FinishHand();
                // HandEnableDisable is disabled on non-owners (see OnNetworkSpawn), so nothing else will
                // hide this hand when tracking is lost - do it ourselves.
                model.gameObject.SetActive(false);
                return;
            }

            // Same reasoning in reverse - with HandEnableDisable disabled, nothing else will show this hand
            // again once tracking resumes.
            model.gameObject.SetActive(true);

            vectorHand.ReadBytes(bytes);
            vectorHand.Decode(handBuffer);
            model.SetLeapHand(handBuffer);

            if (!model.IsTracked)
            {
                model.InitHand();
                model.BeginHand();
                // TODO(debug): temporary - remove once ghost hands are confirmed working on non-owners.
                Debug.Log($"{nameof(LeapHandNetworkSync)} (remote): applying '{model.name}', " +
                          $"activeInHierarchy={model.gameObject.activeInHierarchy}, palmPos={vectorHand.palmPos}, " +
                          $"renderers enabled={CountEnabledRenderers(model)}, " +
                          $"ancestorChain={DescribeAncestorActiveChain(model.transform)}.");
            }

            if (model.gameObject.activeInHierarchy) model.UpdateHandWithEvent();
        }

        // TODO(debug): temporary - remove once ghost hands are confirmed working on non-owners.
        private static int CountEnabledRenderers(HandModelBase model)
        {
            var count = 0;
            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.enabled) count++;
            }
            return count;
        }

        // TODO(debug): temporary - remove once ghost hands are confirmed working on non-owners.
        private static string DescribeAncestorActiveChain(Transform leaf)
        {
            var description = "";
            for (var t = leaf; t != null; t = t.parent)
            {
                description += $"{t.gameObject.name}(active={t.gameObject.activeSelf}) < ";
            }
            return description;
        }
    }
}
