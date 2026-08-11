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

/// The buddy list: sign in to a relay, see who is there, message them, or open
/// a note with them.
///
/// This is the half of the relay a user can see, and it is what makes the pass
/// true rather than merely tested. The transport has been able to reach a peer
/// by handle since the previous pass; until there was a panel in the window, the
/// only pairing anybody could actually perform was still address, port and code
/// read down a phone line.
///
/// TWO KINDS OF PERSON APPEAR IN ONE LIST and the difference is the whole reason
/// a saved list exists. A **friend** is somebody this identity decided to keep
/// (Friends.fs): they are on the list whether or not they are signed in, which
/// is the only way a messenger can tell you somebody is *offline* rather than
/// merely not showing them. Everybody else on the roster is here too, marked as
/// not being on your list, because a name you can see is a name you can add and
/// the alternative is typing a handle you have no way to discover.
///
/// A separate control rather than another row in Shell, because it is a
/// separate responsibility with its own state — the list, the address, who is
/// selected — and Shell already owns the notes and the editor. It takes the
/// join code as a function rather than a box of its own, so there is exactly
/// one place in the window where a join code is typed: two boxes both labelled
/// "join code" would be a genuinely confusing window and an easy mistake to make
/// while pairing.
///
/// WHAT THIS DOES NOT REMOVE IS THE JOIN CODE, for notes. A relay saves you the
/// address and the port; the code is the key your NOTES are sealed under and
/// Chariot has no way to derive it. Messages are different and the hint at the
/// bottom says so, because the two now sit in one panel: a message needs no code
/// at all, since it is sealed to a key its recipient published rather than to a
/// secret two people agreed. Pegasus_Sync.md §7.1.
type BuddyList(pad: Notepad, joinCode: unit -> string, book: ServerBook, openChat: Handle -> unit) as this =
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
        ListBox(MinWidth = 200.0, MinHeight = 160.0)
            .AccessibleName("Buddies")

    let newFriend =
        TextBox(PlaceholderText = "add by handle", HorizontalAlignment = stretch)
            .AccessibleName("Handle to add")
            .HelpText("Adds somebody to your buddy list so you can see when they are online.")

    /// Carries both failures and progress, and neither is any use to somebody
    /// who cannot see the colour change -- so it announces itself.
    let message = (Ui.Hint "").LiveRegion()

    let fail (why: string) =
        message.Text <- why
        message.Foreground <- SolidColorBrush Colors.IndianRed

    let note (what: string) =
        message.Text <- what
        message.Foreground <- SolidColorBrush Colors.Gray

    /// Who is signed in, by folded handle. Replaced whole on every roster,
    /// because a roster arrives whole -- Chariot republishes the entire set on
    /// every change (its §2), so there is no delta to apply and computing one
    /// would be inventing work the protocol deliberately avoided.
    let mutable online: Set<string> = Set.empty

    /// The handles behind the rows, in the order they are shown.
    ///
    /// A PARALLEL ARRAY, WHICH IS A KNOWN SMELL AND STILL THE RIGHT ANSWER
    /// HERE. The alternative is to read the selected row's text and parse a
    /// handle back out of "● alice", and recovering a model field by taking a
    /// display string apart is how a label change silently breaks selection.
    /// This way the label is free to say anything; the identity of a row is its
    /// index and never its text.
    let mutable shown: Handle[] = [||]

    let selected () =
        match roster.SelectedIndex with
        | i when i >= 0 && i < shown.Length -> Some shown[i]
        | _ -> None

    /// Rebuilds the list: friends first, then anybody else who is signed in.
    let refresh () =
        Dispatcher.UIThread.Post(fun () ->
            let chosen = selected ()
            let friends = pad.Friends
            let known = friends |> Array.map _.Folded |> Set.ofArray

            let strangers =
                pad.Roster
                |> Array.map _.Handle
                |> Array.filter (fun h -> not (known.Contains h.Folded))
                |> Array.distinctBy _.Folded
                |> Array.sortBy _.Folded

            shown <- Array.append friends strangers

            roster.ItemsSource <-
                shown
                |> Array.map (fun handle ->
                    let mark = if online.Contains handle.Folded then "●" else "○"

                    if known.Contains handle.Folded then
                        box $"{mark}  {handle.Value}"
                    else
                        // Said in words rather than by a different colour. A
                        // colour is exactly what a screen reader cannot pass on,
                        // and this distinction decides what the buttons below do.
                        box $"{mark}  {handle.Value}  (not on your list)")

            // Keep the selection across a rebuild if that person is still
            // there. Losing it every time somebody signs in would make the
            // buttons unusable on a busy server.
            match chosen with
            | Some previous ->
                match shown |> Array.tryFindIndex (fun h -> h.Folded = previous.Folded) with
                | Some i -> roster.SelectedIndex <- i
                | None -> ()
            | None -> ())

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

    let addFriend () =
        match Handle.TryParse(textOf newFriend) with
        | Error why -> fail why
        | Ok handle ->
            pad.AddFriend handle
            newFriend.Text <- ""
            note $"{handle.Value} is on your list"
            refresh ()

    let removeFriend () =
        match selected () with
        | None -> fail "pick somebody first"
        | Some peer ->
            pad.RemoveFriend peer
            note $"{peer.Value} is off your list. Your saved conversation is kept."
            refresh ()

    do
        pad.RosterChanged.Add(fun peers ->
            online <- peers |> Array.map _.Handle.Folded |> Set.ofArray
            refresh ())

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
                | Offline ->
                    online <- Set.empty
                    refresh ()
                | _ -> ()))

        // Prefill from the last server that worked. The passphrase is not here
        // and never will be: Db.fs says why beside the table.
        book.Recent()
        |> Option.iter (fun server ->
            host.Text <- server.Host
            port.Text <- string server.Port)

        // Double-click opens the conversation, which is the gesture every
        // messenger since AIM has used for exactly this and is therefore the
        // one people try first. The button beside the list is what makes it
        // discoverable, and what a keyboard reaches.
        roster.DoubleTapped.Add(fun _ -> selected () |> Option.iter openChat)

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
                Ui.Row(
                    8.0,
                    Ui.Button("Message", (fun () -> selected () |> Option.iter openChat)),
                    Ui.Button("Open note", (fun () -> openWith ()))
                ),
                Ui.Stack(6.0, newFriend, Ui.Row(8.0, Ui.Button("Add", (fun () -> addFriend ())), Ui.Button("Remove", (fun () -> removeFriend ())))),
                message,
                Ui.Hint(
                    "A message needs no join code: it is sealed to a key your buddy published, and the server cannot read it. "
                    + "A shared NOTE is different — you and your buddy still agree the join code between yourselves."
                )
                    .Wrap()
                    .Width(220.0)
                    .Left()
            )

        refresh ()

    /// The roster control, so a test can assert what the window is showing
    /// rather than what the controller believes.
    member _.Roster = roster

    /// Rebuilds from the store, for a caller that has just changed it.
    member _.Refresh() = refresh ()

    member _.Select(peer: Handle) =
        match shown |> Array.tryFindIndex (fun h -> h.Folded = peer.Folded) with
        | Some i -> roster.SelectedIndex <- i
        | None -> ()
