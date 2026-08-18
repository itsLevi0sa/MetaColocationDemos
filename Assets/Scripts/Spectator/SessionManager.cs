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
    /// to toggle a shared rig's visibility. Neither contains a NetworkObject, so a plain local SceneManager
    /// load (not NetworkManager.SceneManager) is correct - it's never synced to other connected clients,
    /// which is exactly what a role-specific scene needs.
    ///
    /// Environment (Directional Light, world geometry, including the interactive Cube) is different: the
    /// Cube is an in-scene-placed NetworkObject, and NGO's scene-sync protocol only reconciles those for
    /// scenes tracked through NetworkManager.SceneManager - a plain local load left each client to reach
    /// Environment on its own uncoordinated timeline, so NGO could try to sync the Cube against a client
    /// that hadn't loaded it yet and fail the spawn outright. Only the server may drive a
    /// NetworkManager.SceneManager load, and it auto-propagates to every connected client, so this one is
    /// deferred to OnServerStarted below instead of being loaded unconditionally by everyone here.
    /// </summary>
    public class SessionManager : MonoBehaviour
    {
        private const string EnvironmentSceneName = "Environment";
        private const string VrRigSceneName = "Colocation_VR_Rig";
        private const string PcRigSceneName = "Colocation_PC_Rig";

        // Meta Quest Link has to negotiate a session with the desktop Oculus runtime before the display
        // subsystem comes up, unlike a real on-device build where OVRManager.isHmdPresent is already true
        // at boot - a single instantaneous read here can catch it mid-negotiation and misdetect a VR client
        // as a spectator. That misdetection used to just flip a SetActive toggle in an otherwise identical
        // shared scene; now it decides which whole rig scene loads, so it's worth a short poll instead of
        // trusting one read.
        private const float XrDeviceDetectionTimeoutSeconds = 3f;

        private static readonly byte[] SpectatorPayload = { 1 };

        [Tooltip("Force this client to connect as a spectator, regardless of auto-detection or a ParrelSync " +
                 "clone defaulting to VR (see IsParrelSyncClone) - this always wins. Leave off to auto-detect " +
                 "based on whether an XR device is active.")]
        [SerializeField] private bool forceSpectatorMode;

        [Tooltip("Whether VR clients run Meta's colocation/shared-spatial-anchor alignment flow on connect " +
                 "(see SpectatorAwareColocationBootstrapper). Uncheck to build/test with colocation fully " +
                 "skipped - e.g. to rule it out as the source of a tracking/rendering issue.")]
        [SerializeField] private bool colocationEnabled = true;

        [Tooltip("Whether Insight Passthrough (see PassthroughNetworkToggle) is allowed to turn on at all. " +
                 "Uncheck to build/test with Passthrough fully disabled - e.g. to rule out its camera/SLAM " +
                 "usage as the source of a tracking drift issue.")]
        [SerializeField] private bool passthroughEnabled = true;

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

        public static bool IsColocationEnabled { get; private set; }

        public static bool IsPassthroughEnabled { get; private set; }

        private IEnumerator Start()
        {
            IsColocationEnabled = colocationEnabled;
            IsPassthroughEnabled = passthroughEnabled;

            // A ParrelSync clone editor has no real XR device of its own to detect, so without this it
            // would always auto-detect as a spectator - making it useless for locally testing a VR client
            // against the main editor's own instance without a second physical headset. forceSpectatorMode
            // still wins over this if explicitly set, for whenever a clone genuinely needs to be a spectator.
            var isParrelSyncClone = IsParrelSyncClone();

            // The original project has no reliable way to tell a real headset apart from Quest Link just
            // being connected in the background - OVRManager.isHmdPresent can read true here even though a
            // clone is the one actually meant to be the VR side of a local dual-editor test, which is what
            // was making both editors load VR. If a clone of this project is currently running, defer to it
            // and be the spectator regardless of what isHmdPresent reports; if no clone is running, this
            // falls through to the original device-based auto-detect unchanged, e.g. for solo VR testing
            // straight from the main editor.
            var deferToRunningClone = !isParrelSyncClone && IsAnyClonedProjectRunning();

            var skipXrDeviceWait = forceSpectatorMode || isParrelSyncClone || deferToRunningClone;

            if (!skipXrDeviceWait)
            {
                yield return WaitForXrDeviceActivation();
            }

            // OVRManager.isHmdPresent, not the more generic UnityEngine.XR.XRSettings.isDeviceActive: this
            // project's OpenXR settings have "Initialize XR on Startup" on with both the Meta XR feature AND
            // Ultraleap Hand Tracking enabled, so pressing Play with only a Leap Motion controller plugged
            // in (no headset at all) still spins up an OpenXR session and flips isDeviceActive true - it's
            // "is any XR feature active", not "is a headset present". isHmdPresent checks specifically for a
            // running display subsystem, which Ultraleap's hand-tracking-only feature never creates.
            IsLocalClientSpectator = forceSpectatorMode || deferToRunningClone ||
                                      (!isParrelSyncClone && !OVRManager.isHmdPresent);

            // Only fires (and therefore only loads Environment) on whichever instance ends up hosting; NGO
            // propagates that load to every client that connects afterward, so this must NOT also be loaded
            // locally by clients here - that would just recreate the uncoordinated-timeline problem this is
            // fixing, on the client side instead of the host side.
            NetworkManager.Singleton.OnServerStarted += LoadEnvironmentSceneOnServer;

            // Loaded locally so NetworkedPCUser's LocalSpectatorCameraAnchor has a Main Camera to attach once
            // it spawns as this client's own instance (see HandOwnershipToConnectingClient below). The
            // network connection - and therefore the earliest that spawn could happen - is still several
            // awaited async calls away in AutoMatchmakingNGO, so this tiny scene has plenty of time to load.
            var rigSceneName = IsLocalClientSpectator ? PcRigSceneName : VrRigSceneName;
            SceneManager.LoadSceneAsync(rigSceneName, LoadSceneMode.Additive);

            if (IsLocalClientSpectator)
            {
                Debug.Log($"{nameof(SessionManager)}: connecting as spectator (no headset detected).");
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

        // ParrelSync's ClonesManager lives under an Editor/ folder, so it only exists in editor assemblies -
        // referencing it directly here would fail to compile for an Android/Quest build, which this file is
        // also compiled into. The #if keeps this a no-op false outside the editor instead.
        private static bool IsParrelSyncClone()
        {
#if UNITY_EDITOR
            return ParrelSync.ClonesManager.IsClone();
#else
            return false;
#endif
        }

        private static bool IsAnyClonedProjectRunning()
        {
#if UNITY_EDITOR
            foreach (var clonePath in ParrelSync.ClonesManager.GetCloneProjectsPath())
            {
                if (ParrelSync.ClonesManager.IsCloneProjectRunning(clonePath)) return true;
            }
            return false;
#else
            return false;
#endif
        }

        private static IEnumerator WaitForXrDeviceActivation()
        {
            var deadline = Time.realtimeSinceStartup + XrDeviceDetectionTimeoutSeconds;
            while (!OVRManager.isHmdPresent && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
        }

        private void OnDestroy()
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnServerStarted -= LoadEnvironmentSceneOnServer;
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

            // TEMP DIAGNOSTIC: split timing for Instantiate() (GameObject construction + every Awake() in the
            // prefab, e.g. RetargetingLayer/OVRSkeleton rig setup) vs SpawnWithOwnership() (NGO's spawn
            // message + every OnNetworkSpawn() callback) - narrows down which half of avatar spawning is
            // actually expensive, on the server's own copy of the same prefab a connecting client also
            // instantiates. Remove once the frame-rate collapse is root-caused.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var instance = Instantiate(prefab);
            var instantiateMs = stopwatch.ElapsedMilliseconds;

            // destroyWithScene: true, since this is a runtime-spawned session object, not something that
            // should persist across a scene reload the way DontDestroyOnLoad objects would.
            stopwatch.Restart();
            instance.GetComponent<NetworkObject>().SpawnWithOwnership(clientId, destroyWithScene: true);
            var spawnMs = stopwatch.ElapsedMilliseconds;

            Debug.Log($"{nameof(SessionManager)}: spawned {prefab.name} owned by client {clientId}. " +
                      $"[PERF] Instantiate: {instantiateMs}ms, SpawnWithOwnership: {spawnMs}ms.");
        }

        private void LoadEnvironmentSceneOnServer()
        {
            NetworkManager.Singleton.SceneManager.LoadScene(EnvironmentSceneName, LoadSceneMode.Additive);
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
