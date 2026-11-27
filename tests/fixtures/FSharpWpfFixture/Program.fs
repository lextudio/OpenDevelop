module Program

open System
open Sample
open FSharpWpfFixture.Lib

type MyPicker() =
    inherit Picker()
    member val Changed = 0 with get, set
    member val Clicked = 0 with get, set
    member this.OnTextChanged(_sender: obj, _e: System.Windows.Controls.TextChangedEventArgs) = this.Changed <- this.Changed + 1
    member this.OnOk(_sender: obj, _e: System.Windows.RoutedEventArgs) = this.Clicked <- this.Clicked + 1

type MyWindow() =
    inherit MainWindow()
    member val Clicked = 0 with get, set
    member this.OnOk(_sender: obj, _e: System.Windows.RoutedEventArgs) = this.Clicked <- this.Clicked + 1

[<EntryPoint>]
let main _ =
    let picker = MyPicker()
    printfn "box=%s ok=%s" picker.box.Text (string picker.ok.Content)
    picker.box.Text <- "changed"
    printfn "changed events=%d, name registered=%b" picker.Changed (not (isNull (picker.FindName "ok")))

    let app = App()
    let window = MyWindow()
    window.DataContext <- Person()
    printfn "greeting=%s who=%s" window.greeting.Text window.who.Text
    printfn "accent=%A style=%b row=%s" (app.Resources.["Accent"]) (not (isNull (app.Resources.["Title"]))) (string (window.Resources.["Row"].GetType().Name))
    let timer = System.Windows.Threading.DispatcherTimer(Interval = TimeSpan.FromMilliseconds 800.0)
    timer.Tick.Add(fun _ ->
        timer.Stop()
        printfn "window shown, loaded=%b, bound name=%s age=%s" window.IsLoaded window.who.Text window.edit.Text
        window.Close())
    timer.Start()
    app.Run window |> ignore
    printfn "closed"
    0
