using System.IO;
using MetaColocationDemos.Recording;
using UnityEditor;
using UnityEngine;

namespace MetaColocationDemos.EditorTools
{
    /// <summary>
    /// Live Inspector view of RecordManager's recording library - it's built from a fresh disk scan
    /// (RefreshLibrary) rather than a SerializeField, so the default Inspector wouldn't show it at all.
    ///
    /// The library list/delete below works in Edit Mode too - RefreshLibrary/DeleteRecording are plain file
    /// I/O, nothing runtime-only about them - so browsing and cleaning up old recordings doesn't require
    /// entering Play mode first. Recording/playback themselves genuinely do need Play mode though (they rely
    /// on live network sources and Lambertian, which only exist once a session is running - see
    /// RecordManager's class doc comment), so that section of the Inspector is gated accordingly. Buttons
    /// call the exact same public methods F9/F10/F11/arrows/L do, so this is an alternative to the keyboard,
    /// not a separate path with its own behavior to keep in sync.
    /// </summary>
    [CustomEditor(typeof(RecordManager))]
    public class RecordManagerEditor : Editor
    {
        // Shared by every row's Trim button rather than one pair per recording - simpler UI, and trimming is
        // normally the same "reach for the keyboard" amount take to take anyway. Resets to these defaults
        // whenever this Inspector is reopened, same as any other unsaved Editor-only tool state.
        private float _trimStartSeconds = 1f;
        private float _trimEndSeconds = 1f;

        // So the library is already populated the moment this Inspector is shown, rather than starting empty
        // until the user thinks to press Refresh - harmless to call in Edit mode, it's just a directory scan.
        private void OnEnable()
        {
            (target as RecordManager)?.RefreshLibrary();
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var manager = (RecordManager)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Recording Library", EditorStyles.boldLabel);

            if (Application.isPlaying)
            {
                DrawStatus(manager);
                EditorGUILayout.Space();
                DrawTransportControls(manager);
            }
            else
            {
                EditorGUILayout.HelpBox("Enter Play Mode to record or play back - the library below works " +
                                         "in Edit Mode too.", MessageType.Info);
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Refresh Library")) manager.RefreshLibrary();
            DrawLibraryList(manager);

            // The status/library above changes every frame while recording or playing (frame counts,
            // selection after an auto-advance, etc.) but nothing here is a SerializedProperty edit, so the
            // Inspector won't repaint on its own - without this it would only visibly update when something
            // else happened to trigger a repaint (e.g. moving the mouse over it).
            if (Application.isPlaying) Repaint();
        }

        private static void DrawStatus(RecordManager manager)
        {
            if (manager.IsRecording)
            {
                EditorGUILayout.HelpBox($"Recording... {manager.RecordingFrameCount} frames captured.", MessageType.None);
            }
            else if (manager.IsPlaying)
            {
                EditorGUILayout.HelpBox(
                    $"Playing frame {manager.PlaybackFrameIndex + 1}/{manager.PlaybackFrameCount}" +
                    $"{(manager.IsLoopingLibrary ? " (looping library)" : "")}.", MessageType.None);
            }
            else
            {
                EditorGUILayout.LabelField("Idle.");
            }
        }

        private static void DrawTransportControls(RecordManager manager)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (manager.IsRecording)
                {
                    if (GUILayout.Button("Stop Recording (F9)")) manager.StopRecording();
                }
                else
                {
                    using (new EditorGUI.DisabledScope(manager.IsPlaying))
                    {
                        if (GUILayout.Button("Start Recording (F9)")) manager.StartRecording();
                    }
                }
            }

            using (new EditorGUI.DisabledScope(manager.IsRecording || manager.LibraryCount == 0))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("◀ Previous")) manager.SelectPreviousRecording();
                    if (GUILayout.Button("Next ▶")) manager.SelectNextRecording();
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Play Full (F10)")) manager.PlaySelected(keepOwnPosition: false);
                    if (GUILayout.Button("Play Rotation-Only (F11)")) manager.PlaySelected(keepOwnPosition: true);
                }

                if (manager.IsPlaying && GUILayout.Button("Stop Playback")) manager.StopPlayback();
            }

            manager.SetLibraryLooping(EditorGUILayout.ToggleLeft("Loop library (L)", manager.IsLoopingLibrary));
        }

        private void DrawLibraryList(RecordManager manager)
        {
            EditorGUILayout.LabelField($"{manager.LibraryCount} recording(s) in {RecordManager.RecordingsDirectory}",
                                        EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Trim (s)", GUILayout.Width(50));
                EditorGUILayout.LabelField("Start", GUILayout.Width(35));
                _trimStartSeconds = Mathf.Max(0f, EditorGUILayout.FloatField(_trimStartSeconds, GUILayout.Width(40)));
                EditorGUILayout.LabelField("End", GUILayout.Width(28));
                _trimEndSeconds = Mathf.Max(0f, EditorGUILayout.FloatField(_trimEndSeconds, GUILayout.Width(40)));
            }

            for (var i = 0; i < manager.LibraryPaths.Count; i++)
            {
                var path = manager.LibraryPaths[i];
                var isSelected = i == manager.SelectedLibraryIndex;

                using (new EditorGUILayout.HorizontalScope())
                {
                    var style = isSelected ? EditorStyles.boldLabel : EditorStyles.label;
                    EditorGUILayout.LabelField((isSelected ? "> " : "   ") + Path.GetFileNameWithoutExtension(path), style);

                    // Playback needs a live session (see class doc comment) - disabled outright in Edit mode,
                    // rather than letting it silently set IsPlaying with no Update() loop running to ever
                    // advance it.
                    using (new EditorGUI.DisabledScope(manager.IsRecording || !Application.isPlaying))
                    {
                        if (GUILayout.Button("Play", GUILayout.Width(50)))
                        {
                            manager.SelectRecording(i);
                            manager.PlayRecording(path);
                        }
                    }

                    // Trimming is plain file I/O (see TrimRecording), so unlike Play it's not gated on Play
                    // mode - only on not currently recording, same as Delete.
                    using (new EditorGUI.DisabledScope(manager.IsRecording))
                    {
                        if (GUILayout.Button("Trim", GUILayout.Width(45)))
                        {
                            manager.TrimRecording(i, _trimStartSeconds, _trimEndSeconds);
                            // TrimRecording refreshes the library itself, which may reorder/resize this list -
                            // bail out cleanly rather than continuing to index into it this pass.
                            GUIUtility.ExitGUI();
                        }

                        if (GUILayout.Button("Delete", GUILayout.Width(55)))
                        {
                            if (EditorUtility.DisplayDialog("Delete Recording",
                                    $"Delete '{Path.GetFileName(path)}'? This can't be undone.", "Delete", "Cancel"))
                            {
                                manager.DeleteRecording(i);
                                // The list this loop is iterating just changed size/shifted - bail out of this
                                // OnInspectorGUI pass cleanly instead of continuing to index into it.
                                GUIUtility.ExitGUI();
                            }
                        }
                    }
                }
            }
        }
    }
}
