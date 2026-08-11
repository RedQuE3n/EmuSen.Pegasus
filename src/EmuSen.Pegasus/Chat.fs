module EmuSen.Pegasus.Chat

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open EmuSen.LunaP.Fluent
open EmuSen.LunaP.Windowing
open EmuSen.Pegasus.Controller

/// One conversation, in a window of its own.
///
/// A WINDOW PER CORRESPONDENT, which is the AIM and Yahoo shape and is chosen
/// rather than inherited. The alternative — a chat pane docked in the main
/// window with a list to switch between conversations — shows one conversation
/// at a time, and the thing people actually do with a messenger is watch two at
/// once. It would also have to crowd a window that already holds a note list, an
/// editor and a buddy panel.
///
/// It takes the Notepad rather than a send function, because a chat window needs
/// three things from it — send, load the transcript, know who you are — and
/// three lambdas is the point at which passing the controller is the smaller
/// coupling. It does NOT reach the relay, the store or the crypto: everything
/// here goes through the controller's surface.
type ChatWindow(pad: Notepad, peer: Handle) as this =
    inherit ToolWindow()

    /// A list rather than one text block holding the whole conversation, and
    /// the reason is a screen reader. A block is one enormous run of text to
    /// step through; a list is a sequence of items that can be moved between one
    /// message at a time. It costs the ability to select across several lines at
    /// once, which is the smaller loss.
    let transcript =
        ListBox(MinWidth = 320.0, MinHeight = 240.0)
            .AccessibleName($"Conversation with {peer.Value}")

    let compose =
        TextBox(
            PlaceholderText = $"message {peer.Value}",
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = Thickness(8.0, 6.0)
        )
            .AccessibleName($"Message to {peer.Value}")
            .HelpText("Enter sends. Shift and Enter together start a new line.")

    /// Carries refusals and the state of the connection, and announces itself
    /// because somebody who cannot see a colour change still has to learn that
    /// their message did not go anywhere.
    let status = (Ui.Hint "").LiveRegion()

    let lines = ResizeArray<string>()

    let fail (why: string) =
        status.Text <- why
        status.Foreground <- SolidColorBrush Colors.IndianRed

    /// Formats one line for the list.
    ///
    /// The time is the SENDER's clock and is shown as local time, which is worth
    /// knowing when it looks wrong: a correspondent whose machine is an hour out
    /// mislabels their own lines and cannot move yours, because the transcript
    /// is ordered by arrival here rather than by this number (Chats.fs).
    let render (line: Line) =
        let who = if line.Outbound then pad.Self.Handle.Value else peer.Value
        let at = line.SentAt.ToLocalTime().ToString "HH:mm"
        $"{at}  {who}: {line.Body}"

    let append (line: Line) =
        lines.Add(render line)
        transcript.ItemsSource <- lines |> Seq.map box |> Seq.toArray

        // Follow the conversation. Somebody who has scrolled back to read
        // something is moved anyway, which is the wrong behaviour and is left
        // as a known rough edge rather than guessed at -- doing it properly
        // means knowing whether the view is already at the end, and Avalonia's
        // ScrollViewer does not offer that through a ListBox without reaching
        // into its template.
        if lines.Count > 0 then
            transcript.SelectedIndex <- lines.Count - 1
            transcript.ScrollIntoView(lines.Count - 1)

    let send () =
        match compose.Text |> Option.ofObj |> Option.defaultValue "" |> _.Trim() with
        | "" -> ()
        | body ->
            match pad.SendMessage(peer, body) with
            | Error why -> fail why
            | Ok() ->
                compose.Text <- ""
                status.Text <- ""

    do
        this.Title <- $"Chat: {peer.Value}"
        this.Width <- 460.0
        this.Height <- 420.0

        // One key for every chat window rather than one per correspondent. A
        // key per handle would remember each conversation's own geometry, and
        // would also grow the placement store by one row per person ever
        // spoken to; sharing one means the second window opens where the first
        // was left, which is what a person moving one window expects of the
        // next.
        this.WindowKey <- "pegasus-chat"

        // Enter sends, Shift-Enter does not. AcceptsReturn is false so a bare
        // Enter never reaches the box as a newline; Shift-Enter is handled here
        // because with AcceptsReturn false the box would otherwise swallow it
        // and do nothing at all.
        compose.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                if e.KeyModifiers.HasFlag KeyModifiers.Shift then
                    let text = compose.Text |> Option.ofObj |> Option.defaultValue ""
                    let at = compose.CaretIndex
                    compose.Text <- text.Insert(at, "\n")
                    compose.CaretIndex <- at + 1
                else
                    send ()

                e.Handled <- true)

        for line in pad.Conversation peer do
            append line

        let footer =
            Ui.Row(8.0, compose, Ui.Button("Send", (fun () -> send ())))

        compose.Margin <- Thickness(0.0)
        transcript.Margin <- Thickness(12.0, 12.0, 12.0, 8.0)
        footer.Margin <- Thickness(12.0, 0.0, 12.0, 6.0)
        status.Margin <- Thickness(12.0, 0.0, 12.0, 10.0)

        DockPanel.SetDock(footer, Dock.Bottom)
        DockPanel.SetDock(status, Dock.Bottom)
        this.Content <- Ui.Dock(status, footer, transcript)

    member _.Peer = peer

    /// Adds a line that arrived while this window was open. Marshalled, because
    /// a message is decoded on a socket thread and Avalonia may only be touched
    /// on the dispatcher.
    member _.Append(line: Line) =
        Dispatcher.UIThread.Post(fun () -> append line)

    member _.Report(why: string) =
        Dispatcher.UIThread.Post(fun () -> fail why)

    /// The transcript control, so a test can assert what the window is showing
    /// rather than what the store believes.
    member _.Transcript = transcript

    member _.Compose = compose
