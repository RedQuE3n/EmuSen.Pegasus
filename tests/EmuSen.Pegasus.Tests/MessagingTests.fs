module EmuSen.Pegasus.Tests.MessagingTests

open System
open System.IO
open System.Text
open Xunit
open EmuSen.Pegasus
open EmuSen.Pegasus.Tests.Headless
open EmuSen.Pegasus.Tests.Stubs

/// Waits without touching the dispatcher.
///
/// Deliberately not `Headless.pump`, which drains Avalonia's job queue and can
/// only be called from the thread that owns it. Nothing below builds a control:
/// these drive the controller and the relay, which is the layer messaging
/// actually lives in, and borrowing a UI helper for them would tie a network
/// test to a window that is not there.
let private settle (condition: unit -> bool) =
    let deadline = DateTime.UtcNow.AddMilliseconds 5000.0

    while not (condition ()) && DateTime.UtcNow < deadline do
        Threading.Thread.Sleep 10

    condition ()

[<Literal>]
let private Passphrase = "a-server-passphrase"

let private body (text: string) = Encoding.UTF8.GetBytes text

let private sealFrom (sender: Identity) (recipient: Identity) (text: string) =
    Messaging.seal sender recipient.MessagingPublicKey (body text)

// ---------------------------------------------------------------------------
// The envelope a message travels in
// ---------------------------------------------------------------------------

[<Fact>]
let ``a message sealed for somebody opens for them and for nobody else`` () =
    use alice = Peers.identity "alice"
    use bob = Peers.identity "bob"
    use eve = Peers.identity "eve"

    let sealed' = sealFrom alice bob "the quick brown fox"

    Assert.Equal<byte[] option>(Some(body "the quick brown fox"), Messaging.tryOpen bob alice.MessagingPublicKey sealed')

    // Eve holds a perfectly good messaging key. It is not the one this was
    // sealed to, and that is the whole of the guarantee.
    Assert.True((Messaging.tryOpen eve alice.MessagingPublicKey sealed').IsNone, "a third party opened somebody else's message")

[<Fact>]
let ``the sealed body is not the plaintext, and the relay carries only the sealed form`` () =
    // A guard against the failure mode that would be catastrophic and silent:
    // seal returning something that merely looks encrypted. Asserted on the
    // bytes rather than on a promise.
    use alice = Peers.identity "alice"
    use bob = Peers.identity "bob"

    let secret = "a sentence the server must not see"
    let sealed' = sealFrom alice bob secret

    Assert.DoesNotContain(Encoding.UTF8.GetString sealed', secret)
    Assert.True(sealed'.Length > secret.Length, "the sealed form was shorter than the plaintext")

[<Fact>]
let ``a message is refused when it was not sealed by the sender it is attributed to`` () =
    // THE PROPERTY THE STATIC LEG OF THE AGREEMENT BUYS, and the reason
    // Messaging.fs does two Diffie-Hellmans rather than one.
    //
    // Bob's messaging key is published: everybody has it, that is what published
    // means. So Mallory can certainly seal something Bob can open. What Mallory
    // cannot do is make it open under ALICE's key, because half the agreement
    // uses the sender's messaging private key and Mallory does not hold Alice's.
    //
    // Without that leg this test would pass with `Some`, the relay's FromHandle
    // stamp would be the only thing naming a sender, and anybody with an account
    // could put words in anybody's mouth.
    use alice = Peers.identity "alice"
    use bob = Peers.identity "bob"
    use mallory = Peers.identity "mallory"

    let forged = sealFrom mallory bob "alice would never say this"

    Assert.True(
        (Messaging.tryOpen bob alice.MessagingPublicKey forged).IsNone,
        "a message sealed by mallory opened as though alice had written it"
    )

    // And it is a real message, so the test is not passing because the bytes
    // were nonsense.
    Assert.Equal<byte[] option>(Some(body "alice would never say this"), Messaging.tryOpen bob mallory.MessagingPublicKey forged)

[<Fact>]
let ``a tampered message does not open`` () =
    use alice = Peers.identity "alice"
    use bob = Peers.identity "bob"

    let sealed' = sealFrom alice bob "unaltered"

    // The last byte is inside the GCM tag, which is what authenticates the body.
    let tampered = Array.copy sealed'
    tampered[tampered.Length - 1] <- tampered[tampered.Length - 1] ^^^ 0xFFuy

    Assert.True((Messaging.tryOpen bob alice.MessagingPublicKey tampered).IsNone, "a tampered message opened")

[<Fact>]
let ``a truncated or empty blob is refused rather than throwing`` () =
    // These arrive from the network by definition, so the failure has to be a
    // None a caller can report and not an exception that takes the session down.
    use alice = Peers.identity "alice"
    use bob = Peers.identity "bob"

    let sealed' = sealFrom alice bob "long enough to cut"

    for length in [ 0; 1; 4; 20; sealed'.Length - 1 ] do
        Assert.True(
            (Messaging.tryOpen bob alice.MessagingPublicKey sealed'[.. length - 1]).IsNone,
            $"a {length}-byte blob was accepted"
        )

// ---------------------------------------------------------------------------
// Cards: the directory, and what stops a relay lying through it
// ---------------------------------------------------------------------------

[<Fact>]
let ``a card is signed by the identity it names`` () =
    use alice = Peers.identity "alice"
    let card = Messaging.cardOf alice

    Assert.Equal(alice.Handle, card.Handle)
    Assert.Equal<byte[]>(alice.PublicKey, card.Identity)
    Assert.Equal<byte[]>(alice.MessagingPublicKey, card.Messaging)
    Assert.True(Messaging.verifyCard card, "an identity's own card did not verify")

[<Fact>]
let ``a card carrying somebody else's messaging key is refused`` () =
    // The substitution a relay would make if it wanted to read somebody's post:
    // keep the identity key everybody pinned, swap the messaging key for one it
    // holds the private half of. The signature is what catches it.
    use alice = Peers.identity "alice"
    use eve = Peers.identity "eve"

    let swapped =
        { Messaging.cardOf alice with Messaging = eve.MessagingPublicKey }

    Assert.False(Messaging.verifyCard swapped, "a card with a substituted messaging key verified")

[<Fact>]
let ``a card is taken on first sight and pinned to the identity that sent it`` () =
    let root = tempRoot ()
    use alice = Peers.identity "alice"
    use bob = Peers.identity "bob"

    match KnownPeers.acceptCard root alice.Handle (Messaging.cardOf bob) with
    | Error why -> failwith $"a first card was refused: {why}"
    | Ok _ -> ()

    Assert.Equal<byte[] option>(Some bob.MessagingPublicKey, KnownPeers.messagingKeyFor root alice.Handle bob.Handle)

[<Fact>]
let ``a card whose identity key is not the pinned one is refused`` () =
    // The half `verifyCard` cannot do. This card is internally consistent — an
    // impostor signs their own messaging key perfectly well — and it is refused
    // because the identity key is not the one already written down for that
    // handle. A relay inventing a whole card is caught here and nowhere else.
    let root = tempRoot ()
    use alice = Peers.identity "alice"
    use bob = Peers.identity "bob"
    use impostor = Identity.Generate(Handle.Parse "bob")

    KnownPeers.acceptCard root alice.Handle (Messaging.cardOf bob) |> ignore

    match KnownPeers.acceptCard root alice.Handle (Messaging.cardOf impostor) with
    | Ok _ -> failwith "an impostor's card was pinned over the real one"
    | Error why -> Assert.Contains("pinned", why)

    // And the real key is still the one on file.
    Assert.Equal<byte[] option>(Some bob.MessagingPublicKey, KnownPeers.messagingKeyFor root alice.Handle bob.Handle)

[<Fact>]
let ``a new messaging key from the same identity is accepted`` () =
    // Required rather than merely allowed: an identity created before messaging
    // existed mints its messaging key the first time it is unlocked, so its card
    // legitimately changes while its identity stays what everybody pinned. A
    // build that froze the messaging key on first sight would make every such
    // person permanently unreachable.
    let root = tempRoot ()
    use alice = Peers.identity "alice"
    use bob = Peers.identity "bob"

    KnownPeers.acceptCard root alice.Handle (Messaging.cardOf bob) |> ignore

    // The same signing key, a second messaging key, signed by it.
    let rotatedPublic, _ = Messaging.newKeyPair ()

    let rotated =
        { Handle = bob.Handle
          Identity = bob.PublicKey
          Messaging = rotatedPublic
          Signature = bob.Sign(Messaging.keyPayload rotatedPublic) }

    match KnownPeers.acceptCard root alice.Handle rotated with
    | Error why -> failwith $"a rotation signed by the pinned identity was refused: {why}"
    | Ok _ -> ()

    Assert.Equal<byte[] option>(Some rotatedPublic, KnownPeers.messagingKeyFor root alice.Handle bob.Handle)

// ---------------------------------------------------------------------------
// The identity store
// ---------------------------------------------------------------------------

[<Fact>]
let ``the messaging private key never reaches the store in the clear`` () =
    // The same guard the signing key has, pointed at the key that matters more
    // in one specific way: this is the one that opens every message ever sent to
    // this identity, including any still sitting in a relay's mailbox.
    let root = tempRoot ()

    let secret =
        match IdentityStore.create root (Handle.Parse "alice") "a-password" with
        | Ok identity ->
            let exported = identity.ExportMessagingPrivateKey()
            (identity :> IDisposable).Dispose()
            exported
        | Error e -> failwith e.Message

    let raw = File.ReadAllBytes(IdentityStore.databaseIn root)

    let contains (haystack: byte[]) (needle: byte[]) =
        seq { 0 .. haystack.Length - needle.Length }
        |> Seq.exists (fun i -> Seq.forall2 (=) haystack[i .. i + needle.Length - 1] needle)

    Assert.False(contains raw secret, "the messaging private key is sitting in the database unsealed")

[<Fact>]
let ``an identity created before messaging gets a messaging key when it is unlocked`` () =
    // The migration, driven the way it actually happens rather than asserted
    // about: a row is put back into the shape a pre-messaging build wrote, and
    // then opened.
    let root = tempRoot ()
    let handle = Handle.Parse "alice"

    match IdentityStore.create root handle "a-password" with
    | Error e -> failwith e.Message
    | Ok identity -> (identity :> IDisposable).Dispose()

    do
        use db = Db.openAt (IdentityStore.databaseIn root)

        Db.executeWith db "UPDATE identities SET message_public = NULL, message_secret = NULL" []
        |> ignore

    match IdentityStore.unlock root handle "a-password" with
    | Error e -> failwith $"an identity without a messaging key would not open: {e.Message}"
    | Ok reopened ->
        Assert.NotEmpty reopened.MessagingPublicKey

        // And it was written back, so the same key is there next time rather
        // than a fresh one every sign-in — which would make everybody who had
        // pinned a card unable to reach this person.
        let again =
            match IdentityStore.unlock root handle "a-password" with
            | Ok second -> second
            | Error e -> failwith e.Message

        Assert.Equal<byte[]>(reopened.MessagingPublicKey, again.MessagingPublicKey)
        (reopened :> IDisposable).Dispose()
        (again :> IDisposable).Dispose()

[<Fact>]
let ``a wrong password still refuses an identity that has a messaging key`` () =
    let root = tempRoot ()
    let handle = Handle.Parse "alice"

    match IdentityStore.create root handle "a-password" with
    | Error e -> failwith e.Message
    | Ok identity -> (identity :> IDisposable).Dispose()

    match IdentityStore.unlock root handle "not-the-password" with
    | Ok _ -> failwith "a wrong password opened an identity"
    | Error error -> Assert.Equal(WrongPassword, error)

// ---------------------------------------------------------------------------
// The saved list and the saved transcript
// ---------------------------------------------------------------------------

[<Fact>]
let ``a friend stays on the list, and removing one keeps the conversation`` () =
    let root = tempRoot ()
    let alice = Handle.Parse "alice"
    let bob = Handle.Parse "bob"

    Friends.add root alice bob
    // Twice, because a buddy list is something people click at.
    Friends.add root alice bob

    Assert.Equal<Handle[]>([| bob |], Friends.all root alice)
    Assert.True(Friends.has root alice bob)

    let line =
        { Id = MessageId.New()
          Outbound = false
          SentAt = DateTimeOffset.UtcNow
          Body = "kept" }

    Chats.record root alice bob line |> ignore
    Friends.remove root alice bob

    Assert.Empty(Friends.all root alice)

    // Removing somebody from a list is not a request to destroy what they said.
    Assert.Equal(1, (Chats.conversation root alice bob).Length)

[<Fact>]
let ``one identity's friends are not another's`` () =
    let root = tempRoot ()
    Friends.add root (Handle.Parse "alice") (Handle.Parse "bob")

    Assert.Empty(Friends.all root (Handle.Parse "carol"))

[<Fact>]
let ``the same message recorded twice is written once`` () =
    // THE PROPERTY THE WHOLE ACKNOWLEDGEMENT DESIGN LEANS ON. Chariot redelivers
    // anything it has not seen acknowledged, so a client that dies between
    // reading a delivery and writing it down is MEANT to be handed that message
    // again. If this returned true twice the transcript would show it twice, and
    // the durability guarantee would have bought a visible defect.
    let root = tempRoot ()
    let alice = Handle.Parse "alice"
    let bob = Handle.Parse "bob"

    let line =
        { Id = MessageId.New()
          Outbound = false
          SentAt = DateTimeOffset.UtcNow
          Body = "say it once" }

    Assert.True(Chats.record root alice bob line, "the first copy was not recorded")
    Assert.False(Chats.record root alice bob line, "a redelivery was recorded a second time")
    Assert.Equal(1, (Chats.conversation root alice bob).Length)

[<Fact>]
let ``a transcript comes back in the order it arrived`` () =
    // Ordered by arrival here, not by the sender's clock, so a correspondent
    // whose clock is wrong mislabels their own lines without reordering yours.
    let root = tempRoot ()
    let alice = Handle.Parse "alice"
    let bob = Handle.Parse "bob"

    let line text sentAt =
        { Id = MessageId.New()
          Outbound = false
          SentAt = sentAt
          Body = text }

    let now = DateTimeOffset.UtcNow

    Chats.record root alice bob (line "first" now) |> ignore
    // A second message stamped an hour in the PAST by its sender.
    Chats.record root alice bob (line "second" (now.AddHours -1.0)) |> ignore

    Assert.Equal<string[]>(
        [| "first"; "second" |],
        Chats.conversation root alice bob |> Array.map _.Body
    )

// ---------------------------------------------------------------------------
// End to end, through a relay that cannot read any of it
// ---------------------------------------------------------------------------

[<Fact>]
let ``a message reaches the other person's transcript through a relay`` () =
    // The pass in one test, and the only one here that exercises the card
    // fetch, the seal, the routing, the open and the store together.
    use relay = new StubRelay(Passphrase)
    relay.Open()

    use aliceId = Peers.identity "alice"
    use bobId = Peers.identity "bob"

    let aliceRoot = tempRoot ()
    let bobRoot = tempRoot ()

    use alicePad =
        new Controller.Notepad(aliceRoot, aliceId, Controller.pinnedContacts aliceRoot aliceId.Handle)

    use bobPad =
        new Controller.Notepad(bobRoot, bobId, Controller.pinnedContacts bobRoot bobId.Handle)

    // A note has to be open before a relay session starts; that is the
    // controller's existing rule and messaging did not change it.
    alicePad.CreateNote "scratch" |> ignore
    bobPad.CreateNote "scratch" |> ignore

    let delivered = ResizeArray<Handle * Line>()
    bobPad.MessageRecorded.Add delivered.Add

    alicePad.SignInToRelay("127.0.0.1", relay.Port, Passphrase) |> ignore
    bobPad.SignInToRelay("127.0.0.1", relay.Port, Passphrase) |> ignore

    Assert.True(settle (fun () -> alicePad.IsOnRelay && bobPad.IsOnRelay), "neither client signed in")

    match alicePad.SendMessage(bobId.Handle, "hello through a relay") with
    | Error why -> failwith why
    | Ok() -> ()

    Assert.True(settle (fun () -> delivered.Count = 1), "the message never reached bob")
    let peer, line = delivered[0]
    Assert.Equal(aliceId.Handle, peer)
    Assert.Equal("hello through a relay", line.Body)
    Assert.False(line.Outbound)

    // Saved on both sides, which is what "users can save their chats" means.
    Assert.Equal(1, (bobPad.Conversation aliceId.Handle).Length)
    Assert.True(settle (fun () -> (alicePad.Conversation bobId.Handle).Length = 1), "the sender did not keep its own line")

    // AND THE RELAY HELD ONLY SEALED BYTES. Asserted against what it actually
    // carried rather than against a promise.
    let carried = relay.Carried
    Assert.NotEmpty carried

    for payload in carried do
        Assert.DoesNotContain(Encoding.UTF8.GetString payload, "hello through a relay")

    alicePad.Disconnect()
    bobPad.Disconnect()

[<Fact>]
let ``a message to somebody the relay has never heard of is reported, not swallowed`` () =
    use relay = new StubRelay(Passphrase)
    relay.Open()

    use aliceId = Peers.identity "alice"
    let aliceRoot = tempRoot ()

    use alicePad =
        new Controller.Notepad(aliceRoot, aliceId, Controller.pinnedContacts aliceRoot aliceId.Handle)

    alicePad.CreateNote "scratch" |> ignore

    let failures = ResizeArray<Handle * string>()
    alicePad.MessageFailed.Add failures.Add

    alicePad.SignInToRelay("127.0.0.1", relay.Port, Passphrase) |> ignore
    Assert.True(settle (fun () -> alicePad.IsOnRelay), "alice never signed in")

    match alicePad.SendMessage(Handle.Parse "nobody", "into the void") with
    | Error why -> failwith why
    | Ok() -> ()

    // A message that silently did not arrive is the failure this channel was
    // rebuilt to stop having, so the absence of a recipient has to surface.
    Assert.True(settle (fun () -> failures.Count = 1), "a message to an unknown handle was swallowed")
    Assert.Contains("never signed in", snd failures[0])

    alicePad.Disconnect()
