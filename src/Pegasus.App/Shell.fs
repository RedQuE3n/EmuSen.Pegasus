module Pegasus.App.Shell

open System
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Pegasus.Core
open Pegasus.App.Controller

let private statusText =
    function
    | Offline -> "offline"
    | Waiting(code, port) -> $"waiting on port {port}  ·  code {code}"
    | Hosting(code, port) -> $"hosting on port {port}  ·  code {code}"
    | Connected peer -> $"connected to {peer}"
    | Failed reason -> $"failed: {reason}"

let private statusBrush state : IBrush =
    match state with
    | Connected _ -> SolidColorBrush Colors.SeaGreen
    | Failed _ -> SolidColorBrush Colors.IndianRed
    | Waiting _
    | Hosting _ -> SolidColorBrush Colors.Goldenrod
    | Offline -> SolidColorBrush Colors.Gray

let private connectionBar (pad: Notepad) (address: IWritable<string>) (port: IWritable<string>) (code: IWritable<string>) =
    StackPanel.create
        [ StackPanel.dock Dock.Top
          StackPanel.orientation Orientation.Horizontal
          StackPanel.margin 8.0
          StackPanel.spacing 6.0
          StackPanel.children
              [ Button.create
                    [ Button.content "Host"
                      Button.onClick (fun _ -> pad.StartHosting() |> ignore) ]
                TextBox.create
                    [ TextBox.width 130.0
                      TextBox.watermark "address"
                      TextBox.text address.Current
                      TextBox.onTextChanged address.Set ]
                TextBox.create
                    [ TextBox.width 70.0
                      TextBox.watermark "port"
                      TextBox.text port.Current
                      TextBox.onTextChanged port.Set ]
                TextBox.create
                    [ TextBox.width 170.0
                      TextBox.watermark "join code"
                      TextBox.text code.Current
                      TextBox.onTextChanged code.Set ]
                Button.create
                    [ Button.content "Join"
                      Button.onClick (fun _ ->
                          match Int32.TryParse port.Current with
                          | true, p -> pad.Join(address.Current, p, code.Current)
                          | _ -> ()) ]
                Button.create
                    [ Button.content "Disconnect"
                      Button.onClick (fun _ -> pad.Disconnect()) ] ] ]

let private sidebar (pad: Notepad) (notes: IWritable<NoteEntry[]>) (text: IWritable<string>) (newName: IWritable<string>) =
    DockPanel.create
        [ DockPanel.dock Dock.Left
          DockPanel.width 210.0
          DockPanel.margin 8.0
          DockPanel.lastChildFill true
          DockPanel.children
              [ StackPanel.create
                    [ StackPanel.dock Dock.Top
                      StackPanel.orientation Orientation.Horizontal
                      StackPanel.spacing 4.0
                      StackPanel.children
                          [ TextBox.create
                                [ TextBox.width 165.0
                                  TextBox.watermark "new note"
                                  TextBox.text newName.Current
                                  TextBox.onTextChanged newName.Set ]
                            Button.create
                                [ Button.content "+"
                                  Button.onClick (fun _ ->
                                      let name = newName.Current.Trim()

                                      if name <> "" then
                                          pad.CreateNote name |> ignore
                                          newName.Set ""
                                          notes.Set pad.Notes
                                          text.Set pad.Text) ] ] ]
                ListBox.create
                    [ ListBox.dataItems notes.Current
                      ListBox.itemTemplate (
                          DataTemplateView<NoteEntry>.create (fun n -> TextBlock.create [ TextBlock.text n.Name ])
                      )
                      ListBox.onSelectedItemChanged (fun item ->
                          match item with
                          | :? NoteEntry as n ->
                              pad.Open n.Id
                              text.Set pad.Text
                          | _ -> ()) ] ] ]

let view (pad: Notepad) =
    Component(fun ctx ->
        let notes = ctx.useState pad.Notes
        let text = ctx.useState pad.Text
        let caret = ctx.useState 0
        let status = ctx.useState pad.Connection
        let address = ctx.useState "127.0.0.1"
        let port = ctx.useState ""
        let code = ctx.useState ""
        let newName = ctx.useState ""

        // The document changes on a mailbox thread and the UI may only be
        // touched on the dispatcher, so every refresh hops across.
        ctx.useEffect (
            (fun () ->
                let onChanged =
                    pad.Changed.Subscribe(fun () ->
                        Dispatcher.UIThread.Post(fun () ->
                            let incoming = pad.Text

                            if incoming <> text.Current then
                                caret.Set(Caret.adjust text.Current incoming caret.Current)
                                text.Set incoming

                            notes.Set pad.Notes))

                let onConnection =
                    pad.ConnectionChanged.Subscribe(fun s -> Dispatcher.UIThread.Post(fun () -> status.Set s))

                { new IDisposable with
                    member _.Dispose() =
                        onChanged.Dispose()
                        onConnection.Dispose() }),
            [ EffectTrigger.AfterInit ]
        )

        DockPanel.create
            [ DockPanel.lastChildFill true
              DockPanel.children
                  [ connectionBar pad address port code
                    TextBlock.create
                        [ TextBlock.dock Dock.Bottom
                          TextBlock.margin 8.0
                          TextBlock.foreground (statusBrush status.Current)
                          TextBlock.text (statusText status.Current) ]
                    sidebar pad notes text newName
                    TextBox.create
                        [ TextBox.name "editor"
                          TextBox.acceptsReturn true
                          TextBox.acceptsTab true
                          TextBox.margin 8.0
                          TextBox.textWrapping TextWrapping.Wrap
                          TextBox.fontFamily (FontFamily "monospace")
                          TextBox.text text.Current
                          TextBox.onTextChanged (fun t ->
                              if t <> pad.Text then
                                  text.Set t
                                  pad.Edit t) ] ] ])
