module Pegasus.App.Program

open System
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Themes.Fluent
open Avalonia.FuncUI.Hosts
open Pegasus.App.Controller

type MainWindow(pad: Notepad) as this =
    inherit HostWindow()

    do
        this.Title <- "Pegasus"
        this.Width <- 1000.0
        this.Height <- 680.0
        this.Content <- Shell.view pad

type App() =
    inherit Application()

    let pad =
        new Notepad(defaultWorkspaceRoot, Environment.UserName)

    override this.Initialize() =
        this.Styles.Add(FluentTheme())

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            // Start on a note so the editor is usable immediately.
            match pad.Notes |> Array.tryHead with
            | Some first -> pad.Open first.Id
            | None -> pad.CreateNote "scratch" |> ignore

            desktop.MainWindow <- MainWindow pad
            desktop.ShutdownRequested.Add(fun _ -> (pad :> IDisposable).Dispose())
        | _ -> ()

        base.OnFrameworkInitializationCompleted()

[<EntryPoint>]
let main argv =
    AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace()
        .StartWithClassicDesktopLifetime argv
