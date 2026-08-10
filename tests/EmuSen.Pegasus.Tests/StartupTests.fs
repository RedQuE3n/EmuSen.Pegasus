[<Xunit.Collection "Avalonia">]
module EmuSen.Pegasus.Tests.StartupTests

open System
open System.IO
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.LogicalTree
open Avalonia.Threading
open Xunit
open EmuSen.Pegasus
open EmuSen.Pegasus.Tests.Headless

/// Everything a started application exposes to a test.
type private Started =
    { Desktop: IClassicDesktopStyleApplicationLifetime
      IdentityRoot: string
      WorkspaceRoot: string }

    member this.Window = this.Desktop.MainWindow

/// Drives the shipped App through a real desktop lifetime. Nothing here stands
/// in for the startup being tested -- the window swap under test is the one in
/// Program.fs. See Pegasus_Design.md §12.
let private start () =
    started.Force()

    let started' =
        { Desktop = new ClassicDesktopStyleApplicationLifetime()
          IdentityRoot = tempRoot ()
          WorkspaceRoot = tempRoot () }

    let app = Program.App(started'.IdentityRoot, started'.WorkspaceRoot)
    app.ApplicationLifetime <- started'.Desktop
    app.Initialize()
    app.OnFrameworkInitializationCompleted()

    // What ClassicDesktopStyleApplicationLifetime.Start does once the main
    // window has been chosen; the test has to do it because it never starts a
    // message loop.
    started'.Window.Show()
    Dispatcher.UIThread.RunJobs()
    started'

let private mentions (window: Window) (fragment: string) =
    window.GetLogicalDescendants()
    |> Seq.exists (fun c ->
        match box c with
        | :? TextBlock as t -> not (isNull t.Text) && t.Text.Contains(fragment: string)
        | _ -> false)

let private signInAs (app: Started) handle password =
    (boxWith app.Window "handle").Text <- handle
    (boxWith app.Window "password").Text <- password
    click (buttonSaying app.Window "Create")

[<Fact>]
let ``the application opens on the sign-in window`` () =
    let app = start ()
    Assert.IsType<SignIn.SignInWindow>(app.Window) |> ignore
    app.Window.Close()

[<Fact>]
let ``no workspace is touched before anyone has signed in`` () =
    // The notepad is constructed inside the sign-in callback, so an unattended
    // machine sitting at the prompt writes nothing and opens no note.
    let app = start ()
    Assert.Empty(Directory.GetFileSystemEntries app.WorkspaceRoot)
    app.Window.Close()

[<Fact>]
let ``signing in swaps the sign-in window for the notepad`` () =
    // The gap this file exists to close: the swap lives in Program.fs and was
    // previously only ever exercised by launching the application by hand.
    let app = start ()
    let signIn = app.Window
    let mutable signInClosed = false
    signIn.Closed.Add(fun _ -> signInClosed <- true)

    signInAs app "RedQuE3n" "hunter2"

    Assert.IsType<Shell.PegasusWindow>(app.Window) |> ignore
    Assert.NotSame(signIn, app.Window)
    Assert.True(signInClosed, "the sign-in window was left open behind the notepad")

    app.Window.Close()

[<Fact>]
let ``the notepad opens as the handle that signed in`` () =
    let app = start ()
    signInAs app "RedQuE3n" "hunter2"

    Assert.True(mentions app.Window "RedQuE3n", "the notepad does not show who signed in")

    // And it is the identity just created, not a name carried as loose text.
    match IdentityStore.unlock app.IdentityRoot (Handle.Parse "redque3n") "hunter2" with
    | Error e -> failwith e.Message
    | Ok identity ->
        Assert.True(mentions app.Window (identity.Fingerprint.Value[..7]))
        (identity :> IDisposable).Dispose()

    app.Window.Close()

[<Fact>]
let ``the notepad that replaces the sign-in window is usable and templated`` () =
    // Templated as well as present: a swapped-in blank window would be the
    // Pegasus_Design.md §11 failure with an extra step in front of it.
    let app = start ()
    signInAs app "RedQuE3n" "hunter2"

    let untemplated = untemplatedIn app.Window

    Assert.True(
        untemplated.Length = 0,
        $"""controls with no template, so they render blank: {String.Join(", ", untemplated)}"""
    )

    let editor = editorOf app.Window
    editor.Text <- "typed after signing in"
    Dispatcher.UIThread.RunJobs()

    Assert.True(pump (fun () -> mentions app.Window "RedQuE3n"))
    app.Window.Close()

[<Fact>]
let ``signing in opens a note, so the editor is usable immediately`` () =
    let app = start ()
    signInAs app "RedQuE3n" "hunter2"

    Assert.NotEmpty(Directory.GetFiles(app.WorkspaceRoot, "*.pegasus"))
    Assert.NotNull(editorOf app.Window)
    app.Window.Close()

[<Fact>]
let ``a refused sign-in leaves the sign-in window in place`` () =
    let app = start ()
    let signIn = app.Window

    (boxWith app.Window "handle").Text <- "9lives"
    (boxWith app.Window "password").Text <- "hunter2"
    click (buttonSaying app.Window "Create")

    Assert.Same(signIn, app.Window)
    Assert.Empty(Directory.GetFileSystemEntries app.WorkspaceRoot)
    app.Window.Close()

[<Fact>]
let ``closing the notepad ends the application`` () =
    let app = start ()
    signInAs app "RedQuE3n" "hunter2"

    let mutable exited = false
    app.Desktop.Exit.Add(fun _ -> exited <- true)
    app.Window.Close()

    Assert.True(exited, "closing the notepad left the process running")

[<Fact>]
let ``closing the sign-in window without signing in ends the application`` () =
    // Otherwise a user who changes their mind at the prompt is left with a
    // process they cannot see and cannot quit.
    let app = start ()
    let mutable exited = false
    app.Desktop.Exit.Add(fun _ -> exited <- true)
    app.Window.Close()

    Assert.True(exited, "closing the sign-in window left the process running")
