module EmuSen.Pegasus.Shell

open System
open Avalonia
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
///
/// The ServerBook is where the last relay address is remembered. It arrives as
/// a pair of functions rather than a database path so that a headless test can
/// build the window with `Servers.forgetful` and not write to whoever is running
/// the suite; the overload below is what the application uses.
type PegasusWindow(pad: Notepad, book: ServerBook) as this =
    inherit ToolWindow()

    let notes = ListBox(MinWidth = 190.0).AccessibleName("Notes")

    let newName =
        TextBox(PlaceholderText = "new note", Width = 150.0)
            .AccessibleName("New note name")

    /// THE CONTROL THE WHOLE APPLICATION EXISTS FOR, and it announced as "edit"
    /// and nothing else -- no name, and not even a placeholder to fall back on.
    /// It is renamed as notes are opened, in refreshNotes, so it says which note
    /// you are in rather than merely that you are in one.
    ///
    /// Padding because text pressed against the border of a box you are going to
    /// spend an hour in is tiring to read.
    let editor =
        TextBox(
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = FontFamily "monospace",
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = Thickness(10.0, 8.0)
        )
            .AccessibleName("Note")

    let address =
        TextBox(PlaceholderText = "address", Width = 130.0, Text = "127.0.0.1")
            .AccessibleName("Peer address")

    let port = TextBox(PlaceholderText = "port", Width = 70.0).AccessibleName("Peer port")

    let code =
        TextBox(PlaceholderText = "join code", Width = 170.0)
            .AccessibleName("Join code")
            .HelpText("You and your peer must type the same code. It is the key your notes are sealed under.")

    /// Says whether this note is being shared and with whom, which showStatus
    /// below calls the one thing a user must not be wrong about. It announces on
    /// change for the same reason it is coloured: nobody keeps checking it. And
    /// a colour is exactly what a screen reader cannot pass on -- the wording
    /// differs too, but only for somebody who goes and reads it. §13.
    let status = (Ui.Hint "offline").LiveRegion()

    /// One slot per correspondent, so a second message from somebody brings
    /// their window forward instead of opening another one.
    ///
    /// WindowSlot is LunaP's ("at most one of these, else bring it forward" —
    /// its §8.3), and a dictionary of them is what "at most one PER PERSON"
    /// looks like. Every touch of this dictionary happens on the dispatcher,
    /// which is what makes an ordinary Dictionary safe here: messages arrive on
    /// a socket thread, and `deliver` below marshals before it reaches this.
    let chats =
        System.Collections.Generic.Dictionary<string, WindowSlot<Chat.ChatWindow>>()

    let slotFor (peer: Handle) =
        match chats.TryGetValue peer.Folded with
        | true, slot -> slot
        | _ ->
            let slot = WindowSlot<Chat.ChatWindow>()
            chats[peer.Folded] <- slot
            slot

    let openChat (peer: Handle) =
        Dispatcher.UIThread.Post(fun () -> (slotFor peer).Show(this, (fun () -> Chat.ChatWindow(pad, peer))))

    /// The join code lives in the top row and the buddy list reads it from
    /// there, so there is one join code on screen no matter which way you pair.
    /// Passing the getter rather than the box keeps Buddies unable to write to
    /// a control it does not own.
    let buddies =
        Buddies.BuddyList(pad, (fun () -> code.Text |> Option.ofObj |> Option.defaultValue ""), book, openChat)

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

    /// The editor takes the open note's name, so tabbing into it says which note
    /// you are about to type in. With one name for all of them, switching notes
    /// is silent -- and this is an application whose entire subject is having
    /// several of them.
    ///
    /// Called from BOTH places a note can become the open one, and that is the
    /// whole reason it is a function. Doing it inside refreshNotes alone looked
    /// right and was not: refreshNotes runs when the window is built and when a
    /// note is created, and selecting an existing note in the list goes through
    /// neither, so the name stayed on whichever note happened to be open first.
    /// A test caught it; nothing on screen would have.
    let nameEditor () =
        let title =
            pad.CurrentNoteId
            |> Option.bind (fun id -> pad.Notes |> Array.tryFind (fun n -> n.Id = id))
            |> Option.map (fun n -> $"Note: {n.Name}")
            |> Option.defaultValue "Note"

        editor.AccessibleName title |> ignore

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

            nameEditor ()
        finally
            syncingSelection <- false

    let showStatus state =
        status.Text <-
            match state with
            | Offline -> "offline"
            | Waiting(c, p) -> $"waiting on port {p}  ·  code {c}"
            | Hosting(c, p) -> $"hosting on port {p}  ·  code {c}"
            | Linking -> "connecting..."
            | SignedIn server -> $"signed in to {server}"
            | Connected peer -> $"connected to {peer.Value}"
            | Failed reason -> $"failed: {reason}"

        status.Foreground <-
            match state with
            | Connected _ -> SolidColorBrush Colors.SeaGreen
            | Failed _ -> SolidColorBrush Colors.IndianRed
            // Signed in is not connected to a person, and the colour says so.
            // Green for "you are on a server" would read as "you are sharing
            // this note", which is the one thing a user must not be wrong about.
            | SignedIn _
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
                    nameEditor ()
                    pullText ()
                | _ -> ())

        pad.Changed.Add(fun () -> pullText ())
        pad.ConnectionChanged.Add(fun s -> Dispatcher.UIThread.Post(fun () -> showStatus s))

        // A message from somebody with no window open opens one, which is what
        // AIM and Yahoo both did and what makes a messenger usable at all — a
        // conversation nobody can start without first guessing to open a window
        // is not a conversation.
        //
        // APPENDED ONLY WHEN THE WINDOW WAS ALREADY OPEN. A window built now
        // loads the saved conversation in its constructor, and the controller
        // writes the line down BEFORE it announces it (Controller.fs), so the
        // line is already in that transcript. Appending as well would show every
        // first message of a conversation twice, and only the first — which is
        // exactly the kind of defect that survives a demo.
        pad.MessageRecorded.Add(fun (peer, line) ->
            Dispatcher.UIThread.Post(fun () ->
                let slot = slotFor peer

                if slot.IsOpen then
                    slot.RefreshIfOpen(fun window -> window.Append line)
                else
                    slot.Show(this, (fun () -> Chat.ChatWindow(pad, peer)))))

        // Reported where the person is looking if there is a window for that
        // correspondent, and on the main status line otherwise. A refusal that
        // appeared only in a window nobody has open is a refusal nobody sees.
        pad.MessageFailed.Add(fun (peer, why) ->
            Dispatcher.UIThread.Post(fun () ->
                let slot = slotFor peer

                if slot.IsOpen then
                    slot.RefreshIfOpen(fun window -> window.Report why)
                else
                    status.Text <- why
                    status.Foreground <- SolidColorBrush Colors.IndianRed))

        // "+" is what it says on screen and that stays -- it is the right button
        // for the job and a wider one would crowd the name box beside it. But
        // "plus" is not a thing a screen reader can do anything with, so the
        // accessible name says what pressing it does. This is the one control in
        // the application whose name is not its caption, and the reason it is
        // allowed to differ is that the caption is a symbol rather than a word:
        // there is no "click plus" for voice control to match against anyway.
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
                .AccessibleName("Create note")
                .HelpText("Creates a note with the name typed beside this button.")

        let connection =
            Ui.Row(
                8.0,
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

        let sidebar = Ui.Stack(8.0, Ui.Row(6.0, newName, addNote), notes)
        let footer = Ui.Row(14.0, whoami, status)

        // NOTHING HERE HAD A MARGIN, so five docked regions met each other and
        // the window frame with no gap at all: the connection row sat on the
        // window edge, the note list touched the editor, and the buddy panel
        // touched the other side of it. The window read as one dense block
        // rather than as four areas that do different jobs.
        //
        // The gaps are asymmetric on purpose. 12 against the window frame and 8
        // between neighbours, so the outer edge reads as the boundary it is and
        // the interior seams read as lighter than it. The top and bottom strips
        // carry a smaller gap towards the middle (6) than towards the frame
        // (10), which keeps them attached to the work area rather than floating.
        //
        // The editor takes no margin of its own: it is surrounded on all four
        // sides by things that already carry one, and adding a fifth would open
        // a double gap everywhere they meet.
        connection.Margin <- Thickness(12.0, 10.0, 12.0, 6.0)
        footer.Margin <- Thickness(12.0, 6.0, 12.0, 10.0)
        sidebar.Margin <- Thickness(12.0, 0.0, 8.0, 0.0)
        buddies.Margin <- Thickness(8.0, 0.0, 12.0, 0.0)

        DockPanel.SetDock(connection, Dock.Top)
        DockPanel.SetDock(footer, Dock.Bottom)
        DockPanel.SetDock(sidebar, Dock.Left)
        DockPanel.SetDock(buddies, Dock.Right)

        this.Content <- Ui.Dock(connection, footer, sidebar, buddies, editor)

        showStatus pad.Connection
        refreshNotes ()
        pullText ()

    /// The headless suite's window: the same one, with a book that remembers
    /// nothing, so a test never writes to whoever is running it.
    new(pad: Notepad) = PegasusWindow(pad, Servers.forgetful)

    /// The buddy panel, so a test can assert what the window is showing rather
    /// than what the controller believes.
    member _.Buddies = buddies
