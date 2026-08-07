using System.Collections.Generic;
using extOSC;
using MetaColocationDemos.Spectator;
using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.OSC
{
    /// <summary>
    /// Spawns one networked JointCube per body joint of the local VR wearer's Meta Movement avatar,
    /// keeps every cube's pose synced to its joint each frame, and streams the same pose out over OSC.
    ///
    /// Assumes the local VR wearer connects as the Netcode host (this demo's spectators join afterward
    /// as clients, see SpectatorModeManager). JointCube's NetworkTransform is owner-authoritative, and
    /// spawning on the host makes the host the default owner, so no ownership-handoff RPC is needed. If
    /// a build ever has the VR wearer join as a non-host client, this needs a ServerRpc to spawn and
    /// hand over ownership instead - see SpectatorModeManager.HandOwnershipToSpectator for that pattern.
    /// </summary>
    [RequireComponent(typeof(OSCTransmitter))]
    public class JointCubeOscRig : MonoBehaviour
    {
        private const string RootJointName = "Root";
        private const string HeadJointName = "Head";
        private const string LeftWristJointName = "Wrist_L";
        private const string RightWristJointName = "Wrist_R";

        // Target transform name on Meta Movement's ArmatureSkinningUpdateRetarget rig (ExampleAvatar Variant).
        private const string HipsTargetName = "HipsTarget";
        private const string HeadTargetName = "HeadTarget";

        [Tooltip("Root of the local body-tracked avatar to read joints from - NetworkedVRUser or its 'ExampleAvatar Variant' child.")]
        [SerializeField] private Transform avatarRoot;

        [Tooltip("The JointCube prefab (Assets/Prefabs/JointCube.prefab).")]
        [SerializeField] private GameObject jointCubePrefab;

        [Tooltip("Which player this rig represents - addresses are sent as /Player_<N>_Joint_<name>/position|rotation.")]
        [SerializeField] private int playerIndex = 1;

        private OSCTransmitter _transmitter;
        private readonly List<JointBinding> _bindings = new();
        private bool _initialized;
        private bool _isDriving;

        private class JointBinding
        {
            public string Name;
            public Transform Source;
            public Transform Cube;
        }

        private void Awake()
        {
            _transmitter = GetComponent<OSCTransmitter>();
        }

        private void Update()
        {
            if (!_initialized) TryInitialize();
        }

        private void LateUpdate()
        {
            // Runs after Animation Rigging updates the retargeted joint targets for this frame.
            if (_isDriving) DriveJointCubes();
        }

        private void TryInitialize()
        {
            var networkManager = NetworkManager.Singleton;
            if (networkManager == null || !networkManager.IsListening) return; // not connected yet, keep waiting

            _initialized = true;

            if (SpectatorModeManager.IsLocalClientSpectator)
            {
                Debug.Log($"{nameof(JointCubeOscRig)}: local client is a spectator, no body joints to broadcast.");
                return;
            }

            if (!networkManager.IsServer)
            {
                Debug.LogWarning($"{nameof(JointCubeOscRig)}: local VR client isn't the host, so it can't spawn " +
                                  "networked JointCubes (OwnerNetworkTransform needs the spawner to own them). " +
                                  "Skipping joint cube spawn/broadcast - see class comment.");
                return;
            }

            if (avatarRoot == null || jointCubePrefab == null)
            {
                Debug.LogError($"{nameof(JointCubeOscRig)}: avatarRoot/jointCubePrefab not assigned in the Inspector.");
                return;
            }

            SpawnJointCubes();
            _isDriving = true;
        }

        private void SpawnJointCubes()
        {
            // Wrists aren't in the retargeting rig's IK-target list (only Hips/Spine/Chest/Neck/Head/Feet/
            // Toes are), so pull them off the Humanoid Animator instead of the target-transform hierarchy.
            var animator = avatarRoot.GetComponentInChildren<Animator>();
            if (animator == null)
            {
                Debug.LogError($"{nameof(JointCubeOscRig)}: no Animator found under {avatarRoot.name}, can't resolve wrist bones.");
                return;
            }

            var joints = new (string Name, Transform Source)[]
            {
                (RootJointName, FindChildRecursive(avatarRoot, HipsTargetName)),
                (HeadJointName, FindChildRecursive(avatarRoot, HeadTargetName)),
                (LeftWristJointName, animator.GetBoneTransform(HumanBodyBones.LeftHand)),
                (RightWristJointName, animator.GetBoneTransform(HumanBodyBones.RightHand)),
            };

            foreach (var (jointName, source) in joints)
            {
                if (source == null)
                {
                    Debug.LogWarning($"{nameof(JointCubeOscRig)}: couldn't resolve joint '{jointName}', skipping.");
                    continue;
                }

                var initialPosition = jointName == RootJointName ? ProjectToFloor(avatarRoot, source.position) : source.position;

                var cubeInstance = Instantiate(jointCubePrefab, initialPosition, source.rotation, transform);
                cubeInstance.name = $"JointCube_{jointName}";

                cubeInstance.GetComponent<NetworkObject>().Spawn();

                _bindings.Add(new JointBinding { Name = jointName, Source = source, Cube = cubeInstance.transform });
            }
        }

        private void DriveJointCubes()
        {
            foreach (var binding in _bindings)
            {
                if (binding.Cube == null || binding.Source == null) continue;

                var position = binding.Name == RootJointName
                    ? ProjectToFloor(avatarRoot, binding.Source.position)
                    : binding.Source.position;

                binding.Cube.SetPositionAndRotation(position, binding.Source.rotation);

                SendJoint(binding.Name, position, binding.Source.rotation);
            }
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

        private void SendJoint(string jointName, Vector3 position, Quaternion rotation)
        {
            var address = $"/Player_{playerIndex}_Joint_{jointName}";

            _transmitter.Send(OSCMessage.Create($"{address}/position",
                OSCValue.Float(position.x), OSCValue.Float(position.y), OSCValue.Float(position.z)));

            _transmitter.Send(OSCMessage.Create($"{address}/rotation",
                OSCValue.Float(rotation.x), OSCValue.Float(rotation.y), OSCValue.Float(rotation.z), OSCValue.Float(rotation.w)));
        }

        private static Transform FindChildRecursive(Transform root, string childName)
        {
            foreach (Transform child in root)
            {
                if (child.name == childName) return child;

                var found = FindChildRecursive(child, childName);
                if (found != null) return found;
            }

            return null;
        }
    }
}
