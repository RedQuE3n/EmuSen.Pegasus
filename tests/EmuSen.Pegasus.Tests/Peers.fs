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

/// A live identity the caller owns, for tests that need to sign rather than
/// just be named. Not disposed here, unlike `named` above -- the caller holds it.
let identity (handle: string) = Identity.Generate(Handle.Parse handle)

/// The trust rule for tests that are not about trust: take whoever turns up.
///
/// Deliberately not the default anywhere in the application. A session refusing
/// to decide who it trusts, and making the caller say, is what lets this exist
/// without weakening what ships.
let acceptAny: PeerInfo -> byte[] -> Result<unit, string> = fun _ _ -> Ok()
