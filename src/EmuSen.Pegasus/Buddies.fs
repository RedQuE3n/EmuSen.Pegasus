module EmuSen.Pegasus.Buddies

open System
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open EmuSen.LunaP.Fluent
open EmuSen.Pegasus
open EmuSen.Pegasus.Controller

let private textOf (box: TextBox) =
    box.Text |> Option.ofObj |> Option.defaultValue ""

/// The buddy list: sign in to a relay, see who else is there, open a note with
/// one of them by name.
///
/// This is the half of the relay a user can see, and it is what makes the pass
/// true rather than merely tested. The transport has been able to reach a peer
/// by handle since the previous pass; until there was a panel in the window, the
/// only pairing anybody could actually perform was still address, port and code
/// read down a phone line.
///
/// A separate control rather than another row in Shell, because it is a
/// separate responsibility with its own state — the roster, the address, who is
/// selected — and Shell already owns the notes and the editor. It takes the
/// join code as a function rather than a box of its own, so there is exactly
/// one place in the window where a join code is typed: two boxes both labelled
/// "join code" would be a genuinely confusing window and an easy mistake to make
/// while pairing.
///
/// WHAT THIS DOES NOT REMOVE IS THE JOIN CODE. A relay saves you the address and
/// the port; the code is the key your notes are sealed under and Chariot has no
/// way to derive it. The hint at the bottom says so, because a buddy list that
/// looks like every other buddy list will otherwise be assumed to mean what
/// those mean. Pegasus_Sync.md §4.1.
type BuddyList(pad: Notepad, joinCode: unit -> string, book: ServerBook) as this =
    inherit UserControl()

    let stretch = HorizontalAlignment.Stretch

    let host =
        TextBox(PlaceholderText = "server", HorizontalAlignment = stretch)
            .AccessibleName("Server address")

    let port =
        TextBox(PlaceholderText = "server port", Width = 80.0, HorizontalAlignment = HorizontalAlignment.Left)
            .AccessibleName("Server port")

    let passphrase =
        TextBox(PlaceholderText = "server passphrase", PasswordChar = '*', HorizontalAlignment = stretch)
            .AccessibleName("Server passphrase")
            // The distinction the whole panel turns on, and the hint that carries it
            // is at the bottom where a reader arrives last. Said here too.
            .HelpText("Gets you on to the server. It is not the join code, and it does not unseal any notes.")

    let roster =
        ListBox(MinWidth = 180.0, MinHeight = 140.0)
            .AccessibleName("Buddies on this server")

    /// Carries both failures and progress, and neither is any use to somebody
    /// who cannot see the colour change -- so it announces itself.
    let message = (Ui.Hint "").LiveRegion()

    let fail (why: string) =
        message.Text <- why
        message.Foreground <- SolidColorBrush Colors.IndianRed

    let note (what: string) =
        message.Text <- what
        message.Foreground <- SolidColorBrush Colors.Gray

    /// Rebuilt from the roster rather than patched, because a roster arrives
    /// whole — Chariot republishes the entire set on every change (its §2), so
    /// there is no delta to apply and computing one would be inventing work the
    /// protocol deliberately avoided.
    let refresh (peers: PeerInfo[]) =
        Dispatcher.UIThread.Post(fun () ->
            let chosen = roster.SelectedItem |> Option.ofObj |> Option.map string
            roster.ItemsSource <- peers |> Array.map (fun p -> box p.Handle.Value)

            // Keep the selection across a republish if that person is still
            // there. Losing it every time somebody else signs in would make the
            // Open button unusable on a busy server.
            match chosen with
            | Some name ->
                match peers |> Array.tryFindIndex (fun p -> p.Handle.Value = name) with
                | Some i -> roster.SelectedIndex <- i
                | None -> ()
            | None -> ())

    let selected () =
        match roster.SelectedItem with
        | :? string as name -> Handle.TryParse name |> Result.toOption
        | _ -> None

    let signIn () =
        match Int32.TryParse(textOf port) with
        | false, _ -> fail "a server port is a number"
        | true, p ->
            match textOf host with
            | "" -> fail "which server?"
            | server ->
                note $"signing in to {server}..."
                pad.SignInToRelay(server, p, textOf passphrase) |> ignore

    let openWith () =
        match selected () with
        | None -> fail "pick somebody first"
        | Some peer ->
            match joinCode().Trim() with
            // Refused rather than defaulted. An empty code would derive a
            // perfectly good key that the other side would never guess, and the
            // failure would look like the relay dropping frames.
            | "" -> fail "you both need the same join code"
            | code ->
                note $"opening a note with {peer.Value}..."
                pad.OpenWith(peer, code) |> ignore

    do
        pad.RosterChanged.Add refresh

        pad.ConnectionChanged.Add(fun state ->
            Dispatcher.UIThread.Post(fun () ->
                match state with
                | SignedIn server ->
                    // Remembered only once the connection worked. An address
                    // that was typed but did not resolve would otherwise be
                    // offered back on the next launch as though it had.
                    book.Remember server
                    note $"signed in to {server}"
                | Connected peer -> note $"sharing this note with {peer.Value}"
                | Offline -> refresh [||]
                | _ -> ()))

        // Prefill from the last server that worked. The passphrase is not here
        // and never will be: Db.fs says why beside the table.
        book.Recent()
        |> Option.iter (fun server ->
            host.Text <- server.Host
            port.Text <- string server.Port)

        // 10 between rows rather than 8, and the address/port/passphrase group
        // kept together at 6 so it reads as one thing to fill in rather than
        // three unrelated boxes. Shell owns the margin between this panel and
        // the editor beside it, because that is a relationship between the two
        // and belongs where they are put next to each other.
        this.Content <-
            Ui.Stack(
                10.0,
                Ui.Header "Buddies",
                Ui.Stack(6.0, host, port, passphrase),
                Ui.Row(8.0, Ui.Button("Sign in", (fun () -> signIn ())), Ui.Button("Sign out", (fun () -> pad.Disconnect()))),
                roster,
                Ui.Button("Open note", (fun () -> openWith ())),
                message,
                Ui.Hint("A server saves you the address and the port. You and your buddy still agree the join code between yourselves — the server cannot read your notes and this is why.")
                    .Wrap()
                    .Width(200.0)
                    .Left()
            )

    /// The roster control, so a test can assert what the window is showing
    /// rather than what the controller believes.
    member _.Roster = roster

    member _.Select(peer: Handle) =
        match roster.ItemsSource with
        | null -> ()
        | items ->
            items
            |> Seq.cast<obj>
            |> Seq.tryFindIndex (fun item -> string item = peer.Value)
            |> Option.iter (fun i -> roster.SelectedIndex <- i)
