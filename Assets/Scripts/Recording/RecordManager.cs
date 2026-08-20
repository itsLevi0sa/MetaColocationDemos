using System.Collections.Generic;
using System.IO;
using Leap;
using Leap.Encoding;
using MetaColocationDemos.Networking;
using Unity.Netcode;
using UnityEngine;

namespace MetaColocationDemos.Recording
{
    /// <summary>
    /// Records the VR user's head pose (via VrHeadPoseNetworkSync.HeadAnchor) and the PC user's hand poses
    /// (via LeapHandNetworkSync's hand models) to a file, and plays a recorded file back onto a shared
    /// environment prop like the Lambertian prefab - a standalone "ghost" meant to replay a session for
    /// everyone in it, rather than being driven live by any one client.
    ///
    /// Both recording sources are read off the network sync components, not a local OVRCameraRig/LeapProvider
    /// - so recording works the same wherever RecordManager runs in the session, and doesn't care whether the
    /// VR user or PC user it's watching is the owner (live tracking) or a non-owner (already-decoded network
    /// data) copy. Both are resolved lazily and re-tried while missing, since RecordManager lives in
    /// Session_Setup and VRUser/PCUser rigs are spawned later, only once a matching client connects (see
    /// SessionManager) - and only the first of each found is used, so this doesn't yet handle more than one
    /// VR/PC user in the same session.
    ///
    /// Playback is different: since Lambertian is shared (a NetworkObject that must look the same to every
    /// client, not something any one client owns), only the server may actually drive it - its Transform
    /// sits under a server-authoritative NetworkTransform so writes replicate outward, and hand poses are
    /// pushed through its RecordedHandNetworkSync, which is server-write by design. On a non-server client,
    /// playback still advances internally (so StartRecording/StopPlayback etc. stay consistent if this same
    /// file also runs there) but doesn't touch either target - it just waits to see the server's replicated
    /// result like everyone else.
    ///
    /// Lambertian itself isn't wired in by reference: it lives in Environment.unity, which only exists at
    /// runtime (loaded additively by the server once a session starts - see
    /// SessionManager.LoadEnvironmentSceneOnServer), so there's nothing to drag into an Inspector field here
    /// at edit time. Its RecordedHandNetworkSync is resolved lazily via FindObjectOfType instead, same as the
    /// recording-side sources above - and its own Transform doubles as the head playback target, since it
    /// lives on the same GameObject as Lambertian's NetworkObject/NetworkTransform.
    ///
    /// Recordings are plain files under RecordingsDirectory (Application.persistentDataPath/Recordings/), so
    /// they already survive independently of any single Play session or app run - nothing here is in-memory
    /// only. Each StartRecording/StopRecording pass is saved as its own new, timestamped file rather than
    /// overwriting a fixed name, building up a library that RefreshLibrary/SelectNextRecording/
    /// SelectPreviousRecording page through.
    /// </summary>
    public class RecordManager : MonoBehaviour
    {
        private const float SampleRateHz = 30f;
        private const float SampleInterval = 1f / SampleRateHz;
        private const int FileMagic = 0x43455248; // "HREC"
        private const int FileVersion = 1;
        private const string RecordingsDirectoryName = "Recordings";
        private const string RecordingFileExtension = ".hprec";

        [Header("Debug")]
        [Tooltip("F9 starts/stops recording (each stop saves a new file into the library). Left/Right arrows " +
                 "page through the library and preview whichever one is selected. F10 plays the selected " +
                 "recording in full (position + rotation); F11 plays it rotation-only, mirrored, from " +
                 "Lambertian's own spot. L toggles looping through the whole library back-to-back. All for " +
                 "quick manual testing before any UI is wired up.")]
        [SerializeField] private bool enableDebugHotkeys = true;

        private struct Frame
        {
            public float Time;
            public Vector3 HeadPosition;
            public Quaternion HeadRotation;
            public bool LeftTracked;
            public bool RightTracked;
            public byte[] LeftHandBytes;
            public byte[] RightHandBytes;
        }

        // Extra 180-degree yaw applied to the recorded head rotation in F11's rotation-only mode, so
        // Lambertian faces the viewer (mirroring them) instead of facing the same way the recorded VR user did.
        private static readonly Quaternion MirrorRotation = Quaternion.Euler(0f, 180f, 0f);

        private readonly List<Frame> _frames = new();

        // Reused across frames so encoding/decoding doesn't allocate every sample, same reasoning as
        // LeapHandNetworkSync's identical fields. _leftHandBuffer/_rightHandBuffer hold a head-relative hand
        // (see HandSpaceTransform) on both sides: written by ToLocal before encoding while recording, and by
        // Decode before ToWorld while playing back - recording and playback never happen at the same time, so
        // reusing the same buffer for both is safe. _leftWorldHandBuffer/_rightWorldHandBuffer only exist for
        // playback, holding the hand re-anchored onto Lambertian's current head pose before re-encoding.
        private readonly VectorHand _leftVectorHand = new();
        private readonly VectorHand _rightVectorHand = new();
        private readonly Hand _leftHandBuffer = new();
        private readonly Hand _rightHandBuffer = new();
        private readonly Hand _leftWorldHandBuffer = new();
        private readonly Hand _rightWorldHandBuffer = new();

        private VrHeadPoseNetworkSync _vrHeadSource;
        private LeapHandNetworkSync _pcHandSource;
        private float _timeSinceLastSample;
        private float _recordingStartTime;

        // Playback target - see the class doc comment for why this is resolved at runtime instead of an
        // Inspector reference. _homePosition is Lambertian's own authored position in Environment, captured
        // the moment _playbackTarget is first resolved (before any playback has had a chance to move it) -
        // F11 mode restores this every frame rather than just leaving whatever position F10 last left it at.
        private RecordedHandNetworkSync _playbackTarget;
        private Vector3 _homePosition;

        private Frame[] _loadedFrames;
        private int _playbackFrameIndex;
        private float _playbackStartTime;
        private bool _playbackKeepOwnPosition;

        // The recording library: every .hprec file under RecordingsDirectory, sorted (timestamped filenames
        // sort chronologically), plus which one is currently selected for F10/F11/looping to act on. Built by
        // RefreshLibrary, which scans disk fresh each time rather than trying to keep this incrementally in
        // sync with StartRecording/StopRecording alone - simpler, and cheap enough at library sizes a person
        // is actually going to page through by hand.
        private readonly List<string> _libraryPaths = new();
        private int _selectedLibraryIndex = -1;
        private bool _loopLibrary;

        public bool IsRecording { get; private set; }

        public bool IsPlaying { get; private set; }

        public bool IsLoopingLibrary => _loopLibrary;

        // Live readouts for RecordManagerEditor - frame counts, not seconds, since that's what's actually
        // being accumulated/played moment to moment.
        public int RecordingFrameCount => _frames.Count;

        public int PlaybackFrameCount => _loadedFrames?.Length ?? 0;

        public int PlaybackFrameIndex => _playbackFrameIndex;

        public int LibraryCount => _libraryPaths.Count;

        public int SelectedLibraryIndex => _selectedLibraryIndex;

        // Exposed for RecordManagerEditor - a read-only view so the Inspector can list what's in the library
        // without being able to mutate _libraryPaths itself out from under RefreshLibrary/SelectRecording.
        public IReadOnlyList<string> LibraryPaths => _libraryPaths;

        public static string RecordingsDirectory =>
            Path.Combine(Application.persistentDataPath, RecordingsDirectoryName);

        private void Update()
        {
            if (enableDebugHotkeys) HandleDebugHotkeys();

            if (IsRecording) SampleIfDue();
            if (IsPlaying) AdvancePlayback();
        }

        private void HandleDebugHotkeys()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                if (IsRecording) StopRecording();
                else StartRecording();
            }

            // Always (re)starts from the beginning rather than toggling to a stop - F10/F11 are meant purely
            // for "play the selected recording again" while iterating, not a play/pause control.
            if (Input.GetKeyDown(KeyCode.F10) && !IsRecording)
            {
                PlaySelected(keepOwnPosition: false);
            }

            // Rotation-only variant: Lambertian stays wherever it's placed in Environment and just turns to
            // face the recorded head orientation, instead of also being moved to the VR user's recorded
            // world position (which may be nowhere near Lambertian, or off-screen entirely).
            if (Input.GetKeyDown(KeyCode.F11) && !IsRecording)
            {
                PlaySelected(keepOwnPosition: true);
            }

            if (Input.GetKeyDown(KeyCode.LeftArrow) && !IsRecording) SelectPreviousRecording();
            if (Input.GetKeyDown(KeyCode.RightArrow) && !IsRecording) SelectNextRecording();

            if (Input.GetKeyDown(KeyCode.L)) SetLibraryLooping(!_loopLibrary);
        }

        // Exposed (rather than a bare setter on the backing field) so RecordManagerEditor's loop checkbox and
        // the L hotkey both go through the same path and log consistently.
        public void SetLibraryLooping(bool enabled)
        {
            if (_loopLibrary == enabled) return;

            _loopLibrary = enabled;
            Debug.Log($"{nameof(RecordManager)}: library looping {(enabled ? "enabled" : "disabled")}.");
        }

        // --- Recording ---

        public void StartRecording()
        {
            if (IsRecording) return;
            if (IsPlaying) StopPlayback();

            _frames.Clear();
            _recordingStartTime = Time.time;
            _timeSinceLastSample = 0f;
            IsRecording = true;
            Debug.Log($"{nameof(RecordManager)}: recording started.");
        }

        // Always saves as a new file (timestamped, so back-to-back recordings never collide) rather than
        // overwriting a fixed path - each stop grows the library instead of replacing what's in it.
        public void StopRecording()
        {
            if (!IsRecording) return;

            IsRecording = false;

            var path = Path.Combine(RecordingsDirectory, $"recording_{System.DateTime.Now:yyyyMMdd_HHmmss}{RecordingFileExtension}");
            Save(path);
            Debug.Log($"{nameof(RecordManager)}: recording stopped, {_frames.Count} frames saved to {path}.");

            RefreshLibrary();
            SelectRecording(_libraryPaths.IndexOf(path));
        }

        // --- Library ---

        // Rescans RecordingsDirectory from disk - cheap enough for a library sized for manual paging, and
        // means the library reflects whatever's actually on disk (including files dropped in from another
        // session) rather than an in-memory list that could drift from it.
        public void RefreshLibrary()
        {
            var selectedPath = _selectedLibraryIndex >= 0 && _selectedLibraryIndex < _libraryPaths.Count
                ? _libraryPaths[_selectedLibraryIndex] : null;

            _libraryPaths.Clear();
            Directory.CreateDirectory(RecordingsDirectory);
            _libraryPaths.AddRange(Directory.GetFiles(RecordingsDirectory, $"*{RecordingFileExtension}"));
            _libraryPaths.Sort();

            // Keep pointing at the same file across a refresh if it's still there (e.g. after StopRecording
            // adds a new one), rather than resetting selection back to the start of the library.
            _selectedLibraryIndex = selectedPath != null ? _libraryPaths.IndexOf(selectedPath) : -1;
        }

        // Deletes one library entry from disk by its current index (see RecordManagerEditor's per-row Delete
        // button - IndexOf isn't used here since the same file could theoretically appear twice in weird
        // filesystem edge cases, and the caller already has the index from what it's currently displaying).
        public void DeleteRecording(int index)
        {
            if (index < 0 || index >= _libraryPaths.Count) return;

            var path = _libraryPaths[index];

            // Stop playback first if this is the recording actually loaded/playing right now - selection and
            // playback are always kept in sync (see PlaySelected/SelectNextRecording/SelectPreviousRecording),
            // so index == _selectedLibraryIndex while IsPlaying means this is that recording.
            if (IsPlaying && index == _selectedLibraryIndex) StopPlayback();

            try
            {
                File.Delete(path);
                Debug.Log($"{nameof(RecordManager)}: deleted {path}.");
            }
            catch (IOException e)
            {
                Debug.LogError($"{nameof(RecordManager)}: couldn't delete {path}: {e.Message}");
                return;
            }

            RefreshLibrary();
        }

        public void SelectRecording(int index)
        {
            if (_libraryPaths.Count == 0)
            {
                _selectedLibraryIndex = -1;
                return;
            }

            // Wraps rather than clamps - Left/Right are meant to page through the whole library in a loop.
            _selectedLibraryIndex = ((index % _libraryPaths.Count) + _libraryPaths.Count) % _libraryPaths.Count;
        }

        public void SelectNextRecording()
        {
            if (_libraryPaths.Count == 0) RefreshLibrary();
            if (_libraryPaths.Count == 0) return;

            SelectRecording(_selectedLibraryIndex + 1);
            PlaySelected(_playbackKeepOwnPosition);
        }

        public void SelectPreviousRecording()
        {
            if (_libraryPaths.Count == 0) RefreshLibrary();
            if (_libraryPaths.Count == 0) return;

            SelectRecording(_selectedLibraryIndex - 1);
            PlaySelected(_playbackKeepOwnPosition);
        }

        // Plays whichever recording is currently selected, refreshing/defaulting to the most recent one if
        // nothing's selected yet (e.g. the very first F10/F11 press in a session). Public so
        // RecordManagerEditor's Play buttons can trigger the exact same behavior as F10/F11.
        public void PlaySelected(bool keepOwnPosition)
        {
            if (_libraryPaths.Count == 0) RefreshLibrary();
            if (_libraryPaths.Count == 0)
            {
                Debug.LogWarning($"{nameof(RecordManager)}: no recordings in the library yet ({RecordingsDirectory}).");
                return;
            }

            if (_selectedLibraryIndex < 0) SelectRecording(_libraryPaths.Count - 1);

            if (IsPlaying) StopPlayback();
            PlayRecording(_libraryPaths[_selectedLibraryIndex], keepOwnPosition);
        }

        private void SampleIfDue()
        {
            if (_vrHeadSource == null) _vrHeadSource = FindObjectOfType<VrHeadPoseNetworkSync>(includeInactive: true);
            if (_pcHandSource == null) _pcHandSource = FindObjectOfType<LeapHandNetworkSync>(includeInactive: true);

            _timeSinceLastSample += Time.deltaTime;
            if (_timeSinceLastSample < SampleInterval) return;
            _timeSinceLastSample -= SampleInterval;

            var frame = new Frame
            {
                Time = Time.time - _recordingStartTime,
                // Explicit identity, not the struct default - see VrHeadPoseNetworkSync's identical field for
                // why default(Quaternion) (0,0,0,0) is a degenerate/zero-length quaternion, not identity.
                HeadRotation = Quaternion.identity,
                LeftHandBytes = new byte[VectorHand.NUM_BYTES],
                RightHandBytes = new byte[VectorHand.NUM_BYTES],
            };

            var headAnchor = _vrHeadSource != null ? _vrHeadSource.HeadAnchor : null;
            if (headAnchor == null)
            {
                // No head to record relative to yet, so hands can't be meaningfully stored either (see
                // HandSpaceTransform) - this frame just records "nothing happened", same as it would with no
                // VR user connected at all.
                _frames.Add(frame);
                return;
            }

            frame.HeadPosition = headAnchor.position;
            frame.HeadRotation = headAnchor.rotation;

            if (_pcHandSource != null)
            {
                var left = _pcHandSource.LeftHandModel != null ? _pcHandSource.LeftHandModel.GetLeapHand() : null;
                var right = _pcHandSource.RightHandModel != null ? _pcHandSource.RightHandModel.GetLeapHand() : null;

                frame.LeftTracked = left != null;
                frame.RightTracked = right != null;

                // Stored relative to the head, not as absolute world positions - see HandSpaceTransform for
                // why: it's what lets playback re-anchor the hands onto Lambertian's own head pose instead of
                // the VR user's original recording-time world position.
                if (left != null)
                {
                    HandSpaceTransform.ToLocal(left, frame.HeadPosition, frame.HeadRotation, _leftHandBuffer);
                    _leftVectorHand.Encode(_leftHandBuffer);
                    _leftVectorHand.FillBytes(frame.LeftHandBytes);
                }
                if (right != null)
                {
                    HandSpaceTransform.ToLocal(right, frame.HeadPosition, frame.HeadRotation, _rightHandBuffer);
                    _rightVectorHand.Encode(_rightHandBuffer);
                    _rightVectorHand.FillBytes(frame.RightHandBytes);
                }
            }

            _frames.Add(frame);
        }

        // --- Playback ---

        public void PlayRecording(string path, bool keepOwnPosition = false)
        {
            if (IsRecording) return;

            _loadedFrames = Load(path);
            if (_loadedFrames == null || _loadedFrames.Length == 0)
            {
                Debug.LogWarning($"{nameof(RecordManager)}: no frames to play back from {path}.");
                return;
            }

            _playbackFrameIndex = 0;
            _playbackStartTime = Time.time;
            _playbackKeepOwnPosition = keepOwnPosition;
            IsPlaying = true;
            Debug.Log($"{nameof(RecordManager)}: playing back {_loadedFrames.Length} frames from {path}" +
                      $"{(keepOwnPosition ? " (rotation only)" : "")}.");
        }

        public void StopPlayback()
        {
            if (!IsPlaying) return;

            IsPlaying = false;

            if (CanDrivePlayback && _playbackTarget != null) _playbackTarget.ServerHideHands();
        }

        // Lambertian is a shared environment prop, not something any one client owns - only the server may
        // drive it (see the class doc comment). A non-server client still advances _playbackFrameIndex below
        // so its own StartRecording/PlayRecording calls behave consistently if ever used, it just never
        // writes to the playback target - it observes the server's replicated result instead, same as every
        // other client watching Lambertian.
        private bool CanDrivePlayback => NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;

        private void AdvancePlayback()
        {
            if (CanDrivePlayback && _playbackTarget == null)
            {
                _playbackTarget = FindObjectOfType<RecordedHandNetworkSync>(includeInactive: true);
                if (_playbackTarget != null) _homePosition = _playbackTarget.transform.position;
            }

            var playbackTime = Time.time - _playbackStartTime;
            var previousFrameIndex = _playbackFrameIndex;

            while (_playbackFrameIndex < _loadedFrames.Length - 1 &&
                   _loadedFrames[_playbackFrameIndex + 1].Time <= playbackTime)
            {
                _playbackFrameIndex++;
            }

            if (CanDrivePlayback && _playbackTarget != null)
            {
                ApplyFrame(_loadedFrames[_playbackFrameIndex], _playbackFrameIndex != previousFrameIndex);
            }

            if (_playbackFrameIndex < _loadedFrames.Length - 1) return;

            // Reached the end naturally (as opposed to being interrupted by a manual F9/F10/F11/arrow press,
            // which all stop playback through other paths) - with looping on, advance to the next library
            // entry and keep going instead of just stopping, so the whole library plays back-to-back on a
            // loop. StopPlayback still runs first either way, so hands get hidden for the instant between one
            // recording ending and the next one's first frame applying.
            StopPlayback();
            if (_loopLibrary)
            {
                SelectRecording(_selectedLibraryIndex + 1);
                PlaySelected(_playbackKeepOwnPosition);
            }
        }

        private void ApplyFrame(Frame frame, bool isNewFrame)
        {
            if (_playbackKeepOwnPosition)
            {
                // Snaps back to wherever Lambertian is actually placed in Environment (not just "leaves
                // position untouched", which would still show wherever F10 last moved it to) and faces the
                // viewer (mirrored) instead of facing the same way the recorded VR user did.
                _playbackTarget.transform.SetPositionAndRotation(_homePosition, frame.HeadRotation * MirrorRotation);
            }
            else
            {
                // "Becomes" the VR user: moves to their exact recorded position/rotation.
                _playbackTarget.transform.SetPositionAndRotation(frame.HeadPosition, frame.HeadRotation);
            }

            // Hands only need pushing when the recorded frame actually advances (~30Hz, matching how it was
            // sampled) - re-sending the same bytes every render frame would just spam the NetworkVariable
            // with no-op writes.
            if (!isNewFrame) return;

            // Recorded bytes are head-relative (see SampleIfDue/HandSpaceTransform) - re-anchor them onto
            // whatever head pose Lambertian is actually showing this frame (its own fixed+mirrored pose in
            // F11 mode, or the recorded pose verbatim in F10 mode, which round-trips back to the same
            // absolute hand positions the recording captured) before pushing to every client.
            var headPosition = _playbackTarget.transform.position;
            var headRotation = _playbackTarget.transform.rotation;

            var leftBytes = ReanchorHandBytes(frame.LeftTracked, frame.LeftHandBytes, headPosition, headRotation,
                                               _leftVectorHand, _leftHandBuffer, _leftWorldHandBuffer);
            var rightBytes = ReanchorHandBytes(frame.RightTracked, frame.RightHandBytes, headPosition, headRotation,
                                                _rightVectorHand, _rightHandBuffer, _rightWorldHandBuffer);

            _playbackTarget.ServerSetHands(frame.LeftTracked, leftBytes, frame.RightTracked, rightBytes);
        }

        private static byte[] ReanchorHandBytes(bool tracked, byte[] recordedBytes, Vector3 headPosition, Quaternion headRotation,
                                                 VectorHand vectorHand, Hand localBuffer, Hand worldBuffer)
        {
            if (!tracked || recordedBytes == null) return null;

            vectorHand.ReadBytes(recordedBytes);
            vectorHand.Decode(localBuffer);
            HandSpaceTransform.ToWorld(localBuffer, headPosition, headRotation, worldBuffer);
            vectorHand.Encode(worldBuffer);

            // A fresh array every call, not a reused buffer - RecordedHandNetworkSync's NetworkVariable dirty
            // check compares LeapHandsData by field equality, and byte[] equality is by reference. Reusing a
            // buffer here means the reference never changes even though its contents do, so Netcode would
            // decide nothing changed and stop replicating hand updates after the first frame - same gotcha
            // LeapHandNetworkSync.SendHandsIfDue already documents for the exact same reason.
            var bytes = new byte[VectorHand.NUM_BYTES];
            vectorHand.FillBytes(bytes);
            return bytes;
        }

        // --- Trimming ---

        // Cuts the first trimStartSeconds and last trimEndSeconds off a library recording and saves the
        // result as a new file, re-basing every kept frame's Time so playback still starts at 0 - meant for
        // cutting out e.g. the reach-for-the-keyboard-to-hit-record moment at the start/end of a take.
        // Doesn't touch the original file (see DeleteRecording if you don't want to keep it around after
        // confirming the trim looks right) - a bad trim amount shouldn't be able to destroy the only copy.
        // Pure file I/O, no network/session dependency, so this works in Edit Mode as well as Play mode.
        public string TrimRecording(int index, float trimStartSeconds, float trimEndSeconds)
        {
            if (index < 0 || index >= _libraryPaths.Count) return null;

            var sourcePath = _libraryPaths[index];
            var frames = Load(sourcePath);
            if (frames == null || frames.Length == 0)
            {
                Debug.LogWarning($"{nameof(RecordManager)}: {sourcePath} has no frames to trim.");
                return null;
            }

            var totalDuration = frames[^1].Time;
            var trimmed = new List<Frame>(frames.Length);
            foreach (var frame in frames)
            {
                if (frame.Time < trimStartSeconds) continue;
                if (frame.Time > totalDuration - trimEndSeconds) continue;

                var rebased = frame;
                rebased.Time -= trimStartSeconds;
                trimmed.Add(rebased);
            }

            if (trimmed.Count == 0)
            {
                Debug.LogWarning($"{nameof(RecordManager)}: trimming {trimStartSeconds:F1}s from the start and " +
                                  $"{trimEndSeconds:F1}s from the end of {sourcePath} (duration {totalDuration:F1}s) " +
                                  "would remove every frame - not saving anything.");
                return null;
            }

            var destPath = UniqueTrimmedPath(sourcePath);
            SaveFrames(trimmed, destPath);
            Debug.Log($"{nameof(RecordManager)}: trimmed {sourcePath} ({frames.Length} frames, {totalDuration:F1}s) " +
                      $"-> {destPath} ({trimmed.Count} frames, {trimmed[^1].Time:F1}s).");

            RefreshLibrary();
            return destPath;
        }

        private static string UniqueTrimmedPath(string sourcePath)
        {
            var directory = Path.GetDirectoryName(sourcePath);
            var name = Path.GetFileNameWithoutExtension(sourcePath);

            var candidate = Path.Combine(directory, $"{name}_trimmed{RecordingFileExtension}");
            var suffix = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(directory, $"{name}_trimmed{suffix}{RecordingFileExtension}");
                suffix++;
            }
            return candidate;
        }

        // --- File I/O ---

        private void Save(string path) => SaveFrames(_frames, path);

        private static void SaveFrames(IReadOnlyList<Frame> frames, string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);

            writer.Write(FileMagic);
            writer.Write(FileVersion);
            writer.Write(frames.Count);

            foreach (var frame in frames)
            {
                writer.Write(frame.Time);
                WriteVector3(writer, frame.HeadPosition);
                WriteQuaternion(writer, frame.HeadRotation);
                writer.Write(frame.LeftTracked);
                writer.Write(frame.RightTracked);
                writer.Write(frame.LeftHandBytes);
                writer.Write(frame.RightHandBytes);
            }
        }

        private static Frame[] Load(string path)
        {
            if (!File.Exists(path))
            {
                Debug.LogWarning($"{nameof(RecordManager)}: no recording file at {path}.");
                return null;
            }

            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (reader.ReadInt32() != FileMagic || reader.ReadInt32() != FileVersion)
            {
                Debug.LogError($"{nameof(RecordManager)}: {path} isn't a recognised recording file.");
                return null;
            }

            var frames = new Frame[reader.ReadInt32()];
            for (var i = 0; i < frames.Length; i++)
            {
                frames[i] = new Frame
                {
                    Time = reader.ReadSingle(),
                    HeadPosition = ReadVector3(reader),
                    HeadRotation = ReadQuaternion(reader),
                    LeftTracked = reader.ReadBoolean(),
                    RightTracked = reader.ReadBoolean(),
                    LeftHandBytes = reader.ReadBytes(VectorHand.NUM_BYTES),
                    RightHandBytes = reader.ReadBytes(VectorHand.NUM_BYTES),
                };
            }
            return frames;
        }

        private static void WriteVector3(BinaryWriter writer, Vector3 v)
        {
            writer.Write(v.x);
            writer.Write(v.y);
            writer.Write(v.z);
        }

        private static Vector3 ReadVector3(BinaryReader reader) =>
            new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        private static void WriteQuaternion(BinaryWriter writer, Quaternion q)
        {
            writer.Write(q.x);
            writer.Write(q.y);
            writer.Write(q.z);
            writer.Write(q.w);
        }

        private static Quaternion ReadQuaternion(BinaryReader reader) =>
            new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}
