using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.Spectator
{
    /// <summary>
    /// Owner-only: attaches this client's local Main Camera (lives in Colocation_PC_Rig, loaded locally by
    /// SessionManager.Start() before this object ever spawns) onto NetworkedPCUser once it's actually
    /// this client's own instance, so the camera renders from wherever SpectatorFlyCamera (also on this
    /// GameObject) drives it via local input. Mirrors LocalCameraRigAnchor's ownership pattern for the VR
    /// side, just in the opposite direction: there, the networked container follows the local hardware rig;
    /// here, the local rendering camera follows the networked container.
    ///
    /// A non-owner has no local Main Camera to attach - every client only ever loads its own role's rig
    /// scene, never someone else's, so a spectator watching another spectator's NetworkedPCUser simply finds
    /// nothing here and does nothing.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class LocalSpectatorCameraAnchor : NetworkBehaviour
    {
        public override void OnNetworkSpawn()
        {
            if (!IsOwner) return;

            var mainCamera = GameObject.Find("Main Camera");
            if (mainCamera == null)
            {
                Debug.LogWarning($"{nameof(LocalSpectatorCameraAnchor)}: no local Main Camera found " +
                                  $"(is Colocation_PC_Rig loaded?) - {name} will have no rendering camera.");
                return;
            }

            mainCamera.transform.SetParent(transform, worldPositionStays: false);
        }
    }
}
