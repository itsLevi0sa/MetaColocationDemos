using MetaColocationDemos.Spectator;
using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.Networking
{
    /// <summary>
    /// Hides NetworkedPCUser's Cube child specifically from VR viewers, on every client that renders a copy
    /// of it - not owner-gated, since what to render is a purely local choice each viewer makes for
    /// themselves. A spectator's own client (whether it's the owner or another spectator watching) still
    /// sees it; only clients whose local role is VR don't.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class PcUserCubeVisibility : NetworkBehaviour
    {
        public override void OnNetworkSpawn()
        {
            Renderer cubeRenderer = null;
            foreach (var candidate in GetComponentsInChildren<Renderer>(true))
            {
                if (candidate.gameObject.name == "Cube")
                {
                    cubeRenderer = candidate;
                    break;
                }
            }

            if (cubeRenderer == null)
            {
                Debug.LogWarning($"{nameof(PcUserCubeVisibility)}: no 'Cube' renderer found under {name}.");
                return;
            }

            cubeRenderer.enabled = SessionManager.IsLocalClientSpectator;
        }
    }
}
