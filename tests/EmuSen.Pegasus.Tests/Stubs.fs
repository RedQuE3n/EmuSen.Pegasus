module EmuSen.Pegasus.Tests.Stubs

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
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
///
/// It lives here rather than beside the relay tests because the window tests
/// need one too — a buddy list with nobody in it proves nothing — and two
/// stubs of one protocol is how they drift apart.
type StubRelay(passphrase: string) =
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
