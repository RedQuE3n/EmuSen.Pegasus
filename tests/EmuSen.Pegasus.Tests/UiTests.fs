[<Xunit.Collection "Avalonia">]
module EmuSen.Pegasus.Tests.UiTests

open System
open Avalonia.Controls
open Avalonia.Threading
open Xunit
open EmuSen.Pegasus
open EmuSen.Pegasus.Controller
open EmuSen.Pegasus.Tests.Headless
open EmuSen.Pegasus.Tests.Stubs

[<Fact>]
let ``the window renders an editor bound to the open note`` () =
    started.Force()
    use pad = new Notepad(tempRoot (), Peers.identity "alice", Peers.contacts ())
    pad.CreateNote "scratch" |> ignore

    let window = Shell.PegasusWindow pad
    window.Show()
    Dispatcher.UIThread.RunJobs()

    let editor = editorOf window
    editor.Text <- "typed into the window"
    Dispatcher.UIThread.RunJobs()

    Assert.Equal("typed into the window", pad.Text)
    window.Close()

[<Fact>]
let ``every control in the window is actually templated`` () =
    // The blank-window regression: without LunaP's theme the controls exist in
    // the logical tree and render nothing, so every other test here still
    // passed. Applying a template is the thing that was missing, so it is the
    // thing asserted. See Pegasus_Design.md §11.
    started.Force()
    use pad = new Notepad(tempRoot (), Peers.identity "alice", Peers.contacts ())
    pad.CreateNote "scratch" |> ignore

    let window = Shell.PegasusWindow pad
    window.Show()
    Dispatcher.UIThread.RunJobs()

    let untemplated = untemplatedIn window

    Assert.True(
        untemplated.Length = 0,
        $"""controls with no template, so they render blank: {String.Join(", ", untemplated)}"""
    )

    window.Close()

[<Fact>]
let ``the window is a LunaP ToolWindow and carries a placement key`` () =
    // The point of the move: chrome, theme and geometry come from the shared
    // toolkit rather than being rebuilt here. Pegasus_Design.md §8.
    started.Force()
    use pad = new Notepad(tempRoot (), Peers.identity "alice", Peers.contacts ())
    pad.CreateNote "scratch" |> ignore
    let window = Shell.PegasusWindow pad
    Assert.IsAssignableFrom<EmuSen.LunaP.Windowing.ToolWindow>(window) |> ignore
    Assert.Equal("pegasus", window.WindowKey)
    window.Close()

[<Fact>]
let ``a remote edit appears in the rendered editor`` () =
    started.Force()
    use hostPad = new Notepad(tempRoot (), Peers.identity "alice", Peers.contacts ())
    use joinPad = new Notepad(tempRoot (), Peers.identity "bob", Peers.contacts ())
    hostPad.CreateNote "shared" |> ignore
    joinPad.CreateNote "shared" |> ignore

    let window = Shell.PegasusWindow joinPad
    window.Show()
    Dispatcher.UIThread.RunJobs()

    let code, port = hostPad.StartHosting()
    joinPad.Join("127.0.0.1", port, code)
    Assert.True(pump (fun () -> hostPad.Connection <> Offline && joinPad.Connection <> Offline))

    hostPad.Edit "written on the other machine"

    // The whole path: mailbox -> socket -> mailbox -> dispatcher -> TextBox.
    let editor = editorOf window
    Assert.True(pump (fun () -> editor.Text = "written on the other machine"))

    hostPad.Disconnect()
    joinPad.Disconnect()
    window.Close()

[<Fact>]
let ``a note survives closing and reopening the notepad`` () =
    started.Force()
    let root = tempRoot ()

    let noteId =
        use pad = new Notepad(root, Peers.identity "alice", Peers.contacts ())
        let entry = pad.CreateNote "durable"
        pad.Edit "this must still be here"
        pad.Checkpoint()
        entry.Id

    use reopened = new Notepad(root, Peers.identity "alice", Peers.contacts ())
    reopened.Open noteId
    Assert.Equal("this must still be here", reopened.Text)
    Assert.Contains(reopened.Notes, fun n -> n.Name = "durable")

[<Fact>]
let ``opening another note drops the connection rather than the document under it`` () =
    // A Session is handed the DocumentActor that was open when it started and
    // holds it for its lifetime, so switching notes used to dispose a native
    // Yjs handle another thread was still using. That was recorded as a hazard
    // because nothing drove it; a buddy list makes it ordinary, since a
    // conversation now outlives a moment of interest in one note. Disconnecting
    // is visible and recoverable. The alternative was not.
    started.Force()
    use hostPad = new Notepad(tempRoot (), Peers.identity "alice", Peers.contacts ())
    use joinPad = new Notepad(tempRoot (), Peers.identity "bob", Peers.contacts ())
    let first = hostPad.CreateNote "first"
    let second = hostPad.CreateNote "second"
    joinPad.CreateNote "shared" |> ignore
    hostPad.Open first.Id

    let code, port = hostPad.StartHosting()
    joinPad.Join("127.0.0.1", port, code)

    let connected (pad: Notepad) =
        match pad.Connection with
        | Connected _ -> true
        | _ -> false

    Assert.True(pump (fun () -> connected hostPad && connected joinPad))

    hostPad.Open second.Id

    Assert.Equal(Offline, hostPad.Connection)
    Assert.Equal(Some second.Id, hostPad.CurrentNoteId)

    // And the note switched to is a working document rather than a corpse.
    hostPad.Edit "still editable afterwards"
    Assert.Equal("still editable afterwards", hostPad.Text)

    joinPad.Disconnect()

// ---------------------------------------------------------------------------
// The buddy list
// ---------------------------------------------------------------------------

[<Literal>]
let private Passphrase = "a-server-passphrase"

[<Literal>]
let private JoinCode = "7-lantern-quartz"

/// Signs a window in to a relay by typing into it, which is the whole point:
/// the transport has been able to do this since the previous pass, and what
/// this suite is for is proving a person can.
let private signInThrough (window: Window) (relay: StubRelay) =
    (boxWith window "server").Text <- "127.0.0.1"
    (boxWith window "server port").Text <- string relay.Port
    (boxWith window "server passphrase").Text <- Passphrase
    click (buttonSaying window "Sign in")

[<Fact>]
let ``the buddy list fills with whoever else is signed in`` () =
    started.Force()
    use relay = new StubRelay(Passphrase)
    relay.Open()

    use aliceId = Peers.identity "alice"
    use bobId = Peers.identity "bob"
    use alicePad = new Notepad(tempRoot (), aliceId, Peers.contacts ())
    use bobPad = new Notepad(tempRoot (), bobId, Peers.contacts ())
    alicePad.CreateNote "shared" |> ignore
    bobPad.CreateNote "shared" |> ignore

    let window = Shell.PegasusWindow alicePad
    window.Show()
    Dispatcher.UIThread.RunJobs()

    signInThrough window relay
    bobPad.SignInToRelay("127.0.0.1", relay.Port, Passphrase) |> ignore

    // What the window shows, not what the controller believes.
    //
    // CONTAINS RATHER THAN EQUALS, and the row it checks now says more than a
    // name. Bob is signed in and is not on Alice's list, so the row carries a
    // presence mark and says so in words: the panel shows friends and strangers
    // in one list, and a stranger that read identically to a friend would make
    // the Add button meaningless. Asserting the exact string would pin this test
    // to the wording of a label rather than to the behaviour it is about.
    Assert.True(pump (fun () -> window.Buddies.Roster.ItemCount = 1), "the buddy list never filled")
    let row = string window.Buddies.Roster.Items[0]
    Assert.Contains("bob", row)
    Assert.Contains("not on your list", row)

    alicePad.Disconnect()
    bobPad.Disconnect()
    window.Close()

[<Fact>]
let ``a note is opened with somebody by name, with no address and no port`` () =
    // Pass 6a in one test, and the reason the README's pairing section had to be
    // rewritten. Alice types a server once, picks Bob out of a list, types the
    // code they agreed, and their notes converge. Nowhere does anybody read out
    // an address or a port -- and the join code is still theirs, because it is
    // the key the relay must not have.
    started.Force()
    use relay = new StubRelay(Passphrase)
    relay.Open()

    use aliceId = Peers.identity "alice"
    use bobId = Peers.identity "bob"
    use alicePad = new Notepad(tempRoot (), aliceId, Peers.contacts ())
    use bobPad = new Notepad(tempRoot (), bobId, Peers.contacts ())
    alicePad.CreateNote "shared" |> ignore
    bobPad.CreateNote "shared" |> ignore

    let window = Shell.PegasusWindow alicePad
    window.Show()
    Dispatcher.UIThread.RunJobs()

    signInThrough window relay
    bobPad.SignInToRelay("127.0.0.1", relay.Port, Passphrase) |> ignore
    Assert.True(pump (fun () -> window.Buddies.Roster.ItemCount = 1))

    (boxWith window "join code").Text <- JoinCode
    window.Buddies.Select bobId.Handle
    Dispatcher.UIThread.RunJobs()
    click (buttonSaying window "Open note")

    // Bob's side of the same decision: he agreed the code out of band and says
    // so by opening too. Driven through the controller rather than a second
    // window because one window under test is enough.
    bobPad.OpenWith(aliceId.Handle, JoinCode) |> ignore

    Assert.True(pump (fun () -> alicePad.Connection = Connected bobId.Handle), "the window never said it was connected")

    bobPad.Edit "written on the other machine, reached by name"
    let editor = editorOf window
    Assert.True(pump (fun () -> editor.Text = "written on the other machine, reached by name"))

    alicePad.Disconnect()
    bobPad.Disconnect()
    window.Close()

[<Fact>]
let ``a server is remembered only once it has actually worked`` () =
    started.Force()
    use relay = new StubRelay(Passphrase)
    relay.Open()

    let remembered = ResizeArray<ServerAddress>()

    let book =
        { Recent = fun () -> None
          Remember = remembered.Add }

    use aliceId = Peers.identity "alice"
    use alicePad = new Notepad(tempRoot (), aliceId, Peers.contacts ())
    alicePad.CreateNote "shared" |> ignore

    let window = Shell.PegasusWindow(alicePad, book)
    window.Show()
    Dispatcher.UIThread.RunJobs()

    // A port nothing is listening on. Remembering this would offer somebody
    // their own typo back on the next launch as though it had worked.
    (boxWith window "server").Text <- "127.0.0.1"
    (boxWith window "server port").Text <- "1"
    (boxWith window "server passphrase").Text <- Passphrase
    click (buttonSaying window "Sign in")
    Assert.True(pump (fun () -> match alicePad.Connection with Failed _ -> true | _ -> false))
    Assert.Empty remembered

    signInThrough window relay
    Assert.True(pump (fun () -> remembered.Count = 1), "a server that worked was not remembered")
    Assert.Equal(relay.Port, remembered[0].Port)

    alicePad.Disconnect()
    window.Close()

[<Fact>]
let ``a remembered server is offered back without its passphrase`` () =
    started.Force()

    let book =
        { Recent = fun () -> Some { Host = "chariot.example"; Port = 9040 }
          Remember = ignore }

    use aliceId = Peers.identity "alice"
    use alicePad = new Notepad(tempRoot (), aliceId, Peers.contacts ())
    alicePad.CreateNote "shared" |> ignore

    let window = Shell.PegasusWindow(alicePad, book)
    window.Show()
    Dispatcher.UIThread.RunJobs()

    Assert.Equal("chariot.example", (boxWith window "server").Text)
    Assert.Equal("9040", (boxWith window "server port").Text)

    // The one that must stay empty. There is nothing to seal a passphrase
    // under, so it is not stored, and a prefilled box would mean it was.
    Assert.True(String.IsNullOrEmpty (boxWith window "server passphrase").Text)
    window.Close()

// ---------------------------------------------------------------------------
// Sign-in
// ---------------------------------------------------------------------------

[<Fact>]
let ``every control in the sign-in window is actually templated`` () =
    // The same guard as the main window, because the sign-in window is now the
    // first thing a user sees and a blank one would be indistinguishable from a
    // hang. See Pegasus_Design.md §11.
    started.Force()
    let window = SignIn.SignInWindow(tempRoot ())
    window.Show()
    Dispatcher.UIThread.RunJobs()

    let untemplated = untemplatedIn window

    Assert.True(
        untemplated.Length = 0,
        $"""controls with no template, so they render blank: {String.Join(", ", untemplated)}"""
    )

    window.Close()

[<Fact>]
let ``creating an identity through the window signs in as that handle`` () =
    started.Force()
    let root = tempRoot ()
    let window = SignIn.SignInWindow root
    let admitted = ResizeArray<Identity>()
    window.SignedIn.Add admitted.Add
    window.Show()
    Dispatcher.UIThread.RunJobs()

    (boxWith window "handle").Text <- "RedQuE3n"
    (boxWith window "password").Text <- "hunter2"
    click (buttonSaying window "Create")

    Assert.Equal(1, admitted.Count)
    Assert.Equal("RedQuE3n", admitted[0].Handle.Value)
    Assert.True(IdentityStore.exists root (Handle.Parse "redque3n"))

    (admitted[0] :> IDisposable).Dispose()
    window.Close()

[<Fact>]
let ``a wrong password is refused at the window, not merely in the store`` () =
    started.Force()
    let root = tempRoot ()

    match IdentityStore.create root (Handle.Parse "alice") "right" with
    | Ok created -> (created :> IDisposable).Dispose()
    | Error e -> failwith e.Message

    let window = SignIn.SignInWindow root
    let admitted = ResizeArray<Identity>()
    window.SignedIn.Add admitted.Add
    window.Show()
    Dispatcher.UIThread.RunJobs()

    (boxWith window "handle").Text <- "alice"
    (boxWith window "password").Text <- "wrong"
    click (buttonSaying window "Sign in")

    Assert.Empty admitted
    Assert.True(showsText window "wrong password", "the window did not say why it refused")
    window.Close()

[<Fact>]
let ``a malformed handle is refused before the store is consulted`` () =
    started.Force()
    let root = tempRoot ()
    let window = SignIn.SignInWindow root
    let admitted = ResizeArray<Identity>()
    window.SignedIn.Add admitted.Add
    window.Show()
    Dispatcher.UIThread.RunJobs()

    (boxWith window "handle").Text <- "9lives"
    (boxWith window "password").Text <- "whatever"
    click (buttonSaying window "Create")

    Assert.Empty admitted
    Assert.Empty(IdentityStore.list root)
    window.Close()
