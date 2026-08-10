namespace EmuSen.Pegasus

open System

/// Wire and file format versions. See Pegasus_Format.md §1.
module Version =
    [<Literal>]
    let Protocol = 1uy

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

/// Who is at the other end, as far as this peer can tell.
///
/// "As far as it can tell" is load-bearing: the handle here was asserted by
/// whoever sent the Hello and nothing checked it. Anybody holding the join code
/// can claim to be anybody. Binding a handle to its key across the wire -- a
/// signed challenge, and the peer's key pinned from a previous session -- is the
/// pass after this one, and until it lands a displayed handle is a convenience,
/// not authentication.
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

/// One message on the wire. Sync payloads are raw Yjs bytes, so a bridge to
/// y-websocket stays a shim rather than a rewrite -- see Pegasus_Sync.md §3.
type Frame =
    | Hello of PeerInfo
    | SyncStep1 of stateVector: byte[]
    | SyncStep2 of diff: byte[]
    | Update of update: byte[]
    | Awareness of Presence
    | Bye

exception ProtocolError of string
