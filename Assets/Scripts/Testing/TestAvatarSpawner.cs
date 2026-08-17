using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.Testing
{
    /// <summary>
    /// Minimal, isolated test harness: spawns the NetworkedVRUser avatar prefab for each connecting client,
    /// with none of SessionManager's role-detection, scene-loading, colocation, Passthrough, or OSC logic.
    /// Used to test whether the avatar/body-tracking system (RetargetingLayer + OVRBody +
    /// HumanoidPoseNetworkSync) alone reproduces the frame-rate collapse, isolated from everything else the
    /// full project layers on top of a bare Camera Rig + networking setup.
    /// </summary>
    public class TestAvatarSpawner : MonoBehaviour
    {
        [SerializeField] private GameObject vrUserPrefab;

        private void Start()
        {
            NetworkManager.Singleton.OnClientConnectedCallback += SpawnAvatarForConnectingClient;
        }

        private void OnDestroy()
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= SpawnAvatarForConnectingClient;
            }
        }

        private void SpawnAvatarForConnectingClient(ulong clientId)
        {
            if (!NetworkManager.Singleton.IsServer || vrUserPrefab == null) return;

            var instance = Instantiate(vrUserPrefab);
            // destroyWithScene: true, matching SessionManager's own spawn pattern - this is a runtime test
            // object, not something that should persist across a scene reload.
            instance.GetComponent<NetworkObject>().SpawnWithOwnership(clientId, destroyWithScene: true);
        }
    }
}
