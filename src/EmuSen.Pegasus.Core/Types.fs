namespace EmuSen.Pegasus

open System

/// Wire and file format versions.
///
/// Protocol went to 2 when Hello started carrying a public key and the identity
/// proof frames appeared, to 3 when Roster did, and to 4 when Agree did. It is
/// actually sent, in Hello, which it never was at 1 -- two builds that disagreed
/// used to discover it as a decode failure somewhere further down, and now say
/// so in the first frame. Adding a tag is a protocol change even though an old
/// build would only ever meet it by talking to a new one. See Pegasus_Format.md
/// §1 for the file schema, which is unrelated and unchanged.
module Version =
    [<Literal>]
    let Protocol = 4uy

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
    | ToHandle of Handle
    /// Stamped by a relay on delivery, so the recipient knows whose sealed
    /// payload it is holding. An Update is opaque bytes and says nothing about
    /// who wrote it, so without this a client with two correspondents could not
    /// tell their traffic apart.
    ///
    /// This is the RELAY's word, not proof. It knows who sent it because that
    /// connection signed in, so this is exactly as trustworthy as the relay --
    /// which is why what it names is a handle to route by and never a reason to
    /// skip a signature.
    | FromHandle of Handle

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

exception ProtocolError of string
