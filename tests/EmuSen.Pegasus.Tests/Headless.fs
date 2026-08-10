module EmuSen.Pegasus.Tests.Headless

open System
open System.IO
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Headless
open Avalonia.LogicalTree
open Avalonia.Threading
open EmuSen.Pegasus

type private HeadlessApp() =
    inherit Application()
    // Shell.applyTheme, not a bare FluentTheme: loading a different theme than
    // the application loads is what let a blank window pass the suite.
    override this.Initialize() = Shell.applyTheme this

// Avalonia may only be initialised once per process, so every test shares this.
// LunaApp is deliberately not used here -- it resolves the saved theme through
// ConfigStore, which a test has no business touching. See Pegasus_Design.md §9.
let started =
    lazy
        (AppBuilder
            .Configure<HeadlessApp>()
            .UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = true))
            .SetupWithoutStarting()
         |> ignore)

let tempRoot () =
    let dir = Path.Combine(Path.GetTempPath(), "pegasus-ui", Guid.NewGuid().ToString "N")
    Directory.CreateDirectory dir |> ignore
    dir

let editorOf (window: Window) =
    window.GetLogicalDescendants()
    |> Seq.pick (fun c ->
        match box c with
        | :? TextBox as t when t.AcceptsReturn -> Some t
        | _ -> None)

let boxWith (window: Window) (placeholder: string) =
    window.GetLogicalDescendants()
    |> Seq.pick (fun c ->
        match box c with
        | :? TextBox as t when t.PlaceholderText = placeholder -> Some t
        | _ -> None)

let buttonSaying (window: Window) (label: string) =
    window.GetLogicalDescendants()
    |> Seq.pick (fun c ->
        match box c with
        | :? Button as b when string b.Content = label -> Some b
        | _ -> None)

let click (button: Button) =
    button.RaiseEvent(Interactivity.RoutedEventArgs Button.ClickEvent)
    Dispatcher.UIThread.RunJobs()

let showsText (window: Window) (text: string) =
    window.GetLogicalDescendants()
    |> Seq.exists (fun c ->
        match box c with
        | :? TextBlock as t -> t.Text = text
        | _ -> false)

/// Names every control that would render nothing. Measure and arrange first,
/// because a template is applied on the way to being laid out.
let untemplatedIn (window: Window) =
    window.Measure(Size(1000.0, 680.0))
    window.Arrange(Rect(0.0, 0.0, 1000.0, 680.0))
    Dispatcher.UIThread.RunJobs()

    window.GetLogicalDescendants()
    |> Seq.choose (fun c ->
        match box c with
        | :? TemplatedControl as t when isNull t.Template -> Some(t.GetType().Name)
        | _ -> None)
    |> Seq.distinct
    |> Seq.toArray

/// Waits on a condition rather than sleeping a fixed time, pumping the
/// dispatcher so posted work runs. See Pegasus_Design.md §5.
let pump (predicate: unit -> bool) =
    let deadline = DateTime.UtcNow.AddSeconds 5.0

    while not (predicate ()) && DateTime.UtcNow < deadline do
        Dispatcher.UIThread.RunJobs()
        Thread.Sleep 10

    Dispatcher.UIThread.RunJobs()
    predicate ()

/// Avalonia's dispatcher belongs to the thread that set the platform up, for
/// the life of the process. Every test that touches a window therefore joins
/// this collection and they run in sequence rather than across xunit's pool.
/// See Pegasus_Design.md §12.
[<Xunit.CollectionDefinition "Avalonia">]
type AvaloniaCollection() =
    class
    end
