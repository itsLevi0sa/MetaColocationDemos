using System.Collections.Generic;
using Leap;
using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.Spectator
{
    /// <summary>
    /// Marks the local client as a non-VR spectator (e.g. a PC running in the Editor) before it connects,
    /// and tracks which connected clients are spectators so <see cref="HiddenFromSpectators"/> objects
    /// know who to hide from.
    ///
    /// Runs in Start() rather than Awake(): NetworkManager.Singleton is only set inside NetworkManager's
    /// own Awake(), and Awake-vs-Awake ordering between objects isn't reliably guaranteed. All Awake()
    /// calls in the scene are guaranteed to finish before any Start() runs, and AutoMatchmakingNGO only
    /// calls StartClient()/StartHost() after several awaited async calls, so Start() has plenty of margin.
    /// </summary>
    public class SpectatorModeManager : MonoBehaviour
    {
        private static readonly byte[] SpectatorPayload = { 1 };

        [Tooltip("Force this client to connect as a spectator, regardless of auto-detection. " +
                 "Leave off to auto-detect based on whether an XR device is active.")]
        [SerializeField] private bool forceSpectatorMode;

        [Tooltip("When running as a ParrelSync clone, connect as a VR user (skip spectator auto-detection) " +
                 "even though no XR device is active - lets the original instance (with a real spectator " +
                 "device, e.g. a Leap Motion controller) and a clone act as two independent test clients " +
                 "without needing a headset on either. Overridden by forceSpectatorMode if that's also set.")]
        [SerializeField] private bool forceVrUserModeInParrelSyncClone = true;

        [Tooltip("The [BuildingBlock] Camera Rig object to disable when running as a spectator, " +
                 "so it doesn't compete with the spectator camera.")]
        [SerializeField] private GameObject vrCameraRig;

        [Tooltip("The camera (with SpectatorFlyCamera) to enable when running as a spectator.")]
        [SerializeField] private Camera spectatorCamera;

        [Tooltip("The LeapServiceProvider on 'Service Provider Desktop' (NetworkedPCUser), enabled only for " +
                 "the spectator. It defaults to disabled in the scene: on Android, LeapServiceProvider.OnEnable() " +
                 "calls AndroidServiceBinder.Bind() and Update() then retries connecting to a Leap service that " +
                 "will never exist on a Quest - leaving it enabled by default meant every VR client's build was " +
                 "paying for a doomed native service-bind + reconnect loop on every play session, every frame.")]
        [SerializeField] private LeapProvider spectatorLeapProvider;

        [Tooltip("The Ultraleap 'Physical Hands Manager' under NetworkedPCUser, enabled only for the " +
                 "spectator. It defaults to disabled in the scene: its OnEnable() starts a permanent " +
                 "per-FixedUpdate physics coroutine (hand-contact simulation) regardless of whether its " +
                 "input provider is actually enabled or connected, so leaving it active by default meant " +
                 "every VR client's build was running that hand-contact physics simulation for nobody's hands.")]
        [SerializeField] private Behaviour spectatorPhysicalHandsManager;

        [Tooltip("The NetworkObject on the spectator rig (e.g. NetworkedPCUser) that should be handed to " +
                 "the spectator client on connect, so their local movement is what gets replicated.")]
        [SerializeField] private NetworkObject spectatorNetworkObject;

        [Tooltip("The NetworkedVRUser prefab (registered in DefaultNetworkPrefabs) to spawn for each " +
                 "connecting VR client. Bone-level pose is replicated via HumanoidPoseNetworkSync; the " +
                 "container's own position/rotation is replicated by NetworkTransform, sourced from the " +
                 "owner's LocalCameraRigAnchor so it includes that owner's colocation alignment.")]
        [SerializeField] private GameObject vrUserPrefab;

        public static readonly HashSet<ulong> SpectatorClientIds = new();

        public static bool IsLocalClientSpectator { get; private set; }

        private void Start()
        {
            var forcedVrUser = !forceSpectatorMode && forceVrUserModeInParrelSyncClone && IsRunningAsParrelSyncClone();
            IsLocalClientSpectator = forceSpectatorMode || (!forcedVrUser && !UnityEngine.XR.XRSettings.isDeviceActive);

            if (IsLocalClientSpectator)
            {
                Debug.Log($"{nameof(SpectatorModeManager)}: connecting as spectator (no XR device detected).");
                // Read by ApprovalCheck on the server before any networked objects become visible to this client.
                NetworkManager.Singleton.NetworkConfig.ConnectionData = SpectatorPayload;

                if (vrCameraRig != null) vrCameraRig.SetActive(false);
                if (spectatorCamera != null) spectatorCamera.gameObject.SetActive(true);
                // Defaults disabled in the scene (see spectatorLeapProvider tooltip) - only the actual
                // spectator, who might have real Leap hardware, should ever turn this on.
                if (spectatorLeapProvider != null) spectatorLeapProvider.enabled = true;
                // Defaults disabled in the scene (see spectatorPhysicalHandsManager tooltip) - same reasoning.
                if (spectatorPhysicalHandsManager != null) spectatorPhysicalHandsManager.enabled = true;
            }
            else
            {
                if (forcedVrUser)
                {
                    Debug.Log($"{nameof(SpectatorModeManager)}: connecting as a VR user (ParrelSync clone override - " +
                              "no XR device detected, but forceVrUserModeInParrelSyncClone forced non-spectator).");
                }

                // NetworkedPCUser's Main Camera (+ AudioListener) defaults to active in the scene - spectator
                // mode turns it on above, but nothing previously turned it back off for non-spectators, so it
                // sat active alongside every VR user's own camera rig (duplicate AudioListener warning, wasted
                // render).
                if (spectatorCamera != null) spectatorCamera.gameObject.SetActive(false);
                // Already disabled by default in the scene - set explicitly anyway so this doesn't silently
                // regress if that scene default ever changes.
                if (spectatorLeapProvider != null) spectatorLeapProvider.enabled = false;
                if (spectatorPhysicalHandsManager != null) spectatorPhysicalHandsManager.enabled = false;
            }

            // ConnectionApproval must be explicitly enabled, otherwise the callback below is never invoked
            // even though it's assigned - NGO just silently skips it.
            NetworkManager.Singleton.NetworkConfig.ConnectionApproval = true;
            // AutoMatchmakingNGO doesn't set an approval callback itself, so this is safe to assign directly.
            NetworkManager.Singleton.ConnectionApprovalCallback = ApprovalCheck;
            NetworkManager.Singleton.OnClientConnectedCallback += HandOwnershipToConnectingClient;
        }

        private void OnDestroy()
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= HandOwnershipToConnectingClient;
            }
        }

        /// <summary>
        /// ClonesManager is an Editor-only ParrelSync type (lives under an Editor/ folder), so it can't be
        /// referenced from player builds - this always returns false there, leaving forceSpectatorMode/XR
        /// auto-detection as the only options outside the Editor, same as before this override existed.
        /// </summary>
        private static bool IsRunningAsParrelSyncClone()
        {
#if UNITY_EDITOR
            return ParrelSync.ClonesManager.IsClone();
#else
            return false;
#endif
        }

        /// <summary>
        /// The spectator rig's NetworkObject (see OwnerNetworkTransform) is server-owned by default, same
        /// as any other in-scene placed NetworkObject. Hand it to the connecting spectator so their local
        /// SpectatorFlyCamera movement is what gets replicated, instead of being silently ignored because a
        /// non-owner is writing to it. VR clients don't share a single pre-placed rig - each one gets its
        /// own NetworkedVRUser instance, spawned fresh and owned by that client from the start.
        /// </summary>
        private void HandOwnershipToConnectingClient(ulong clientId)
        {
            // Unconditional and role-agnostic, unlike everything below it: fires on every client (server or
            // not) whenever ANY client connects, purely so a device log confirms this client is in a session
            // at all - the logic below only ever logs on whichever side happens to be server.
            Debug.Log($"{nameof(SpectatorModeManager)}: OnClientConnectedCallback fired for client {clientId} " +
                      $"(local IsServer: {NetworkManager.Singleton.IsServer}, LocalClientId: {NetworkManager.Singleton.LocalClientId}).");

            if (!NetworkManager.Singleton.IsServer) return;

            if (SpectatorClientIds.Contains(clientId))
            {
                if (spectatorNetworkObject == null)
                {
                    Debug.LogWarning($"{nameof(SpectatorModeManager)}: {nameof(spectatorNetworkObject)} isn't set, " +
                                      "so it can't be handed to the connecting spectator and won't be visible to others.");
                    return;
                }

                spectatorNetworkObject.ChangeOwnership(clientId);
                Debug.Log($"{nameof(SpectatorModeManager)}: handed {nameof(spectatorNetworkObject)} ownership to client {clientId}.");
                return;
            }

            if (vrUserPrefab == null)
            {
                Debug.LogWarning($"{nameof(SpectatorModeManager)}: {nameof(vrUserPrefab)} isn't set, " +
                                  "so no avatar can be spawned for the connecting VR client.");
                return;
            }

            var vrUserInstance = Instantiate(vrUserPrefab);
            // destroyWithScene: true, since this is a runtime-spawned session object, not something that
            // should persist across a scene reload the way DontDestroyOnLoad objects would.
            vrUserInstance.GetComponent<NetworkObject>().SpawnWithOwnership(clientId, destroyWithScene: true);
            Debug.Log($"{nameof(SpectatorModeManager)}: spawned {nameof(vrUserPrefab)} owned by client {clientId}.");
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
