module EmuSen.Pegasus.Tests.Stubs

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open EmuSen.Pegasus

let private ct = CancellationToken.None

/// One connected client, as the stub relay sees it.
///
/// The key is per connection and not per server, which is the whole of pass 7
/// in one field: after sign-in, what this server says to one client is sealed
/// under a key agreed with that client and with nobody else.
type private Client =
    { Stream: Stream
      Gate: SemaphoreSlim
      mutable Key: byte[] }

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
/// It also records everything it carries and everything it says, so a test can
/// assert the two things that matter most: a relay moves payloads it cannot
/// read, and what it says on its own account is not readable to somebody
/// holding only the passphrase.
///
/// It lives here rather than beside the relay tests because the window tests
/// need one too — a buddy list with nobody in it proves nothing — and two
/// stubs of one protocol is how they drift apart.
type StubRelay(passphrase: string, identity: Identity) =
    let doorKey = Crypto.deriveKey passphrase
    let listener = new TcpListener(IPAddress.Loopback, 0)
    let clients = ConcurrentDictionary<string, Client>()
    let carried = ConcurrentBag<byte[]>()
    let said = ConcurrentBag<byte[]>()

    /// The card directory, which a messaging client cannot proceed without: a
    /// message is sealed to a key its recipient published, so a relay that
    /// cannot answer "what is their key" is a relay nobody can message through.
    let cards = ConcurrentDictionary<string, Card>()

    /// Post ids, handed out so a client has something to acknowledge. This stub
    /// keeps no mailbox, which is the one place it is knowingly not the real
    /// thing — enough to exercise a client, not enough to test redelivery. The
    /// Chariot suite has the real server for that.
    let mutable posts = 0L

    let write (client: Client) envelope payload =
        task {
            do! client.Gate.WaitAsync()

            try
                do! Framing.writeSealed client.Stream envelope payload ct
            finally
                client.Gate.Release() |> ignore
        }

    /// Everything this server says on its own account, sealed under whatever key
    /// was current when it said it.
    let say (client: Client) frame =
        let payload = Crypto.seal client.Key (Codec.encode frame)
        said.Add payload
        write client Direct payload

    /// A server that generates its own identity, which is what every test but
    /// the key-pinning one wants. That one supplies two servers with two keys
    /// and the same handle, which is the situation being refused.
    new(passphrase: string) = new StubRelay(passphrase, Identity.Generate(Handle.Parse "chariot"))

    member _.Port = (listener.LocalEndpoint :?> IPEndPoint).Port
    member _.Carried = carried.ToArray()
    member _.Said = said.ToArray()
    member _.Identity = identity
    member _.Start() = listener.Start()

    member private _.Broadcast() =
        for handle in clients.Keys do
            let others =
                clients.Keys
                |> Seq.filter (fun other -> other <> handle)
                |> Seq.map (fun other -> { Id = PeerId other; Handle = Handle.Parse other; Color = "#ffffff" })
                |> Seq.toArray

            say clients[handle] (Roster others) |> _.GetAwaiter().GetResult()

    /// The pass 7 exchange, server side, in the order the frames go out.
    ///
    /// Mirrors Chariot rather than sharing code with it, deliberately: if this
    /// and the server were one implementation, the suite would prove they agree
    /// with themselves rather than that either agrees with the protocol.
    member private _.SignInAsync(client: Client) =
        task {
            let ourNonce = Crypto.newChallenge ()
            use ephemeral = new Agreement.Ephemeral()

            do! say client (Hello(identity.Peer, identity.PublicKey, Version.Protocol))
            do! say client (Challenge ourNonce)

            let mutable who: PeerInfo option = None
            let mutable theirKey: byte[] option = None
            let mutable theirNonce: byte[] option = None
            let mutable proven = false
            let mutable offered = false
            let mutable admitted: Handle option = None

            while admitted.IsNone do
                let! _, payload = Framing.readSealed client.Stream ct

                match Codec.decode (Crypto.openSealed doorKey payload) with
                | Hello(peer, publicKey, _) ->
                    who <- Some peer
                    theirKey <- Some publicKey
                | Challenge nonce ->
                    theirNonce <- Some nonce
                    do! say client (Proof(Attestation.prove identity nonce))
                | Proof signature ->
                    // Verified for real, so a test cannot pass by sending a
                    // proof this would have accepted from anybody.
                    match who, theirKey with
                    | Some peer, Some publicKey ->
                        match Attestation.verify publicKey peer.Id ourNonce signature with
                        | Ok() -> proven <- true
                        | Error why -> failwith why
                    | _ -> failwith "a proof arrived before the hello it belongs to"
                | Agree(theirs, signature) ->
                    match who, theirKey, theirNonce with
                    | Some peer, Some publicKey, Some nonce when proven ->
                        match ephemeral.Accept(publicKey, theirs, signature, ourNonce, Agreement.salt ourNonce nonce) with
                        | Ok agreed ->
                            client.Key <- agreed
                            admitted <- Some peer.Handle
                        | Error why -> failwith why
                    | _ -> failwith "a key agreement arrived before the identity it belongs to"
                | _ -> ()

                // Offered once the client has proved itself and we hold the
                // nonce it wants us to sign over. Sent under the door key,
                // because the session key is the thing it produces.
                if proven && theirNonce.IsSome && not offered then
                    offered <- true
                    do! say client (ephemeral.Offer(identity, theirNonce.Value))

            return admitted.Value
        }

    member private this.ServeAsync(socket: TcpClient) =
        task {
            let client =
                { Stream = socket.GetStream()
                  Gate = new SemaphoreSlim(1, 1)
                  Key = doorKey }

            do! Handshake.asHost client.Stream doorKey ct

            let mutable who: Handle option = None

            try
                let! handle = this.SignInAsync client
                who <- Some handle
                clients[handle.Folded] <- client
                this.Broadcast()

                while true do
                    let! envelope, payload = Framing.readSealed client.Stream ct

                    match envelope with
                    | Direct ->
                        // Decodable because the agreement above left the session
                        // key on the client record. Answers the two frames a
                        // messaging client cannot proceed without — publish a
                        // card, ask for one — and reads acknowledgements without
                        // acting on them, there being no mailbox here to clear.
                        match Codec.decode (Crypto.openSealed client.Key payload) with
                        | Card card -> cards[card.Handle.Folded] <- card
                        | Ask who ->
                            match cards.TryGetValue who.Folded with
                            | true, card -> do! say client (Card card)
                            | _ -> do! say client (Unknown who)
                        | _ -> ()
                    | ToHandle(destination, channel) ->
                        carried.Add payload

                        match clients.TryGetValue destination.Folded with
                        | true, target ->
                            let post = Interlocked.Increment &posts
                            do! write target (FromHandle(handle, channel, post)) payload
                        | _ -> ()
                    | FromHandle _ -> ()
            with _ ->
                who |> Option.iter (fun handle -> clients.TryRemove handle.Folded |> ignore)
                this.Broadcast()
        }

    member this.RunAsync() =
        task {
            while true do
                let! socket = listener.AcceptTcpClientAsync ct
                socket.NoDelay <- true
                this.ServeAsync socket |> ignore
        }

    /// Started and accepting in one call, because every caller wants both and
    /// forgetting the second produces a test that hangs rather than one that
    /// fails.
    member this.Open() =
        this.Start()
        this.RunAsync() |> ignore

    interface IDisposable with
        member _.Dispose() = listener.Stop()

/// Waits for a condition on a background thread, for tests with no dispatcher
/// to pump. The UI suite has Headless.pump, which does the same thing and also
/// runs posted work.
let waitFor (timeoutMs: int) (condition: unit -> bool) =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

    while not (condition ()) && DateTime.UtcNow < deadline do
        Thread.Sleep 10

    condition ()
