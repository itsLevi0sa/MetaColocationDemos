using Unity.Netcode;

namespace MetaColocationDemos.Networking
{
    /// <summary>
    /// Wire format for one frame of left/right Leap hand poses, VectorHand-encoded (Ultraleap's compact
    /// 86-byte-per-hand encoding). Shared by LeapHandNetworkSync (live tracking, owner-written) and
    /// RecordedHandNetworkSync (recorded playback, server-written) so both decode through the same
    /// HandPoseApplier path.
    /// </summary>
    public struct LeapHandsData : INetworkSerializable
    {
        public bool LeftTracked;
        public bool RightTracked;
        public byte[] LeftHandBytes;
        public byte[] RightHandBytes;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref LeftTracked);
            serializer.SerializeValue(ref RightTracked);
            serializer.SerializeValue(ref LeftHandBytes);
            serializer.SerializeValue(ref RightHandBytes);
        }
    }
}
