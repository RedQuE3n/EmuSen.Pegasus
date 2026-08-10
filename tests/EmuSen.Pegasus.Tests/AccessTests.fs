[<Xunit.Collection "Avalonia">]
module EmuSen.Pegasus.Tests.AccessTests

open Avalonia
open Avalonia.Automation
open Avalonia.Automation.Peers
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open EmuSen.Pegasus
open EmuSen.Pegasus.Controller
open EmuSen.Pegasus.Tests.Headless

/// What a screen reader would find in each window -- see Pegasus_Design.md §13.
///
/// Shown, then measured and arranged. All three are needed and the first was
/// missing in the first draft of this file, which is worth recording because of
/// how it failed: `IsEffectivelyVisible` is false for every control in a window
/// that was never shown, so the tab-stop walk found NOTHING and the guard below
/// passed by having nothing to check. A test that cannot fail is not a test, and
/// one that passes because its subject is empty is the version of that which
/// looks healthiest. The counts asserted below are what stop it coming back.
let private laidOut (window: Window) =
    window.Show()
    window.Measure(Size(1000.0, 680.0))
    window.Arrange(Rect(0.0, 0.0, 1000.0, 680.0))
    Dispatcher.UIThread.RunJobs()
    window

let private nameOf (control: Control) =
    match ControlAutomationPeer.CreatePeerForElement(control).GetName() with
    | null -> ""
    | s -> s

/// Every control the keyboard can actually land on, with the name it announces.
///
/// IsEffectivelyVisible is load-bearing and was found the hard way in LunaP: a
/// ComboBox template carries a hidden TextBox for its editable mode, and counting
/// it reports an unnamed tab stop that no keyboard can reach. LunaP.md §24.5.
let private tabStops (window: Window) =
    window.GetVisualDescendants()
    |> Seq.choose (fun v ->
        match box v with
        | :? InputElement as e when e.Focusable && e.IsTabStop && e.IsEffectivelyVisible ->
            Some(v.GetType().Name, nameOf (v :?> Control))
        | _ -> None)
    |> Seq.toArray

let private unnamed (window: Window) =
    tabStops window
    |> Array.filter (fun (_, name) -> System.String.IsNullOrWhiteSpace name)
    |> Array.map fst

let private findByName (window: Window) (name: string) =
    window.GetVisualDescendants()
    |> Seq.tryPick (fun v ->
        match box v with
        | :? Control as c when nameOf c = name -> Some c
        | _ -> None)

/// THE GUARD THAT MATTERS, because it does not know what the controls are and so
/// covers anything added later. An unnamed tab stop is a dead end for somebody
/// who cannot see where the focus went: the control announces as its kind and
/// nothing else, so "edit", and there were eight of those in this window.
[<Fact>]
let ``nothing the keyboard can reach in the main window is unnamed`` () =
    started.Force()
    use pad = new Notepad(tempRoot (), Peers.identity "alice", Peers.acceptAny)
    pad.CreateNote "scratch" |> ignore

    let window = laidOut (Shell.PegasusWindow pad)

    // The count first, so this cannot pass by finding nothing to check.
    Assert.Equal(16, (tabStops window).Length)
    Assert.Empty(unnamed window)
    window.Close()

[<Fact>]
let ``nothing the keyboard can reach in the sign-in window is unnamed`` () =
    started.Force()

    let window = laidOut (SignIn.SignInWindow(tempRoot ()))

    Assert.Equal(4, (tabStops window).Length)
    Assert.Empty(unnamed window)
    window.Close()

/// A placeholder is not a name. Both are present on every box on purpose, and
/// this is what stops somebody deleting the name later on the grounds that the
/// placeholder already says it.
[<Fact>]
let ``every text box carries a name as well as its placeholder`` () =
    started.Force()
    use pad = new Notepad(tempRoot (), Peers.identity "alice", Peers.acceptAny)
    pad.CreateNote "scratch" |> ignore

    let window = laidOut (Shell.PegasusWindow pad)

    let boxes =
        window.GetVisualDescendants()
        |> Seq.choose (fun v ->
            match box v with
            | :? TextBox as t when t.IsEffectivelyVisible -> Some t
            | _ -> None)
        |> Seq.toArray

    Assert.NotEmpty boxes

    for b in boxes do
        Assert.False(System.String.IsNullOrWhiteSpace(nameOf b), $"a text box with placeholder '{b.PlaceholderText}' has no name")

    window.Close()

/// The editor is the application, and it announced as "edit" with no name and no
/// placeholder to fall back on. It takes the open note's name so that switching
/// notes is not silent.
[<Fact>]
let ``the editor announces which note is open`` () =
    started.Force()
    use pad = new Notepad(tempRoot (), Peers.identity "alice", Peers.acceptAny)
    pad.CreateNote "groceries" |> ignore

    let window = laidOut (Shell.PegasusWindow pad)

    Assert.Equal("Note: groceries", nameOf (editorOf window))
    window.Close()

[<Fact>]
let ``the editor renames itself when another note is opened`` () =
    started.Force()
    use pad = new Notepad(tempRoot (), Peers.identity "alice", Peers.acceptAny)
    pad.CreateNote "groceries" |> ignore
    pad.CreateNote "chapter one" |> ignore

    let window = laidOut (Shell.PegasusWindow pad)
    let before = nameOf (editorOf window)

    // Through the list, which is how a user does it, rather than by calling the
    // controller -- the wiring between the two is exactly what could break.
    let notes =
        window.GetVisualDescendants()
        |> Seq.pick (fun v ->
            match box v with
            | :? ListBox as l when nameOf l = "Notes" -> Some l
            | _ -> None)

    notes.SelectedIndex <- (if notes.SelectedIndex = 0 then 1 else 0)
    Dispatcher.UIThread.RunJobs()

    let after = nameOf (editorOf window)

    Assert.NotEqual<string>(before, after)
    Assert.StartsWith("Note: ", after)
    window.Close()

/// The one control whose accessible name differs from its caption, and the only
/// reason it is allowed to is that the caption is a symbol rather than a word.
/// The caption must stay a plus: this fails if somebody "fixes" it by widening
/// the button, which would crowd the name box beside it.
[<Fact>]
let ``the add button still says plus and announces what it does`` () =
    started.Force()
    use pad = new Notepad(tempRoot (), Peers.identity "alice", Peers.acceptAny)

    let window = laidOut (Shell.PegasusWindow pad)

    let add = findByName window "Create note"
    Assert.True(add.IsSome, "no control announces as 'Create note'")

    match add with
    | Some(:? Button as b) -> Assert.Equal("+", string b.Content)
    | _ -> failwith "the control named 'Create note' is not a button"

    window.Close()

/// Connection state is carried on screen by a colour, and a colour is exactly
/// what a screen reader cannot pass on. The text differs too, but only somebody
/// who goes looking would read it -- a live region is what makes it arrive.
[<Fact>]
let ``the status line and the buddy message announce themselves`` () =
    started.Force()
    use pad = new Notepad(tempRoot (), Peers.identity "alice", Peers.acceptAny)

    let window = laidOut (Shell.PegasusWindow pad)

    let live =
        window.GetVisualDescendants()
        |> Seq.choose (fun v ->
            match box v with
            | :? Control as c when AutomationProperties.GetLiveSetting c = AutomationLiveSetting.Polite -> Some c
            | _ -> None)
        |> Seq.toArray

    // The window's own status line, and the buddy panel's message line.
    Assert.Equal(2, live.Length)
    window.Close()

/// A list of rows that announce as handles is still a list of nothing in
/// particular until the list itself says what it holds.
[<Fact>]
let ``every list says what it is a list of`` () =
    started.Force()
    use pad = new Notepad(tempRoot (), Peers.identity "alice", Peers.acceptAny)

    let window = laidOut (Shell.PegasusWindow pad)

    let lists =
        window.GetVisualDescendants()
        |> Seq.choose (fun v ->
            match box v with
            | :? ListBox as l -> Some l
            | _ -> None)
        |> Seq.toArray

    Assert.NotEmpty lists

    for l in lists do
        Assert.False(System.String.IsNullOrWhiteSpace(nameOf l), "a list box has no name")

    window.Close()

/// PADDING, and it is asserted rather than eyeballed because it is the kind of
/// thing that silently goes back to zero when somebody rebuilds a layout.
///
/// Not a pixel-exact baseline -- Pegasus_Design.md is clear that those are an
/// artefact of one machine. The assertion is the thing that was actually wrong:
/// five docked regions met each other and the window frame with no gap at all.
[<Fact>]
let ``the docked regions do not touch each other or the window frame`` () =
    started.Force()
    use pad = new Notepad(tempRoot (), Peers.identity "alice", Peers.acceptAny)

    let window = laidOut (Shell.PegasusWindow pad)

    let dock =
        window.GetVisualDescendants()
        |> Seq.pick (fun v ->
            match box v with
            | :? DockPanel as d -> Some d
            | _ -> None)

    let bare =
        dock.Children
        |> Seq.filter (fun child ->
            // The editor is the fill child and is surrounded on all four sides
            // by neighbours that carry their own margin, so it wants none.
            match box child with
            | :? TextBox -> false
            | _ -> child.Margin = Thickness 0.0)
        |> Seq.map (fun c -> c.GetType().Name)
        |> Seq.toArray

    Assert.Empty bare
    window.Close()
