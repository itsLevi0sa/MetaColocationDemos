using System.Collections.Generic;
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

        [Tooltip("The [BuildingBlock] Camera Rig object to disable when running as a spectator, " +
                 "so it doesn't compete with the spectator camera.")]
        [SerializeField] private GameObject vrCameraRig;

        [Tooltip("The camera (with SpectatorFlyCamera) to enable when running as a spectator.")]
        [SerializeField] private Camera spectatorCamera;

        [Tooltip("The NetworkObject on the spectator rig (e.g. NetworkedPCUser) that should be handed to " +
                 "the spectator client on connect, so their local movement is what gets replicated.")]
        [SerializeField] private NetworkObject spectatorNetworkObject;

        public static readonly HashSet<ulong> SpectatorClientIds = new();

        public static bool IsLocalClientSpectator { get; private set; }

        private void Start()
        {
            IsLocalClientSpectator = forceSpectatorMode || !UnityEngine.XR.XRSettings.isDeviceActive;

            if (IsLocalClientSpectator)
            {
                Debug.Log($"{nameof(SpectatorModeManager)}: connecting as spectator (no XR device detected).");
                // Read by ApprovalCheck on the server before any networked objects become visible to this client.
                NetworkManager.Singleton.NetworkConfig.ConnectionData = SpectatorPayload;

                if (vrCameraRig != null) vrCameraRig.SetActive(false);
                if (spectatorCamera != null) spectatorCamera.gameObject.SetActive(true);
            }

            // ConnectionApproval must be explicitly enabled, otherwise the callback below is never invoked
            // even though it's assigned - NGO just silently skips it.
            NetworkManager.Singleton.NetworkConfig.ConnectionApproval = true;
            // AutoMatchmakingNGO doesn't set an approval callback itself, so this is safe to assign directly.
            NetworkManager.Singleton.ConnectionApprovalCallback = ApprovalCheck;
            NetworkManager.Singleton.OnClientConnectedCallback += HandOwnershipToSpectator;
        }

        private void OnDestroy()
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= HandOwnershipToSpectator;
            }
        }

        /// <summary>
        /// The spectator rig's NetworkObject (see OwnerNetworkTransform) is server-owned by default, same
        /// as any other in-scene placed NetworkObject. Hand ownership to the connecting spectator so their
        /// local SpectatorFlyCamera movement is what gets replicated to everyone else, instead of being
        /// silently ignored because a non-owner is writing to it.
        /// </summary>
        private void HandOwnershipToSpectator(ulong clientId)
        {
            // Unconditional and role-agnostic, unlike everything below it: fires on every client (server or
            // not) whenever ANY client connects, purely so a device log confirms this client is in a session
            // at all - the logic below only ever logs on whichever side happens to be server.
            Debug.Log($"{nameof(SpectatorModeManager)}: OnClientConnectedCallback fired for client {clientId} " +
                      $"(local IsServer: {NetworkManager.Singleton.IsServer}, LocalClientId: {NetworkManager.Singleton.LocalClientId}).");

            if (!NetworkManager.Singleton.IsServer || !SpectatorClientIds.Contains(clientId)) return;

            if (spectatorNetworkObject == null)
            {
                Debug.LogWarning($"{nameof(SpectatorModeManager)}: spectatorNetworkObject isn't set, " +
                                  "so it can't be handed to the spectator client and won't be visible to others.");
                return;
            }

            spectatorNetworkObject.ChangeOwnership(clientId);
            Debug.Log($"{nameof(SpectatorModeManager)}: handed spectatorNetworkObject ownership to client {clientId}.");
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
