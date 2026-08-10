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

/// The first window a user sees: pick a handle, type a password, get an
/// Identity out the other side.
///
/// What this window actually does is unlock a keypair sitting on this disk. It
/// proves nothing to anyone else -- there is no server here to check a password
/// against, and there is deliberately not going to be one. The hint at the
/// bottom of the window says so in the user's words, because a login box that
/// looks like every other login box will otherwise be assumed to mean what
/// those mean.
///
/// It raises SignedIn rather than opening the notepad itself. The window that
/// knows how to take a password should not also be the window that knows what
/// the application does next; Program.fs owns that.
type SignInWindow(root: string) as this =
    inherit ToolWindow()

    let signedIn = Event<Identity>()

    let left = HorizontalAlignment.Left

    let handle =
        TextBox(PlaceholderText = "handle", Width = 260.0, HorizontalAlignment = left)
            .AccessibleName("Handle")

    let password =
        TextBox(PlaceholderText = "password", Width = 260.0, PasswordChar = '*', HorizontalAlignment = left)
            .AccessibleName("Password")
            // Said out loud because the window says it out loud. The hint at the
            // bottom exists precisely because this box does not mean what a password
            // box usually means, and a user who cannot see that hint is exactly the
            // user most likely to assume the usual thing.
            .HelpText("Unlocks a key kept on this machine. It is never sent anywhere.")

    /// Failures and progress both land here, and a user who cannot see it turn
    /// red needs to be told it changed at all -- so it announces itself.
    let message = (Ui.Hint "").LiveRegion()

    let known =
        ListBox(MaxHeight = 130.0, Width = 260.0, HorizontalAlignment = left)
            // The rows announce as the handles they are; without this the list they
            // sit in is a list of nothing in particular.
            .AccessibleName("Identities on this machine")

    /// Fills the list of identities already on this machine, and hides it
    /// entirely when there are none -- on a first run the list would otherwise
    /// be an empty box with no explanation, which reads as something broken.
    let refreshKnown () =
        let handles = IdentityStore.list root
        known.ItemsSource <- handles |> Array.map (fun h -> box h.Value)
        known.IsVisible <- handles.Length > 0

    let fail (why: string) =
        message.Text <- why
        message.Foreground <- SolidColorBrush Colors.IndianRed

    /// Signing in and creating differ only in which store operation they run,
    /// so they share everything else: the same handle parse, the same failure
    /// path, the same clearing of the password box. Taking the operation as a
    /// parameter is what lets the two buttons be one line each.
    ///
    /// The handle is parsed before the store is touched, so a malformed one is
    /// refused without ever reaching the disk, and the user is told which of the
    /// grammar rules they broke rather than "invalid".
    let attempt (operation: Handle -> string -> Result<Identity, IdentityError>) =
        match Handle.TryParse(textOf handle) with
        | Error why -> fail why
        | Ok parsed ->
            match operation parsed (textOf password) with
            | Error e -> fail e.Message
            | Ok identity ->
                // Cleared on success, not on failure: a user who mistyped it
                // wants to correct it, and a user who is through with it should
                // not leave it sitting in a control.
                password.Text <- ""
                signedIn.Trigger identity

    do
        this.Title <- "Pegasus — sign in"
        // Grown with the padding rather than letting the padding eat the
        // content: at 420x330 the hint at the bottom was already close, and
        // 24 of margin plus the wider gaps would have clipped it. ToolWindow
        // remembers geometry per WindowKey, so this is the size of a first run
        // and not an override of anybody's saved one.
        this.Width <- 460.0
        this.Height <- 400.0
        this.WindowKey <- "pegasus-signin"

        known.SelectionChanged.Add(fun _ ->
            match known.SelectedItem with
            | :? string as chosen ->
                handle.Text <- chosen
                password.Focus() |> ignore
            | _ -> ())

        // Enter signs in rather than creating. Getting this the wrong way round
        // would mean a mistyped password silently making a second account.
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
                        // Refreshed unconditionally: on success the new handle
                        // joins the list, and on failure the list is already
                        // right, so there is nothing to branch on.
                        refreshKnown ()
                )
            )

        // The two boxes are one thing to fill in, so they sit closer to each
        // other (6) than to the list above or the buttons below (14). Spacing
        // that is uniform everywhere makes a form read as an undifferentiated
        // column; this is the cheapest way to say which parts belong together.
        this.Content <-
            Ui.Stack(
                14.0,
                Ui.Header "Who are you?",
                known,
                Ui.Stack(6.0, handle, password),
                buttons,
                message,
                Ui.Hint("Your handle is how your peer sees you. The password unlocks a key kept on this machine and is never sent anywhere.")
                    .Wrap()
                    .Width(320.0)
                    .Left()
            )
                .Margin(24.0)

    member _.SignedIn = signedIn.Publish
