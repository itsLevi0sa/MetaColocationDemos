using MetaColocationDemos.Spectator;
using Oculus.Movement.AnimationRigging;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace MetaColocationDemos.Networking
{
    /// <summary>
    /// Hides this avatar's humanoid body mesh when SessionManager.IsHumanoidIkEnabled is unchecked, on every
    /// client that renders a copy of it - not owner-gated, since what to render is a purely local choice
    /// each viewer makes for themselves, not something the avatar's owner controls for everyone else.
    ///
    /// On the owner's own copy specifically (the one actually computing the pose, not just displaying it),
    /// unchecking also disables the Humanoid IK pipeline itself (OVRBody/RigBuilder/RetargetingLayer) for a
    /// real perf win on the VR headset when nobody needs to see the body/Meta hands anyway. Non-owner copies
    /// already have these disabled unconditionally by HumanoidPoseNetworkSync (they're driven purely by the
    /// received pose, never their own local body tracking), so there's nothing further to do there.
    ///
    /// Safe to disable the IK pipeline this way specifically because VrHeadPoseNetworkSync tracks the real
    /// HMD pose independently of it (straight from OVRCameraRig.centerEyeAnchor) - PcUserHeadFollower reads
    /// that, not the Animator's Head bone, so head-follow keeps working correctly either way.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class AvatarBodyVisibility : NetworkBehaviour
    {
        public override void OnNetworkSpawn()
        {
            var renderer = GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (renderer != null)
            {
                renderer.enabled = SessionManager.IsHumanoidIkEnabled;
            }
            else
            {
                Debug.LogWarning($"{nameof(AvatarBodyVisibility)}: no SkinnedMeshRenderer found under {name}.");
            }

            if (IsOwner && !SessionManager.IsHumanoidIkEnabled)
            {
                foreach (var ovrBody in GetComponentsInChildren<OVRBody>(true)) ovrBody.enabled = false;
                foreach (var rigBuilder in GetComponentsInChildren<RigBuilder>(true)) rigBuilder.enabled = false;
                foreach (var retargetingLayer in GetComponentsInChildren<RetargetingLayer>(true)) retargetingLayer.enabled = false;
            }
        }
    }
}
