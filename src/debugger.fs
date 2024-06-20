namespace Elmish.Debug

open Fable.Import
open Fable.Import.RemoteDev
open Fable.Core.JsInterop
open Fable.Core
open Thoth.Json

[<RequireQualifiedAccess>]
module Debugger =
    open FSharp.Reflection

    let showError (msgs: obj list) = JS.console.error("[ELMISH DEBUGGER]", List.toArray msgs)
    let showWarning (msgs: obj list) = JS.console.warn("[ELMISH DEBUGGER]", List.toArray msgs)

    type ConnectionOptions =
        | ViaExtension of name:string
        | Remote of address:string * port:int
        | Secure of address:string * port:int

    let inline connect<'msg> opt =
        let makeMsgObj (case, fields) =
            createObj ["type" ==> case; "msg" ==> fields]

        let getCase (x: obj) =
            if Reflection.isUnion x then
                let rec getCaseName acc (x: obj) =
                    let acc = (Reflection.getCaseName x)::acc
                    let fields = Reflection.getCaseFields x
                    if fields.Length = 1 && Reflection.isUnion fields.[0] then
                        getCaseName acc fields.[0]
                    else
                        // Case names are intentionally left reverted so we see
                        // the most meaningfull message first
                        makeMsgObj(acc |> String.concat "/", fields)
                getCaseName [] x
            else
                makeMsgObj("NOT-AN-F#-UNION", x)

        let fallback = { Options.remote = true
                         hostname = "remotedev.io"
                         port = 443
                         secure = true
                         name = "Elmish"
                         getActionType = Some getCase }

        match opt with
        | ViaExtension name -> { fallback with remote = false; hostname = "localhost"; port = 8000; secure = false ; name = name}
        | Remote (address,port) -> { fallback with hostname = address; port = port; secure = false }
        | Secure (address,port) -> { fallback with hostname = address; port = port }
        |> connectViaExtension

    type Send<'msg,'model> = 'msg*'model -> unit

[<RequireQualifiedAccess>]
module Program =
    open Elmish

    let inline private getTransformersWith encoder decoder =
        let deflate x =
            try encoder x
            with er ->
                Debugger.showWarning [er.Message]
                box x
        let inflate x =
            match Decode.fromValue "$" decoder x with
            | Ok x -> x
            | Error er -> failwith er
        deflate, inflate

    let inline private getTransformers<'model>() =
        try
            let coders =
                Extra.empty
                |> Extra.withDecimal
                |> Extra.withInt64
                |> Extra.withUInt64
                // |> Extra.withBigInt
            let encoder = Encode.Auto.generateEncoder<'model>(extra = coders)
            let decoder = Decode.Auto.generateDecoder<'model>(extra = coders)
            getTransformersWith encoder decoder
        with er ->
            Debugger.showWarning [er.Message]
            box, fun _ -> failwith "Cannot inflate model"

    let withDebuggerUsing (deflater: 'model->obj) (inflater: obj->'model) (connection:Connection) (program : Program<'a,'model,'msg,'view>) : Program<'a,'model,'msg,'view> =
        let init userInit a =
            let (model,cmd) = userInit a
            connection.init (deflater model, None)
            model,cmd

        let update userUpdate msg model : 'model * Cmd<'msg> =
            let (model',cmd) = userUpdate msg model
            connection.send(msg, deflater model')
            (model',cmd)

        let sub dispatch =
            function
            | (msg:Msg) when msg.``type`` = MsgTypes.Dispatch ->
                try
                    match msg.payload.``type`` with
                    | PayloadTypes.JumpToAction
                    | PayloadTypes.JumpToState ->
                        let state = extractState msg |> inflater
                        Program.setState program state dispatch
                    | PayloadTypes.ImportState ->
                        let state = msg.payload.nextLiftedState.computedStates |> Array.last
                        let state = inflater state?state
                        Program.setState program state dispatch
                        connection.send(null, msg.payload.nextLiftedState)
                    | _ -> ()
                with ex ->
                    Debugger.showError ["Unable to process monitor command"; ex.Message; msg]
            | _ -> ()
            |> connection.subscribe
            |> fun unsub -> { new System.IDisposable with
                                member _.Dispose() = unsub() }

        let subscribe userSubscribe model =
            Sub.batch
                [ [["debugger"],sub]
                  userSubscribe model |> Sub.map "user" id ]

        let onError userOnError (text,ex: exn) =
            userOnError (text, ex)
            connection.error (text + ex.Message)

        let termination (predicate,terminate) =
            predicate, terminate

        program
        |> Program.map init update id id subscribe termination
        |> Program.mapErrorHandler onError

    let inline withDebuggerConnection connection program : Program<'a,'model,'msg,'view> =
        let deflater, inflater = getTransformers<'model>()
        withDebuggerUsing deflater inflater connection program

    let inline withDebuggerCoders (encoder: Encoder<'model>) (decoder: Decoder<'model>) program : Program<'a,'model,'msg,'view> =
        let deflater, inflater = getTransformersWith encoder decoder
        let connection = Debugger.connect<'msg> (Debugger.ViaExtension "Elmish")
        withDebuggerUsing deflater inflater connection program

    let inline withDebuggerAt options program : Program<'a,'model,'msg,'view> =
        try
            let deflater, inflater = getTransformers<'model>()
            let connection = Debugger.connect<'msg> options
            withDebuggerUsing deflater inflater connection program
        with ex ->
            Debugger.showError ["Unable to connect to the monitor, continuing w/o debugger"; ex.Message]
            program
            
    let inline withDebuggerName (name:string) (program : Program<'a,'model,'msg,'view>) : Program<'a,'model,'msg,'view> =
            try
                let deflater, inflater = getTransformers<'model>()
                let connection = Debugger.connect<'msg> (Debugger.ViaExtension name)
                withDebuggerUsing deflater inflater connection program
            with ex ->
                Debugger.showError ["Unable to connect to the monitor, continuing w/o debugger"; ex.Message]
                program

    let inline withDebugger (program : Program<'a,'model,'msg,'view>) : Program<'a,'model,'msg,'view> =
        withDebuggerName "Elmish" program
    
