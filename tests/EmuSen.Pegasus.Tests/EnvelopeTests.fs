module EmuSen.Pegasus.Tests.EnvelopeTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open EmuSen.Pegasus

let private ct = CancellationToken.None

// ---------------------------------------------------------------------------
// The envelope itself
// ---------------------------------------------------------------------------

[<Fact>]
let ``both envelopes survive a round trip`` () =
    for envelope in [ Direct; ToHandle(Handle.Parse "RedQuE3n", NoteTraffic); FromHandle(Handle.Parse "RedQuE3n", NoteTraffic, 0L) ] do
        let encoded = Codec.encodeEnvelope envelope
        let decoded, consumed = Codec.decodeEnvelope encoded
        Assert.Equal(envelope, decoded)
        Assert.Equal(encoded.Length, consumed)

[<Fact>]
let ``the envelope reports its own length, so the payload can be found`` () =
    // The sealed payload begins immediately after the envelope and nothing
    // separates them, so a decoder that got this count wrong would hand the
    // wrong bytes to AES and the failure would look like a corrupt frame.
    let envelope = ToHandle(Handle.Parse "RedQuE3n", NoteTraffic)
    let head = Codec.encodeEnvelope envelope
    let payload = "sealed bytes go here"B
    let buffer = Array.append head payload

    let decoded, consumed = Codec.decodeEnvelope buffer
    Assert.Equal(envelope, decoded)
    Assert.Equal<byte[]>(payload, buffer[consumed..])

[<Fact>]
let ``to and from are not confused for one another`` () =
    // They carry the same payload and differ only by tag, which is exactly the
    // shape of mistake a decoder makes silently.
    let handle = Handle.Parse "RedQuE3n"
    Assert.NotEqual<byte[]>(Codec.encodeEnvelope (ToHandle(handle, NoteTraffic)), Codec.encodeEnvelope (FromHandle(handle, NoteTraffic, 0L)))
    Assert.Equal(ToHandle(handle, NoteTraffic), fst (Codec.decodeEnvelope (Codec.encodeEnvelope (ToHandle(handle, NoteTraffic)))))
    Assert.Equal(FromHandle(handle, NoteTraffic, 0L), fst (Codec.decodeEnvelope (Codec.encodeEnvelope (FromHandle(handle, NoteTraffic, 0L)))))

[<Fact>]
let ``an unknown envelope tag is refused rather than guessed at`` () =
    Assert.Throws<ProtocolError>(fun () -> Codec.decodeEnvelope [| 99uy |] |> ignore)

[<Fact>]
let ``an empty envelope is refused`` () =
    Assert.Throws<ProtocolError>(fun () -> Codec.decodeEnvelope [||] |> ignore)

[<Fact>]
let ``a destination that is not a usable handle is refused`` () =
    // The grammar holds for a destination arriving off the wire exactly as it
    // does for one typed locally, so a relay can never be made to queue under a
    // key it could not have produced itself.
    use ms = new MemoryStream()
    use w = new BinaryWriter(ms, UTF8Encoding false, true)
    w.Write 1uy
    w.Write "not a handle!"
    w.Flush()

    Assert.Throws<ProtocolError>(fun () -> Codec.decodeEnvelope (ms.ToArray()) |> ignore)

// ---------------------------------------------------------------------------
// Framing: the peer path and the relay path over the same wire
// ---------------------------------------------------------------------------

let private roundTrip (envelope: Envelope) (frame: Frame) (key: byte[]) =
    use stream = new MemoryStream()
    Framing.writeFrame stream key envelope frame ct |> _.GetAwaiter().GetResult()
    stream.Position <- 0L
    Framing.readFrame stream key ct |> _.GetAwaiter().GetResult()

[<Fact>]
let ``a frame survives the wire with its envelope`` () =
    let key = Crypto.deriveKey "7-lantern-quartz"
    let sent = Update [| 1uy; 2uy; 3uy |]

    for envelope in [ Direct; ToHandle(Handle.Parse "RedQuE3n", NoteTraffic); FromHandle(Handle.Parse "RedQuE3n", NoteTraffic, 0L) ] do
        let gotEnvelope, gotFrame = roundTrip envelope sent key
        Assert.Equal(envelope, gotEnvelope)
        Assert.Equal(sent, gotFrame)

[<Fact>]
let ``a relay reads the destination and cannot read the payload`` () =
    // The whole point of this pass, in one test. Chariot holds no join code, so
    // it must be able to route a frame it has no way to open. If this ever
    // fails in the other direction -- a relay that CAN open payloads -- the
    // end-to-end property is gone and Chariot_Design.md §1 is a lie.
    let key = Crypto.deriveKey "7-lantern-quartz"
    let secret = Update(Encoding.UTF8.GetBytes "the contents of somebody's note")
    let destination = ToHandle(Handle.Parse "RedQuE3n", NoteTraffic)

    use stream = new MemoryStream()
    Framing.writeFrame stream key destination secret ct |> _.GetAwaiter().GetResult()
    stream.Position <- 0L

    // Exactly what a relay can do: no key is passed to this call.
    let envelope, sealedBytes = Framing.readSealed stream ct |> _.GetAwaiter().GetResult()

    Assert.Equal(destination, envelope)
    Assert.DoesNotContain("somebody's note", Encoding.UTF8.GetString sealedBytes)

    // And it stays shut against anything a relay could reasonably try.
    Assert.True((Crypto.tryOpenSealed (Crypto.deriveKey "7-lantern-cobalt") sealedBytes).IsNone)
    Assert.True((Crypto.tryOpenSealed (Array.zeroCreate Crypto.KeyBytes) sealedBytes).IsNone)

    // The peer it was addressed to, which does hold the code, opens it.
    Assert.Equal(secret, Codec.decode (Crypto.openSealed key sealedBytes))

[<Fact>]
let ``a relay can forward a payload it never opened`` () =
    // Store and forward in miniature: bytes in, bytes out, no key anywhere in
    // between, and the recipient still decodes what the sender wrote.
    let key = Crypto.deriveKey "3-ember-tulip"
    let sent = Update(Encoding.UTF8.GetBytes "carried, not read")

    use inbound = new MemoryStream()
    Framing.writeFrame inbound key (ToHandle(Handle.Parse "RedQuE3n", NoteTraffic)) sent ct |> _.GetAwaiter().GetResult()
    inbound.Position <- 0L

    let envelope, sealedBytes = Framing.readSealed inbound ct |> _.GetAwaiter().GetResult()
    Assert.Equal(ToHandle(Handle.Parse "RedQuE3n", NoteTraffic), envelope)

    // The relay rewrites the destination to Direct on delivery: the recipient
    // is the destination, so there is nothing left to route.
    use outbound = new MemoryStream()
    Framing.writeSealed outbound Direct sealedBytes ct |> _.GetAwaiter().GetResult()
    outbound.Position <- 0L

    let delivered, frame = Framing.readFrame outbound key ct |> _.GetAwaiter().GetResult()
    Assert.Equal(Direct, delivered)
    Assert.Equal(sent, frame)

[<Fact>]
let ``a frame with an envelope and no payload is refused`` () =
    use stream = new MemoryStream()
    Framing.writeSealed stream Direct [||] ct |> _.GetAwaiter().GetResult()
    stream.Position <- 0L

    let failure =
        Assert.Throws<ProtocolError>(fun () -> Framing.readSealed stream ct |> _.GetAwaiter().GetResult() |> ignore)

    Assert.Contains("no payload", failure.Data0)

[<Fact>]
let ``a hostile length is refused before anything is allocated`` () =
    // A relay talks to strangers by definition, so this check matters more on
    // that path than on a peer connection where the join code has already been
    // proved. int32.MaxValue here would be a 2 GB allocation on a claim.
    use stream = new MemoryStream()
    stream.Write(BitConverter.GetBytes Int32.MaxValue, 0, 4)
    stream.Position <- 0L

    Assert.Throws<ProtocolError>(fun () -> Framing.readSealed stream ct |> _.GetAwaiter().GetResult() |> ignore)
    |> ignore

[<Fact>]
let ``a routed frame is refused on a direct peer connection`` () =
    // Nothing put a plain socket behind a relay, so an addressed frame arriving
    // on one is either a confused intermediary or somebody probing. Driven over
    // a real socket in NetTests; this pins the decision itself.
    use alice = new DocumentActor()
    use aliceId = Peers.identity "alice"
    let code = Crypto.newJoinCode ()
    let key = Crypto.deriveKey code

    use host = new Host(0, code, aliceId, alice, Peers.acceptAny)
    host.Start()
    let accepted = host.AcceptAsync ct

    let rogue =
        task {
            let client = new Net.Sockets.TcpClient()
            do! client.ConnectAsync("127.0.0.1", host.Port)
            let stream = client.GetStream()
            do! Handshake.asJoiner stream key ct
            do! Framing.writeFrame stream key (ToHandle(Handle.Parse "elsewhere", NoteTraffic)) Bye ct
            return client
        }

    use session = accepted.GetAwaiter().GetResult()
    let faults = ResizeArray<exn>()
    session.Faulted.Add faults.Add
    session.RunAsync() |> ignore

    let deadline = DateTime.UtcNow.AddSeconds 5.0
    while faults.Count = 0 && DateTime.UtcNow < deadline do
        Threading.Thread.Sleep 10

    Assert.True(faults.Count > 0, "a routed frame was accepted on a direct connection")
    Assert.Contains("routed frame", faults[0].Message)
    (rogue.GetAwaiter().GetResult() :> IDisposable).Dispose()
