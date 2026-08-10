module EmuSen.Pegasus.Tests.Peers

open EmuSen.Pegasus

/// A throwaway identity with a real keypair, so a test builds its PeerInfo the
/// same way the application does. See Pegasus_Identity.md §6.
let named (handle: string) =
    use identity = Identity.Generate(Handle.Parse handle)
    identity.Peer
