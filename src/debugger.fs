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
        let makeObj (case, fields) =
            createObj ["type" ==> case
                       "msg" ==> fields]
        let getCase =
            let t = typeof<'msg>
            if Reflection.FSharpType.IsUnion t then
                fun x -> FSharpValue.GetUnionFields(x, t) |> fun (c, fs) -> makeObj(c.Name, fs)
            else
                fun x -> makeObj("NOT-AN-F#-UNION", x)
        getCase, Encode.Auto.generateEncoder<'model>(), Decode.Auto.generateDecoder<'model>()

    let withDebuggerUsing (encoder: Encoder<'model>) (decoder: Decoder<'model>) (connection:Connection) (program : Program<'a,'model,'msg,'view>) : Program<'a,'model,'msg,'view> =
        let init userInit a =
            let (model,cmd) = userInit a
            connection.init (encoder model, None)
            model,cmd

        let update userUpdate msg model : 'model * Cmd<'msg> =
            let (model',cmd) = userUpdate msg model
            connection.send(msg, encoder model')
            (model',cmd)

        let subscribe userSubscribe model =
            let sub dispatch =
                function
                | (msg:Msg) when msg.``type`` = MsgTypes.Dispatch ->
                    try
                        match msg.payload.``type`` with
                        | PayloadTypes.JumpToAction
                        | PayloadTypes.JumpToState ->
                            match extractState msg |> Decode.fromValue "$" decoder with
                            | Ok state -> (state,dispatch) ||> Program.setState program
                            | Error er -> JS.console.error("[DEBUGGER]", er)
                        | PayloadTypes.ImportState ->
                            let state = msg.payload.nextLiftedState.computedStates |> Array.last
                            match Decode.fromValue "$" decoder state?state with
                            | Ok state -> (state,dispatch) ||> Program.setState program
                            | Error er -> JS.console.error("[DEBUGGER]", er)
                            connection.send(null, msg.payload.nextLiftedState)
                        | _ -> ()
                    with ex ->
                        JS.console.error ("[DEBUGGER] Unable to process monitor command", msg, ex)
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
        let _, encoder, decoder = getHelpers<'msg, 'model>()
        withDebuggerUsing encoder decoder connection program

    let inline withDebuggerAt options program : Program<'a,'model,'msg,'view> =
        try
            let getCase, encoder, decoder = getHelpers<'msg, 'model>()
            let connection = Debugger.connect getCase options
            withDebuggerUsing encoder decoder connection program
        with ex ->
            JS.console.error ("Unable to connect to the monitor, continuing w/o debugger", ex)
            program

    let inline withDebugger (program : Program<'a,'model,'msg,'view>) : Program<'a,'model,'msg,'view> =
        try
            let getCase, encoder, decoder = getHelpers<'msg, 'model>()
            let connection = Debugger.connect getCase Debugger.ViaExtension
            withDebuggerUsing encoder decoder connection program
        with ex ->
            JS.console.error ("Unable to connect to the monitor, continuing w/o debugger", ex)
            program
