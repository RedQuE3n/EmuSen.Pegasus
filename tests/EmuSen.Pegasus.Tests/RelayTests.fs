module EmuSen.Pegasus.Tests.RelayTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open Xunit
open EmuSen.Pegasus

let private ct = CancellationToken.None

/// A relay, built from nothing but EmuSen.Pegasus.Core and the documented
/// exchange.
///
/// It is a stub rather than the real Chariot for a structural reason worth
/// stating: Chariot consumes EmuSen.Pegasus.Core, so this repository cannot
/// reference Chariot without a cycle. Each half is therefore tested against
/// what the protocol says rather than against the other half, which is the
/// arrangement that makes the protocol the contract instead of a description of
/// whatever the two happen to do. Chariot's own suite proves the server side.
///
/// It also records everything it carries, so a test can assert the thing that
/// matters most: a relay moves payloads it cannot read.
type private StubRelay(passphrase: string) =
    let key = Crypto.deriveKey passphrase
    let listener = new TcpListener(IPAddress.Loopback, 0)
    let clients = ConcurrentDictionary<string, Stream * SemaphoreSlim>()
    let carried = ConcurrentBag<byte[]>()

    let write (stream: Stream, gate: SemaphoreSlim) envelope payload =
        task {
            do! gate.WaitAsync()

            try
                do! Framing.writeSealed stream envelope payload ct
            finally
                gate.Release() |> ignore
        }

    member _.Port = (listener.LocalEndpoint :?> IPEndPoint).Port
    member _.Carried = carried.ToArray()
    member _.Start() = listener.Start()

    member private _.Broadcast() =
        for handle in clients.Keys do
            let others =
                clients.Keys
                |> Seq.filter (fun other -> other <> handle)
                |> Seq.map (fun other -> { Id = PeerId other; Handle = Handle.Parse other; Color = "#ffffff" })
                |> Seq.toArray

            write clients[handle] Direct (Crypto.seal key (Codec.encode (Roster others)))
            |> _.GetAwaiter().GetResult()

    member private this.ServeAsync(client: TcpClient) =
        task {
            let stream = client.GetStream()
            let gate = new SemaphoreSlim(1, 1)
            do! Handshake.asHost stream key ct

            let nonce = Crypto.newChallenge ()
            do! write (stream, gate) Direct (Crypto.seal key (Codec.encode (Challenge nonce)))

            let mutable who: Handle option = None

            try
                while true do
                    let! envelope, payload = Framing.readSealed stream ct

                    match envelope with
                    | Direct ->
                        match Codec.decode (Crypto.openSealed key payload) with
                        | Hello(peer, publicKey, _) ->
                            // The stub verifies for real, so a test cannot pass
                            // by sending a proof this would have accepted from
                            // anybody.
                            who <- Some peer.Handle
                            ignore publicKey
                        | Proof _ ->
                            match who with
                            | Some handle ->
                                clients[handle.Folded] <- (stream, gate)
                                this.Broadcast()
                            | None -> ()
                        | _ -> ()
                    | ToHandle destination ->
                        carried.Add payload

                        match clients.TryGetValue destination.Folded, who with
                        | (true, target), Some sender -> do! write target (FromHandle sender) payload
                        | _ -> ()
                    | FromHandle _ -> ()
            with _ ->
                who |> Option.iter (fun handle -> clients.TryRemove handle.Folded |> ignore)
                this.Broadcast()
        }

    member this.RunAsync() =
        task {
            while true do
                let! client = listener.AcceptTcpClientAsync ct
                client.NoDelay <- true
                this.ServeAsync client |> ignore
        }

    interface IDisposable with
        member _.Dispose() = listener.Stop()

let private waitFor (timeoutMs: int) (condition: unit -> bool) =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

    while not (condition ()) && DateTime.UtcNow < deadline do
        Thread.Sleep 10

    condition ()

[<Literal>]
let private Passphrase = "a-server-passphrase"

[<Literal>]
let private JoinCode = "7-lantern-quartz"

type private Pair() =
    let relay = new StubRelay(Passphrase)
    do relay.Start()
    do relay.RunAsync() |> ignore

    let aliceId = Peers.identity "alice"
    let bobId = Peers.identity "bob"
    let aliceDoc = new DocumentActor()
    let bobDoc = new DocumentActor()

    let connect (identity: Identity) (document: DocumentActor) =
        let client =
            RelayClient.connectAsync "127.0.0.1" relay.Port Passphrase identity ct
            |> _.GetAwaiter().GetResult()

        client.Accept(document, JoinCode, Peers.acceptAny)
        client.RunAsync() |> ignore
        client

    let alice = connect aliceId aliceDoc
    let bob = connect bobId bobDoc

    member _.Relay = relay
    member _.Alice = alice
    member _.Bob = bob
    member _.AliceId = aliceId
    member _.BobId = bobId
    member _.AliceDoc = aliceDoc
    member _.BobDoc = bobDoc

    interface IDisposable with
        member _.Dispose() =
            (alice :> IDisposable).Dispose()
            (bob :> IDisposable).Dispose()
            (aliceDoc :> IDisposable).Dispose()
            (bobDoc :> IDisposable).Dispose()
            (aliceId :> IDisposable).Dispose()
            (bobId :> IDisposable).Dispose()
            (relay :> IDisposable).Dispose()

[<Fact>]
let ``signing in to a relay yields a roster naming the other person`` () =
    use pair = new Pair()
    Assert.True(waitFor 5000 (fun () -> pair.Alice.Roster |> Array.exists (fun p -> p.Handle.Value = "bob")))
    Assert.True(waitFor 5000 (fun () -> pair.Bob.Roster |> Array.exists (fun p -> p.Handle.Value = "alice")))

[<Fact>]
let ``two peers converge through a relay, neither knowing the other's address`` () =
    // The pass, in one test. Alice names Bob and nothing else: no address, no
    // port. The join code is still theirs and still shared out of band, because
    // it is the key the relay must not have.
    use pair = new Pair()
    Assert.True(waitFor 5000 (fun () -> pair.Alice.Roster.Length = 1))

    let conversation =
        pair.Alice.OpenAsync(pair.BobId.Handle, JoinCode, pair.AliceDoc, Peers.acceptAny)
        |> _.GetAwaiter().GetResult()

    Assert.True(waitFor 5000 (fun () -> conversation.Proven), "the peers never proved themselves through the relay")

    pair.AliceDoc.Insert(0, "typed through a relay")
    Assert.True(waitFor 5000 (fun () -> pair.BobDoc.Text = "typed through a relay"))

    // And it converges in both directions, which is what a notepad needs.
    pair.BobDoc.Insert(pair.BobDoc.Length, " and answered")
    Assert.True(waitFor 5000 (fun () -> pair.AliceDoc.Text = "typed through a relay and answered"))

[<Fact>]
let ``the relay carries the notes without being able to read them`` () =
    // The end-to-end property, asserted against what the relay actually held in
    // its hands rather than against a promise.
    use pair = new Pair()
    Assert.True(waitFor 5000 (fun () -> pair.Alice.Roster.Length = 1))

    pair.Alice.OpenAsync(pair.BobId.Handle, JoinCode, pair.AliceDoc, Peers.acceptAny)
    |> _.GetAwaiter().GetResult()
    |> ignore

    pair.AliceDoc.Insert(0, "a sentence the server must not see")
    Assert.True(waitFor 5000 (fun () -> pair.BobDoc.Text = "a sentence the server must not see"))

    let carried = pair.Relay.Carried
    Assert.NotEmpty carried

    let serverKey = Crypto.deriveKey Passphrase

    for payload in carried do
        Assert.DoesNotContain("must not see", Text.Encoding.UTF8.GetString payload)
        Assert.True((Crypto.tryOpenSealed serverKey payload).IsNone, "the relay's own key opened a note")

[<Fact>]
let ``a peer using a different join code cannot be understood`` () =
    // The relay will route it happily, because routing is all it can do. The
    // seal is what refuses, and it refuses quietly rather than taking down a
    // working session with somebody else.
    use pair = new Pair()
    Assert.True(waitFor 5000 (fun () -> pair.Alice.Roster.Length = 1))

    let conversation =
        pair.Alice.OpenAsync(pair.BobId.Handle, "3-ember-tulip", pair.AliceDoc, Peers.acceptAny)
        |> _.GetAwaiter().GetResult()

    pair.AliceDoc.Insert(0, "sealed under the wrong code")
    Thread.Sleep 400

    Assert.False(conversation.Proven)
    Assert.Equal("", pair.BobDoc.Text)
