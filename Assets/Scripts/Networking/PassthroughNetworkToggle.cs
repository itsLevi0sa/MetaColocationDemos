using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.Networking
{
    /// <summary>
    /// Lives on the [BuildingBlock] Network Manager GameObject rather than on a NetworkObject of its own -
    /// NetworkManager warns/refuses to coexist with a NetworkObject in its own hierarchy, so this uses NGO's
    /// CustomMessagingManager instead of a NetworkVariable/RPC, neither of which need one.
    ///
    /// Flip passthroughEnabled in the Inspector during Play mode from any connected peer: the request goes
    /// to the server, which applies it locally and re-broadcasts to every client (including late joiners,
    /// via SendCurrentStateToNewClient) so every Quest user's Passthrough - and every checkbox - ends up
    /// agreeing with the host.
    /// </summary>
    public class PassthroughNetworkToggle : MonoBehaviour
    {
        private const string RequestMessageName = nameof(PassthroughNetworkToggle) + ".Request";
        private const string BroadcastMessageName = nameof(PassthroughNetworkToggle) + ".Broadcast";

        [Tooltip("Editor-only control: toggle during Play mode to switch Insight Passthrough on/off for " +
                 "every connected Quest user at once, including this one.")]
        [SerializeField] private bool passthroughEnabled = true;

        // [BuildingBlock] Passthrough lives in the VR-only Colocation_VR_Rig scene, loaded locally and only
        // for VR clients - it won't exist for a spectator, and isn't loaded yet when this object's own
        // Awake() runs, so it's looked up lazily instead of through a (cross-scene, unsupported) serialized
        // reference. Not cached: a failed lookup (e.g. spectator client, or VR rig not loaded yet) should
        // retry on the next toggle rather than staying null forever.
        private OVRPassthroughLayer PassthroughLayer => FindObjectOfType<OVRPassthroughLayer>();

        private bool _lastKnownValue;
        private bool _handlersRegistered;

        private void Awake()
        {
            _lastKnownValue = passthroughEnabled;
            ApplyLocally(passthroughEnabled);
        }

        // NetworkManager.Singleton is only guaranteed to be set once NetworkManager's own Awake() has run,
        // and Awake-vs-Awake ordering between components isn't reliable - see SessionManager for the
        // same reasoning. Start() is safe since all Awake() calls finish first.
        private void Start()
        {
            var networkManager = NetworkManager.Singleton;
            networkManager.OnServerStarted += RegisterHandlers;
            networkManager.OnClientStarted += RegisterHandlers;
            networkManager.OnClientConnectedCallback += SendCurrentStateToNewClient;
        }

        private void OnDestroy()
        {
            var networkManager = NetworkManager.Singleton;
            if (networkManager == null) return;

            networkManager.OnServerStarted -= RegisterHandlers;
            networkManager.OnClientStarted -= RegisterHandlers;
            networkManager.OnClientConnectedCallback -= SendCurrentStateToNewClient;

            if (networkManager.CustomMessagingManager != null)
            {
                networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(RequestMessageName);
                networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(BroadcastMessageName);
            }
        }

        // Editor-only Unity callback, fired whenever a serialized field changes in the Inspector - including
        // during Play mode, which is the whole point: this is the "toggle checkbox" entry point.
        private void OnValidate()
        {
            if (!Application.isPlaying || passthroughEnabled == _lastKnownValue) return;
            RequestSetPassthrough(passthroughEnabled);
        }

        private void RegisterHandlers()
        {
            // OnServerStarted and OnClientStarted both fire for a host - guard so it only registers once.
            if (_handlersRegistered) return;
            var messagingManager = NetworkManager.Singleton.CustomMessagingManager;
            if (messagingManager == null) return;

            if (NetworkManager.Singleton.IsServer)
            {
                messagingManager.RegisterNamedMessageHandler(RequestMessageName, OnRequestReceivedByServer);
            }
            messagingManager.RegisterNamedMessageHandler(BroadcastMessageName, OnBroadcastReceivedByClient);
            _handlersRegistered = true;
        }

        private void RequestSetPassthrough(bool value)
        {
            var networkManager = NetworkManager.Singleton;
            if (networkManager == null || !networkManager.IsListening || networkManager.CustomMessagingManager == null)
            {
                // Not connected (e.g. flipped before StartHost/StartClient) - just apply locally.
                ApplyLocally(value);
                return;
            }

            if (networkManager.IsServer)
            {
                ApplyAndBroadcast(value);
                return;
            }

            using var writer = new FastBufferWriter(sizeof(bool), Allocator.Temp);
            writer.WriteValueSafe(value);
            networkManager.CustomMessagingManager.SendNamedMessage(RequestMessageName, NetworkManager.ServerClientId, writer);
        }

        private void OnRequestReceivedByServer(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out bool value);
            ApplyAndBroadcast(value);
        }

        private void ApplyAndBroadcast(bool value)
        {
            ApplyLocally(value);

            using var writer = new FastBufferWriter(sizeof(bool), Allocator.Temp);
            writer.WriteValueSafe(value);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(BroadcastMessageName, writer);
        }

        private void OnBroadcastReceivedByClient(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out bool value);
            ApplyLocally(value);
        }

        // Custom messages only reach clients connected at send time, so a late joiner needs the current
        // state pushed to them directly once they connect - otherwise their Passthrough (and checkbox) would
        // stay at whatever passthroughEnabled happened to be serialized as in the scene.
        private void SendCurrentStateToNewClient(ulong clientId)
        {
            var networkManager = NetworkManager.Singleton;
            if (!networkManager.IsServer || clientId == NetworkManager.ServerClientId) return;

            using var writer = new FastBufferWriter(sizeof(bool), Allocator.Temp);
            writer.WriteValueSafe(passthroughEnabled);
            networkManager.CustomMessagingManager.SendNamedMessage(BroadcastMessageName, clientId, writer);
        }

        private void ApplyLocally(bool value)
        {
            _lastKnownValue = value;
            passthroughEnabled = value;
            var layer = PassthroughLayer;
            if (layer != null) layer.enabled = value;
        }
    }
}
