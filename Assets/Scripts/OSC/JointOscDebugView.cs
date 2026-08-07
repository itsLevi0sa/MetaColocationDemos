using System;
using System.Collections.Generic;
using UnityEngine;

namespace MetaColocationDemos.OSC
{
    /// <summary>
    /// Read-only debug view of the joint values JointCubeOscManager is currently sending over OSC - shows
    /// every connected player's data live in the Inspector, including a new player's entry appearing the
    /// moment their avatar spawns and disappearing when they disconnect. JointCubeOscManager pushes a full
    /// snapshot here every frame; editing these values in the Inspector has no effect since they're
    /// overwritten right away.
    /// </summary>
    public class JointOscDebugView : MonoBehaviour
    {
        [Serializable]
        public class JointValue
        {
            public string Joint;

            public string PositionAddress;
            public Vector3 Position;

            public string RotationAddress;
            // The 4 raw floats sent over OSC, in send order (x, y, z, w) - a Vector4 rather than Quaternion
            // so the Inspector shows exactly those transmitted values instead of Unity's Quaternion widget.
            public Vector4 Rotation;
        }

        [Serializable]
        public class PlayerValues
        {
            public ulong OwnerClientId;
            public List<JointValue> Joints = new();
        }

        [SerializeField] private List<PlayerValues> players = new();

        public void SetSnapshot(List<PlayerValues> snapshot)
        {
            players = snapshot;
        }
    }
}
