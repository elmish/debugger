namespace Elmish.Debug
open Fable.Import.RemoteDev
open Fable.Core.JsInterop
open Fable.Core

[<RequireQualifiedAccess>]
module Debugger =

    type ConnectionOptions =
        | ViaExtension
        | Remote of address:string * port:int
        | Secure of address:string * port:int

    let connect getCase opt =
        let fallback = { Options.remote = true; hostname = "remotedev.io"; port = 443; secure = true; getActionType = Some getCase }

        match opt with
        | ViaExtension -> { fallback with remote = false; hostname = "localhost"; port = 8000; secure = false }
        | Remote (address,port) -> { fallback with hostname = address; port = port; secure = false; getActionType = None }
        | Secure (address,port) -> { fallback with hostname = address; port = port; getActionType = None }
        |> connectViaExtension

    type Send<'msg,'model> = 'msg*'model -> unit
    type Debounce<'msg,'model> = Send<'msg,'model> -> 'msg -> 'model -> unit

    let [<Global>] private setTimeout(f: unit->unit, ms: int): unit = jsNative

    let inline nobounce send msg model = send(msg,model)
    let debounce timeoutMs =
        let mutable timeoutActive = false
        let mutable store = Unchecked.defaultof<'msg * 'model>
        fun send msg model ->
            store <- msg, model
            if not timeoutActive then
                timeoutActive <- true
                setTimeout((fun () ->
                    send store
                    timeoutActive <- false), timeoutMs)


[<RequireQualifiedAccess>]
module Program =
    open Elmish
    open FSharp.Reflection

    let inline private duName (x:'a) =
        match FSharpValue.GetUnionFields(x, typeof<'a>) with
        | case, _ -> case.Name

    let inline private getCase<'msg> (cmd: 'msg) : obj =
        createObj ["type" ==> duName cmd
                   "msg" ==> cmd]

    let withDebuggerUsing (debounce:Debugger.Debounce<'msg,'model>) (connection:Connection) (program : Program<'a,'model,'msg,'view>) : Program<'a,'model,'msg,'view> =
        let init a =
            let (model,cmd) = program.init a
            // simple looking one liner to do a recursive deflate
            // needed otherwise extension gets F# obj
            connection.init (model, None)
            model,cmd

        let update msg model : 'model * Cmd<'msg> =
            let (model',cmd) = program.update msg model
            debounce connection.send msg model'
            (model',cmd)

        let subscribe model =
            let sub dispatch =
                function
                | (msg:Msg) when msg.``type`` = MsgTypes.Dispatch ->
                    try
                        match msg.payload.``type`` with
                        | PayloadTypes.JumpToAction
                        | PayloadTypes.JumpToState ->
                            let state : 'model = unbox (extractState msg)
                            program.setState state dispatch
                        | PayloadTypes.ImportState ->
                            let state = msg.payload.nextLiftedState.computedStates |> Array.last
                            program.setState (unbox state?state) dispatch
                            connection.send(null, msg.payload.nextLiftedState)
                        | _ -> ()
                    with ex ->
                        Fable.Import.Browser.console.error ("Unable to process monitor command", msg, ex)
                | _ -> ()
                |> connection.subscribe
                |> ignore

            Cmd.batch
                [ [sub]
                  program.subscribe model ]

        let onError (text,ex: exn) =
            program.onError (text, ex)
            connection.error (text + ex.Message)

        { program with
                    init = init
                    update = update
                    subscribe = subscribe
                    onError = onError }

    let inline withDebuggerConnection connection program : Program<'a,'model,'msg,'view> =
        withDebuggerUsing Debugger.nobounce connection program


    let inline withDebuggerAt options program : Program<'a,'model,'msg,'view> =
        try
            (Debugger.nobounce, Debugger.connect getCase<'msg> options, program)
            |||> withDebuggerUsing
        with ex ->
            Fable.Import.Browser.console.error ("Unable to connect to the monitor, continuing w/o debugger", ex)
            program


    let inline withDebugger (program : Program<'a,'model,'msg,'view>) : Program<'a,'model,'msg,'view> =
        try
            (Debugger.nobounce, (Debugger.connect getCase<'msg> Debugger.ViaExtension),program)
            |||> withDebuggerUsing
        with ex ->
            Fable.Import.Browser.console.error ("Unable to connect to the monitor, continuing w/o debugger", ex)
            program

    /// It will send the update to the debugger only once
    /// within the space of a given time (in milliseconds).
    /// Intended for apps with many state updates per second, like games.
    let inline withDebuggerDebounce (debounceTimeout: int) (program : Program<'a,'model,'msg,'view>) : Program<'a,'model,'msg,'view> =
        try
            (Debugger.debounce debounceTimeout, (Debugger.connect getCase<'msg> Debugger.ViaExtension),program)
            |||> withDebuggerUsing
        with ex ->
            Fable.Import.Browser.console.error ("Unable to connect to the monitor, continuing w/o debugger", ex)
            program
