using Leap;
using UnityEngine;

namespace MetaColocationDemos.Networking
{
    /// <summary>
    /// Re-expresses a Leap.Hand's pose relative to a given head position/rotation, or the reverse. A raw
    /// Leap.Hand from LeapProvider.GetHand() is in world space, tied to wherever the tracking device physically
    /// was - fine for live network sync (see LeapHandNetworkSync), where the receiving hand models always sit
    /// under the SAME replicated object the hand was captured from. It's wrong for RecordManager's recorded
    /// playback though: the hands need to appear next to Lambertian's head, not at the VR user's original
    /// world position from when the recording was made. ToLocal converts a captured world-space Hand into an
    /// offset from the recording head's pose; ToWorld converts it back using whatever head pose it should
    /// appear relative to during playback (which may be a different position/rotation than it was recorded at
    /// - see RecordManager's F11 rotation-only/mirrored mode).
    ///
    /// Covers the same field set HandPoseApplier's LerpHand already treats as "what matters for rendering".
    /// </summary>
    public static class HandSpaceTransform
    {
        public static void ToLocal(Hand worldHand, Vector3 headPosition, Quaternion headRotation, Hand result)
        {
            var inverseRotation = Quaternion.Inverse(headRotation);
            Apply(worldHand, inverseRotation, inverseRotation * -headPosition, result);
        }

        public static void ToWorld(Hand localHand, Vector3 headPosition, Quaternion headRotation, Hand result)
        {
            Apply(localHand, headRotation, headPosition, result);
        }

        // Rigid-translates a whole hand by a fixed offset, no rotation - used by HandPoseApplier.ApplyWithGrace
        // to slide a hand along its last known velocity during a brief tracking-loss grace period, without
        // otherwise reshaping it.
        public static void Translate(Hand hand, Vector3 translation, Hand result)
        {
            Apply(hand, Quaternion.identity, translation, result);
        }

        // Rigid transform: position' = rotation * position + translation; direction' = rotation * direction
        // (directions/normals are orientations, not points, so they never translate); rotation' = rotation *
        // original rotation.
        private static void Apply(Hand hand, Quaternion rotation, Vector3 translation, Hand result)
        {
            result.Id = hand.Id;
            result.Confidence = hand.Confidence;
            result.GrabStrength = hand.GrabStrength;
            result.PinchStrength = hand.PinchStrength;
            result.PinchDistance = hand.PinchDistance;
            result.PalmWidth = hand.PalmWidth;
            result.IsLeft = hand.IsLeft;
            result.TimeVisible = hand.TimeVisible;

            result.Rotation = rotation * hand.Rotation;
            result.PalmPosition = rotation * hand.PalmPosition + translation;
            result.StabilizedPalmPosition = rotation * hand.StabilizedPalmPosition + translation;
            result.PalmVelocity = rotation * hand.PalmVelocity;
            result.PalmNormal = rotation * hand.PalmNormal;
            result.Direction = rotation * hand.Direction;
            result.WristPosition = rotation * hand.WristPosition + translation;

            ApplyBone(hand.Arm, rotation, translation, result.Arm);

            for (var i = 0; i < 5; i++)
            {
                ApplyFinger(hand.fingers[i], rotation, translation, result.fingers[i]);
            }
        }

        private static void ApplyFinger(Finger from, Quaternion rotation, Vector3 translation, Finger result)
        {
            result.Id = from.Id;
            result.HandId = from.HandId;
            result.TimeVisible = from.TimeVisible;
            result.Width = from.Width;
            result.Length = from.Length;
            result.IsExtended = from.IsExtended;
            result.Type = from.Type;

            result.TipPosition = rotation * from.TipPosition + translation;
            result.Direction = rotation * from.Direction;

            for (var i = 0; i < 4; i++)
            {
                ApplyBone(from.bones[i], rotation, translation, result.bones[i]);
            }
        }

        // Also used for Hand.Arm - Arm is-a Bone (see Leap.Arm), same field set.
        private static void ApplyBone(Bone from, Quaternion rotation, Vector3 translation, Bone result)
        {
            result.Length = from.Length;
            result.Width = from.Width;
            result.Type = from.Type;

            result.PrevJoint = rotation * from.PrevJoint + translation;
            result.NextJoint = rotation * from.NextJoint + translation;
            result.Center = rotation * from.Center + translation;
            result.Direction = rotation * from.Direction;
            result.Rotation = rotation * from.Rotation;
        }
    }
}
