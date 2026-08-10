namespace EmuSen.Pegasus

open System
open System.Security.Cryptography
open System.Text

/// An ephemeral key agreed for one connection, so a passphrase stops being able
/// to read everything said under it.
///
/// THE PROBLEM THIS SOLVES. Control traffic between a client and Chariot —
/// signing in, the roster — used to be sealed under a key derived from the
/// server passphrase, with the fixed salt Pegasus_Sync.md §5 already calls a
/// weakness. Every client holds that passphrase, so every client could read
/// every other client's roster off the wire, and anyone who recorded a session
/// could read it later by learning one shared secret. The passphrase was meant
/// to be a doorbell and had quietly become a key.
///
/// So: once both ends have PROVED who they are (Attestation), each generates a
/// throwaway ECDH keypair, signs it with the long-term key it just proved, and
/// sends it. Both derive the same session key from the two halves; nobody who
/// merely holds the passphrase can. The private halves are never stored and are
/// gone when the connection ends, so a recording is not readable later either.
///
/// WHY THE SIGNATURE IS NOT OPTIONAL. An unsigned ephemeral key can be replaced
/// in transit by whoever carries it, and both ends would happily agree a key
/// with the attacker instead of with each other — the classic unauthenticated
/// Diffie-Hellman failure. Signing binds "this ephemeral is mine" to an identity
/// already proved a frame earlier, so the signature is what turns key agreement
/// into authenticated key agreement.
///
/// Note traffic is untouched by all of this. It is sealed under a join code
/// Chariot never has, which is the property the whole design rests on, and
/// wrapping it in a second key agreed WITH the server would be strictly worse.
/// Pegasus_Sync.md §4.3.
module Agreement =

    /// Its own domain tag, separate from the identity proof's. A signature made
    /// to say "this ephemeral is mine" must not be replayable as a signature
    /// saying "I am who I claim", and vice versa; different tags is what makes
    /// that true rather than hoped for.
    [<Literal>]
    let private Domain = "pegasus/key-agreement/v1"

    /// What gets signed: the tag, the ephemeral public key, and the nonce the
    /// OTHER side challenged us with.
    ///
    /// The nonce is what ties this ephemeral to this connection. Without it a
    /// signed ephemeral harvested from one session could be replayed into
    /// another by anyone who recorded it, and the far side would agree a key
    /// with a recording.
    let payload (ephemeral: byte[]) (nonce: byte[]) =
        Array.concat [ Encoding.UTF8.GetBytes Domain; nonce; ephemeral ]

    /// The salt both ends must compute identically, so it is defined once here
    /// rather than assembled twice.
    ///
    /// Order is by ROLE, not by who happens to call this: the accepting end's
    /// nonce first, the dialling end's second. Both know which they are, and an
    /// order agreed by convention is the only kind available — sorting the two
    /// would be one more thing to get wrong for no benefit.
    let salt (hostNonce: byte[]) (joinerNonce: byte[]) =
        Array.append hostNonce joinerNonce

    /// One end's half. Disposable, because it holds a native key handle, and
    /// short-lived on purpose: the forward secrecy this buys is exactly the fact
    /// that nothing here outlives the connection.
    type Ephemeral() =
        let key = ECDiffieHellman.Create ECCurve.NamedCurves.nistP256
        let publicKey = key.ExportSubjectPublicKeyInfo()

        member _.PublicKey = publicKey

        /// This half, ready to send: the public key and a signature over it made
        /// with the identity that has just been proved.
        member _.Offer(identity: Identity, theirNonce: byte[]) =
            Agree(publicKey, identity.Sign(payload publicKey theirNonce))

        /// Checks the other half and derives the session key, or says why not.
        ///
        /// The signature is verified against the public key that was PROVED in
        /// the identity exchange, not against anything in this frame. Taking a
        /// key from the frame that the signature is on would be checking a
        /// signature against itself.
        ///
        /// P-256 for the same reason Identity uses it: it is in the framework,
        /// exports as standard SubjectPublicKeyInfo, and adding a curve here
        /// would mean adding a dependency there. Pegasus_Identity.md §5.
        member _.Accept(provenKey: byte[], theirs: byte[], signature: byte[], myNonce: byte[], sessionSalt: byte[]) =
            if not (Identity.VerifyWith(provenKey, payload theirs myNonce, signature)) then
                Error "the other end did not sign its ephemeral key with the identity it proved"
            else
                try
                    use theirKey = ECDiffieHellman.Create()
                    let mutable read = 0
                    theirKey.ImportSubjectPublicKeyInfo(ReadOnlySpan theirs, &read)

                    // DeriveKeyFromHash rather than the raw agreement plus a
                    // separate KDF: this is SHA-256 over salt || Z || tag, which
                    // is the concatenation KDF of SP 800-56A, produces exactly
                    // the 32 bytes AES-256-GCM wants, and needs nothing imported
                    // that is not already here. The salt carries both nonces, so
                    // two connections between the same pair never agree the same
                    // key even if an ephemeral were somehow repeated.
                    Ok(
                        key.DeriveKeyFromHash(
                            theirKey.PublicKey,
                            HashAlgorithmName.SHA256,
                            sessionSalt,
                            Encoding.UTF8.GetBytes Domain
                        )
                    )
                with :? CryptographicException ->
                    // A key that will not import is a malformed frame, not a
                    // crash. It arrived from a stranger by definition.
                    Error "the other end offered a key that is not a P-256 public key"

        interface IDisposable with
            member _.Dispose() = key.Dispose()
