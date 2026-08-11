module EmuSen.Pegasus.Controller

open System
open System.IO
open System.Threading
open EmuSen.Pegasus
open EmuSen.Pegasus

/// What the status bar is showing, and the only thing the view needs to know
/// about the network.
///
/// Linking is the gap between "the socket is up" and "the other side has told
/// us who it is". It exists because Connected carries a Handle: there is no
/// honest handle to put in it until Hello arrives, and the previous code filled
/// that gap with Connected "connecting...", a fake name in a field meant for a
/// real one. The type refusing to hold that is the type doing its job.
type ConnectionState =
    | Offline
    | Hosting of code: string * port: int
    | Waiting of code: string * port: int
    | Linking
    /// Signed in to a relay, and not yet in a conversation with anybody. This
    /// is a real resting state rather than a step on the way to one: you can
    /// sit on a buddy list all day without opening a note with somebody, which
    /// is most of what a buddy list is for.
    | SignedIn of server: ServerAddress
    | Connected of peer: Handle
    | Failed of reason: string

/// Where notes live. Resolved through SpecialFolder rather than a literal
/// ".local/share", which was Linux-only and wrong on the two other RIDs we
/// publish. An existing workspace at the old path keeps being used rather than
/// being stranded, the same order ConfigStore already follows for settings.
/// See Pegasus_Design.md §11.
let defaultWorkspaceRoot =
    let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

    let data =
        match Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData with
        | "" -> Path.Combine(home, ".local", "share")
        | path -> path

    let current = Path.Combine(data, "Pegasus", "workspace")
    let legacy = Path.Combine(home, ".local", "share", "pegasus", "workspace")

    if not (Directory.Exists current) && Directory.Exists legacy then
        legacy
    else
        current

/// The rule the application connects under: write down a peer's key the first
/// time, and refuse a different key for that handle afterwards.
///
/// Built here rather than inside Session, so that the session knows how to
/// check a signature and nothing about where contacts are filed. The message is
/// written for somebody who has to decide what to do about it, since the honest
/// answer to a changed key is either "they reinstalled" or "this is not them"
/// and only a person can tell which.
let pinnedTrust (identityRoot: string) (local: Handle) =
    fun (peer: PeerInfo) (publicKey: byte[]) ->
        match KnownPeers.trust identityRoot local peer publicKey with
        | FirstSight
        | Recognised -> Ok()
        | Impostor(pinned, offered) ->
            Error(
                $"{peer.Handle.Value} is presenting key {offered.Value}, but {pinned.Value} was pinned "
                + "for that handle. Either they have a new identity file, or this is not them."
            )

/// Everything this machine knows about other people, as functions.
///
/// One record rather than seven constructor parameters, and one record rather
/// than three, because these are not seven unrelated capabilities — they are
/// the single question "what does this machine believe about the people you
/// talk to", asked about keys, about the list, and about what was said. A
/// constructor taking seven loose lambdas is a constructor whose arguments get
/// swapped eventually, and two of these have the same type.
///
/// It stays a record of FUNCTIONS rather than a database path for the reason
/// `trust` always was one: where a machine files what it knows about identities
/// is not the controller's business, and a headless test must be able to drive
/// one without writing to whoever is running the suite. `pinnedContacts` is how
/// the application builds the real one.
type Contacts =
    { Trust: PeerInfo -> byte[] -> Result<unit, string>
      AcceptCard: Card -> Result<Card, string>
      MessagingKey: Handle -> byte[] option
      Friends: unit -> Handle[]
      AddFriend: Handle -> unit
      RemoveFriend: Handle -> unit
      /// True when the line was new. False means it had already been recorded,
      /// which is an ordinary outcome rather than a failure — see Chats.record.
      Record: Handle -> Line -> bool
      Conversation: Handle -> Line[] }

/// The real one: pinned keys, the saved buddy list, and saved transcripts, all
/// scoped to whoever is signed in.
///
/// Built from one `identityRoot` and one `local` handle so every part of it
/// agrees about whose contacts these are. Two identities on a machine are two
/// people, and a build that scoped the keys by owner while leaving the
/// transcripts global would put one person's conversations in another's window.
let pinnedContacts (identityRoot: string) (local: Handle) =
    { Trust = pinnedTrust identityRoot local
      AcceptCard = KnownPeers.acceptCard identityRoot local
      MessagingKey = KnownPeers.messagingKeyFor identityRoot local
      Friends = fun () -> Friends.all identityRoot local
      AddFriend = Friends.add identityRoot local
      RemoveFriend = Friends.remove identityRoot local
      Record = Chats.record identityRoot local
      Conversation = Chats.conversation identityRoot local }

/// Owns the workspace, the open note and the session, so the view stays a
/// function of state. Compaction threshold and projection policy live here.
///
/// Takes the unlocked Identity rather than the PeerInfo it used to, because a
/// session now has to SIGN a challenge and a name and a colour cannot do that.
/// This is a real narrowing of an earlier decision, which was that nothing here
/// should know keypairs exist; proving who you are needs the key, and there is
/// no arrangement in which the thing that owns the session does not reach it.
/// The identity FORMAT is still none of this file's business -- IdentityStore
/// keeps that -- and `trust` is a parameter for the same reason.
type Notepad(root: string, self: Identity, contacts: Contacts) =
    let workspace = new Workspace(root)

    // Named locally so every existing use reads as it did. The session layer
    // takes this one function and never the whole record: a Conversation has no
    // business with a buddy list.
    let trust = contacts.Trust

    let changed = Event<unit>()
    let stateChanged = Event<ConnectionState>()
    let remotePresence = Event<Presence>()
    let rosterChanged = Event<PeerInfo[]>()

    /// A message that has been written down, ready for a window to show. The
    /// handle is the correspondent, not the sender — an outbound line and an
    /// inbound one both belong to the same conversation.
    let messageRecorded = Event<Handle * Line>()
    let messageFailed = Event<Handle * string>()

    let mutable openNote: (NoteId * DocumentActor * Store.NoteFile * IDisposable) option = None
    let mutable session: Session option = None
    let mutable host: Host option = None
    let mutable relay: Relay option = None
    let mutable connection = Offline
    let mutable cts = new CancellationTokenSource()

    /// Rewrites the log as one snapshot once it has grown past this many
    /// records. See Pegasus_Format.md §3.
    let compactThreshold = 512

    let setState s =
        connection <- s
        stateChanged.Trigger s

    let closeNote () =
        match openNote with
        | Some(_, doc, file, sub) ->
            sub.Dispose()
            file.WriteProjection doc.Text
            if file.RecordCount > compactThreshold then file.Compact doc.Snapshot
            file.Sync()
            (file :> IDisposable).Dispose()
            (doc :> IDisposable).Dispose()
        | None -> ()

        openNote <- None

    member _.Self = self.Peer
    member _.Workspace = workspace
    member _.Changed = changed.Publish
    member _.ConnectionChanged = stateChanged.Publish
    member _.RemotePresence = remotePresence.Publish
    member _.Connection = connection
    member _.Notes = workspace.Notes

    /// Who else is signed in to the relay, or empty when there is no relay.
    /// Empty rather than an option, because a buddy list with nobody in it and
    /// no buddy list at all look the same on screen and the view should not
    /// have to branch to say so.
    member _.Roster =
        match relay with
        | Some r -> r.Roster
        | None -> [||]

    member _.RosterChanged = rosterChanged.Publish
    member _.IsOnRelay = relay.IsSome

    member _.CurrentNoteId = openNote |> Option.map (fun (id, _, _, _) -> id)
    member _.Document = openNote |> Option.map (fun (_, doc, _, _) -> doc)

    member _.Text =
        match openNote with
        | Some(_, doc, _, _) -> doc.Text
        | None -> ""

    /// Opens a note, disconnecting first if anything is connected.
    ///
    /// The disconnect is not politeness. A Session or a Conversation is handed
    /// the DocumentActor that was open when it started and holds it for its
    /// lifetime, so switching notes underneath one disposes a native Yjs handle
    /// another thread is still using. That was recorded as a hazard rather than
    /// a behaviour because nothing in the suite drove it; a buddy list makes it
    /// ordinary — a conversation now outlives a moment of interest in one note —
    /// so it is closed here rather than left to be discovered. Dropping the
    /// connection is a visible, recoverable thing; the alternative was not.
    /// Pegasus_Sync.md §4 carries the correction.
    member this.Open(id: NoteId) =
        if this.CurrentNoteId <> Some id then
            if connection <> Offline then this.Disconnect()
            closeNote ()
            let doc, file, sub = workspace.OpenNote id
            openNote <- Some(id, doc, file, sub)
            doc.Changed.Add(fun () -> changed.Trigger())
            changed.Trigger()

    member this.CreateNote(name: string) =
        let entry = workspace.Create name
        this.Open entry.Id
        entry

    member _.Rename(id, name) =
        workspace.Rename(id, name)
        changed.Trigger()

    member _.Edit(text: string) =
        match openNote with
        | Some(_, doc, _, _) -> doc.ReplaceAll text
        | None -> ()

    /// Writes the readable projection and forces the log to media.
    member _.Checkpoint() =
        match openNote with
        | Some(_, doc, file, _) ->
            file.WriteProjection doc.Text
            file.Sync()
        | None -> ()

    member private this.Attach(s: Session) =
        session <- Some s
        s.PresenceChanged.Add remotePresence.Trigger
        s.PeerJoined.Add(fun p -> setState (Connected p.Handle))
        s.Faulted.Add(fun e -> setState (Failed e.Message))
        s.Closed.Add(fun () -> if connection <> Offline then setState Offline)
        s.RunAsync() |> ignore

    /// Starts listening and returns the code the other peer needs.
    member this.StartHosting(?port: int) =
        match this.Document with
        | None -> failwith "open a note before hosting"
        | Some doc ->

        this.Disconnect()
        cts <- new CancellationTokenSource()
        let code = Crypto.newJoinCode ()
        let h = new Host(defaultArg port 0, code, self, doc, trust)
        h.Start()
        host <- Some h
        setState (Waiting(code, h.Port))

        task {
            try
                let! s = h.AcceptAsync cts.Token
                setState (Hosting(code, h.Port))
                this.Attach s
            with
            | :? OperationCanceledException -> ()
            | e -> setState (Failed e.Message)
        }
        |> ignore

        code, h.Port

    member this.Join(address: string, port: int, code: string) =
        match this.Document with
        | None -> failwith "open a note before joining"
        | Some doc ->

        this.Disconnect()
        cts <- new CancellationTokenSource()

        task {
            try
                let! s = Client.connectAsync address port code self doc trust cts.Token

                // Set Linking BEFORE Attach, not after. Attach starts the frame
                // pump, and the peer's Hello can arrive and set Connected before
                // the next line of this function runs -- setting Linking
                // afterwards would overwrite a real connection with "connecting"
                // and leave it there, since nothing else fires to correct it.
                setState Linking
                this.Attach s
            with e ->
                setState (Failed e.Message)
        }
        |> ignore

    /// Signs in to a relay, so peers can be reached by handle.
    ///
    /// Returns the task so a caller that needs the outcome can await it; the
    /// window does not, because every outcome it cares about arrives as a
    /// ConnectionState. Nothing here throws — a server that is not there is an
    /// ordinary thing, and it belongs on the status line, not in a stack trace.
    ///
    /// The address is remembered by whoever passed a ServerBook in, not here:
    /// where a machine files what it knows about identities is deliberately not
    /// this type's business, the same reason `trust` is a parameter.
    member this.SignInToRelay(host: string, port: int, passphrase: string) =
        match this.Document with
        | None -> failwith "open a note before signing in to a server"
        | Some _ ->

        this.Disconnect()
        cts <- new CancellationTokenSource()
        let token = cts.Token
        setState Linking

        task {
            try
                // The same trust rule the peers get. Chariot proves itself now,
                // so a server whose key changed is refused on exactly the terms
                // a person whose key changed is refused.
                let! r = RelayClient.connectAsync host port passphrase self trust contacts.AcceptCard contacts.MessagingKey token
                r.RosterChanged.Add rosterChanged.Trigger
                r.PresenceChanged.Add remotePresence.Trigger
                r.PeerJoined.Add(fun p -> setState (Connected p.Handle))
                r.Faulted.Add(fun e -> setState (Failed e.Message))
                r.Closed.Add(fun () -> if connection <> Offline then setState Offline)

                // WRITTEN DOWN BEFORE IT IS ANNOUNCED, and the relay is relying
                // on that: this handler runs inside MessageReceived's trigger,
                // and the relay acknowledges the message to Chariot only once
                // the trigger returns (Relay.receiveMessage). Recording after
                // announcing, or announcing on another thread, would let a
                // crash land in the window between Chariot forgetting a message
                // and this machine keeping it.
                //
                // The event is raised only for a line that was NEW. A
                // redelivery — which is ordinary, since anything unacknowledged
                // comes back — must not add a second line to a window that
                // already shows it.
                r.MessageReceived.Add(fun (peer, line) ->
                    if contacts.Record peer line then
                        messageRecorded.Trigger(peer, line))

                r.MessageFailed.Add messageFailed.Trigger
                relay <- Some r

                // Set before the pump starts, for the same reason Join sets
                // Linking before Attach: a roster can arrive on the first read
                // and overwrite a state this line has not written yet.
                setState (SignedIn { Host = host; Port = port })
                r.RunAsync() |> ignore
            with e ->
                setState (Failed e.Message)
        }

    /// Opens a note with somebody on the roster, by name.
    ///
    /// Run off the calling thread deliberately: deriving the join key is
    /// 210,000 PBKDF2 iterations, and this is called from a button, so doing it
    /// inline would freeze the window for as long as it takes.
    member this.OpenWith(peer: Handle, joinCode: string) =
        match relay, this.Document with
        | Some r, Some doc ->
            setState Linking

            Tasks.Task.Run(fun () ->
                task {
                    try
                        let! _ = r.OpenAsync(peer, joinCode, doc, trust)
                        return ()
                    with e ->
                        setState (Failed e.Message)
                }
                :> Tasks.Task)
        | _ -> failwith "sign in to a server before opening a note with somebody"

    /// A line that has been saved and should appear in a window.
    member _.MessageRecorded = messageRecorded.Publish

    /// Every way a message did not happen, in words for the person who tried.
    member _.MessageFailed = messageFailed.Publish

    member _.Friends = contacts.Friends()
    member _.Conversation(peer: Handle) = contacts.Conversation peer
    member _.RemoveFriend(peer: Handle) = contacts.RemoveFriend peer

    /// Adds somebody to the buddy list and fetches their card while it is
    /// nobody's hurry, so the first message of the first conversation does not
    /// wait on a round trip.
    member _.AddFriend(peer: Handle) =
        contacts.AddFriend peer

        match relay with
        | Some r -> r.PrefetchAsync peer |> ignore
        | None -> ()

    /// Sends a message, saves it, and announces the saved line.
    ///
    /// SAVED WHETHER OR NOT IT COULD BE SENT YET. A message to somebody whose
    /// card has not arrived is parked in the relay and goes out when it does
    /// (Relay.SendMessageAsync), and a message sent while offline is refused
    /// outright below — the difference is visible to the user, which is the
    /// point. What must never happen is a line the user typed vanishing because
    /// the network was not ready for it.
    member _.SendMessage(peer: Handle, body: string) =
        match relay with
        | None -> Error "sign in to a server before sending a message"
        | Some r ->
            let sending = r.SendMessageAsync(peer, body)

            task {
                let! line = sending

                if contacts.Record peer line then
                    messageRecorded.Trigger(peer, line)
            }
            |> ignore

            Ok()

    member _.Disconnect() =
        cts.Cancel()
        session |> Option.iter (fun s -> (s :> IDisposable).Dispose())
        session <- None
        host |> Option.iter (fun h -> (h :> IDisposable).Dispose())
        host <- None

        relay
        |> Option.iter (fun r ->
            (r :> IDisposable).Dispose()
            // The roster is a property of a connection that no longer exists,
            // so it empties with it. Leaving the last one on screen would show
            // a list of people this machine can no longer reach.
            rosterChanged.Trigger [||])

        relay <- None
        setState Offline

    interface IDisposable with
        member this.Dispose() =
            this.Disconnect()
            closeNote ()
            (workspace :> IDisposable).Dispose()
            cts.Dispose()
