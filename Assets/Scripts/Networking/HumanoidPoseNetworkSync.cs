using System;
using Oculus.Movement.AnimationRigging;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace MetaColocationDemos.Networking
{
    /// <summary>
    /// Networks the humanoid body pose that OVRBody + RetargetingLayer (Movement SDK) drive locally on the
    /// owning client - the bone-level motion (Hips, Spine, hands, etc.), not the rig's container position.
    /// Container position/rotation is a separate concern, replicated by NetworkTransform and driven by
    /// LocalCameraRigAnchor (owner-only) - see that class for why it has to be network-synced from the
    /// owner rather than computed locally by every viewer.
    ///
    /// The owner captures its Animator's HumanPose every frame and writes it to a NetworkVariable; every
    /// other client applies the received pose directly to its own copy's Animator instead of running its
    /// own local body tracking, which would otherwise overwrite the owner's pose with the viewer's own body.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class HumanoidPoseNetworkSync : NetworkBehaviour
    {
        // Body movement doesn't need a fresh sample every render frame (72-90Hz on Quest) to look smooth -
        // sending this often just burns CPU/GC on repeated NetworkVariable writes for no visible benefit.
        private const float SendRateHz = 30f;
        private const float SendInterval = 1f / SendRateHz;

        private float _timeSinceLastSend;

        /// <summary>
        /// Fires on every machine, once per avatar, whenever any NetworkedVRUser instance spawns/despawns -
        /// not just the local client's own. NetworkedVRUser is spawned per connecting client at runtime (see
        /// SessionManager), so there's no fixed in-scene object a host-side manager (e.g.
        /// JointCubeOscManager) can reference in the Inspector to discover every connected player's avatar -
        /// this is how it finds them instead. Consumers that only act on the server should check
        /// NetworkManager.Singleton.IsServer themselves, since these fire everywhere a copy exists.
        /// </summary>
        public static event Action<HumanoidPoseNetworkSync> AvatarSpawned;
        public static event Action<HumanoidPoseNetworkSync> AvatarDespawned;

        private struct HumanoidPoseData : INetworkSerializable
        {
            public Vector3 BodyPosition;
            public Quaternion BodyRotation;
            public float[] Muscles;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref BodyPosition);
                serializer.SerializeValue(ref BodyRotation);
                serializer.SerializeValue(ref Muscles);
            }
        }

        // Muscles must never be null when this gets serialized - Netcode's array writer has no null-check
        // and crashes, which happens the moment this is spawned: the default value (struct fields all
        // zeroed/null) is what gets sent for the initial state sync, before the owner's first LateUpdate
        // has had a chance to write a real pose into it. Built in Awake(), not a field initializer -
        // HumanTrait.MuscleCount calls into native engine code, which Unity disallows during construction.
        private NetworkVariable<HumanoidPoseData> _networkedPose;

        private Animator _animator;
        private HumanPoseHandler _poseHandler;
        private HumanPose _humanPose;

        private void Awake()
        {
            // TEMP DIAGNOSTIC: wall-clock timestamp (matches adb logcat's HH:mm:ss.fff format) for exactly
            // when this avatar's GameObject construction reaches this component - Awake() runs synchronously
            // as part of Instantiate(), on every device (the spawning server AND every receiving client), so
            // this pinpoints avatar-instantiation timing precisely enough to correlate against a frame-rate
            // drop seen in a VrApi logcat capture. Remove once the frame-rate collapse is root-caused.
            Debug.Log($"[PERF] {nameof(HumanoidPoseNetworkSync)}: Awake at {DateTime.Now:HH:mm:ss.fff} for {name}.");

            _networkedPose = new NetworkVariable<HumanoidPoseData>(
                new HumanoidPoseData { Muscles = new float[HumanTrait.MuscleCount] },
                writePerm: NetworkVariableWritePermission.Owner);
        }

        public override void OnNetworkSpawn()
        {
            // TEMP DIAGNOSTIC: see the note on Awake() above.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Debug.Log($"[PERF] {nameof(HumanoidPoseNetworkSync)}: OnNetworkSpawn starting at {DateTime.Now:HH:mm:ss.fff} " +
                      $"for {name} (IsOwner: {IsOwner}).");

            _animator = GetComponentInChildren<Animator>(true);
            if (_animator == null || !_animator.isHuman)
            {
                Debug.LogWarning($"{nameof(HumanoidPoseNetworkSync)}: no humanoid Animator found under " +
                                  $"{name}, so there's no pose to sync.");
                enabled = false;

                // TEMP DIAGNOSTIC: see the note above OnNetworkSpawn's stopwatch.
                stopwatch.Stop();
                Debug.Log($"[PERF] {nameof(HumanoidPoseNetworkSync)}: OnNetworkSpawn bailed early (no humanoid " +
                          $"Animator) for {name} after {stopwatch.ElapsedMilliseconds}ms.");
                return;
            }

            // TEMP DIAGNOSTIC: disables all rendering for this avatar, on every device (owner and remote
            // copies alike), to isolate whether the frame-rate collapse seen shortly after connecting is a
            // render/shader-compile cost (would disappear with rendering off) or a CPU/logic cost (would
            // persist regardless, since retargeting/animation/networking below still run identically either
            // way). Remove once the frame-rate collapse is root-caused.
            foreach (var avatarRenderer in GetComponentsInChildren<Renderer>(true)) avatarRenderer.enabled = false;
            Debug.Log($"[PERF] {nameof(HumanoidPoseNetworkSync)}: disabled rendering for {name}.");

            _poseHandler = new HumanPoseHandler(_animator.avatar, _animator.transform);

            if (!IsOwner)
            {
                // A remote copy must not run its own local body tracking - left enabled, OVRBody would pull
                // *this viewer's own* body pose instead of the owner's, fighting the pose we apply below.
                foreach (var ovrBody in GetComponentsInChildren<OVRBody>(true)) ovrBody.enabled = false;
                foreach (var rigBuilder in GetComponentsInChildren<RigBuilder>(true)) rigBuilder.enabled = false;
                foreach (var retargetingLayer in GetComponentsInChildren<RetargetingLayer>(true)) retargetingLayer.enabled = false;

                // Disabling RigBuilder tears down its PlayableGraph, which leaves the Animator's own
                // Controller running standalone again - it keeps evaluating every frame and silently
                // overwrites whatever SetHumanPose just wrote, freezing the mesh at the Controller's current
                // state pose. HumanPoseHandler doesn't need Animator to be enabled (it drives the Avatar's
                // bone mapping directly via the Transform hierarchy), so disabling it here is safe.
                _animator.enabled = false;

                _networkedPose.OnValueChanged += (_, newPose) => ApplyPose(newPose);
                ApplyPose(_networkedPose.Value);
            }
            // If IsOwner, there's nothing else to do here - the owner's own OVRBody/RigBuilder/RetargetingLayer
            // keep driving the Animator locally exactly as they would for a non-networked avatar.

            AvatarSpawned?.Invoke(this);

            // TEMP DIAGNOSTIC: see the note on Awake() above.
            stopwatch.Stop();
            Debug.Log($"[PERF] {nameof(HumanoidPoseNetworkSync)}: OnNetworkSpawn finished for {name} in " +
                      $"{stopwatch.ElapsedMilliseconds}ms (ended {DateTime.Now:HH:mm:ss.fff}).");
        }

        public override void OnNetworkDespawn()
        {
            _poseHandler = null;
            AvatarDespawned?.Invoke(this);
        }

        private void LateUpdate()
        {
            if (!IsOwner || _poseHandler == null) return;

            _timeSinceLastSend += Time.deltaTime;
            if (_timeSinceLastSend < SendInterval) return;
            _timeSinceLastSend -= SendInterval;

            // LateUpdate so this runs after RigBuilder (Animation Rigging evaluates in LateUpdate) has
            // applied this frame's retargeted body-tracking pose to the Animator.
            _poseHandler.GetHumanPose(ref _humanPose);
            _networkedPose.Value = new HumanoidPoseData
            {
                BodyPosition = _humanPose.bodyPosition,
                BodyRotation = _humanPose.bodyRotation,
                // GetHumanPose reuses the same backing array in place, so this must be cloned - otherwise
                // the NetworkVariable's dirty check sees the same array reference every frame and never
                // considers the value changed, and the pose never actually gets sent past the first frame.
                Muscles = (float[])_humanPose.muscles.Clone(),
            };
        }

        private void ApplyPose(HumanoidPoseData pose)
        {
            if (_poseHandler == null || pose.Muscles == null || pose.Muscles.Length != HumanTrait.MuscleCount) return;

            _humanPose.bodyPosition = pose.BodyPosition;
            _humanPose.bodyRotation = pose.BodyRotation;
            _humanPose.muscles = pose.Muscles;
            _poseHandler.SetHumanPose(ref _humanPose);
        }
    }
}
