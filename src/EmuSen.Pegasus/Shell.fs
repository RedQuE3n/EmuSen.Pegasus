module EmuSen.Pegasus.Shell

open System
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Avalonia.Markup.Xaml.Styling
open EmuSen.LunaP.Fluent
open EmuSen.LunaP.Windowing
open EmuSen.Pegasus.Controller

/// The one style line every EmuSen App includes. It lives here rather than in
/// Program so the tests load exactly what the application loads; a headless
/// pass that misses it asserts over untemplated controls and passes green,
/// which is how the blank window shipped. See Pegasus_Design.md §11.
let applyTheme (app: Avalonia.Application) =
    let theme = StyleInclude(Uri("avares://EmuSen.Pegasus/", UriKind.Absolute))
    theme.Source <- Uri("avares://EmuSen.LunaP/Theme/LunaTheme.axaml", UriKind.Absolute)
    app.Styles.Add theme

/// Built from LunaP rather than raw Avalonia, so Pegasus inherits the shared
/// theme, the placement memory and the bootstrap. See Pegasus_Design.md §8.
type PegasusWindow(pad: Notepad) as this =
    inherit ToolWindow()

    let notes = ListBox(MinWidth = 190.0)
    let newName = TextBox(PlaceholderText = "new note", Width = 150.0)

    let editor =
        TextBox(
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = FontFamily "monospace",
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top
        )

    let address = TextBox(PlaceholderText = "address", Width = 130.0, Text = "127.0.0.1")
    let port = TextBox(PlaceholderText = "port", Width = 70.0)
    let code = TextBox(PlaceholderText = "join code", Width = 170.0)
    let status = Ui.Hint "offline"

    /// Who you are signed in as: handle, then the leading half of the
    /// fingerprint, tinted with the colour that is derived from the same key.
    ///
    /// The fingerprint is shown rather than hidden because it is the only thing
    /// on screen that a person could read down a phone line to check they are
    /// the identity their peer expects. Eight characters is what fits and what
    /// somebody will actually read aloud.
    let whoami =
        let label = Ui.Hint $"{pad.Self.Handle.Value}  ·  {pad.Self.Id.Value[..7]}"
        label.Foreground <- SolidColorBrush(Color.Parse pad.Self.Color)
        label

    // Set while a control is being rewritten from state, so the change events
    // it raises are not fed straight back in. Without the selection guard the
    // list and the editor drive each other forever - Pegasus_Design.md §8.1.
    let mutable applying = false
    let mutable syncingSelection = false

    let refreshNotes () =
        syncingSelection <- true

        try
            notes.ItemsSource <- pad.Notes |> Array.map (fun n -> box n.Name)

            match pad.CurrentNoteId with
            | Some id ->
                match pad.Notes |> Array.tryFindIndex (fun n -> n.Id = id) with
                | Some i -> notes.SelectedIndex <- i
                | None -> ()
            | None -> ()
        finally
            syncingSelection <- false

    let showStatus state =
        status.Text <-
            match state with
            | Offline -> "offline"
            | Waiting(c, p) -> $"waiting on port {p}  ·  code {c}"
            | Hosting(c, p) -> $"hosting on port {p}  ·  code {c}"
            | Linking -> "connecting..."
            | Connected peer -> $"connected to {peer.Value}"
            | Failed reason -> $"failed: {reason}"

        status.Foreground <-
            match state with
            | Connected _ -> SolidColorBrush Colors.SeaGreen
            | Failed _ -> SolidColorBrush Colors.IndianRed
            | Waiting _
            | Hosting _
            | Linking -> SolidColorBrush Colors.Goldenrod
            | Offline -> SolidColorBrush Colors.Gray

    /// The document changes on a mailbox thread; Avalonia may only be touched
    /// on the dispatcher, so every refresh hops across. Deliberately does not
    /// touch the note list -- see the re-entrancy note above.
    let pullText () =
        Dispatcher.UIThread.Post(fun () ->
            let incoming = pad.Text
            let shown = editor.Text |> Option.ofObj |> Option.defaultValue ""

            if incoming <> shown then
                applying <- true
                let moved = Caret.adjust shown incoming editor.CaretIndex
                editor.Text <- incoming
                editor.CaretIndex <- min moved incoming.Length
                applying <- false)

    do
        this.Title <- "Pegasus"
        this.Width <- 1000.0
        this.Height <- 680.0
        // ToolWindow persists geometry against this key.
        this.WindowKey <- "pegasus"

        editor.TextChanged.Add(fun _ ->
            if not applying then
                pad.Edit(editor.Text |> Option.ofObj |> Option.defaultValue ""))

        notes.SelectionChanged.Add(fun _ ->
            if not syncingSelection then
                match notes.SelectedIndex with
                | i when i >= 0 && i < pad.Notes.Length ->
                    pad.Open pad.Notes[i].Id
                    pullText ()
                | _ -> ())

        pad.Changed.Add(fun () -> pullText ())
        pad.ConnectionChanged.Add(fun s -> Dispatcher.UIThread.Post(fun () -> showStatus s))

        let addNote =
            Ui.Button(
                "+",
                fun () ->
                    let name = newName.Text |> Option.ofObj |> Option.defaultValue "" |> _.Trim()

                    if name <> "" then
                        pad.CreateNote name |> ignore
                        newName.Text <- ""
                        refreshNotes ()
                        pullText ()
            )

        let connection =
            Ui.Row(
                6.0,
                Ui.Button("Host", fun () -> pad.StartHosting() |> ignore),
                address,
                port,
                code,
                Ui.Button(
                    "Join",
                    fun () ->
                        match Int32.TryParse(port.Text |> Option.ofObj |> Option.defaultValue "") with
                        | true, p ->
                            pad.Join(
                                address.Text |> Option.ofObj |> Option.defaultValue "127.0.0.1",
                                p,
                                code.Text |> Option.ofObj |> Option.defaultValue ""
                            )
                        | _ -> ()
                ),
                Ui.Button("Disconnect", fun () -> pad.Disconnect())
            )

        let sidebar = Ui.Stack(6.0, Ui.Row(4.0, newName, addNote), notes)
        let footer = Ui.Row(14.0, whoami, status)

        DockPanel.SetDock(connection, Dock.Top)
        DockPanel.SetDock(footer, Dock.Bottom)
        DockPanel.SetDock(sidebar, Dock.Left)

        this.Content <- Ui.Dock(connection, footer, sidebar, editor)

        showStatus pad.Connection
        refreshNotes ()
        pullText ()
