using Leap;
using Leap.Encoding;
using UnityEngine;

namespace MetaColocationDemos.Networking
{
    /// <summary>
    /// Decodes VectorHand-encoded bytes onto a HandModelBase (e.g. a HandBinder under GhostHands), with or
    /// without blending between two decoded poses. Extracted out of LeapHandNetworkSync so
    /// RecordedHandNetworkSync (recorded playback) can drive the exact same HandModelBase lifecycle
    /// (InitHand/BeginHand/UpdateHandWithEvent/FinishHand) as live network sync does, instead of a second
    /// copy of this logic drifting out of sync with it over time.
    /// </summary>
    public static class HandPoseApplier
    {
        // How long to keep showing a hand after it's reported untracked before actually hiding it. Raw Leap
        // tracking briefly drops a hand for a frame or two all the time (edge of FOV, fast motion, momentary
        // occlusion) - locally that's barely perceptible because Ultraleap's own rendering just keeps ticking,
        // but LeapHandNetworkSync only samples/sends tracked state at 30Hz, so any single missed sample used
        // to hide the hand outright and pop it back a frame later. This treats a loss as real only once it
        // outlasts a short grace window instead of on the very first untracked sample.
        private const float TrackingGraceSeconds = 0.25f;

        // Caps how far a hand can drift during the grace window (see ApplyInterpolated below) - without this,
        // a hand tracking loss that happens mid-fast-swipe (a large PalmVelocity) would fling the held hand a
        // visible distance instead of just gently continuing in place.
        private const float MaxGraceDriftMeters = 0.1f;

        // Instant apply, no blending - used for the first pose applied to a model (nothing to interpolate
        // from yet) by both LeapHandNetworkSync and RecordedHandNetworkSync.
        public static void ApplySnapshot(HandModelBase model, VectorHand vectorHand, Hand handBuffer, bool tracked, byte[] bytes)
        {
            if (model == null) return;

            if (!tracked || bytes == null)
            {
                Hide(model);
                return;
            }

            vectorHand.ReadBytes(bytes);
            vectorHand.Decode(handBuffer);
            Show(model, handBuffer);
        }

        // Blends between two already-decoded hands - t is unclamped past 1 by design (see
        // HumanoidPoseNetworkSync.ApplyInterpolatedPose for why: predicts forward on a late update instead of
        // freezing dead on target and visibly catching up).
        //
        // timeSinceLostTracking is how long ago targetTracked last flipped from true to false (see
        // LeapHandNetworkSync/RecordedHandNetworkSync's _leftLostTrackingTime) - +infinity/a very large value
        // if it's never been lost, which naturally always exceeds TrackingGraceSeconds and has no effect
        // whenever targetTracked is true anyway.
        public static void ApplyInterpolated(HandModelBase model, Hand previous, Hand target, Hand render,
                                              bool previousTracked, bool targetTracked, float t,
                                              float timeSinceLostTracking)
        {
            if (model == null) return;

            if (!targetTracked)
            {
                if (timeSinceLostTracking >= TrackingGraceSeconds)
                {
                    Hide(model);
                    return;
                }

                // Grace period: still within the hold window, so keep showing target (its last known tracked
                // pose - see OnHandsReceived, which stops overwriting it once untracked) drifting gently along
                // its last known velocity instead of freezing dead in place, capped so a fast-motion tracking
                // loss doesn't fling it.
                var drift = Vector3.ClampMagnitude(target.PalmVelocity * timeSinceLostTracking, MaxGraceDriftMeters);
                HandSpaceTransform.Translate(target, drift, render);
                Show(model, render);
                return;
            }

            // Can't interpolate from "no hand" - snap straight to the target on the first frame it's tracked.
            if (previousTracked)
            {
                LerpHand(previous, target, t, render);
            }
            else
            {
                render.CopyFrom(target);
            }

            Show(model, render);
        }

        public static void Hide(HandModelBase model)
        {
            if (model == null) return;

            model.SetLeapHand(null);
            if (model.IsTracked) model.FinishHand();
            // HandEnableDisable only reacts to its OWN LeapProvider's OnHandFound/OnHandLost events, which
            // never fire for a hand driven from network/recorded data - nothing else will hide this hand when
            // tracking is lost, so this has to do it directly.
            model.gameObject.SetActive(false);
        }

        private static void Show(HandModelBase model, Hand hand)
        {
            // Same reasoning in reverse - with HandEnableDisable not reacting to this data source, nothing
            // else will show this hand again once tracking resumes.
            model.gameObject.SetActive(true);
            model.SetLeapHand(hand);

            if (!model.IsTracked)
            {
                model.InitHand();
                model.BeginHand();
            }

            if (model.gameObject.activeInHierarchy) model.UpdateHandWithEvent();
        }

        // Mirrors CopyFromOtherExtensions.CopyFrom's field set exactly, just blending positions/rotations
        // instead of assigning them outright. Fields that don't benefit from interpolation (widths, lengths,
        // IDs, extended flags) are taken from the target, same as a plain CopyFrom would.
        private static void LerpHand(Hand from, Hand to, float t, Hand result)
        {
            result.Id = to.Id;
            result.Confidence = to.Confidence;
            result.GrabStrength = to.GrabStrength;
            result.Rotation = Quaternion.SlerpUnclamped(from.Rotation, to.Rotation, t);
            result.PinchStrength = to.PinchStrength;
            result.PinchDistance = to.PinchDistance;
            result.PalmWidth = to.PalmWidth;
            result.IsLeft = to.IsLeft;
            result.TimeVisible = to.TimeVisible;
            result.PalmPosition = Vector3.LerpUnclamped(from.PalmPosition, to.PalmPosition, t);
            result.StabilizedPalmPosition = Vector3.LerpUnclamped(from.StabilizedPalmPosition, to.StabilizedPalmPosition, t);
            result.PalmVelocity = Vector3.LerpUnclamped(from.PalmVelocity, to.PalmVelocity, t);
            result.PalmNormal = Vector3.SlerpUnclamped(from.PalmNormal, to.PalmNormal, t);
            result.Direction = Vector3.SlerpUnclamped(from.Direction, to.Direction, t);
            result.WristPosition = Vector3.LerpUnclamped(from.WristPosition, to.WristPosition, t);

            // Not interpolated - the forearm isn't fed into HandBinder's finger/wrist bones, so a one-frame
            // snap here whenever a new target arrives isn't perceptible the way finger choppiness would be.
            result.Arm.CopyFrom(to.Arm);

            for (var i = 0; i < 5; i++)
            {
                LerpFinger(from.fingers[i], to.fingers[i], t, result.fingers[i]);
            }
        }

        private static void LerpFinger(Finger from, Finger to, float t, Finger result)
        {
            for (var i = 0; i < 4; i++)
            {
                LerpBone(from.bones[i], to.bones[i], t, result.bones[i]);
            }

            result.Id = to.Id;
            result.HandId = to.HandId;
            result.TimeVisible = to.TimeVisible;
            result.TipPosition = Vector3.LerpUnclamped(from.TipPosition, to.TipPosition, t);
            result.Direction = Vector3.SlerpUnclamped(from.Direction, to.Direction, t);
            result.Width = to.Width;
            result.Length = to.Length;
            result.IsExtended = to.IsExtended;
            result.Type = to.Type;
        }

        private static void LerpBone(Bone from, Bone to, float t, Bone result)
        {
            result.PrevJoint = Vector3.LerpUnclamped(from.PrevJoint, to.PrevJoint, t);
            result.NextJoint = Vector3.LerpUnclamped(from.NextJoint, to.NextJoint, t);
            result.Direction = Vector3.SlerpUnclamped(from.Direction, to.Direction, t);
            result.Center = Vector3.LerpUnclamped(from.Center, to.Center, t);
            result.Length = to.Length;
            result.Width = to.Width;
            result.Rotation = Quaternion.SlerpUnclamped(from.Rotation, to.Rotation, t);
            result.Type = to.Type;
        }
    }
}
