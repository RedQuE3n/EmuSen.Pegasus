module EmuSen.Pegasus.Tests.RelayTests

open System
open System.Threading
open Xunit
open EmuSen.Pegasus
open EmuSen.Pegasus.Tests.Stubs

let private ct = CancellationToken.None

[<Literal>]
let private Passphrase = "a-server-passphrase"

[<Literal>]
let private JoinCode = "7-lantern-quartz"

type private Pair(?accepting: bool) =
    let relay = new StubRelay(Passphrase)
    do relay.Open()

    let aliceId = Peers.identity "alice"
    let bobId = Peers.identity "bob"
    let aliceDoc = new DocumentActor()
    let bobDoc = new DocumentActor()

    /// Whether each client says up front which note it will answer to. False
    /// is the harder case and the one the late-opener test needs: nobody is
    /// willing to be invited, so the first frames of a conversation land on a
    /// client with nothing to receive them.
    let accepting = defaultArg accepting true

    let connect (identity: Identity) (document: DocumentActor) =
        let client =
            RelayClient.connectAsync "127.0.0.1" relay.Port Passphrase identity ct
            |> _.GetAwaiter().GetResult()

        if accepting then
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

[<Fact>]
let ``an opener that arrives second still completes the handshake for both`` () =
    // Found by building the window: two people click "Open note" a few seconds
    // apart, which is what people do. Whoever clicks first sends a Hello and a
    // Challenge into a client that has nothing to receive them with, and both
    // are dropped -- so the first Challenge is gone and the early end can never
    // become proven. It then refuses the late end's SyncStep1 as document
    // traffic from somebody unproven, and the pairing fails in a way that looks
    // like the relay eating frames.
    //
    // The fix is that a Hello re-sends our own challenge, unchanged. This is
    // the guard for it, and removing that line turns it red.
    use pair = new Pair(accepting = false)
    Assert.True(waitFor 5000 (fun () -> pair.Alice.Roster.Length = 1))

    let early =
        pair.Alice.OpenAsync(pair.BobId.Handle, JoinCode, pair.AliceDoc, Peers.acceptAny)
        |> _.GetAwaiter().GetResult()

    // Nothing at the other end is listening for this yet, and that is the point.
    Thread.Sleep 300
    Assert.False(early.Proven)

    let late =
        pair.Bob.OpenAsync(pair.AliceId.Handle, JoinCode, pair.BobDoc, Peers.acceptAny)
        |> _.GetAwaiter().GetResult()

    Assert.True(waitFor 5000 (fun () -> early.Proven && late.Proven), "the late open left half a handshake behind")

    // And it is a working conversation rather than merely two proven ends.
    pair.AliceDoc.Insert(0, "clicked a few seconds apart")
    Assert.True(waitFor 5000 (fun () -> pair.BobDoc.Text = "clicked a few seconds apart"))
