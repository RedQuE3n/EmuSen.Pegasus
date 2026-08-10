module EmuSen.Pegasus.Tests.Peers

open EmuSen.Pegasus

/// A throwaway identity with a real keypair, for tests that need a peer but do
/// not care about sign-in.
///
/// It generates rather than faking a PeerInfo by hand, so the id and colour a
/// test sees are derived the same way the application derives them. A test that
/// built `{ Id = PeerId "abc"; ... }` would still pass if fingerprinting broke.
///
/// `use` disposes the ECDsa handle on the way out; Peer has already been
/// materialised by then and holds no reference to the key.
let named (handle: string) =
    use identity = Identity.Generate(Handle.Parse handle)
    identity.Peer
