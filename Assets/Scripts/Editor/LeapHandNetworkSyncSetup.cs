using Leap;
using MetaColocationDemos.Networking;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MetaColocationDemos.EditorTools
{
    /// <summary>
    /// One-click wiring for LeapHandNetworkSync: adds it to NetworkedPCUser (if missing) and hooks up its
    /// LeapProvider/hand-model references by searching NetworkedPCUser's own children, rather than requiring
    /// those to be dragged in by hand - GhostHands and Service Provider Desktop are nested prefab instances,
    /// so their component fileIDs aren't something a text edit to the .unity file could safely reference.
    /// Run from the menu with ColocationBasicDemoWithSpectatorLeapMotionOSC open and active.
    /// </summary>
    public static class LeapHandNetworkSyncSetup
    {
        [MenuItem("Tools/Colocation/Wire Up Leap Hand Networking")]
        private static void Wire()
        {
            var pcUser = GameObject.Find("NetworkedPCUser");
            if (pcUser == null)
            {
                Debug.LogError($"{nameof(LeapHandNetworkSyncSetup)}: no 'NetworkedPCUser' object found in the " +
                                "open scene. Open ColocationBasicDemoWithSpectatorLeapMotionOSC and try again.");
                return;
            }

            var leapProvider = pcUser.GetComponentInChildren<LeapServiceProvider>(true);

            HandModelBase leftHand = null;
            HandModelBase rightHand = null;
            foreach (var handModel in pcUser.GetComponentsInChildren<HandModelBase>(true))
            {
                if (handModel.Handedness == Chirality.Left && leftHand == null) leftHand = handModel;
                else if (handModel.Handedness == Chirality.Right && rightHand == null) rightHand = handModel;
            }

            if (leapProvider == null || leftHand == null || rightHand == null)
            {
                Debug.LogError($"{nameof(LeapHandNetworkSyncSetup)}: couldn't find everything needed under " +
                                $"NetworkedPCUser (LeapServiceProvider: {leapProvider}, left HandModelBase: " +
                                $"{leftHand}, right HandModelBase: {rightHand}). Wire LeapHandNetworkSync's " +
                                "fields manually instead.");
                return;
            }

            var sync = pcUser.GetComponent<LeapHandNetworkSync>();
            if (sync == null)
            {
                sync = Undo.AddComponent<LeapHandNetworkSync>(pcUser);
            }
            else
            {
                Undo.RecordObject(sync, "Wire Up Leap Hand Networking");
            }

            var serialized = new SerializedObject(sync);
            serialized.FindProperty("localLeapProvider").objectReferenceValue = leapProvider;
            serialized.FindProperty("leftHandModel").objectReferenceValue = leftHand;
            serialized.FindProperty("rightHandModel").objectReferenceValue = rightHand;
            serialized.ApplyModifiedProperties();

            EditorSceneManager.MarkSceneDirty(pcUser.scene);
            Debug.Log($"{nameof(LeapHandNetworkSyncSetup)}: wired LeapHandNetworkSync on NetworkedPCUser - " +
                      $"provider: {leapProvider.name}, left: {leftHand.name}, right: {rightHand.name}. " +
                      "Remember to save the scene.");
        }
    }
}
