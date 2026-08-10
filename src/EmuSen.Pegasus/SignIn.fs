module EmuSen.Pegasus.SignIn

open System
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open EmuSen.LunaP.Fluent
open EmuSen.LunaP.Windowing
open EmuSen.Pegasus

let private textOf (box: TextBox) =
    box.Text |> Option.ofObj |> Option.defaultValue ""

/// Unlocks a key held on this disk. It proves nothing to a peer, and the
/// window says so rather than implying otherwise -- Pegasus_Identity.md §2.
type SignInWindow(root: string) as this =
    inherit ToolWindow()

    let signedIn = Event<Identity>()

    let left = HorizontalAlignment.Left
    let handle = TextBox(PlaceholderText = "handle", Width = 260.0, HorizontalAlignment = left)
    let password = TextBox(PlaceholderText = "password", Width = 260.0, PasswordChar = '*', HorizontalAlignment = left)
    let message = Ui.Hint ""
    let known = ListBox(MaxHeight = 130.0, Width = 260.0, HorizontalAlignment = left)

    let refreshKnown () =
        let handles = IdentityStore.list root
        known.ItemsSource <- handles |> Array.map (fun h -> box h.Value)
        known.IsVisible <- handles.Length > 0

    let fail (why: string) =
        message.Text <- why
        message.Foreground <- SolidColorBrush Colors.IndianRed

    /// Sign in and create differ only in which store operation they run, so
    /// they share everything else including the failure path.
    let attempt (operation: Handle -> string -> Result<Identity, IdentityError>) =
        match Handle.TryParse(textOf handle) with
        | Error why -> fail why
        | Ok parsed ->
            match operation parsed (textOf password) with
            | Error e -> fail e.Message
            | Ok identity ->
                password.Text <- ""
                signedIn.Trigger identity

    do
        this.Title <- "Pegasus — sign in"
        this.Width <- 420.0
        this.Height <- 330.0
        this.WindowKey <- "pegasus-signin"

        known.SelectionChanged.Add(fun _ ->
            match known.SelectedItem with
            | :? string as chosen ->
                handle.Text <- chosen
                password.Focus() |> ignore
            | _ -> ())

        password.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                attempt (IdentityStore.unlock root))

        refreshKnown ()

        let buttons =
            Ui.Row(
                8.0,
                Ui.Button("Sign in", fun () -> attempt (IdentityStore.unlock root)),
                Ui.Button(
                    "Create",
                    fun () ->
                        attempt (IdentityStore.create root)
                        refreshKnown ()
                )
            )

        this.Content <-
            Ui.Stack(
                10.0,
                Ui.Header "Who are you?",
                known,
                handle,
                password,
                buttons,
                message,
                Ui.Hint("Your handle is how your peer sees you. The password unlocks a key kept on this machine and is never sent anywhere.")
                    .Wrap()
                    .Width(320.0)
                    .Left()
            )
                .Margin(20.0)

    member _.SignedIn = signedIn.Publish
