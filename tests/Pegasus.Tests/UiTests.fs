module Pegasus.Tests.UiTests

open System
open System.IO
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.LogicalTree
open Avalonia.Threading
open Avalonia.Themes.Fluent
open Avalonia.FuncUI.Hosts
open Xunit
open Pegasus.Core
open Pegasus.App.Controller

type private HeadlessApp() =
    inherit Application()
    override this.Initialize() = this.Styles.Add(FluentTheme())

// Avalonia may only be initialised once per process, so every test shares this.
let private started =
    lazy
        (AppBuilder
            .Configure<HeadlessApp>()
            .UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = true))
            .SetupWithoutStarting()
         |> ignore)

let private tempRoot () =
    let dir = Path.Combine(Path.GetTempPath(), "pegasus-ui", Guid.NewGuid().ToString "N")
    Directory.CreateDirectory dir |> ignore
    dir

/// FuncUI builds no XAML name scope, so controls are reached through the
/// logical tree -- docs/Pegasus_Design.md §4.4.
let private firstEditor (window: Window) =
    window.GetLogicalDescendants()
    |> Seq.choose (fun c ->
        match box c with
        | :? TextBox as t when t.AcceptsReturn -> Some t
        | _ -> None)
    |> Seq.head

let private pump (predicate: unit -> bool) =
    let deadline = DateTime.UtcNow.AddSeconds 5.0

    while not (predicate ()) && DateTime.UtcNow < deadline do
        Dispatcher.UIThread.RunJobs()
        Thread.Sleep 10

    Dispatcher.UIThread.RunJobs()
    predicate ()

[<Fact>]
let ``the shell renders an editor bound to the open note`` () =
    started.Force()
    use pad = new Notepad(tempRoot (), "alice")
    pad.CreateNote "scratch" |> ignore

    let window = HostWindow(Content = Pegasus.App.Shell.view pad)
    window.Show()
    Dispatcher.UIThread.RunJobs()

    let editor = firstEditor window
    editor.Text <- "typed into the window"
    Dispatcher.UIThread.RunJobs()

    Assert.Equal("typed into the window", pad.Text)
    window.Close()

[<Fact>]
let ``a remote edit appears in the rendered editor`` () =
    started.Force()
    use hostPad = new Notepad(tempRoot (), "alice")
    use joinPad = new Notepad(tempRoot (), "bob")
    hostPad.CreateNote "shared" |> ignore
    joinPad.CreateNote "shared" |> ignore

    let window = HostWindow(Content = Pegasus.App.Shell.view joinPad)
    window.Show()
    Dispatcher.UIThread.RunJobs()

    let code, port = hostPad.StartHosting()
    joinPad.Join("127.0.0.1", port, code)
    Assert.True(pump (fun () -> hostPad.Connection <> Offline && joinPad.Connection <> Offline))

    hostPad.Edit "written on the other machine"

    // The whole path: mailbox -> socket -> mailbox -> dispatcher -> TextBox.
    let editor = firstEditor window
    Assert.True(pump (fun () -> editor.Text = "written on the other machine"))

    hostPad.Disconnect()
    joinPad.Disconnect()
    window.Close()

[<Fact>]
let ``a note survives closing and reopening the notepad`` () =
    started.Force()
    let root = tempRoot ()
    let noteId =
        use pad = new Notepad(root, "alice")
        let entry = pad.CreateNote "durable"
        pad.Edit "this must still be here"
        pad.Checkpoint()
        entry.Id

    use reopened = new Notepad(root, "alice")
    reopened.Open noteId
    Assert.Equal("this must still be here", reopened.Text)
    Assert.Contains(reopened.Notes, fun n -> n.Name = "durable")
