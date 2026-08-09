using System.Collections.Generic;
using extOSC;
using MetaColocationDemos.Networking;
using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.OSC
{
    /// <summary>
    /// Host-only: spawns a set of networked JointCubes per body joint for every connected VR wearer's
    /// avatar (not just one fixed player), keeps each set synced to its avatar's joints, and streams every
    /// player's pose out over OSC as avatars connect and disconnect over the session.
    ///
    /// Netcode only allows the server to spawn NetworkObjects, so this only ever does anything on the host -
    /// clients ignore HumanoidPoseNetworkSync's avatar spawn/despawn notifications entirely. Since the host
    /// itself owns every JointCube it spawns (regardless of which client's joints it tracks), no ownership
    /// handoff is needed - the host is always the one calling SetPositionAndRotation on them.
    /// </summary>
    public class JointCubeOscManager : MonoBehaviour
    {
        private const string RootJointName = "Root";
        private const string HeadJointName = "Head";
        private const string LeftWristJointName = "Wrist_L";
        private const string RightWristJointName = "Wrist_R";

        [Tooltip("Sends the joint OSC messages - lives on the OSCMessaging object; wired here rather than " +
                 "required on this GameObject so OSC transport config stays separate from spawn coordination.")]
        [SerializeField] private OSCTransmitter transmitter;

        [Tooltip("The JointCube prefab (Assets/Prefabs/OSC/JointCube.prefab).")]
        [SerializeField] private GameObject jointCubePrefab;

        [Tooltip("Optional - shows every connected player's current OSC joint values live in its own " +
                 "Inspector, for debugging. Leave unassigned to skip building the snapshot entirely.")]
        [SerializeField] private JointOscDebugView debugView;

        private class JointBinding
        {
            public string Name;
            public Transform Source;
            public Transform Cube;
        }

        private class AvatarRig
        {
            public ulong OwnerClientId;
            public Transform AvatarRoot;
            public readonly List<JointBinding> Bindings = new();
        }

        private readonly Dictionary<ulong, AvatarRig> _rigsByOwnerClientId = new();

        private void OnEnable()
        {
            HumanoidPoseNetworkSync.AvatarSpawned += OnAvatarSpawned;
            HumanoidPoseNetworkSync.AvatarDespawned += OnAvatarDespawned;
        }

        private void OnDisable()
        {
            HumanoidPoseNetworkSync.AvatarSpawned -= OnAvatarSpawned;
            HumanoidPoseNetworkSync.AvatarDespawned -= OnAvatarDespawned;
        }

        private void LateUpdate()
        {
            // Runs after Animation Rigging/pose-apply updates every avatar's joints for this frame.
            var snapshot = debugView != null ? new List<JointOscDebugView.PlayerValues>() : null;

            foreach (var rig in _rigsByOwnerClientId.Values)
            {
                var playerValues = DriveJointCubes(rig);
                snapshot?.Add(playerValues);
            }

            if (debugView != null) debugView.SetSnapshot(snapshot);
        }

        private void OnAvatarSpawned(HumanoidPoseNetworkSync avatar)
        {
            if (!NetworkManager.Singleton.IsServer) return;

            if (jointCubePrefab == null || transmitter == null)
            {
                Debug.LogError($"{nameof(JointCubeOscManager)}: jointCubePrefab/transmitter not assigned in the Inspector.");
                return;
            }

            SpawnJointCubes(avatar);
        }

        private void OnAvatarDespawned(HumanoidPoseNetworkSync avatar)
        {
            if (!NetworkManager.Singleton.IsServer) return;
            if (!_rigsByOwnerClientId.TryGetValue(avatar.OwnerClientId, out var rig)) return;

            _rigsByOwnerClientId.Remove(avatar.OwnerClientId);

            foreach (var binding in rig.Bindings)
            {
                if (binding.Cube == null) continue;

                var cubeNetworkObject = binding.Cube.GetComponent<NetworkObject>();
                if (cubeNetworkObject != null && cubeNetworkObject.IsSpawned) cubeNetworkObject.Despawn();
            }
        }

        private void SpawnJointCubes(HumanoidPoseNetworkSync avatar)
        {
            var avatarRoot = avatar.transform;
            var ownerClientId = avatar.OwnerClientId;

            // Read straight off the Animator's own humanoid bones, not the Animation Rigging IK-target
            // transforms (e.g. HipsTarget/HeadTarget) - those only get updated while RigBuilder is running,
            // which HumanoidPoseNetworkSync disables on non-owner copies (driving the mesh purely via
            // SetHumanPose instead). The bone transforms are what SetHumanPose actually writes to, so
            // they're correct regardless of whether the avatar we're reading is locally owned or not.
            var animator = avatarRoot.GetComponentInChildren<Animator>();
            if (animator == null)
            {
                Debug.LogError($"{nameof(JointCubeOscManager)}: no Animator found under {avatarRoot.name}, can't resolve bones.");
                return;
            }

            var joints = new (string Name, Transform Source)[]
            {
                (RootJointName, animator.GetBoneTransform(HumanBodyBones.Hips)),
                (HeadJointName, animator.GetBoneTransform(HumanBodyBones.Head)),
                (LeftWristJointName, animator.GetBoneTransform(HumanBodyBones.LeftHand)),
                (RightWristJointName, animator.GetBoneTransform(HumanBodyBones.RightHand)),
            };

            var rig = new AvatarRig { OwnerClientId = ownerClientId, AvatarRoot = avatarRoot };

            foreach (var (jointName, source) in joints)
            {
                if (source == null)
                {
                    Debug.LogWarning($"{nameof(JointCubeOscManager)}: couldn't resolve joint '{jointName}' " +
                                      $"for client {ownerClientId}, skipping.");
                    continue;
                }

                var initialPosition = jointName == RootJointName ? ProjectToFloor(avatarRoot, source.position) : source.position;

                var cubeInstance = Instantiate(jointCubePrefab, initialPosition, source.rotation, transform);
                cubeInstance.name = $"JointCube_Player{ownerClientId}_{jointName}";

                cubeInstance.GetComponent<NetworkObject>().Spawn();

                rig.Bindings.Add(new JointBinding { Name = jointName, Source = source, Cube = cubeInstance.transform });
            }

            _rigsByOwnerClientId[ownerClientId] = rig;
        }

        private JointOscDebugView.PlayerValues DriveJointCubes(AvatarRig rig)
        {
            var playerValues = new JointOscDebugView.PlayerValues { OwnerClientId = rig.OwnerClientId };

            foreach (var binding in rig.Bindings)
            {
                if (binding.Cube == null || binding.Source == null) continue;

                var position = binding.Name == RootJointName
                    ? ProjectToFloor(rig.AvatarRoot, binding.Source.position)
                    : binding.Source.position;
                var rotation = binding.Source.rotation;

                binding.Cube.SetPositionAndRotation(position, rotation);

                playerValues.Joints.Add(SendJoint(rig.OwnerClientId, binding.Name, position, rotation));
            }

            return playerValues;
        }

        /// <summary>
        /// Flattens a world position onto the avatar root's local Y=0 plane - the floor, since
        /// NetworkedVRUser sits at the same floor-level origin as the Camera Rig (Floor tracking origin).
        /// </summary>
        private static Vector3 ProjectToFloor(Transform root, Vector3 worldPosition)
        {
            var local = root.InverseTransformPoint(worldPosition);
            local.y = 0f;
            return root.TransformPoint(local);
        }

        private JointOscDebugView.JointValue SendJoint(ulong ownerClientId, string jointName, Vector3 position, Quaternion rotation)
        {
            var baseAddress = $"/Player_{ownerClientId}_Joint_{jointName}";
            var positionAddress = $"{baseAddress}/position";
            var rotationAddress = $"{baseAddress}/rotation";

            transmitter.Send(OSCMessage.Create(positionAddress,
                OSCValue.Float(position.x), OSCValue.Float(position.y), OSCValue.Float(position.z)));

            transmitter.Send(OSCMessage.Create(rotationAddress,
                OSCValue.Float(rotation.x), OSCValue.Float(rotation.y), OSCValue.Float(rotation.z), OSCValue.Float(rotation.w)));

            return new JointOscDebugView.JointValue
            {
                Joint = jointName,
                PositionAddress = positionAddress,
                Position = position,
                RotationAddress = rotationAddress,
                // Same order as the floats actually sent above - x, y, z, w.
                Rotation = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w),
            };
        }
    }
}
