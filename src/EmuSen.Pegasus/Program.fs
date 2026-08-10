module EmuSen.Pegasus.Program

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open EmuSen.LunaP
open EmuSen.Pegasus
open EmuSen.Pegasus.Controller

/// Takes both roots rather than naming them, so the suite can drive the real
/// startup without writing into the real workspace. See Pegasus_Design.md §12.
type App(identityRoot: string, workspaceRoot: string) =
    inherit Application()

    new() = App(IdentityStore.defaultRoot, defaultWorkspaceRoot)

    override this.Initialize() = Shell.applyTheme this

    /// Sign in, then open the notepad as whoever signed in.
    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            // Explicit, because the sign-in window is main window first and the
            // notepad replaces it; on the default mode that swap ends the process.
            desktop.ShutdownMode <- ShutdownMode.OnExplicitShutdown

            let signIn = SignIn.SignInWindow identityRoot
            let mutable admitted = false

            let enter (identity: Identity) =
                admitted <- true
                let pad = new Notepad(workspaceRoot, identity.Peer)

                // Start on a note so the editor is usable immediately.
                match pad.Notes |> Array.tryHead with
                | Some first -> pad.Open first.Id
                | None -> pad.CreateNote "scratch" |> ignore

                let window = Shell.PegasusWindow pad

                window.Closed.Add(fun _ ->
                    (pad :> IDisposable).Dispose()
                    (identity :> IDisposable).Dispose()
                    desktop.Shutdown())

                desktop.MainWindow <- window
                window.Show()
                signIn.Close()

            signIn.SignedIn.Add enter
            signIn.Closed.Add(fun _ -> if not admitted then desktop.Shutdown())
            desktop.MainWindow <- signIn
        | _ -> ()

        base.OnFrameworkInitializationCompleted()

[<EntryPoint>]
let main argv =
    // LunaApp, not a hand-rolled AppBuilder: it applies the saved theme and
    // picks X11, which UsePlatformDetect does not do on Wayland.
    LunaApp.Configure<App>().StartWithClassicDesktopLifetime argv
