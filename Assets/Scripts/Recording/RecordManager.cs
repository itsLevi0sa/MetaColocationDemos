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
    /// </summary>
    public class RecordManager : MonoBehaviour
    {
        private const float SampleRateHz = 30f;
        private const float SampleInterval = 1f / SampleRateHz;
        private const int FileMagic = 0x43455248; // "HREC"
        private const int FileVersion = 1;

        [Header("Debug")]
        [Tooltip("F9 starts/stops recording to DefaultRecordingPath, F10 plays/stops that same file - for " +
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

        public bool IsRecording { get; private set; }

        public bool IsPlaying { get; private set; }

        public static string DefaultRecordingPath =>
            Path.Combine(Application.persistentDataPath, "Recordings", "recording.hprec");

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
                if (IsRecording) StopRecording(DefaultRecordingPath);
                else StartRecording();
            }

            // Always (re)starts from the beginning rather than toggling to a stop - F10/F11 are meant purely
            // for "play the recording again" while iterating, not a play/pause control.
            if (Input.GetKeyDown(KeyCode.F10) && !IsRecording)
            {
                if (IsPlaying) StopPlayback();
                PlayRecording(DefaultRecordingPath, keepOwnPosition: false);
            }

            // Rotation-only variant: Lambertian stays wherever it's placed in Environment and just turns to
            // face the recorded head orientation, instead of also being moved to the VR user's recorded
            // world position (which may be nowhere near Lambertian, or off-screen entirely).
            if (Input.GetKeyDown(KeyCode.F11) && !IsRecording)
            {
                if (IsPlaying) StopPlayback();
                PlayRecording(DefaultRecordingPath, keepOwnPosition: true);
            }
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

        public void StopRecording(string path)
        {
            if (!IsRecording) return;

            IsRecording = false;
            Save(path);
            Debug.Log($"{nameof(RecordManager)}: recording stopped, {_frames.Count} frames saved to {path}.");
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

            if (_playbackFrameIndex >= _loadedFrames.Length - 1) StopPlayback();
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

        // --- File I/O ---

        private void Save(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);

            writer.Write(FileMagic);
            writer.Write(FileVersion);
            writer.Write(_frames.Count);

            foreach (var frame in _frames)
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
