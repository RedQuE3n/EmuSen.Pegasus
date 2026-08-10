namespace EmuSen.Pegasus

open System

/// Wire and file format versions. See Pegasus_Format.md §1.
module Version =
    [<Literal>]
    let Protocol = 1uy

    [<Literal>]
    let FileSchema = 1uy

/// Derived from a public key, not drawn, so it survives a restart. See
/// Pegasus_Identity.md §6.
type PeerId =
    | PeerId of string

    member this.Value = let (PeerId v) = this in v

/// A login name. Grammar, and why comparison folds case, in
/// Pegasus_Identity.md §1.
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

/// The handle is asserted rather than proven -- Pegasus_Identity.md §2.
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
