namespace EmuSen.Pegasus

open System
open System.IO
open System.Text

/// Frame <-> bytes. Format and tag assignments are in Pegasus_Sync.md §3.
module Codec =

    [<Literal>]
    let private TagHello = 0uy

    [<Literal>]
    let private TagSyncStep1 = 1uy

    [<Literal>]
    let private TagSyncStep2 = 2uy

    [<Literal>]
    let private TagUpdate = 3uy

    [<Literal>]
    let private TagAwareness = 4uy

    [<Literal>]
    let private TagBye = 5uy

    [<Literal>]
    let private TagChallenge = 6uy

    [<Literal>]
    let private TagProof = 7uy

    [<Literal>]
    let private TagRoster = 8uy

    [<Literal>]
    let private TagDirect = 0uy

    [<Literal>]
    let private TagToHandle = 1uy

    [<Literal>]
    let private TagFromHandle = 2uy

    /// Frames are bounded so a malformed or hostile length cannot make us
    /// allocate arbitrarily; see Pegasus_Sync.md §5.
    [<Literal>]
    let MaxFrameBytes = 64 * 1024 * 1024

    // Binary rather than JSON: System.Text.Json cannot serialise F# unions and
    // PeerId is one. See Pegasus_Design.md §4.6.
    let private writeWith (tag: byte) (write: BinaryWriter -> unit) =
        use ms = new MemoryStream()
        use w = new BinaryWriter(ms, UTF8Encoding false, true)
        w.Write tag
        write w
        w.Flush()
        ms.ToArray()

    let private writePeer (w: BinaryWriter) (p: PeerInfo) =
        w.Write p.Id.Value
        w.Write p.Handle.Value
        w.Write p.Color

    /// Validates the handle rather than trusting it, so the grammar holds for
    /// remote peers as well as local ones. Nothing downstream should have to
    /// wonder whether a Handle it is holding came off a socket.
    ///
    /// This rejects a malformed handle; it does not and cannot check that the
    /// sender is entitled to the handle they sent. See PeerInfo in Types.fs.
    let private readPeer (r: BinaryReader) : PeerInfo =
        let id = r.ReadString()
        let handle = r.ReadString()
        let color = r.ReadString()

        match Handle.TryParse handle with
        | Error why -> raise (ProtocolError $"peer sent an unusable handle: {why}")
        | Ok parsed ->
            { Id = PeerId id
              Handle = parsed
              Color = color }

    /// Serialise a frame to its plaintext body. Encryption and length-prefixing
    /// happen above this, in Pegasus.Net.
    let encode (frame: Frame) : byte[] =
        match frame with
        | Hello(peer, publicKey, protocol) ->
            writeWith TagHello (fun w ->
                w.Write protocol
                writePeer w peer
                w.Write publicKey.Length
                w.Write publicKey)
        | SyncStep1 sv -> writeWith TagSyncStep1 (fun w -> w.Write sv)
        | SyncStep2 diff -> writeWith TagSyncStep2 (fun w -> w.Write diff)
        | Update u -> writeWith TagUpdate (fun w -> w.Write u)
        | Awareness p ->
            writeWith TagAwareness (fun w ->
                writePeer w p.Peer
                w.Write p.Caret
                w.Write p.Anchor)
        | Bye -> [| TagBye |]
        | Challenge nonce -> writeWith TagChallenge (fun w -> w.Write nonce)
        | Roster peers ->
            writeWith TagRoster (fun w ->
                w.Write peers.Length
                for peer in peers do
                    writePeer w peer)
        | Proof signature -> writeWith TagProof (fun w -> w.Write signature)

    let decode (body: byte[]) : Frame =
        if body.Length = 0 then
            raise (ProtocolError "empty frame")

        let tag = body[0]
        let payload = body[1..]

        try
            match tag with
            | TagSyncStep1 -> SyncStep1 payload
            | TagSyncStep2 -> SyncStep2 payload
            | TagUpdate -> Update payload
            | TagBye -> Bye
            | TagChallenge -> Challenge payload
            | TagRoster ->
                use r = new BinaryReader(new MemoryStream(payload), UTF8Encoding false)
                let count = r.ReadInt32()

                // A count is a claim until it has been checked. Three bytes is
                // the smallest a peer could possibly encode to, so anything
                // beyond that many is a lie and must not become an allocation.
                if count < 0 || count > payload.Length / 3 then
                    raise (ProtocolError $"roster claims {count} peers in a {payload.Length}-byte frame")

                Roster(Array.init count (fun _ -> readPeer r))
            | TagProof -> Proof payload
            | TagHello ->
                use r = new BinaryReader(new MemoryStream(payload), UTF8Encoding false)
                let protocol = r.ReadByte()
                let peer = readPeer r

                // Length-prefixed rather than "the rest of the frame", so the
                // Hello can grow another field later without the key having to
                // be last. Bounded against the frame we already hold, so a
                // hostile length cannot ask for an arbitrary allocation.
                let keyLength = r.ReadInt32()

                if keyLength < 0 || keyLength > payload.Length then
                    raise (ProtocolError $"Hello declares a {keyLength}-byte key inside a {payload.Length}-byte frame")

                Hello(peer, r.ReadBytes keyLength, protocol)
            | TagAwareness ->
                use r = new BinaryReader(new MemoryStream(payload), UTF8Encoding false)
                let peer = readPeer r

                Awareness
                    { Peer = peer
                      Caret = r.ReadInt32()
                      Anchor = r.ReadInt32() }
            | unknown -> raise (ProtocolError $"unknown frame tag {unknown}")
        with
        | :? EndOfStreamException -> raise (ProtocolError $"truncated payload for frame tag {tag}")
        | :? ArgumentException -> raise (ProtocolError $"malformed payload for frame tag {tag}")

    let private addressed (tag: byte) (handle: Handle) =
        use ms = new MemoryStream()
        use w = new BinaryWriter(ms, UTF8Encoding false, true)
        w.Write tag
        w.Write handle.Value
        w.Flush()
        ms.ToArray()

    /// The envelope goes on the wire in the clear, ahead of the sealed payload.
    let encodeEnvelope (envelope: Envelope) : byte[] =
        match envelope with
        | Direct -> [| TagDirect |]
        | ToHandle handle -> addressed TagToHandle handle
        | FromHandle handle -> addressed TagFromHandle handle

    /// Returns the envelope and how many bytes it used, because the sealed
    /// payload begins immediately after it and the caller has to find it. A
    /// relay decodes this holding no key at all.
    let decodeEnvelope (buffer: byte[]) : Envelope * int =
        if buffer.Length = 0 then
            raise (ProtocolError "empty envelope")

        try
            match buffer[0] with
            | TagDirect -> Direct, 1
            | TagToHandle
            | TagFromHandle ->
                use ms = new MemoryStream(buffer, 1, buffer.Length - 1)
                use r = new BinaryReader(ms, UTF8Encoding false)
                let raw = r.ReadString()

                // Validated here so a handle arriving from the wire obeys the
                // same grammar as one typed locally, and a relay never queues
                // under a key it could not have produced itself.
                match Handle.TryParse raw with
                | Error why -> raise (ProtocolError $"envelope names an unusable handle: {why}")
                | Ok handle ->
                    let envelope =
                        if buffer[0] = TagToHandle then ToHandle handle else FromHandle handle

                    envelope, 1 + int ms.Position
            | unknown -> raise (ProtocolError $"unknown envelope tag {unknown}")
        with
        | :? EndOfStreamException -> raise (ProtocolError "truncated envelope")
        | :? ArgumentException -> raise (ProtocolError "malformed envelope")

/// CRC-32 (IEEE), used by the store to detect a torn trailing record.
module Crc32 =

    let private table =
        lazy
            (Array.init 256 (fun i ->
                let mutable c = uint32 i

                for _ in 0..7 do
                    c <- if c &&& 1u <> 0u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1

                c))

    let compute (data: byte[]) (offset: int) (count: int) =
        let t = table.Value
        let mutable crc = 0xFFFFFFFFu

        for i in offset .. offset + count - 1 do
            crc <- t[int ((crc ^^^ uint32 data[i]) &&& 0xFFu)] ^^^ (crc >>> 8)

        crc ^^^ 0xFFFFFFFFu

    let ofBytes (data: byte[]) = compute data 0 data.Length
