using Unity.Netcode.Components;

namespace MetaColocationDemos.Spectator
{
    /// <summary>
    /// Standard NetworkTransform is server-authoritative: only the server may move the object, and
    /// non-server owners have their writes silently rejected. The spectator camera is driven purely
    /// locally by SpectatorFlyCamera on whichever client is spectating, so it needs owner authority
    /// instead - the owning client's transform is the source of truth and gets replicated outward.
    /// </summary>
    public class OwnerNetworkTransform : NetworkTransform
    {
        protected override bool OnIsServerAuthoritative() => false;
    }
}
