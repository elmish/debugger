(**
---
layout: standard
title:
toc: false
---
**)

(*** hide ***)

#load "./prelude.fsx"

let view _ _ = failwith "view not implemented"
let update _ _ = failwith "update not implemented"
let init _ = failwith "init not implemented"

open Elmish

(**

## Remote DevTools debugger

Elmish applications can benefit from sophisiticated time-travelling debuger with deep state and message inspection capabilities and import/export functionality.
Wether you target browser, native or any other platform, as long as you can connect to a monitor you can start collecting and visualizing the events.
For SPAs running in a browser, it's as easy as installing a plugin.

![dbg](https://github.com/zalmoxisus/remotedev/raw/master/demo.gif)


### Installation
Add Remote DevTools client package as a devDependency:
```sh
yarn add remotedev@^0.2.4 -D
```

and add Fable package:

```sh
dotnet add package Fable.Elmish.Debugger
```

Follow the monitor installation instructions at [Remotedev tools](https://github.com/zalmoxisus/remotedev) site.

Among all the monitoring apps mentioned there, for local web debugging in Chrome we recommend using [Redux DevTools Chrome extension](https://chrome.google.com/webstore/detail/redux-devtools/lmhkpmbekcpmknklioeibfkpmmfibljd) as the fastest and the most seamless monitoring option.

### Program module functions
Augment your program instance with a debugger, making sure it's the last item in the line of `Program` modifications:

Usage:
*)


open Elmish.Debug

Program.mkProgram init update view
|> Program.withDebugger // connect to a devtools monitor via Chrome/Firefox extension if available
|> Program.run

(**

or in case of a remote debugger:

*)


open Elmish.Debug

Program.mkProgram init update view
|> Program.withDebuggerAt (Debugger.Remote("localhost",8000)) // connect to a server running on localhost:8000
|> Program.run


(**

or, using a custom connection:

*)


open Elmish.Debug

let connection = Debugger.connect (Debugger.Remote("localhost",8080)) // obtain the connection, for example if sending some information directly

Program.mkProgram init update view
|> Program.withDebuggerConnection connection
|> Program.run


(**

### Conditional compilation
If don't want to include the debugger in production builds surround it with `#if DEBUG`/`#endif` and define the symbol conditionally in your build system.

*)
