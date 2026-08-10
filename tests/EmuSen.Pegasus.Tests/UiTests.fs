[<Xunit.Collection "Avalonia">]
module EmuSen.Pegasus.Tests.UiTests

open System
open Avalonia.Controls
open Avalonia.Threading
open Xunit
open EmuSen.Pegasus
open EmuSen.Pegasus.Controller
open EmuSen.Pegasus.Tests.Headless

[<Fact>]
let ``the window renders an editor bound to the open note`` () =
    started.Force()
    use pad = new Notepad(tempRoot (), Peers.identity "alice", Peers.acceptAny)
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
    use pad = new Notepad(tempRoot (), Peers.identity "alice", Peers.acceptAny)
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
    use pad = new Notepad(tempRoot (), Peers.identity "alice", Peers.acceptAny)
    pad.CreateNote "scratch" |> ignore
    let window = Shell.PegasusWindow pad
    Assert.IsAssignableFrom<EmuSen.LunaP.Windowing.ToolWindow>(window) |> ignore
    Assert.Equal("pegasus", window.WindowKey)
    window.Close()

[<Fact>]
let ``a remote edit appears in the rendered editor`` () =
    started.Force()
    use hostPad = new Notepad(tempRoot (), Peers.identity "alice", Peers.acceptAny)
    use joinPad = new Notepad(tempRoot (), Peers.identity "bob", Peers.acceptAny)
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
        use pad = new Notepad(root, Peers.identity "alice", Peers.acceptAny)
        let entry = pad.CreateNote "durable"
        pad.Edit "this must still be here"
        pad.Checkpoint()
        entry.Id

    use reopened = new Notepad(root, Peers.identity "alice", Peers.acceptAny)
    reopened.Open noteId
    Assert.Equal("this must still be here", reopened.Text)
    Assert.Contains(reopened.Notes, fun n -> n.Name = "durable")

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
