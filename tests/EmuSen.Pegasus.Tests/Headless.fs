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

/// Names every control in the window that would render nothing.
///
/// Measure and arrange first: a template is applied on the way to being laid
/// out, so a window that has only been shown has not necessarily got any yet,
/// and asserting before laying out would fail on controls that are perfectly
/// fine.
///
/// This is the shape of the guard that caught the blank window -- a headless
/// test that walks the logical tree proves the tree exists, not that anything
/// is drawn. If what can break is whether anything appears at all, the
/// assertion has to be about templates. Pegasus_Design.md §11 is the incident.
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

/// Waits for a condition, pumping the dispatcher so posted work actually runs.
///
/// Waiting on the condition rather than sleeping a fixed time is what keeps the
/// suite from being timing-fragile on a loaded machine: a fixed sleep is either
/// too short sometimes or too slow always. RunJobs is needed because nothing
/// here starts a message loop, so posted continuations only run when asked.
let pump (predicate: unit -> bool) =
    let deadline = DateTime.UtcNow.AddSeconds 5.0

    while not (predicate ()) && DateTime.UtcNow < deadline do
        Dispatcher.UIThread.RunJobs()
        Thread.Sleep 10

    Dispatcher.UIThread.RunJobs()
    predicate ()

/// Every test class that touches a window joins this collection.
///
/// xunit parallelises across test classes. Avalonia's dispatcher belongs to the
/// thread that set the platform up, for the life of the process, so two UI test
/// classes running at once fail with "the calling thread cannot access this
/// object because a different thread owns it". A collection is xunit's way of
/// saying these run in sequence; the non-UI tests keep their parallelism.
///
/// With only one UI test class this was invisible, and adding a second turned
/// eight passing tests red immediately.
[<Xunit.CollectionDefinition "Avalonia">]
type AvaloniaCollection() =
    class
    end
