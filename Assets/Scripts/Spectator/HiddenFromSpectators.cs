using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.Spectator
{
    /// <summary>
    /// Attach to any networked "[BuildingBlock]" object whose Quest/Platform-SDK-dependent logic
    /// (colocation, avatars, player name tags) should never run on a PC spectator client.
    /// Prevents this NetworkObject from ever being spawned/visible on clients marked as spectators,
    /// so their OnNetworkSpawn (and the OVR/Platform SDK calls inside it) never fires for them.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class HiddenFromSpectators : MonoBehaviour
    {
        private void Awake()
        {
            GetComponent<NetworkObject>().CheckObjectVisibility =
                clientId => !SpectatorModeManager.SpectatorClientIds.Contains(clientId);
        }
    }
}
