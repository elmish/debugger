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

    let connect<'msg, 'model> (getCase: 'msg->obj) opt =
        let fallback = { Options.remote = true
                         hostname = "remotedev.io"
                         port = 443
                         secure = true
                         getActionType = Some getCase }

        match opt with
        | ViaExtension -> { fallback with remote = false; hostname = "localhost"; port = 8000; secure = false }
        | Remote (address,port) -> { fallback with hostname = address; port = port; secure = false; getActionType = None }
        | Secure (address,port) -> { fallback with hostname = address; port = port; getActionType = None }
        |> connectViaExtension

    type Send<'msg,'model> = 'msg*'model -> unit

[<RequireQualifiedAccess>]
module Program =
    open Elmish
    open FSharp.Reflection

    let inline private getHelpers<'msg,'model>() =
        let showEncodeError =
            let mutable hasShownError = false
            fun (er: System.Exception) ->
                if not hasShownError then
                    hasShownError <- true
                    JS.console.warn("[ELMISH DEBUGGER]", er.Message)
                    JS.console.warn("[ELMISH DEBUGGER] Falling back to simple deflater")

        let fallbackDeflater (value: obj): obj =
            JS.JSON.stringify(value, (fun _ value ->
                match value with
                // Match string before so it's not considered an IEnumerable
                | :? string -> value
                | :? System.Collections.IEnumerable ->
                    if JS.Array.isArray(value) then value
                    else value :?> seq<obj> |> Seq.toArray |> box
                | _ -> value
            )) |> JS.JSON.parse

        let makeMsgObj (case, fields) =
            createObj ["type" ==> case; "msg" ==> fields]

        let getCase =
            let t = typeof<'msg>
            if Reflection.FSharpType.IsUnion t then
                fun x -> FSharpValue.GetUnionFields(x, t) |> fun (c, fs) -> makeMsgObj(c.Name, fs)
            else
                fun x -> makeMsgObj("NOT-AN-F#-UNION", x)

        let deflater, inflater =
            try
                let encoder = Encode.Auto.generateEncoder<'model>()
                let decoder = Decode.Auto.generateDecoder<'model>()
                let deflate x =
                    try encoder x
                    with er ->
                        showEncodeError er
                        fallbackDeflater x
                let inflate x =
                    match Decode.fromValue "$" decoder x with
                    | Ok x -> x
                    | Error er -> failwith er
                deflate, inflate
            with er ->
                showEncodeError er
                fallbackDeflater, fun _ -> failwith "Cannot inflate model"
        getCase, deflater, inflater

    let withDebuggerUsing (deflater: 'model->obj) (inflater: obj->'model) (connection:Connection) (program : Program<'a,'model,'msg,'view>) : Program<'a,'model,'msg,'view> =
        let init userInit a =
            let (model,cmd) = userInit a
            connection.init (deflater model, None)
            model,cmd

        let update userUpdate msg model : 'model * Cmd<'msg> =
            let (model',cmd) = userUpdate msg model
            connection.send(msg, deflater model')
            (model',cmd)

        let subscribe userSubscribe model =
            let trySetState dispatch deflatedState =
                try
                    let state = inflater deflatedState
                    (state, dispatch) ||> Program.setState program
                with er ->
                    JS.console.error("[ELMISH DEBUGGER]", er.Message)

            let sub dispatch =
                function
                | (msg:Msg) when msg.``type`` = MsgTypes.Dispatch ->
                    try
                        match msg.payload.``type`` with
                        | PayloadTypes.JumpToAction
                        | PayloadTypes.JumpToState ->
                            extractState msg |> trySetState dispatch
                        | PayloadTypes.ImportState ->
                            let state = msg.payload.nextLiftedState.computedStates |> Array.last
                            trySetState dispatch state?state
                            connection.send(null, msg.payload.nextLiftedState)
                        | _ -> ()
                    with ex ->
                        JS.console.error ("[ELMISH DEBUGGER] Unable to process monitor command", msg, ex)
                | _ -> ()
                |> connection.subscribe
                |> ignore

            Cmd.batch
                [ [sub]
                  userSubscribe model ]

        let onError userOnError (text,ex: exn) =
            userOnError (text, ex)
            connection.error (text + ex.Message)

        program
        |> Program.map init update id id subscribe
        |> Program.mapErrorHandler onError

    let inline withDebuggerConnection connection program : Program<'a,'model,'msg,'view> =
        let _, deflater, inflater = getHelpers<'msg, 'model>()
        withDebuggerUsing deflater inflater connection program

    let inline withDebuggerAt options program : Program<'a,'model,'msg,'view> =
        try
            let getCase, deflater, inflater = getHelpers<'msg, 'model>()
            let connection = Debugger.connect getCase options
            withDebuggerUsing deflater inflater connection program
        with ex ->
            JS.console.error ("Unable to connect to the monitor, continuing w/o debugger", ex)
            program

    let inline withDebugger (program : Program<'a,'model,'msg,'view>) : Program<'a,'model,'msg,'view> =
        try
            let getCase, deflater, inflater = getHelpers<'msg, 'model>()
            let connection = Debugger.connect getCase Debugger.ViaExtension
            withDebuggerUsing deflater inflater connection program
        with ex ->
            JS.console.error ("Unable to connect to the monitor, continuing w/o debugger", ex)
            program
