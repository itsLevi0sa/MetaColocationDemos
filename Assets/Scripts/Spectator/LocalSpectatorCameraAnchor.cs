using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MetaColocationDemos.Spectator
{
    /// <summary>
    /// Owner-only: attaches this client's local Main Camera (lives in PCUser, loaded locally by
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
        private const string PcRigSceneName = "PCUser";

        public override void OnNetworkSpawn()
        {
            if (!IsOwner) return;

            var mainCamera = FindPcRigCamera();
            if (mainCamera == null)
            {
                Debug.LogWarning($"{nameof(LocalSpectatorCameraAnchor)}: no Camera found in the " +
                                  $"{PcRigSceneName} scene (is it loaded?) - {name} will have no rendering camera.");
                return;
            }

            mainCamera.transform.SetParent(transform, worldPositionStays: false);
        }

        // Scoped to the PC rig scene specifically, rather than the scene-wide GameObject.Find("Main Camera")
        // this used to be - any other camera loaded elsewhere and named the same (e.g. a Leap Motion desktop
        // rig's own bundled camera, which follows Unity's default "Main Camera" naming too) is an equally
        // valid match for a plain name search and could get grabbed instead, depending on load order.
        private static Camera FindPcRigCamera()
        {
            var scene = SceneManager.GetSceneByName(PcRigSceneName);
            if (!scene.IsValid())
            {
                return null;
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                var camera = root.GetComponentInChildren<Camera>(includeInactive: true);
                if (camera != null)
                {
                    return camera;
                }
            }

            return null;
        }
    }
}
