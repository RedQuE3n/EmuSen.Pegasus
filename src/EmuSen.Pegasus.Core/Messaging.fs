namespace EmuSen.Pegasus

open System
open System.IO
open System.Security.Cryptography
open System.Text

/// Sealing a message to somebody who is not there.
///
/// THE PROBLEM THIS SOLVES, and why none of the three keys already in the
/// project would do. A note is sealed under a join code both people typed
/// (Crypto.deriveKey); control traffic is sealed under a key agreed live with
/// the relay (Agreement); an identity file is sealed under its owner's password.
/// A message has to be sealed for somebody who is ASLEEP -- there is nobody at
/// the far end to agree anything with, and asking two people to agree a password
/// before they can say hello is not an instant messenger. So the recipient's key
/// has to be knowable in advance, which means published, which is what a Card is.
///
/// THE CONSTRUCTION. Two Diffie-Hellman agreements, hashed together:
///
///     H1 = ECDH(ephemeral_sender, messaging_recipient)
///     H2 = ECDH(messaging_sender,  messaging_recipient)
///     K  = SHA-256(domain || salt || H1 || H2)
///
/// and the body is AES-256-GCM under K, with the ephemeral public key sent
/// beside it in the clear. Each half is there for a different reason and
/// removing either takes a property with it:
///
/// - **H1 is why a recording does not stay readable.** The ephemeral private
///   half is generated per message and never stored, so an attacker who later
///   steals the SENDER's disk cannot recompute K for anything already sent.
/// - **H2 is why the sender is the sender.** Only a holder of the sender's
///   messaging private key can compute it, so a message that opens is a message
///   that came from that key. Without H2 anybody who knows the recipient's
///   published key -- which is everybody, that is what published means -- could
///   seal a message and let the relay's FromHandle stamp name whoever they
///   liked. The stamp is the relay's word (Types.fs); H2 is the proof.
///
/// This is X3DH with the parts that need a server-held one-time prekey pool
/// left out, and it is worth being exact about what that costs rather than
/// implying Signal. What it does NOT give:
///
/// - **No post-compromise security.** Steal the recipient's messaging private
///   key and every message ever sealed to it opens, past and future, because
///   the recipient's half of both agreements never rotates. A ratchet is what
///   fixes that and there is no ratchet here.
/// - **No forward secrecy against the RECIPIENT's key**, for the same reason.
///   The FS above is one-sided: it protects against the sender being compromised
///   later, not the recipient.
/// - **No protection against a relay that lied about the very first card.**
///   Trust on first use has a first use. Pegasus_Identity.md §7.
///
/// A signature over the body would have been the simpler way to authenticate
/// the sender, and it was rejected: a signed message is transferable proof that
/// a named person said a specific thing, which a private conversation should
/// not manufacture as a side effect. H2 authenticates to the RECIPIENT and to
/// nobody else, because the recipient could have computed the same key itself
/// and therefore could have written the message. Pegasus_Sync.md §7.2.
module Messaging =

    /// The curve, and it is P-256 for the reason Identity and Agreement are:
    /// it is in the framework, exports as standard SubjectPublicKeyInfo that
    /// another language can read, and adding a curve here would mean taking a
    /// dependency the core does not have. Pegasus_Identity.md §5.
    let private curve = ECCurve.NamedCurves.nistP256

    /// Its own domain tag, separate from the identity proof's and the key
    /// agreement's. A signature made to say "this messaging key is mine" must
    /// not be replayable as a signature saying "I am who I claim", and the only
    /// thing that makes that true rather than hoped for is that the two are
    /// signing different bytes.
    [<Literal>]
    let private KeyDomain = "pegasus/messaging-key/v1"

    /// The tag mixed into the final hash. Separate again from KeyDomain, so a
    /// value derived for sealing can never be confused with one derived for
    /// binding a key to an identity.
    [<Literal>]
    let private SealDomain = "pegasus/message/v1"

    /// Tags for the two agreements, so H1 and H2 are drawn from different
    /// derivations of the same curve rather than being the same shape of thing
    /// twice. Two identical constructions hashed in order would still be
    /// order-dependent, but distinguishing them here means neither can ever be
    /// substituted for the other even by a caller passing arguments the wrong
    /// way round.
    [<Literal>]
    let private EphemeralTag = "pegasus/message/ephemeral/v1"

    [<Literal>]
    let private StaticTag = "pegasus/message/static/v1"

    /// What an identity signs to say a messaging key is its own.
    let keyPayload (messagingPublicKey: byte[]) =
        Array.append (Encoding.UTF8.GetBytes KeyDomain) messagingPublicKey

    /// A fresh messaging keypair: the public half as SubjectPublicKeyInfo, the
    /// private half as PKCS#8 for a store to seal.
    ///
    /// Here rather than in the store because the curve is chosen here and
    /// nowhere else. A store that created its own keypair would be a second
    /// place the curve is named, and the two would eventually disagree quietly
    /// -- a P-384 key and a P-256 key will not agree a secret, and the failure
    /// would surface as messages that do not open rather than as anything that
    /// mentions curves.
    ///
    /// Both halves come back together because a store needs both and deriving
    /// the public one later means importing the private one again to do it.
    let newKeyPair () =
        use key = ECDiffieHellman.Create curve
        key.ExportSubjectPublicKeyInfo(), key.ExportPkcs8PrivateKey()

    /// An identity that signs and is never written to.
    ///
    /// FOR A RELAY, and the messaging half it gets here is generated fresh on
    /// every load and stored nowhere. That is not laziness about a column: a
    /// relay proves who it is (so it needs the signing key, pinned by every
    /// client) and is never anybody's correspondent, so a messaging key it kept
    /// would be a private key on disk that nothing can ever use — the sort of
    /// thing that gets found later and assumed to be load-bearing.
    ///
    /// The consequence to know before changing this: a relay that ever DOES
    /// publish a card must start persisting this half first, because clients pin
    /// what they are given and a key regenerated every boot would look like a
    /// different server each time.
    let signingOnly (handle: Handle) (pkcs8: byte[]) =
        Identity.OfPrivateKey(handle, pkcs8, snd (newKeyPair ()))

    /// The card an identity publishes: its two public keys, and its own
    /// signature binding the second to the first.
    let cardOf (identity: Identity) : Card =
        { Handle = identity.Handle
          Identity = identity.PublicKey
          Messaging = identity.MessagingPublicKey
          Signature = identity.Sign(keyPayload identity.MessagingPublicKey) }

    /// Whether a card's messaging key really was vouched for by the identity key
    /// beside it.
    ///
    /// This is HALF the check and callers must not mistake it for the whole one.
    /// It proves the two keys in the card belong together; it says nothing about
    /// whether that identity key is the one you pinned for this handle. A card
    /// invented wholesale by a relay passes this and fails the pin, which is why
    /// KnownPeers.acceptCard in the application does both and this is not called
    /// on its own anywhere that matters.
    let verifyCard (card: Card) =
        try
            Identity.VerifyWith(card.Identity, keyPayload card.Messaging, card.Signature)
        with :? CryptographicException ->
            // A card whose identity key will not even import is a malformed
            // card, not a crash. It arrived from the network by definition.
            false

    let private importPublic (spki: byte[]) =
        let key = ECDiffieHellman.Create()
        let mutable read = 0
        key.ImportSubjectPublicKeyInfo(ReadOnlySpan spki, &read)
        key

    /// Hashes the two agreements into the key the body is sealed under.
    ///
    /// The salt carries the ephemeral public key and BOTH parties' messaging
    /// keys, so a sealed body is bound to the exact pair it was written for. A
    /// ciphertext copied at a relay and re-addressed to somebody else derives a
    /// different key at that somebody else and does not open -- which matters
    /// because the destination rides outside the seal and is therefore the one
    /// field a relay could rewrite.
    let private combine (ephemeralPublic: byte[]) (senderMessaging: byte[]) (recipientMessaging: byte[]) (h1: byte[]) (h2: byte[]) =
        SHA256.HashData(
            Array.concat
                [ Encoding.UTF8.GetBytes SealDomain
                  ephemeralPublic
                  senderMessaging
                  recipientMessaging
                  h1
                  h2 ]
        )

    /// Seals a body for the holder of `recipientMessaging`, from `sender`.
    ///
    /// The ephemeral is disposed on the way out and its private half is never
    /// written anywhere, which is not tidiness -- it is the entire forward
    /// secrecy claim above. A build that kept it to "save regenerating one"
    /// would silently delete that property while every test still passed.
    let seal (sender: Identity) (recipientMessaging: byte[]) (plaintext: byte[]) =
        use ephemeral = ECDiffieHellman.Create curve
        let ephemeralPublic = ephemeral.ExportSubjectPublicKeyInfo()
        use theirs = importPublic recipientMessaging

        let h1 =
            ephemeral.DeriveKeyFromHash(theirs.PublicKey, HashAlgorithmName.SHA256, [||], Encoding.UTF8.GetBytes EphemeralTag)

        let h2 = sender.AgreeMessaging(recipientMessaging, Encoding.UTF8.GetBytes StaticTag)
        let key = combine ephemeralPublic sender.MessagingPublicKey recipientMessaging h1 h2

        // Length-prefixed rather than "the rest is the body", for the reason
        // Hello's key is: two variable-length blobs in one buffer need a
        // boundary that is stated instead of inferred.
        use ms = new MemoryStream()
        use w = new BinaryWriter(ms, UTF8Encoding false, true)
        w.Write ephemeralPublic.Length
        w.Write ephemeralPublic
        w.Write(Crypto.seal key plaintext)
        w.Flush()
        ms.ToArray()

    /// Opens a sealed message, or None.
    ///
    /// None covers every way this can fail and deliberately does not distinguish
    /// them: a truncated blob, a body that was tampered with, a sender whose
    /// card is not the card the message was actually sealed with. GCM cannot
    /// tell those apart and neither can this, and the one that matters --
    /// **a message that does not open is a message that was not written by the
    /// sender the relay named** -- is reported by the caller as a refusal
    /// rather than as an empty conversation.
    let tryOpen (recipient: Identity) (senderMessaging: byte[]) (blob: byte[]) =
        try
            use ms = new MemoryStream(blob)
            use r = new BinaryReader(ms, UTF8Encoding false)
            let length = r.ReadInt32()

            if length <= 0 || length > blob.Length then
                None
            else
                let ephemeralPublic = r.ReadBytes length
                let body = r.ReadBytes(blob.Length - 4 - length)

                let h1 = recipient.AgreeMessaging(ephemeralPublic, Encoding.UTF8.GetBytes EphemeralTag)
                let h2 = recipient.AgreeMessaging(senderMessaging, Encoding.UTF8.GetBytes StaticTag)
                let key = combine ephemeralPublic senderMessaging recipient.MessagingPublicKey h1 h2

                Crypto.tryOpenSealed key body
        with
        | :? EndOfStreamException
        | :? ArgumentException
        | :? CryptographicException -> None
