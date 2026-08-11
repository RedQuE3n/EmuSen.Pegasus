module EmuSen.Pegasus.Program

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open EmuSen.LunaP
open EmuSen.Pegasus
open EmuSen.Pegasus.Controller

/// The application: sign in, then open the notepad as whoever signed in.
///
/// Takes both roots rather than resolving them itself. When it named them, no
/// test could drive this class without creating identities and notes in the
/// developer's own ~/.local/share/Pegasus, so the startup sequence went
/// untested and was only ever checked by launching the thing and looking at it.
/// The parameterless overload supplies the real pair, because
/// LunaApp.Configure<App>() needs a constructor it can call with no arguments.
type App(identityRoot: string, workspaceRoot: string) =
    inherit Application()

    new() = App(IdentityStore.defaultRoot, defaultWorkspaceRoot)

    override this.Initialize() = Shell.applyTheme this

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            // The default mode ends the process when the main window closes,
            // and this application deliberately closes its main window: the
            // sign-in window holds that role first and the notepad takes it
            // over. On the default the handover would look exactly like the
            // user quitting. Taking it explicit means every exit below is one
            // this code asked for.
            desktop.ShutdownMode <- ShutdownMode.OnExplicitShutdown

            let signIn = SignIn.SignInWindow identityRoot
            let mutable admitted = false

            // Runs once, when the sign-in window says somebody got in.
            let enter (identity: Identity) =
                admitted <- true

                // Constructed HERE, not above: nothing in the workspace is
                // opened or written while the machine is sitting unattended at
                // the sign-in prompt, and no note is ever opened as nobody.
                let pad =
                    new Notepad(workspaceRoot, identity, pinnedContacts identityRoot identity.Handle)

                // Start on a note so the editor is usable immediately.
                match pad.Notes |> Array.tryHead with
                | Some first -> pad.Open first.Id
                | None -> pad.CreateNote "scratch" |> ignore

                // The book is built here rather than inside the window because
                // this is the only place that knows both where identities are
                // filed and who just signed in. Two identities on one machine
                // keep two lists of servers, the same way they keep two lists
                // of pinned peers.
                let window =
                    Shell.PegasusWindow(pad, Servers.bookFor identityRoot identity.Handle)

                // Closing the notepad is how a user quits, so it has to flush
                // the workspace, release the key, and actually end the process
                // -- under OnExplicitShutdown nothing else will.
                window.Closed.Add(fun _ ->
                    (pad :> IDisposable).Dispose()
                    (identity :> IDisposable).Dispose()
                    desktop.Shutdown())

                // Order matters: hand over the main window role, show the new
                // window, and only then close the old one. Closing first would
                // leave a moment with no window on screen.
                desktop.MainWindow <- window
                window.Show()
                signIn.Close()

            signIn.SignedIn.Add enter

            // Closing the sign-in window without signing in means the user
            // changed their mind, and must end the process. Without this they
            // would be left with no window and a process still running, which
            // they cannot see and cannot quit. The guard is what stops the
            // handover above from being read as the same thing.
            signIn.Closed.Add(fun _ -> if not admitted then desktop.Shutdown())
            desktop.MainWindow <- signIn
        | _ -> ()

        base.OnFrameworkInitializationCompleted()

[<EntryPoint>]
let main argv =
    // LunaApp, not a hand-rolled AppBuilder: it applies the saved theme and
    // picks X11, which UsePlatformDetect does not do on Wayland.
    LunaApp.Configure<App>().StartWithClassicDesktopLifetime argv
