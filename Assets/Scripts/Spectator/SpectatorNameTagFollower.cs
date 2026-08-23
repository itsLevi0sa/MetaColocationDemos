using System.Collections;
using Meta.XR.MultiplayerBlocks.NGO;
using Unity.Netcode.Components;
using UnityEngine;

namespace MetaColocationDemos.Spectator
{
    /// <summary>
    /// PlayerNameTagNGO positions a client's own name tag every FixedUpdate by writing transform.position
    /// directly (from OVRManager's centerEyeAnchor for a VR user, or never, for a spectator whose VR
    /// camera rig is disabled). That only replicates correctly when the writer is the server: NGO's
    /// NetworkTransform is server-authoritative by default, so a non-host owner's raw position write gets
    /// silently discarded - overwritten back to the last known server state on the very next Update(). In
    /// practice this means only the host's own name tag tracks correctly for everyone else today; any
    /// other player's (VR or spectator) tag just sits frozen wherever it spawned.
    ///
    /// This drives the LOCAL player's own name tag through NetworkTransform.SetState() instead, which
    /// correctly routes through a ServerRpc when the caller isn't authoritative, so it replicates
    /// regardless of whether this client happens to be host. Runs identically on every client (VR or
    /// spectator) and picks its follow target based on role: a spectator follows the spectator rig; a VR
    /// user follows their own centerEyeAnchor, same source PlayerNameTagNGO would use if its own write
    /// replicated correctly.
    ///
    /// Also enforces SessionManager.IsShowNameTagsEnabled: PlayerNameTagNGO.OnNetworkSpawn() (Meta's own
    /// script, in the com.meta.xr.sdk.core package, not something this project can edit) sets its "NameTag"
    /// child active/inactive purely based on ownership (hidden for the owner, shown to everyone else) and
    /// never touches it again. When the setting is unchecked, HideAllNameTagsPeriodically below overrides that per
    /// viewer - same as every other local rendering toggle on SessionManager (see AvatarBodyVisibility,
    /// MetaHandVisualsToggle), each client independently hides every tag it renders, VR and PC alike, since
    /// both roles spawn the same PlayerNameTagNGO prefab. Polls on a coroutine rather than every FixedUpdate
    /// since new tags can spawn at any time as clients connect through the session, and a plain one-shot
    /// check at Start() would miss them.
    /// </summary>
    public class SpectatorNameTagFollower : MonoBehaviour
    {
        [SerializeField] private float heightOffset = 0.3f;

        private const float PositionThreshold = 0.01f;
        private const string NameTagChildName = "NameTag";
        private const float HideNameTagsPollIntervalSeconds = 0.5f;

        private NetworkTransform _ownNameTagTransform;
        private Transform _centerEye;
        private bool _lookedUpCenterEye;
        private Transform _spectatorRig;
        private bool _lookedUpSpectatorRig;
        private Vector3 _lastSentPosition;

        private void Start()
        {
            if (!SessionManager.IsShowNameTagsEnabled)
            {
                StartCoroutine(HideAllNameTagsPeriodically());
            }
        }

        private static IEnumerator HideAllNameTagsPeriodically()
        {
            var wait = new WaitForSeconds(HideNameTagsPollIntervalSeconds);
            while (true)
            {
                foreach (var tag in FindObjectsByType<PlayerNameTagNGO>(FindObjectsSortMode.None))
                {
                    var nameTagGo = tag.transform.Find(NameTagChildName);
                    if (nameTagGo != null && nameTagGo.gameObject.activeSelf)
                    {
                        nameTagGo.gameObject.SetActive(false);
                    }
                }

                yield return wait;
            }
        }

        private void FixedUpdate()
        {
            if (_ownNameTagTransform == null)
            {
                _ownNameTagTransform = FindOwnNameTagTransform();
                if (_ownNameTagTransform == null) return;
            }

            var followTarget = GetFollowTarget();
            if (followTarget == null) return;

            var targetPosition = followTarget.position + Vector3.up * heightOffset;
            if ((targetPosition - _lastSentPosition).sqrMagnitude < PositionThreshold * PositionThreshold) return;

            _ownNameTagTransform.SetState(targetPosition);
            _lastSentPosition = targetPosition;
        }

        private Transform GetFollowTarget()
        {
            if (SessionManager.IsLocalClientSpectator)
            {
                if (!_lookedUpSpectatorRig)
                {
                    _lookedUpSpectatorRig = true;
                    // NetworkedPCUser is spawned at runtime (see SessionManager), so its instance name
                    // is "NetworkedPCUser(Clone)", not "NetworkedPCUser" - look it up by the (otherwise
                    // unused) OwnerNetworkTransform type instead of by name, same IsOwner filter as
                    // FindOwnNameTagTransform below picks the local client's own instance among any others.
                    foreach (var rig in FindObjectsByType<OwnerNetworkTransform>(FindObjectsSortMode.None))
                    {
                        if (!rig.IsOwner) continue;
                        _spectatorRig = rig.transform;
                        break;
                    }
                }

                return _spectatorRig;
            }

            if (!_lookedUpCenterEye)
            {
                _lookedUpCenterEye = true;
                if (OVRManager.instance != null)
                {
                    _centerEye = OVRManager.instance.GetComponentInChildren<OVRCameraRig>().centerEyeAnchor;
                }
            }

            return _centerEye;
        }

        private static NetworkTransform FindOwnNameTagTransform()
        {
            foreach (var tag in FindObjectsByType<PlayerNameTagNGO>(FindObjectsSortMode.None))
            {
                if (tag.IsOwner) return tag.GetComponent<NetworkTransform>();
            }

            return null;
        }
    }
}
