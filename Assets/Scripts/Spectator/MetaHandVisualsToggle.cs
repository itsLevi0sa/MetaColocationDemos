using UnityEngine;

namespace MetaColocationDemos.Spectator
{
    /// <summary>
    /// Hides Meta's own hand visuals when SessionManager.IsHumanoidIkEnabled is unchecked. Purely local and
    /// unrelated to AvatarBodyVisibility - these renderers only ever exist on the VR wearer's own machine
    /// (they live in VRUser.unity, not on NetworkedVRUser.prefab), so there's nothing to network.
    ///
    /// There are two independent hand visuals to account for, both installed as Interaction SDK Building
    /// Blocks in VRUser.unity:
    ///
    /// - The classic hand-tracking mesh (l_handMeshNode/r_handMeshNode under OculusHand_L/OculusHand_R) -
    ///   a plain SkinnedMeshRenderer, safe to just disable directly.
    /// - The Synthetic Hand (HandVisual component, under '[BuildingBlock] Synthetic Left/Right Hand') - used
    ///   by the Interaction SDK's poke/grab interactors, and a completely separate GameObject tree, NOT
    ///   nested under OculusHand_L/R, so the classic hand search never finds it. Its own Update() re-enables
    ///   its SkinnedMeshRenderer every frame based on Hand.IsTrackedDataValid (see HandVisual.UpdateVisibility
    ///   in the Interaction SDK), so disabling just the renderer would get fought back the next frame -
    ///   deactivating the whole block GameObject is what actually sticks, since a disabled GameObject's
    ///   Update() never runs at all.
    ///
    /// Self-locating throughout: auto-finds both by name anywhere in the loaded scene, rather than requiring
    /// anything to be dragged into the Inspector by hand - this component can be added to any GameObject in
    /// VRUser.unity with nothing further to wire.
    ///
    /// Runs in Start(), which is safe read-ordering against SessionManager.IsHumanoidIkEnabled: VRUser.unity
    /// is only loaded (from Session_Setup's SessionManager.Start()) after that flag is already set, and every
    /// Awake() in a newly-loaded scene still finishes before this component's own Start() runs.
    /// </summary>
    public class MetaHandVisualsToggle : MonoBehaviour
    {
        [Tooltip("Optional manual override - leave both empty to auto-locate the classic hand-tracking mesh " +
                 "renderers by name anywhere in the scene. Only set these if auto-locate ever finds the " +
                 "wrong renderer. Doesn't cover the Synthetic Hand blocks, which are always auto-located.")]
        [SerializeField] private Renderer leftHandRenderer;
        [SerializeField] private Renderer rightHandRenderer;

        private void Start()
        {
            var visible = SessionManager.IsHumanoidIkEnabled;

            if (leftHandRenderer == null) leftHandRenderer = FindHandRenderer("l_handMeshNode", "OculusHand_L");
            if (rightHandRenderer == null) rightHandRenderer = FindHandRenderer("r_handMeshNode", "OculusHand_R");
            ApplyRenderer(leftHandRenderer, visible, "left", "'l_handMeshNode'/'OculusHand_L'");
            ApplyRenderer(rightHandRenderer, visible, "right", "'r_handMeshNode'/'OculusHand_R'");

            SetBlockActive("[BuildingBlock] Synthetic Left Hand", visible);
            SetBlockActive("[BuildingBlock] Synthetic Right Hand", visible);
        }

        private static void ApplyRenderer(Renderer renderer, bool visible, string side, string lookedFor)
        {
            if (renderer != null)
            {
                renderer.enabled = visible;
            }
            else
            {
                Debug.LogWarning($"{nameof(MetaHandVisualsToggle)}: couldn't find the {side} Meta hand " +
                                  $"renderer (looked for {lookedFor}) - wire it manually if the name " +
                                  "differs in this scene.");
            }
        }

        private static void SetBlockActive(string blockName, bool active)
        {
            foreach (var candidate in FindObjectsOfType<Transform>(true))
            {
                if (candidate.name != blockName) continue;
                candidate.gameObject.SetActive(active);
                return;
            }

            Debug.LogWarning($"{nameof(MetaHandVisualsToggle)}: couldn't find '{blockName}' to toggle.");
        }

        // FindObjectsOfType<Transform>(true), not GameObject.Find - the hand visuals can be inactive
        // (hidden whenever hand tracking isn't currently the active input source), and GameObject.Find only
        // ever matches active objects.
        private static Renderer FindHandRenderer(string meshNodeName, string parentObjectName)
        {
            foreach (var candidate in FindObjectsOfType<Transform>(true))
            {
                if (candidate.name != meshNodeName) continue;
                var renderer = candidate.GetComponent<Renderer>();
                if (renderer != null) return renderer;
            }

            foreach (var candidate in FindObjectsOfType<Transform>(true))
            {
                if (candidate.name != parentObjectName) continue;
                var renderer = candidate.GetComponentInChildren<Renderer>(true);
                if (renderer != null) return renderer;
            }

            return null;
        }
    }
}
