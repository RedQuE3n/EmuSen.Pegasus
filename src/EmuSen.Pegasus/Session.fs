namespace EmuSen.Pegasus

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open EmuSen.Pegasus

/// The peer protocol, with no opinion about how frames travel.
///
/// This was pulled out of Session when Chariot arrived, and the reason is worth
/// keeping. Over a direct socket a frame is sealed under the join code and sent
/// `Direct`. Through a relay the SAME frame is sealed under the same join code
/// but addressed `ToHandle`, and it shares a socket with Chariot's own traffic,
/// which is sealed under a different key entirely. One connection, two key
/// domains. A protocol that owned its stream could not be used both ways
/// without being written twice, and two implementations of an identity
/// handshake is how one of them ends up weaker than the other.
///
/// So this takes a way to send a frame and is fed the frames that arrive. What
/// wraps and unwraps them is somebody else's business.
type Conversation(send: Frame -> Threading.Tasks.Task<unit>, self: Identity, document: DocumentActor, trust: PeerInfo -> byte[] -> Result<unit, string>) =
    let peerJoined = Event<PeerInfo>()
    let presenceChanged = Event<Presence>()
    let faulted = Event<exn>()

    /// Ours, sent in Challenge and expected back inside their Proof. Fresh per
    /// conversation, so a proof recorded from an earlier one is no use here.
    let nonce = Crypto.newChallenge ()

    let mutable updateSub: IDisposable = null
    let mutable remotePeer: PeerInfo option = None
    let mutable remoteKey: byte[] option = None
    let mutable proven = false
    let mutable finished = false

    /// Retained, because Hello arrives once and a late subscriber would miss it.
    member _.RemotePeer = remotePeer

    /// False until the far side has signed our challenge. Nothing about the
    /// document should be believed or disclosed while this is false.
    member _.Proven = proven

    member _.Finished = finished
    member _.PeerJoined = peerJoined.Publish
    member _.PresenceChanged = presenceChanged.Publish
    member _.Faulted = faulted.Publish

    member _.SendPresence(caret, anchor) =
        send (
            Awareness
                { Peer = self.Peer
                  Caret = caret
                  Anchor = anchor }
        )

    /// Says hello and asks who they are. Called once, by whatever owns the
    /// transport, as soon as there is somewhere for frames to go.
    member _.BeginAsync() =
        task {
            do! send (Hello(self.Peer, self.PublicKey, Version.Protocol))
            do! send (Challenge nonce)
        }

    /// Handles one frame. Raises ProtocolError on anything a peer should not
    /// have sent, and the caller decides what a broken conversation costs --
    /// over a direct socket that is the connection, through a relay it is only
    /// this correspondent.
    member _.ReceiveAsync(frame: Frame) =
        task {
            match frame with
            | Hello(peer, publicKey, protocol) ->
                // Said in the first frame rather than discovered as a decode
                // failure three frames later. Protocol 1 did not carry this at
                // all, which is what made a mismatch between two builds so
                // confusing to diagnose.
                if protocol <> Version.Protocol then
                    raise (ProtocolError $"peer speaks protocol {protocol}, this build speaks {Version.Protocol}")

                match trust peer publicKey with
                | Error why -> raise (ProtocolError why)
                | Ok() ->
                    remotePeer <- Some peer
                    remoteKey <- Some publicKey

            | Challenge theirs ->
                // Answered without waiting for anything: signing costs us
                // nothing and discloses nothing.
                do! send (Proof(Attestation.prove self theirs))

            | Proof signature ->
                match remotePeer, remoteKey with
                | Some peer, Some publicKey ->
                    match Attestation.verify publicKey peer.Id nonce signature with
                    | Error why -> raise (ProtocolError why)
                    | Ok() ->
                        proven <- true

                        // Local edits start flowing only now. Subscribing at
                        // construction would have sent this peer our typing
                        // before it had proved anything.
                        // Never block here -- this fires on the document's
                        // mailbox thread.
                        updateSub <-
                            document.LocalUpdate.Subscribe(fun update ->
                                let sending = send (Update update)

                                sending.ContinueWith(
                                    (fun (t: Threading.Tasks.Task) -> faulted.Trigger t.Exception),
                                    Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted
                                )
                                |> ignore)

                        peerJoined.Trigger peer
                        do! send (SyncStep1 document.StateVector)
                | _ -> raise (ProtocolError "proof arrived before the hello it belongs to")

            // Everything below moves or reveals document state, so none of it is
            // entertained from an unproven peer.
            | _ when not proven -> raise (ProtocolError "peer sent document traffic before proving its identity")

            | SyncStep1 stateVector ->
                // Answer with exactly what they lack.
                do! send (SyncStep2(document.DiffSince stateVector))
            | SyncStep2 diff
            | Update diff -> document.ApplyRemote diff
            | Awareness presence -> presenceChanged.Trigger presence
            | Bye -> finished <- true
            | Roster _ ->
                // Chariot's, not a peer's. Two people on a socket already know
                // who is there.
                raise (ProtocolError "a peer sent a roster")
        }

    interface IDisposable with
        member _.Dispose() =
            if not (isNull updateSub) then updateSub.Dispose()

/// One connected peer over a socket of its own. Both host and joiner drive this
/// identically, which is what keeps a relay from needing a third role.
///
/// This owns the transport and nothing else: the protocol is Conversation
/// above, so the relay path can reuse it rather than reimplement it.
type Session(stream: Stream, key: byte[], self: Identity, document: DocumentActor, trust: PeerInfo -> byte[] -> Result<unit, string>) =
    let cts = new CancellationTokenSource()
    let faulted = Event<exn>()
    let closed = Event<unit>()

    // Serialises writes; two frames must never interleave on the stream.
    let writeLock = new SemaphoreSlim(1, 1)

    let send frame =
        task {
            do! writeLock.WaitAsync cts.Token

            try
                do! Framing.writeFrame stream key Direct frame cts.Token
            finally
                writeLock.Release() |> ignore
        }

    let conversation = new Conversation(send, self, document, trust)
    do conversation.Faulted.Add faulted.Trigger

    member _.RemotePeer = conversation.RemotePeer
    member _.Proven = conversation.Proven
    member _.PeerJoined = conversation.PeerJoined
    member _.PresenceChanged = conversation.PresenceChanged
    member _.Faulted = faulted.Publish
    member _.Closed = closed.Publish
    member _.Send(frame) = send frame
    member _.SendPresence(caret, anchor) = conversation.SendPresence(caret, anchor)

    /// Greets, proves, and only then pumps document frames until the peer goes.
    member _.RunAsync() =
        task {
            try
                do! conversation.BeginAsync()

                while not cts.IsCancellationRequested && not conversation.Finished do
                    let! envelope, frame = Framing.readFrame stream key cts.Token

                    // Nothing put this connection behind a relay, so nothing
                    // should be arriving addressed. A routed frame here means
                    // either a confused intermediary or somebody probing.
                    if envelope <> Direct then
                        raise (ProtocolError "a routed frame arrived on a direct connection")

                    do! conversation.ReceiveAsync frame
            with
            | :? OperationCanceledException -> ()
            | :? EndOfStreamException -> ()
            | e -> faulted.Trigger e

            closed.Trigger()
        }

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel()
            (conversation :> IDisposable).Dispose()
            cts.Dispose()
            writeLock.Dispose()
            stream.Dispose()

/// Accepts one joiner on a TCP port.
type Host(port: int, joinCode: string, self: Identity, document: DocumentActor, trust: PeerInfo -> byte[] -> Result<unit, string>) =
    let key = Crypto.deriveKey joinCode
    let listener = new TcpListener(IPAddress.Any, port)

    member _.Port =
        (listener.LocalEndpoint :?> IPEndPoint).Port

    member _.Start() = listener.Start()

    member this.AcceptAsync(ct: CancellationToken) =
        task {
            let! client = listener.AcceptTcpClientAsync ct
            client.NoDelay <- true
            let stream = client.GetStream()
            do! Handshake.asHost stream key ct
            return new Session(stream, key, self, document, trust)
        }

    interface IDisposable with
        member _.Dispose() = listener.Stop()

/// Connects to a host.
module Client =

    let connectAsync
        (host: string)
        (port: int)
        (joinCode: string)
        (self: Identity)
        (document: DocumentActor)
        (trust: PeerInfo -> byte[] -> Result<unit, string>)
        (ct: CancellationToken)
        =
        task {
            let key = Crypto.deriveKey joinCode
            let client = new TcpClient()
            do! client.ConnectAsync(host, port, ct)
            client.NoDelay <- true
            let stream = client.GetStream()
            do! Handshake.asJoiner stream key ct
            return new Session(stream, key, self, document, trust)
        }
