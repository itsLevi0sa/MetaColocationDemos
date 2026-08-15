using Meta.XR.MultiplayerBlocks.NGO;
using UnityEngine;

namespace MetaColocationDemos.Spectator
{
    /// <summary>
    /// A PC spectator has no real-world spatial anchors to align, so letting NGONetworkBootstrapper run its
    /// full colocation flow for it just fails outright (there's nothing real to anchor to) and spams
    /// anchor-creation errors. This skips only the colocation trigger for a spectator client.
    ///
    /// Deliberately doesn't touch this GameObject's active state or NetworkObject at all - a spectator
    /// hosting the session still needs the NetcodeGameObjectsMessenger NetworkBehaviour on this same object
    /// alive and spawned, since it's how VR clients relay their own anchor-sharing RPCs to each other
    /// through the host. Disabling the whole object would silently break VR-to-VR colocation too.
    /// </summary>
    public class SpectatorAwareColocationBootstrapper : NGONetworkBootstrapper
    {
        public override void OnNetworkSpawn()
        {
            if (SessionManager.IsLocalClientSpectator)
            {
                Debug.Log($"{nameof(SpectatorAwareColocationBootstrapper)}: local client is a spectator, skipping colocation.");
                return;
            }

            base.OnNetworkSpawn();
        }
    }
}
