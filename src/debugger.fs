namespace Elmish.Debug

open Fable.Import
open Fable.Import.RemoteDev
open Fable.Core.JsInterop
open Fable.Core
open Thoth.Json

[<RequireQualifiedAccess>]
module Debugger =

    type ConnectionOptions =
        | ViaExtension
        | Remote of address:string * port:int
        | Secure of address:string * port:int

    let connect getCase opt =
        let serialize = createObj [
            // TODO: This should be exposed by Thoth.Json
            "replacer" ==> fun (_key: string) (value: obj) ->
                match value with
                // Match string before so it's not considered an IEnumerable
                | :? string -> value
                | :? System.Collections.IEnumerable ->
                    if JS.Array.isArray(value)
                    then value
                    else value :?> obj seq |> Seq.toArray |> box
                | _ -> value
        ]

        let fallback = { Options.remote = true
                         hostname = "remotedev.io"
                         port = 443
                         secure = true
                         getActionType = Some getCase
                         serialize = serialize }

        match opt with
        | ViaExtension -> { fallback with remote = false; hostname = "localhost"; port = 8000; secure = false }
        | Remote (address,port) -> { fallback with hostname = address; port = port; secure = false; getActionType = None }
        | Secure (address,port) -> { fallback with hostname = address; port = port; getActionType = None }
        |> connectViaExtension

    type Send<'msg,'model> = 'msg*'model -> unit
    type Debounce<'msg,'model> = Send<'msg,'model> -> 'msg -> 'model -> unit

    let inline nobounce send msg model = send(msg,model)

    let debounce timeoutMs =
        let mutable timeoutActive = false
        let mutable store = Unchecked.defaultof<'msg * 'model>
        fun send msg model ->
            store <- msg, model
            if not timeoutActive then
                timeoutActive <- true
                JS.setTimeout (fun () ->
                    send store
                    timeoutActive <- false) timeoutMs |> ignore


[<RequireQualifiedAccess>]
module Program =
    open Elmish
    open FSharp.Reflection

    let inline private duName (x:'a) =
        let t = typeof<'a>
        if (isNull t?cases)
        then "not-a-f#-union"
        else
            match FSharpValue.GetUnionFields(x, t) with
            | case, _ -> case.Name

    let inline private getCase<'msg> (cmd: 'msg) : obj =
        createObj ["type" ==> duName cmd
                   "msg" ==> cmd]

    let withDebuggerUsing (debounce:Debugger.Debounce<'msg,'model>) (decoder: Decoder<'model>) (connection:Connection) (program : Program<'a,'model,'msg,'view>) : Program<'a,'model,'msg,'view> =
        let init a =
            let (model,cmd) = program.init a
            let deflated = Encode.Auto.toString(0, model) |> JS.JSON.parse
            connection.init (deflated, None)
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
                            match extractState msg |> Decode.fromValue "$" decoder with
                            | Ok state -> program.setState state dispatch
                            | Error er -> JS.console.error(er)
                        | PayloadTypes.ImportState ->
                            let state = msg.payload.nextLiftedState.computedStates |> Array.last
                            match Decode.fromValue "$" decoder state?state with
                            | Ok state -> program.setState state dispatch
                            | Error er -> JS.console.error(er)
                            connection.send(null, msg.payload.nextLiftedState)
                        | _ -> ()
                    with ex ->
                        JS.console.error ("Unable to process monitor command", msg, ex)
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
        let decoder = Decode.Auto.generateDecoder()
        withDebuggerUsing Debugger.nobounce decoder connection program

    let inline withDebuggerAt options program : Program<'a,'model,'msg,'view> =
        try
            let decoder = Decode.Auto.generateDecoder()
            let connection = Debugger.connect getCase<'msg> options
            withDebuggerUsing Debugger.nobounce decoder connection program
        with ex ->
            JS.console.error ("Unable to connect to the monitor, continuing w/o debugger", ex)
            program

    let inline withDebugger (program : Program<'a,'model,'msg,'view>) : Program<'a,'model,'msg,'view> =
        try
            let decoder = Decode.Auto.generateDecoder()
            let connection = Debugger.connect getCase<'msg> Debugger.ViaExtension
            withDebuggerUsing Debugger.nobounce decoder connection program
        with ex ->
            JS.console.error ("Unable to connect to the monitor, continuing w/o debugger", ex)
            program

    /// It will send the update to the debugger only once
    /// within the space of a given time (in milliseconds).
    /// Intended for apps with many state updates per second, like games.
    let inline withDebuggerDebounce (debounceTimeout: int) (program : Program<'a,'model,'msg,'view>) : Program<'a,'model,'msg,'view> =
        try
            let decoder = Decode.Auto.generateDecoder()
            let connection = Debugger.connect getCase<'msg> Debugger.ViaExtension
            withDebuggerUsing (Debugger.debounce debounceTimeout) decoder connection program
        with ex ->
            JS.console.error ("Unable to connect to the monitor, continuing w/o debugger", ex)
            program
