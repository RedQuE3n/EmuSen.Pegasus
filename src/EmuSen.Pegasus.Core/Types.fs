namespace EmuSen.Pegasus

open System

/// Wire and file format versions.
///
/// Protocol went to 2 when Hello started carrying a public key and the identity
/// proof frames appeared, to 3 when Roster did, to 4 when Agree did, and to 5
/// when messages did. It is actually sent, in Hello, which it never was at 1 --
/// two builds that disagreed used to discover it as a decode failure somewhere
/// further down, and now say so in the first frame. Adding a tag is a protocol
/// change even though an old build would only ever meet it by talking to a new
/// one. See Pegasus_Format.md §1 for the file schema, which is unrelated and
/// unchanged.
///
/// 5 IS NOT A TAG-ONLY BUMP and an older build cannot be talked round with
/// care. The ENVELOPE changed shape -- a routed frame now names the channel it
/// is on, and a delivery names the post it came out of -- and the envelope is
/// the one part of a frame a relay reads without holding any key at all. A 4
/// relay handed a 5 envelope reads the handle it expects and then finds bytes
/// it has no field for. Pegasus_Sync.md §7.
module Version =
    [<Literal>]
    let Protocol = 5uy

    [<Literal>]
    let FileSchema = 1uy

/// Names a person. Derived from their public key rather than drawn at random,
/// so it is the same on Tuesday as it was on Monday -- see Fingerprint in
/// Identity.fs. There is deliberately no New(): a PeerId that is not a
/// fingerprint of something is a PeerId that means nothing.
///
/// This is NOT the Yjs client id. That names a replica, this names a person,
/// and one person may hold two replicas -- a laptop and a desktop signed in as
/// the same handle. Giving those the same client id is exactly the silent data
/// loss demonstrated in Pegasus_Design.md §4.5, so ClientId.fresh stays random
/// per document and owes nothing to this.
type PeerId =
    | PeerId of string

    member this.Value = let (PeerId v) = this in v

/// A login name -- `RedQuE3n`, the thing your peer sees you as.
///
/// The grammar is narrow because a handle gets read aloud and retyped: 3 to 20
/// characters, letters, digits, hyphen and underscore, and it must begin with a
/// letter so it can never be mistaken for a number or a flag.
///
/// Comparison folds case and the display form is kept, which is the rule AIM
/// used and the right one for a name people say out loud: `RedQuE3n` and
/// `redque3n` are one account, and the one the user typed is the one shown.
/// That is why equality is custom -- structural equality would compare the
/// display strings and let the same person own two accounts.
///
/// The case is private so the only way to hold a Handle is to have parsed one.
/// Anything that has a Handle can stop asking whether it is well formed.
[<CustomEquality; NoComparison>]
type Handle =
    private
    | Handle of string

    member this.Value = let (Handle v) = this in v
    member this.Folded = this.Value.ToLowerInvariant()

    override this.Equals(other) =
        match other with
        | :? Handle as h -> this.Folded = h.Folded
        | _ -> false

    override this.GetHashCode() = this.Folded.GetHashCode()

    static member TryParse(raw: string) =
        let value = (if isNull raw then "" else raw).Trim()
        let allowed c = Char.IsAsciiLetterOrDigit c || c = '-' || c = '_'

        if value.Length < 3 || value.Length > 20 then
            Error "a handle is 3 to 20 characters long"
        elif not (Char.IsAsciiLetter value[0]) then
            Error "a handle starts with a letter"
        elif not (Seq.forall allowed value) then
            Error "a handle holds only letters, digits, hyphen and underscore"
        else
            Ok(Handle value)

    static member Parse(raw: string) =
        match Handle.TryParse raw with
        | Ok h -> h
        | Error why -> invalidArg (nameof raw) why

type NoteId =
    | NoteId of string

    static member New() = NoteId(Guid.NewGuid().ToString("N"))
    member this.Value = let (NoteId v) = this in v

/// Names one message, minted by the sender and never reissued.
///
/// THIS IS WHAT MAKES A REDELIVERY HARMLESS, and it exists because a message is
/// not a Yjs update. The mailbox was built on updates being idempotent -- hand
/// the same one over twice and the document is unchanged -- so it skipped
/// deduplication entirely and was correct to (Chariot_Design.md §6). Hand the
/// same MESSAGE over twice and it appears in the transcript twice, which is a
/// visible defect rather than a wasted merge. The recipient files messages under
/// this id and a second copy lands on a primary key that already exists.
///
/// It is inside the seal, so the relay cannot read it, cannot deduplicate on the
/// client's behalf, and cannot correlate two deliveries as being the same
/// message. That is the correct division: the id is the sender's word to the
/// recipient, and a relay that could read it would be a relay that could tell
/// how often two people say the same thing.
type MessageId =
    | MessageId of string

    static member New() = MessageId(Guid.NewGuid().ToString("N"))
    member this.Value = let (MessageId v) = this in v

/// Who is at the other end.
///
/// This used to carry the warning that the handle was asserted and unchecked.
/// It no longer is: a peer sends its public key in Hello, signs a challenge with
/// the matching private key, and the Id here must be that key's fingerprint or
/// the session is refused. See Attestation below, and Session.fs for where the
/// exchange is driven.
///
/// What that buys and what it does not is worth keeping straight. It proves the
/// far side holds the key whose fingerprint it claims. Whether that key is the
/// person you meant is a separate question, answered by pinning the key the
/// first time you see it and refusing a change later -- KnownPeers in the
/// application.
type PeerInfo =
    { Id: PeerId
      Handle: Handle
      /// "#rrggbb", used to tint this peer's caret.
      Color: string }

/// Where a peer's caret and selection anchor sit, as offsets into the note.
type Presence =
    { Peer: PeerInfo
      Caret: int
      Anchor: int }

/// What kind of traffic a routed payload is.
///
/// This rides OUTSIDE the seal, beside the destination, and that is a
/// deliberate widening of what the relay is told. It was worth arguing about,
/// because every field added out here is a field Chariot learns, and §5's
/// promise is that it learns who and when and how big but never what.
///
/// The relay needs it because THE TWO CHANNELS HAVE DIFFERENT DELIVERY RULES
/// and it cannot infer which is which from bytes it cannot open. Note traffic
/// is Yjs updates: idempotent, order-independent, and safe to drop, because
/// both replicas converge the next time the two peers are online together. A
/// message is none of those things -- dropping one destroys it, since there is
/// no second replica to converge with. So a message is stored until the
/// recipient acknowledges it and a full queue is refused to the sender, while
/// note traffic keeps the old trim-the-oldest behaviour it was always safe to
/// have. Chariot_Design.md §13 carries the correction that forced this.
///
/// What it costs is honest and small: Chariot learns whether a payload is a
/// note edit or a message. It already learned the sender, the recipient, the
/// time and the length, and this adds one bit to that -- it does not move the
/// line, which is content.
type Channel =
    | NoteTraffic
    | MessageTraffic

/// The part of a frame an intermediary is allowed to read.
///
/// Everything else on the wire is sealed end to end, which is exactly the
/// problem: a relay has to know where to send a payload and must not be able to
/// read it. So a destination rides outside the seal, and nothing else does.
///
/// This LEAKS METADATA and there is no way around it. Chariot necessarily
/// learns who is connected, who sends to whom, when, and how many bytes. It
/// does not learn content. Anyone who needs the routing itself hidden wants an
/// onion router, and should be told so rather than reassured.
///
/// Direct is what two peers on a socket use: there is no intermediary, so there
/// is nothing to tell. A session that finds anything else on a direct
/// connection refuses it, because being routed is not something that should
/// happen without the relay having put it there.
type Envelope =
    | Direct
    | ToHandle of Handle * Channel
    /// Stamped by a relay on delivery, so the recipient knows whose sealed
    /// payload it is holding. An Update is opaque bytes and says nothing about
    /// who wrote it, so without this a client with two correspondents could not
    /// tell their traffic apart.
    ///
    /// This is the RELAY's word, not proof. It knows who sent it because that
    /// connection signed in, so this is exactly as trustworthy as the relay --
    /// which is why what it names is a handle to route by and never a reason to
    /// skip a signature.
    ///
    /// `post` is the mailbox row this came out of, and it is what the recipient
    /// acknowledges so the relay may forget it. ZERO MEANS THERE IS NOTHING TO
    /// ACKNOWLEDGE -- note traffic is forwarded live and never stored, so there
    /// is no row to clear and an ack for one would name nothing. Every message
    /// carries a real id, including one delivered to somebody who was online the
    /// whole time, because a message is stored before it is handed over and not
    /// instead of being handed over. Chariot_Design.md §13.2.
    | FromHandle of Handle * Channel * post: int64

/// Everything needed to send somebody a message they can open, and nobody else
/// can.
///
/// A card is published to the relay by its owner and handed out by the relay to
/// whoever asks. THAT MAKES THE RELAY A KEY DIRECTORY, which is the one place
/// this design lets it near the question of who is who, so what stops it lying
/// has to be stated rather than assumed:
///
/// - `Messaging` is signed by `Identity`, so a relay cannot swap the messaging
///   key for one it holds the private half of without also forging a signature
///   from a key it does not have.
/// - `Identity` is the key already pinned on first sight (`KnownPeers` in the
///   application). A card whose identity key is not the pinned one is refused,
///   so a relay cannot substitute a whole card either.
///
/// What it cannot defend is the FIRST card for a handle you have never seen,
/// which is exactly the first-contact hole trust on first use always has
/// (Pegasus_Identity.md §7). The mitigation is the same and it is human: the
/// fingerprint is on screen to be read aloud. A relay that lies at that moment
/// has been the person you meant from the start, and no amount of signing
/// inside the system detects it.
type Card =
    { Handle: Handle
      /// The identity public key -- the one that signs challenges and gets
      /// pinned. Present so a card can be checked against the pin without a
      /// second lookup.
      Identity: byte[]
      /// The messaging public key: P-256, for key agreement, never for signing.
      /// Messages are sealed to this and opened with the half that never leaves
      /// its owner's disk.
      Messaging: byte[]
      /// `Identity` over `Messaging`, under a domain tag of its own. Messaging.fs
      /// carries why a tag of its own is not optional.
      Signature: byte[] }

/// One message on the wire.
///
/// Sync payloads are raw Yjs bytes, so a bridge to a y-websocket client stays a
/// shim at the frame boundary rather than a rewrite of the document model --
/// Pegasus_Sync.md §3 has the tag assignments and the byte layout.
///
/// Hello carries the protocol version and the sender's public key. Challenge
/// and Proof are the identity exchange: each side sends a random nonce and each
/// side signs the other's, so the proof is mutual and neither end is trusted
/// first. Nothing that touches the document is accepted until it has verified,
/// which is why the exchange comes before SyncStep1 rather than beside it.
type Frame =
    | Hello of peer: PeerInfo * publicKey: byte[] * protocol: byte
    | SyncStep1 of stateVector: byte[]
    | SyncStep2 of diff: byte[]
    | Update of update: byte[]
    | Awareness of Presence
    | Bye
    | Challenge of nonce: byte[]
    | Proof of signature: byte[]
    /// Who else is signed in. Sent by Chariot to a client that has proved
    /// itself, and republished whenever the set changes. A peer never sends
    /// this: two people on a socket already know who is there.
    | Roster of peers: PeerInfo[]
    /// Half of an ephemeral key agreement: a one-session public key, and a
    /// signature over it made with the identity key that has just been proved.
    ///
    /// The signature is the whole point. An unsigned ephemeral key can be
    /// swapped in transit by whoever is carrying it, and both ends would agree
    /// a key with the attacker rather than with each other. Signing it with the
    /// long-term key binds "this ephemeral is mine" to an identity that was
    /// proved by Challenge and Proof a moment earlier.
    ///
    /// Sent on the control channel between a client and Chariot, and nowhere
    /// else: two peers already seal under a join code no intermediary has, so
    /// they have nothing to agree. See Agreement.fs, and Pegasus_Sync.md §4.3.
    | Agree of ephemeral: byte[] * signature: byte[]

    /// A client publishing its own card, or Chariot answering an Ask with
    /// somebody else's. One frame for both directions because it carries the
    /// same thing either way, and the handle inside it says whose it is.
    ///
    /// PUBLISHED ONLY AFTER THE SENDER HAS PROVED ITSELF. A card accepted
    /// during the part of sign-in where the far side is still a stranger would
    /// let anybody overwrite anybody's messaging key by claiming their handle,
    /// which is the whole attack the directory has to survive. Chariot's
    /// Server.fs holds it back for exactly that reason.
    | Card of card: Card

    /// "What is this handle's card?" -- asked of Chariot, because a message can
    /// be sent to somebody who is not signed in and their key therefore is not
    /// on any roster.
    | Ask of who: Handle

    /// "I have no card for that handle." Distinct from a Card frame rather than
    /// a Card with empty fields: a caller has to tell "nobody by that name" from
    /// "here are their keys", and an empty byte array is the kind of sentinel
    /// that gets sealed to by mistake.
    | Unknown of who: Handle

    /// One instant message. NEVER TRAVELS UNDER THE CONTROL KEY and is never a
    /// Direct frame: it is sealed to the recipient's messaging key and addressed
    /// through the relay, so this case is what the recipient decodes after
    /// opening a payload the relay carried and could not read.
    ///
    /// `sentAt` is the SENDER's clock, in Unix milliseconds, and is therefore a
    /// claim rather than a fact -- two machines disagree, and a sender may lie
    /// outright. It orders a transcript, which is what it is for; nothing
    /// security-relevant may rest on it. The recipient files messages in
    /// arrival order and shows this, which is the same thing every messenger
    /// does and has the same weakness.
    | Message of id: MessageId * sentAt: int64 * body: string

    /// "I have these; you may forget them." Sent by a client to Chariot naming
    /// the mailbox rows it has written to disk.
    ///
    /// Post is deleted on this and on nothing else, which is what makes the
    /// queue durable rather than best-effort: a client that dies between the
    /// delivery and the disk write gets the message again on its next sign-in,
    /// and MessageId turns that second copy into a no-op.
    | Ack of posts: int64[]

    /// "That message did not go anywhere, and here is why." The one refusal a
    /// sender is told about rather than merely logged, because a message that
    /// was never delivered and a message that was are indistinguishable on a
    /// sender's screen otherwise -- and silently indistinguishable is exactly
    /// what a mailbox that drops post looks like. Chariot_Design.md §13.1.
    | Undeliverable of who: Handle * why: string

exception ProtocolError of string
