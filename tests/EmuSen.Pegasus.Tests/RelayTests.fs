module EmuSen.Pegasus.Tests.RelayTests

open System
open System.Threading
open Xunit
open EmuSen.Pegasus
open EmuSen.Pegasus.Tests.Stubs

let private ct = CancellationToken.None

/// A throwaway identity database, for the tests that need a real pin rather
/// than a rule that takes whoever turns up.
let private tempIdentityRoot () =
    let dir = IO.Path.Combine(IO.Path.GetTempPath(), "pegasus-relay", Guid.NewGuid().ToString "N")
    IO.Directory.CreateDirectory dir |> ignore
    dir

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
            RelayClient.connectAsync "127.0.0.1" relay.Port Passphrase identity Peers.acceptAny ct
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

// ---------------------------------------------------------------------------
// Pass 7: the server proves itself, and the passphrase stops being a key
// ---------------------------------------------------------------------------

[<Fact>]
let ``a server whose key changed is refused`` () =
    // Before this, possession of the passphrase was a client's ONLY assurance
    // it had reached the right server, so anybody holding it could stand one up
    // and be believed. Chariot now sends a Hello carrying its own key and signs
    // the nonce the client sends, and the client pins it exactly the way it
    // pins a person -- one implementation, one table.
    let root = tempIdentityRoot ()
    use alice = Peers.identity "alice"
    let trust = Controller.pinnedTrust root alice.Handle

    use honest = new StubRelay(Passphrase, Identity.Generate(Handle.Parse "chariot"))
    honest.Open()

    let first =
        RelayClient.connectAsync "127.0.0.1" honest.Port Passphrase alice trust ct
        |> _.GetAwaiter().GetResult()

    Assert.Equal(Some "chariot", first.Server |> Option.map _.Handle.Value)
    (first :> IDisposable).Dispose()

    // The same name, a different key. Somebody who holds the passphrase and
    // wants to be believed.
    use impostor = new StubRelay(Passphrase, Identity.Generate(Handle.Parse "chariot"))
    impostor.Open()

    let refused =
        Assert.Throws<ProtocolError>(fun () ->
            RelayClient.connectAsync "127.0.0.1" impostor.Port Passphrase alice trust ct
            |> _.GetAwaiter().GetResult()
            |> ignore)

    // The message is the one a person has to act on: either the server was
    // rebuilt, or this is not the server. Only a human can tell which.
    Assert.Contains("pinned", refused.Data0)

[<Fact>]
let ``the same server on a second connection is recognised rather than refused`` () =
    // The other half of the guard above. A pin that refused everything would
    // pass that test and be useless.
    let root = tempIdentityRoot ()
    use alice = Peers.identity "alice"
    let trust = Controller.pinnedTrust root alice.Handle

    use relay = new StubRelay(Passphrase)
    relay.Open()

    for _ in 1..2 do
        let client =
            RelayClient.connectAsync "127.0.0.1" relay.Port Passphrase alice trust ct
            |> _.GetAwaiter().GetResult()

        Assert.True(client.Server.IsSome)
        (client :> IDisposable).Dispose()

[<Fact>]
let ``holding the passphrase does not let you read a roster`` () =
    // The pass, from the other end. The passphrase is the doorbell: it opens
    // the connection and seals the sign-in exchange, and then both ends agree
    // an ephemeral key and everything after is sealed under that. Asserted
    // against every frame the server actually said, not against a promise.
    use pair = new Pair()
    Assert.True(waitFor 5000 (fun () -> pair.Alice.Roster.Length = 1))

    let doorKey = Crypto.deriveKey Passphrase
    let said = pair.Relay.Said
    Assert.NotEmpty said

    let readable =
        said
        |> Array.choose (Crypto.tryOpenSealed doorKey)
        |> Array.map Codec.decode

    // The sign-in exchange is under the door key and has to be -- it is what
    // produces the session key. Nothing beyond it may be.
    Assert.All(
        readable,
        fun frame ->
            match frame with
            | Hello _
            | Challenge _
            | Proof _
            | Agree _ -> ()
            | other -> failwith $"the passphrase opened a {other.GetType().Name} the server said after signing in"
    )

    // And a roster was genuinely sent, so the assertion above is not vacuous.
    Assert.True(said.Length > readable.Length, "the server said nothing the passphrase could not open")
