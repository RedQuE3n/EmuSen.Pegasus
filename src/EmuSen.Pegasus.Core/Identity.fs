namespace EmuSen.Pegasus

open System
open System.Security.Cryptography

/// Both of a peer's visible marks -- its id and its caret colour -- are derived
/// from its public key, so they are stable for as long as the key is and cannot
/// be set to something misleading by hand.
///
/// Before this existed, PeerId was Guid.NewGuid() minted on every launch, which
/// meant a peer had no identity to recognise across a restart, not even a wrong
/// one.
module Fingerprint =

    /// A fixed palette indexed by the key, rather than a hue computed in a
    /// colour space. Computing one risks landing on a tint that is unreadable
    /// against the theme, or on two tints too close to tell apart; twelve
    /// hand-picked colours cannot. Extend the list rather than replacing it,
    /// since changing the order changes everybody's colour.
    let private palette =
        [| "#7c5cff"; "#2fa4a0"; "#d2691e"; "#4a90d9"
           "#c04a7a"; "#5aa02c"; "#b58900"; "#8f6fd0"
           "#e0533d"; "#2e8b8b"; "#a0522d"; "#3b7dd8" |]

    /// The first 8 bytes of SHA-256 over the public key, as 16 hex characters.
    ///
    /// Eight bytes is short enough for a human to compare down a phone line and
    /// long enough that nobody is going to collide with it by accident. It is
    /// NOT long enough to resist someone deliberately grinding out a key with a
    /// matching prefix, which will matter when keys are pinned and a mismatch
    /// has to be trusted -- until then nothing depends on it being unforgeable.
    let ofPublicKey (publicKey: byte[]) =
        let digest = SHA256.HashData publicKey
        PeerId(Convert.ToHexStringLower digest[0..7])

    /// Byte 8, deliberately not one of the eight the id uses, so two peers with
    /// visibly similar ids do not also get the same colour.
    let colourOf (publicKey: byte[]) =
        let digest = SHA256.HashData publicKey
        palette[int digest[8] % palette.Length]

/// One signed-in person: a handle bound to a keypair this machine holds.
///
/// The keypair is generated, stored, loaded and exercised, but NOTHING ON THE
/// WIRE IS SIGNED YET. A peer receiving a Hello has no way to tell whether the
/// sender holds the key its fingerprint names, so a displayed handle is a
/// convenience and not authentication. Sign and Verify exist so that the pass
/// which adds a signed challenge is a small step rather than a redesign, and so
/// the suite can prove the key survives the trip through the file.
///
/// Disposable because ECDsa holds a native key handle. Dropping one without
/// disposing leaves it to the finaliser rather than leaking outright, but the
/// key material stays in memory longer than it needs to.
type Identity private (handle: Handle, key: ECDsa) =
    // Exported once: it is asked for on every Peer read, and re-exporting per
    // call would hash the same bytes repeatedly for no reason.
    let publicKey = key.ExportSubjectPublicKeyInfo()

    member _.Handle = handle
    member _.PublicKey = publicKey
    member _.Fingerprint = Fingerprint.ofPublicKey publicKey

    /// What this identity looks like to the other peer.
    member this.Peer : PeerInfo =
        { Id = this.Fingerprint
          Handle = handle
          Color = Fingerprint.colourOf publicKey }

    member _.Sign(data: byte[]) = key.SignData(data, HashAlgorithmName.SHA256)

    member _.Verify(data: byte[], signature: byte[]) =
        key.VerifyData(data, signature, HashAlgorithmName.SHA256)

    /// P-256, and Ed25519 would have been the better choice.
    ///
    /// .NET 10 does not have Ed25519 -- System.Security.Cryptography offers
    /// ECDsa and the post-quantum MLDsa and nothing in between. Getting it means
    /// taking on NSec or BouncyCastle, and a dependency has to earn itself; for
    /// binding a handle between two people, it does not. P-256 with SHA-256 is
    /// in the framework, exports as the standard SubjectPublicKeyInfo and
    /// Pkcs8PrivateKey encodings that any other language can read, and is
    /// entirely adequate here. ECDSA's famous sharp edge -- a repeated nonce
    /// leaking the private key -- lives inside the platform's implementation,
    /// not in anything written here.
    ///
    /// Switching later is a version bump on the file's first line: the format
    /// carries a `public` line and a `kdf` line and assumes no curve.
    /// Pegasus_Identity.md §5 has the argument in full.
    static member Generate(handle: Handle) =
        new Identity(handle, ECDsa.Create ECCurve.NamedCurves.nistP256)

    static member internal OfPrivateKey(handle: Handle, pkcs8: byte[]) =
        let key = ECDsa.Create()
        // The out parameter is how many bytes were consumed. We do not check it:
        // the array came from our own file and a short read would fail at the
        // first signature anyway.
        let mutable read = 0
        key.ImportPkcs8PrivateKey(ReadOnlySpan pkcs8, &read)
        new Identity(handle, key)

    /// Internal, and visible to the test assembly only so the suite can assert
    /// these bytes never reach the file unsealed. Nothing else should want it.
    member internal _.ExportPrivateKey() = key.ExportPkcs8PrivateKey()

    /// Verifies against a bare public key, with no private half anywhere. This
    /// is the shape the pinned-key handshake will need: you hold your peer's
    /// public key from a previous session and check what they just sent.
    static member VerifyWith(publicKey: byte[], data: byte[], signature: byte[]) =
        use key = ECDsa.Create()
        let mutable read = 0
        key.ImportSubjectPublicKeyInfo(ReadOnlySpan publicKey, &read)
        key.VerifyData(data, signature, HashAlgorithmName.SHA256)

    interface IDisposable with
        member _.Dispose() = key.Dispose()
