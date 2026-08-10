namespace EmuSen.Pegasus

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Sockets
open System.Threading

/// Pegasus as a client of Chariot.
///
/// What this removes is the address and the port. You sign in to a server once,
/// see who else is there, and reach them by handle. What it does NOT remove is
/// the join code: that is the key your notes are sealed under, and Chariot
/// never has it. The server learns who is talking to whom and how much; it
/// cannot learn a word of what.
///
/// ONE SOCKET, TWO KEY DOMAINS, and everything here follows from that. Frames
/// addressed to Chariot -- signing in, the roster -- are sealed under the
/// control key. Frames for a peer are sealed under the join code and merely
/// addressed through Chariot. The envelope is what keeps them apart: `Direct` is
/// Chariot's, `ToHandle` and `FromHandle` are somebody else's and are passed
/// through without this class being able to read them either, since it holds the
/// join code only for the conversations it opened.
///
/// THE CONTROL KEY IS NOT THE PASSPHRASE, and that is the point of this pass.
/// The passphrase opens the door and seals the sign-in exchange, and then both
/// ends agree an ephemeral key and everything after is sealed under that. Before
/// it, every client held the key to every other client's roster, and a recording
/// stayed readable to whoever later learned one shared secret. Agreement.fs
/// carries the argument; Pegasus_Sync.md §4.3 is the exchange.
///
/// `trust` is the same trust-on-first-use rule a peer gets, pointed at the
/// server. Chariot now sends a Hello carrying its own key and signs a challenge
/// with it, so a server whose key has changed is refused exactly the way a
/// person whose key has changed is refused -- one implementation, one table, one
/// thing to reason about.
type Relay(stream: Stream, self: Identity, passphrase: string, trust: PeerInfo -> byte[] -> Result<unit, string>) =
    /// The doorbell: what Handshake ran on, and what the sign-in exchange itself
    /// is sealed under. Everything after the agreement uses the session key.
    let doorKey = Crypto.deriveKey passphrase

    let cts = new CancellationTokenSource()
    let rosterChanged = Event<PeerInfo[]>()
    let peerJoined = Event<PeerInfo>()
    let presenceChanged = Event<Presence>()
    let faulted = Event<exn>()
    let closed = Event<unit>()

    // Serialises writes across every conversation sharing this socket, which is
    // the price of multiplexing: two frames interleaved would decode as neither.
    let writeLock = new SemaphoreSlim(1, 1)

    /// One live conversation per correspondent, keyed by their folded handle,
    /// because that is what a delivery is stamped with.
    let conversations = ConcurrentDictionary<string, Conversation * byte[]>()

    let mutable roster: PeerInfo[] = [||]

    /// The key `Direct` traffic is sealed under. Starts as the door key,
    /// because the sign-in exchange has to be sealed under something both ends
    /// already share, and is replaced by the agreed session key the moment that
    /// exchange completes. Both ends switch at the same frame — after the two
    /// Agree frames, which are themselves under the door key — so there is no
    /// window in which one is talking in a key the other is not reading.
    let mutable controlKey = doorKey

    /// Who Chariot said it was, once it has proved it. None until then, and
    /// nothing here treats an unproven server as a server.
    let mutable server: PeerInfo option = None

    /// What to do when somebody addresses us first. Without this a
    /// conversation could only ever be opened by whoever spoke first, and the
    /// other side would silently drop the Hello -- which is not a race that can
    /// be won by ordering, because both ends open at once.
    let mutable accepting: (DocumentActor * byte[] * (PeerInfo -> byte[] -> Result<unit, string>)) option =
        None

    let writeSealed envelope payload =
        task {
            do! writeLock.WaitAsync cts.Token

            try
                do! Framing.writeSealed stream envelope payload cts.Token
            finally
                writeLock.Release() |> ignore
        }

    /// Says something to Chariot under whichever key is current. Reading the
    /// mutable at call time rather than capturing it is what makes the switch a
    /// single assignment instead of a rewiring.
    let say frame =
        writeSealed Direct (Crypto.seal controlKey (Codec.encode frame))

    /// Says something under a stated key, for the two frames that must go under
    /// the door key no matter what the current one is.
    let sayUnder k frame =
        writeSealed Direct (Crypto.seal k (Codec.encode frame))

    /// One conversation, wired up and filed under the correspondent's handle.
    /// Both ways of starting one -- opening deliberately and being contacted
    /// out of the blue -- come through here, so there is one place where a
    /// conversation is given its send function and its events.
    let start (correspondent: Handle) (joinKey: byte[]) document trust =
        let send frame =
            writeSealed (ToHandle correspondent) (Crypto.seal joinKey (Codec.encode frame))

        let conversation = new Conversation(send, self, document, trust)
        conversation.Faulted.Add faulted.Trigger
        conversation.PeerJoined.Add peerJoined.Trigger
        conversation.PresenceChanged.Add presenceChanged.Trigger
        conversations[correspondent.Folded] <- (conversation, joinKey)
        conversation

    member _.Roster = roster
    member _.RosterChanged = rosterChanged.Publish

    /// Raised when any correspondent has proved itself, whichever end opened
    /// the conversation. A caller that only wants to know "am I talking to
    /// somebody" should not have to hold every Conversation to find out.
    member _.PeerJoined = peerJoined.Publish

    member _.PresenceChanged = presenceChanged.Publish
    member _.Faulted = faulted.Publish
    member _.Closed = closed.Publish
    member _.Self = self.Peer

    /// Who the server proved itself to be, once it has. None before sign-in
    /// completes, and there is no unproven form of this to accidentally show.
    member _.Server = server

    /// Signing in: both ends prove who they are, and then agree a key nobody
    /// holding the passphrase can derive.
    ///
    /// It is the peer exchange with one thing added, and it is mutual in both
    /// directions now. Chariot sends a Hello carrying its own key and signs the
    /// nonce we send it, so `trust` refuses a server whose key changed on the
    /// same terms it refuses a person whose key changed. Before this, possession
    /// of the passphrase was the client's ONLY assurance it had reached the
    /// right server, which meant anybody holding it could stand one up and be
    /// believed.
    ///
    /// Then the agreement. Both Agree frames go under the door key — they have
    /// to, since the session key is what they are for — and everything after is
    /// under the key they produce. The order below is the order of the frames on
    /// the wire, and the guards are what keep it from being merely the order we
    /// hope for: nothing derives a key before the far side has proved itself,
    /// because an ephemeral signed by an unproven identity is an ephemeral
    /// signed by anybody. Pegasus_Sync.md §4.3.
    member _.SignInAsync(ct: CancellationToken) =
        task {
            use ephemeral = new Agreement.Ephemeral()

            /// Ours, sent in our Challenge and expected inside Chariot's Proof.
            let ourNonce = Crypto.newChallenge ()
            let mutable theirNonce: byte[] option = None
            let mutable serverKey: byte[] option = None
            let mutable serverProven = false
            let mutable signedIn = false

            while not signedIn do
                let! envelope, payload = Framing.readSealed stream ct

                if envelope <> Direct then
                    raise (ProtocolError "a routed frame arrived before sign-in")

                match Codec.decode (Crypto.openSealed doorKey payload) with
                | Hello(who, offered, protocol) ->
                    if protocol <> Version.Protocol then
                        raise (ProtocolError $"server speaks protocol {protocol}, this build speaks {Version.Protocol}")

                    match trust who offered with
                    | Error why -> raise (ProtocolError why)
                    | Ok() ->
                        server <- Some who
                        serverKey <- Some offered

                | Challenge nonce ->
                    theirNonce <- Some nonce
                    // Our Challenge before our Proof, so the far side already
                    // holds the nonce it has to sign by the time it decides
                    // whether to believe us.
                    do! sayUnder doorKey (Hello(self.Peer, self.PublicKey, Version.Protocol))
                    do! sayUnder doorKey (Challenge ourNonce)
                    do! sayUnder doorKey (Proof(Attestation.prove self nonce))

                | Proof signature ->
                    match server, serverKey with
                    | Some who, Some offered ->
                        match Attestation.verify offered who.Id ourNonce signature with
                        | Error why -> raise (ProtocolError why)
                        | Ok() -> serverProven <- true
                    | _ -> raise (ProtocolError "the server proved itself before saying who it was")

                | Agree(theirs, signature) ->
                    match serverKey, theirNonce with
                    | Some offered, Some nonce when serverProven ->
                        let sessionSalt = Agreement.salt nonce ourNonce

                        match ephemeral.Accept(offered, theirs, signature, ourNonce, sessionSalt) with
                        | Error why -> raise (ProtocolError why)
                        | Ok agreed ->
                            // Ours under the door key, because that is the last
                            // thing the far side can read until it has ours.
                            // Only then does the key change, and only then is
                            // sign-in over.
                            do! sayUnder doorKey (ephemeral.Offer(self, nonce))
                            controlKey <- agreed
                            signedIn <- true
                    | _ -> raise (ProtocolError "the server offered a key agreement before proving who it was")

                | other -> raise (ProtocolError $"expected a sign-in frame, got {other.GetType().Name}")
        }

    /// Says what to do with an unexpected correspondent: which note they are
    /// joining, under which join code. A client that has not called this can
    /// start conversations but cannot be invited into one.
    ///
    /// Deriving the key costs 210,000 PBKDF2 iterations, so this is not
    /// something to call from a keystroke handler.
    member _.Accept(document: DocumentActor, joinCode: string, trust: PeerInfo -> byte[] -> Result<unit, string>) =
        accepting <- Some(document, Crypto.deriveKey joinCode, trust)

    /// Opens a conversation with somebody by name. The join code is the key the
    /// two of you share and the server does not; it still has to be agreed out
    /// of band, exactly as before.
    ///
    /// Opening also makes this client willing to ANSWER under the same code,
    /// which is not a side effect so much as the same intent stated once: a code
    /// you are willing to speak under is a code you are willing to be spoken to
    /// under, and the alternative was every caller deriving the same key twice
    /// to say both halves. It is also what makes the pairing symmetric — both
    /// people click the same button and neither has to be first.
    member _.OpenAsync(peer: Handle, joinCode: string, document: DocumentActor, trust: PeerInfo -> byte[] -> Result<unit, string>) =
        task {
            let joinKey = Crypto.deriveKey joinCode
            accepting <- Some(document, joinKey, trust)
            let conversation = start peer joinKey document trust
            do! conversation.BeginAsync()
            return conversation
        }

    /// The live conversation with somebody, if there is one.
    member _.ConversationWith(peer: Handle) =
        match conversations.TryGetValue peer.Folded with
        | true, (conversation, _) -> Some conversation
        | _ -> None

    /// Reads until the socket ends, handing each frame to whoever it belongs to.
    ///
    /// A frame from a correspondent nobody opened a conversation with is
    /// dropped rather than treated as an error: the server may legitimately
    /// deliver post from somebody this client has not opened a note with yet,
    /// and one stranger's traffic must not take down a working session with
    /// somebody else.
    member _.RunAsync() =
        task {
            try
                while not cts.IsCancellationRequested do
                    let! envelope, payload = Framing.readSealed stream cts.Token

                    match envelope with
                    | Direct ->
                        match Codec.decode (Crypto.openSealed controlKey payload) with
                        | Roster peers ->
                            roster <- peers
                            rosterChanged.Trigger peers
                        | _ -> ()
                    | FromHandle sender ->
                        // Create one on first contact if we are open to being
                        // invited, so neither end has to speak first.
                        if not (conversations.ContainsKey sender.Folded) then
                            match accepting with
                            | Some(document, joinKey, trust) ->
                                let conversation = start sender joinKey document trust
                                do! conversation.BeginAsync()
                            | None -> ()

                        match conversations.TryGetValue sender.Folded with
                        | true, (conversation, joinKey) ->
                            match Crypto.tryOpenSealed joinKey payload with
                            | Some plain -> do! conversation.ReceiveAsync(Codec.decode plain)
                            | None ->
                                // Sealed under a code we do not share. Either
                                // they are using a different one, or the relay
                                // misrouted; neither is ours to crash over.
                                ()
                        | _ -> ()
                    | ToHandle _ ->
                        // A destination is a client's to write and a server's
                        // to read. Nothing should route one back to us.
                        raise (ProtocolError "the relay sent a frame addressed onward")
            with
            | :? OperationCanceledException -> ()
            | :? EndOfStreamException -> ()
            | :? IOException -> ()
            | e -> faulted.Trigger e

            closed.Trigger()
        }

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel()

            for conversation, _ in conversations.Values do
                (conversation :> IDisposable).Dispose()

            cts.Dispose()
            writeLock.Dispose()
            stream.Dispose()

/// Connects to a Chariot.
module RelayClient =

    let connectAsync
        (host: string)
        (port: int)
        (passphrase: string)
        (self: Identity)
        (trust: PeerInfo -> byte[] -> Result<unit, string>)
        (ct: CancellationToken)
        =
        task {
            let client = new TcpClient()
            do! client.ConnectAsync(host, port, ct)
            client.NoDelay <- true
            let stream = client.GetStream()

            // The same front-door handshake a peer does, for the same reason:
            // prove you know the code before anything is allocated for you.
            // This is all the passphrase does now -- SignInAsync replaces it
            // with an agreed key before anything worth reading is said.
            do! Handshake.asJoiner stream (Crypto.deriveKey passphrase) ct
            let relay = new Relay(stream, self, passphrase, trust)
            let mutable refused: exn option = None

            try
                do! relay.SignInAsync ct
            with e ->
                // A server that refuses to prove itself leaves nothing worth
                // keeping. Disposed here rather than handed back, so a caller
                // cannot accidentally hold a Relay that never signed in. The
                // failure is carried out rather than rethrown in the handler,
                // because a task expression will not let it be rethrown there.
                (relay :> IDisposable).Dispose()
                refused <- Some e

            match refused with
            | Some e -> return raise e
            | None -> return relay
        }
