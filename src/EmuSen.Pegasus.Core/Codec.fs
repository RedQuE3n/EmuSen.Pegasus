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
    let private TagAgree = 9uy

    [<Literal>]
    let private TagCard = 10uy

    [<Literal>]
    let private TagAsk = 11uy

    [<Literal>]
    let private TagUnknown = 12uy

    [<Literal>]
    let private TagMessage = 13uy

    [<Literal>]
    let private TagAck = 14uy

    [<Literal>]
    let private TagUndeliverable = 15uy

    [<Literal>]
    let private TagDirect = 0uy

    [<Literal>]
    let private TagToHandle = 1uy

    [<Literal>]
    let private TagFromHandle = 2uy

    // Channel tags live in their own numbering because they sit in the envelope
    // rather than in a frame, and the two are read by different things: a relay
    // reads the envelope holding no key, and only a peer ever reads a frame tag.
    // Sharing one numbering between them would make an off-by-one in either
    // decode as a plausible value in the other.
    [<Literal>]
    let private TagNoteTraffic = 0uy

    [<Literal>]
    let private TagMessageTraffic = 1uy

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

    /// Same rule as readPeer, for the frames that carry a bare handle: the
    /// grammar is checked here so nothing downstream has to wonder whether a
    /// Handle it is holding came off a socket.
    let private readHandle (r: BinaryReader) =
        match Handle.TryParse(r.ReadString()) with
        | Error why -> raise (ProtocolError $"frame names an unusable handle: {why}")
        | Ok handle -> handle

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
        | Agree(ephemeral, signature) ->
            // Length-prefixed rather than "the rest of the frame", for the same
            // reason Hello's key is: two variable-length blobs in one frame need
            // a boundary that is stated instead of inferred.
            writeWith TagAgree (fun w ->
                w.Write ephemeral.Length
                w.Write ephemeral
                w.Write signature)
        | Card card ->
            // Three variable-length blobs, so two of them state their length
            // and the last takes what is left. Same rule as Agree, one blob
            // further along.
            writeWith TagCard (fun w ->
                w.Write card.Handle.Value
                w.Write card.Identity.Length
                w.Write card.Identity
                w.Write card.Messaging.Length
                w.Write card.Messaging
                w.Write card.Signature)
        | Ask who -> writeWith TagAsk (fun w -> w.Write who.Value)
        | Unknown who -> writeWith TagUnknown (fun w -> w.Write who.Value)
        | Message(id, sentAt, body) ->
            writeWith TagMessage (fun w ->
                w.Write id.Value
                w.Write sentAt
                w.Write body)
        | Ack posts ->
            writeWith TagAck (fun w ->
                w.Write posts.Length
                for post in posts do
                    w.Write post)
        | Undeliverable(who, why) ->
            writeWith TagUndeliverable (fun w ->
                w.Write who.Value
                w.Write why)

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
            | TagAgree ->
                use r = new BinaryReader(new MemoryStream(payload), UTF8Encoding false)
                let length = r.ReadInt32()

                if length < 0 || length > payload.Length then
                    raise (ProtocolError $"Agree declares a {length}-byte key inside a {payload.Length}-byte frame")

                let ephemeral = r.ReadBytes length
                Agree(ephemeral, r.ReadBytes(payload.Length - 4 - length))
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
            | TagCard ->
                use r = new BinaryReader(new MemoryStream(payload), UTF8Encoding false)
                let handle = readHandle r

                // Each declared length is checked against the frame actually in
                // hand before it becomes an allocation. A card arrives from the
                // network from a party that has not necessarily proved anything
                // yet, which makes this the most exposed decode in the file.
                let readBlob (what: string) =
                    let length = r.ReadInt32()

                    if length < 0 || length > payload.Length then
                        raise (ProtocolError $"card declares a {length}-byte {what} inside a {payload.Length}-byte frame")

                    r.ReadBytes length

                let identity = readBlob "identity key"
                let messaging = readBlob "messaging key"

                Card
                    { Handle = handle
                      Identity = identity
                      Messaging = messaging
                      Signature = r.ReadBytes(payload.Length - int r.BaseStream.Position) }
            | TagAsk ->
                use r = new BinaryReader(new MemoryStream(payload), UTF8Encoding false)
                Ask(readHandle r)
            | TagUnknown ->
                use r = new BinaryReader(new MemoryStream(payload), UTF8Encoding false)
                Unknown(readHandle r)
            | TagMessage ->
                use r = new BinaryReader(new MemoryStream(payload), UTF8Encoding false)
                Message(MessageId(r.ReadString()), r.ReadInt64(), r.ReadString())
            | TagAck ->
                use r = new BinaryReader(new MemoryStream(payload), UTF8Encoding false)
                let count = r.ReadInt32()

                // Eight bytes each, so a count past that many cannot be honest
                // and must not become an allocation. Same rule as Roster's,
                // with the size that applies here.
                if count < 0 || count > (payload.Length - 4) / 8 then
                    raise (ProtocolError $"ack claims {count} posts in a {payload.Length}-byte frame")

                Ack(Array.init count (fun _ -> r.ReadInt64()))
            | TagUndeliverable ->
                use r = new BinaryReader(new MemoryStream(payload), UTF8Encoding false)
                Undeliverable(readHandle r, r.ReadString())
            | unknown -> raise (ProtocolError $"unknown frame tag {unknown}")
        with
        | :? EndOfStreamException -> raise (ProtocolError $"truncated payload for frame tag {tag}")
        | :? ArgumentException -> raise (ProtocolError $"malformed payload for frame tag {tag}")

    let private channelTag channel =
        match channel with
        | NoteTraffic -> TagNoteTraffic
        | MessageTraffic -> TagMessageTraffic

    /// An unknown channel is refused rather than defaulted to notes.
    ///
    /// Defaulting would be the friendlier-looking choice and it is the wrong
    /// one: the channel decides whether the relay may drop a payload when the
    /// queue is full, so guessing it wrong on a message is exactly the silent
    /// data loss the channel exists to prevent. A frame this build cannot
    /// classify is a frame it must not carry.
    let private readChannel (r: BinaryReader) =
        match r.ReadByte() with
        | TagNoteTraffic -> NoteTraffic
        | TagMessageTraffic -> MessageTraffic
        | unknown -> raise (ProtocolError $"unknown channel tag {unknown}")

    /// The envelope goes on the wire in the clear, ahead of the sealed payload.
    let encodeEnvelope (envelope: Envelope) : byte[] =
        let addressed (tag: byte) (write: BinaryWriter -> unit) =
            use ms = new MemoryStream()
            use w = new BinaryWriter(ms, UTF8Encoding false, true)
            w.Write tag
            write w
            w.Flush()
            ms.ToArray()

        match envelope with
        | Direct -> [| TagDirect |]
        | ToHandle(handle, channel) ->
            addressed TagToHandle (fun w ->
                w.Write handle.Value
                w.Write(channelTag channel))
        | FromHandle(handle, channel, post) ->
            addressed TagFromHandle (fun w ->
                w.Write handle.Value
                w.Write(channelTag channel)
                w.Write post)

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
                    let channel = readChannel r

                    let envelope =
                        if buffer[0] = TagToHandle then
                            ToHandle(handle, channel)
                        else
                            FromHandle(handle, channel, r.ReadInt64())

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
