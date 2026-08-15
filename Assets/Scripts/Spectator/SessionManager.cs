using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MetaColocationDemos.Spectator
{
    /// <summary>
    /// Detects whether the local client is a VR user or a non-VR spectator (e.g. a PC running in the
    /// Editor), loads that role's rig scene, and owns connection approval and per-client avatar spawning for
    /// the whole session - VR and spectator alike. Also tracks which connected clients are spectators so
    /// <see cref="HiddenFromSpectators"/> objects know who to hide from.
    ///
    /// Runs in Start() rather than Awake(): NetworkManager.Singleton is only set inside NetworkManager's
    /// own Awake(), and Awake-vs-Awake ordering between objects isn't reliably guaranteed. All Awake()
    /// calls in the scene are guaranteed to finish before any Start() runs, and AutoMatchmakingNGO only
    /// calls StartClient()/StartHost() after several awaited async calls, so Start() has plenty of margin.
    ///
    /// The VR rig (Colocation_VR_Rig) and PC rig (Colocation_PC_Rig) live in their own scenes, additively
    /// loaded locally here based on role - only ever one or the other, never both, so there's no more need
    /// to toggle a shared rig's visibility. Environment (Directional Light, world geometry) has no
    /// role-specific content, so every client loads it unconditionally. All of these loads are plain local
    /// SceneManager calls rather than NetworkManager.SceneManager, so none of them are synced to other
    /// connected clients.
    /// </summary>
    public class SessionManager : MonoBehaviour
    {
        private const string EnvironmentSceneName = "Environment";
        private const string VrRigSceneName = "Colocation_VR_Rig";
        private const string PcRigSceneName = "Colocation_PC_Rig";

        // Meta Quest Link has to negotiate a session with the desktop Oculus runtime before the display
        // subsystem comes up, unlike a real on-device build where XRSettings.isDeviceActive is already
        // true at boot - a single instantaneous read here can catch it mid-negotiation and misdetect a VR
        // client as a spectator. That misdetection used to just flip a SetActive toggle in an otherwise
        // identical shared scene; now it decides which whole rig scene loads, so it's worth a short poll
        // instead of trusting one read.
        private const float XrDeviceDetectionTimeoutSeconds = 3f;

        private static readonly byte[] SpectatorPayload = { 1 };

        [Tooltip("Force this client to connect as a spectator, regardless of auto-detection. " +
                 "Leave off to auto-detect based on whether an XR device is active.")]
        [SerializeField] private bool forceSpectatorMode;

        [Tooltip("The NetworkedVRUser prefab (registered in DefaultNetworkPrefabs) to spawn for each " +
                 "connecting VR client. Bone-level pose is replicated via HumanoidPoseNetworkSync; the " +
                 "container's own position/rotation is replicated by NetworkTransform, sourced from the " +
                 "owner's LocalCameraRigAnchor so it includes that owner's colocation alignment.")]
        [SerializeField] private GameObject vrUserPrefab;

        [Tooltip("The NetworkedPCUser prefab (registered in DefaultNetworkPrefabs) to spawn for each " +
                 "connecting spectator client. SpectatorFlyCamera (on the prefab) drives its own transform " +
                 "directly from local input, replicated via OwnerNetworkTransform; LocalSpectatorCameraAnchor " +
                 "attaches this client's local Main Camera onto it once it spawns as this client's own instance.")]
        [SerializeField] private GameObject pcUserPrefab;

        public static readonly HashSet<ulong> SpectatorClientIds = new();

        public static bool IsLocalClientSpectator { get; private set; }

        private IEnumerator Start()
        {
            if (!forceSpectatorMode)
            {
                yield return WaitForXrDeviceActivation();
            }

            IsLocalClientSpectator = forceSpectatorMode || !UnityEngine.XR.XRSettings.isDeviceActive;

            // No role-specific content, so every client (VR or spectator) loads it the same way.
            SceneManager.LoadSceneAsync(EnvironmentSceneName, LoadSceneMode.Additive);

            // Loaded locally so NetworkedPCUser's LocalSpectatorCameraAnchor has a Main Camera to attach once
            // it spawns as this client's own instance (see HandOwnershipToConnectingClient below). The
            // network connection - and therefore the earliest that spawn could happen - is still several
            // awaited async calls away in AutoMatchmakingNGO, so this tiny scene has plenty of time to load.
            var rigSceneName = IsLocalClientSpectator ? PcRigSceneName : VrRigSceneName;
            SceneManager.LoadSceneAsync(rigSceneName, LoadSceneMode.Additive);

            if (IsLocalClientSpectator)
            {
                Debug.Log($"{nameof(SessionManager)}: connecting as spectator (no XR device detected).");
                // Read by ApprovalCheck on the server before any networked objects become visible to this client.
                NetworkManager.Singleton.NetworkConfig.ConnectionData = SpectatorPayload;
            }

            // ConnectionApproval must be explicitly enabled, otherwise the callback below is never invoked
            // even though it's assigned - NGO just silently skips it.
            NetworkManager.Singleton.NetworkConfig.ConnectionApproval = true;
            // AutoMatchmakingNGO doesn't set an approval callback itself, so this is safe to assign directly.
            NetworkManager.Singleton.ConnectionApprovalCallback = ApprovalCheck;
            NetworkManager.Singleton.OnClientConnectedCallback += HandOwnershipToConnectingClient;
        }

        private static IEnumerator WaitForXrDeviceActivation()
        {
            var deadline = Time.realtimeSinceStartup + XrDeviceDetectionTimeoutSeconds;
            while (!UnityEngine.XR.XRSettings.isDeviceActive && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
        }

        private void OnDestroy()
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= HandOwnershipToConnectingClient;
            }
        }

        /// <summary>
        /// Spawns a fresh rig instance owned by the connecting client, rather than something pre-placed in
        /// any scene - a spectator client gets its own NetworkedPCUser exactly the way a VR client already
        /// gets its own NetworkedVRUser, so there's no single shared rig to hand off, and nothing sitting
        /// around driven by nobody before a matching client actually connects.
        /// </summary>
        private void HandOwnershipToConnectingClient(ulong clientId)
        {
            // Unconditional and role-agnostic, unlike everything below it: fires on every client (server or
            // not) whenever ANY client connects, purely so a device log confirms this client is in a session
            // at all - the logic below only ever logs on whichever side happens to be server.
            Debug.Log($"{nameof(SessionManager)}: OnClientConnectedCallback fired for client {clientId} " +
                      $"(local IsServer: {NetworkManager.Singleton.IsServer}, LocalClientId: {NetworkManager.Singleton.LocalClientId}).");

            if (!NetworkManager.Singleton.IsServer) return;

            var isSpectator = SpectatorClientIds.Contains(clientId);
            var prefab = isSpectator ? pcUserPrefab : vrUserPrefab;
            if (prefab == null)
            {
                Debug.LogWarning($"{nameof(SessionManager)}: {(isSpectator ? nameof(pcUserPrefab) : nameof(vrUserPrefab))} " +
                                  $"isn't set, so no rig can be spawned for the connecting {(isSpectator ? "spectator" : "VR")} client.");
                return;
            }

            var instance = Instantiate(prefab);
            // destroyWithScene: true, since this is a runtime-spawned session object, not something that
            // should persist across a scene reload the way DontDestroyOnLoad objects would.
            instance.GetComponent<NetworkObject>().SpawnWithOwnership(clientId, destroyWithScene: true);
            Debug.Log($"{nameof(SessionManager)}: spawned {prefab.name} owned by client {clientId}.");
        }

        private void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            var isSpectator = request.Payload is { Length: > 0 } && request.Payload[0] == SpectatorPayload[0];
            if (isSpectator)
            {
                SpectatorClientIds.Add(request.ClientNetworkId);
            }

            response.Approved = true;
            response.CreatePlayerObject = false; // spectators don't need a networked player/avatar object of their own
        }
    }
}
