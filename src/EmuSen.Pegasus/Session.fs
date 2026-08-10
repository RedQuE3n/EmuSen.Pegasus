namespace EmuSen.Pegasus

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open EmuSen.Pegasus

/// Proves both ends derived the same key from the join code, without sending it.
///
/// This is a different question from the one Attestation answers and both are
/// asked. The join code decides whether a stranger may open a session at all;
/// the signature decides who they are once they have. Passing this proves
/// somebody read the code aloud to you, and nothing more -- an impostor who was
/// in the room when you did will get past it, and be refused by the proof.
module Handshake =

    let private challengeLength = 32

    let asHost (stream: Stream) (key: byte[]) (ct: CancellationToken) =
        task {
            let challenge = Crypto.newChallenge ()
            do! stream.WriteAsync(ReadOnlyMemory challenge, ct)
            do! stream.FlushAsync ct
            let response = Array.zeroCreate<byte> 32
            let mutable read = 0

            while read < 32 do
                let! n = stream.ReadAsync(Memory(response, read, 32 - read), ct)
                if n = 0 then raise (ProtocolError "peer closed during handshake")
                read <- read + n

            if not (Crypto.verifyChallenge key challenge response) then
                raise (ProtocolError "join code did not match")
        }

    let asJoiner (stream: Stream) (key: byte[]) (ct: CancellationToken) =
        task {
            let challenge = Array.zeroCreate<byte> challengeLength
            let mutable read = 0

            while read < challengeLength do
                let! n = stream.ReadAsync(Memory(challenge, read, challengeLength - read), ct)
                if n = 0 then raise (ProtocolError "peer closed during handshake")
                read <- read + n

            let response = Crypto.respondToChallenge key challenge
            do! stream.WriteAsync(ReadOnlyMemory response, ct)
            do! stream.FlushAsync ct
        }

/// One connected peer. Both host and joiner drive this identically, which is
/// what keeps a future relay from needing a third role -- Pegasus_Sync.md §1.
///
/// The session will not touch the document until the far side has proved who it
/// is. Nothing is sent that could disclose a note, and nothing received is
/// applied, until a Proof has verified against the key that arrived in Hello.
/// That ordering is the point of the whole exchange: a peer who fails the proof
/// must learn nothing and change nothing.
///
/// `trust` is supplied by the caller rather than decided here, so this type
/// knows how to check a signature and nothing about contact lists, files or
/// what a user should be asked. The application passes KnownPeers; the tests
/// pass whatever they are testing.
type Session
    (
        stream: Stream,
        key: byte[],
        self: Identity,
        document: DocumentActor,
        trust: PeerInfo -> byte[] -> Result<unit, string>
    ) =
    let cts = new CancellationTokenSource()
    let peerJoined = Event<PeerInfo>()
    let presenceChanged = Event<Presence>()
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

    /// Ours, sent in Challenge and expected back inside their Proof. Fresh per
    /// session, so a proof recorded from an earlier one is no use here.
    let nonce = Crypto.newChallenge ()

    let mutable updateSub: IDisposable = null
    let mutable remotePeer: PeerInfo option = None
    let mutable remoteKey: byte[] option = None
    let mutable proven = false

    /// Retained, because Hello arrives once and a late subscriber would miss it.
    member _.RemotePeer = remotePeer

    /// False until the far side has signed our challenge. Nothing about the
    /// document should be believed or disclosed while this is false.
    member _.Proven = proven

    member _.PeerJoined = peerJoined.Publish
    member _.PresenceChanged = presenceChanged.Publish
    member _.Faulted = faulted.Publish
    member _.Closed = closed.Publish

    member _.Send(frame) = send frame

    member this.SendPresence(caret, anchor) =
        send (
            Awareness
                { Peer = self.Peer
                  Caret = caret
                  Anchor = anchor }
        )

    /// Greets, proves, and only then pumps document frames until the peer goes.
    member this.RunAsync() =
        task {
            try
                do! send (Hello(self.Peer, self.PublicKey, Version.Protocol))
                do! send (Challenge nonce)

                while not cts.IsCancellationRequested do
                    let! envelope, frame = Framing.readFrame stream key cts.Token

                    // Nothing put this connection behind a relay, so nothing
                    // should be arriving addressed. A routed frame here means
                    // either a confused intermediary or somebody probing.
                    if envelope <> Direct then
                        raise (ProtocolError "a routed frame arrived on a direct connection")

                    match frame with
                    | Hello(peer, publicKey, protocol) ->
                        // Said in the first frame rather than discovered as a
                        // decode failure three frames later. Protocol 1 did not
                        // carry this at all, which is what made a mismatch
                        // between two builds so confusing to diagnose.
                        if protocol <> Version.Protocol then
                            raise (ProtocolError $"peer speaks protocol {protocol}, this build speaks {Version.Protocol}")

                        match trust peer publicKey with
                        | Error why -> raise (ProtocolError why)
                        | Ok() ->
                            remotePeer <- Some peer
                            remoteKey <- Some publicKey

                    | Challenge theirs ->
                        // Answered without waiting for anything: signing costs
                        // us nothing and discloses nothing.
                        do! send (Proof(Attestation.prove self theirs))

                    | Proof signature ->
                        match remotePeer, remoteKey with
                        | Some peer, Some publicKey ->
                            match Attestation.verify publicKey peer.Id nonce signature with
                            | Error why -> raise (ProtocolError why)
                            | Ok() ->
                                proven <- true

                                // Local edits start flowing only now. Subscribing
                                // at construction would have sent this peer our
                                // typing before it had proved anything.
                                // Never block here -- this fires on the
                                // document's mailbox thread.
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

                    // Everything below moves or reveals document state, so none
                    // of it is entertained from an unproven peer.
                    | _ when not proven ->
                        raise (ProtocolError "peer sent document traffic before proving its identity")

                    | SyncStep1 stateVector ->
                        // Answer with exactly what they lack.
                        do! send (SyncStep2(document.DiffSince stateVector))
                    | SyncStep2 diff
                    | Update diff -> document.ApplyRemote diff
                    | Awareness presence -> presenceChanged.Trigger presence
                    | Bye -> cts.Cancel()
            with
            | :? OperationCanceledException -> ()
            | :? EndOfStreamException -> ()
            | e -> faulted.Trigger e

            closed.Trigger()
        }

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel()
            if not (isNull updateSub) then updateSub.Dispose()
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
