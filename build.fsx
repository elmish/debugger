// include Fake libs
#r "./packages/build/FAKE/tools/FakeLib.dll"
open Fake.DotNet
#r "System.IO.Compression.FileSystem"

open System
open System.IO
open Fake.Core
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.Tools.Git
open Fake.IO.Globbing.Operators
open Fake.Core.TargetOperators

// Filesets
let projects  =
      !! "src/**.fsproj"


Target.create "InstallDotNetCore" (fun _ ->
   DotNet.Options.Create() |> DotNet.install (fun o -> { o with Version = DotNet.CliVersion.GlobalJson}) |> ignore
)


Target.create "Install" (fun _ ->
    projects
    |> Seq.iter (fun s ->
        let dir = IO.Path.GetDirectoryName s
        DotNet.restore id dir
    )
)

Target.create "Clean" (fun _ ->
    projects
    |> Seq.iter (fun s ->
        let dir = IO.Path.GetDirectoryName s
        dir </> "bin" |> Shell.cleanDir
        dir </> "obj" |> Shell.cleanDir)
)

Target.create "Build" (fun _ ->
    projects
    |> Seq.iter (fun s ->
        let dir = IO.Path.GetDirectoryName s
        DotNet.build id dir)
)

let release = ReleaseNotes.load "RELEASE_NOTES.md"

Target.create "Meta" (fun _ ->
    [ "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">"
      "<PropertyGroup>"
      "<Description>Debugger for Elmish apps</Description>"
      "<PackageProjectUrl>https://github.com/elmish/debugger</PackageProjectUrl>"
      "<PackageLicenseUrl>https://raw.githubusercontent.com/elmish/debugger/master/LICENSE.md</PackageLicenseUrl>"
      "<PackageIconUrl>https://raw.githubusercontent.com/elmish/debugger/master/docs/files/img/logo.png</PackageIconUrl>"
      "<RepositoryUrl>https://github.com/elmish/debugger.git</RepositoryUrl>"
      sprintf "<PackageReleaseNotes>%s</PackageReleaseNotes>" (List.head release.Notes)
      "<PackageTags>fable;elmish;fsharp;debugger</PackageTags>"
      "<Authors>Eugene Tolmachev</Authors>"
      sprintf "<Version>%s</Version>" (string release.SemVer)
      "</PropertyGroup>"
      "</Project>"]
    |> File.write false "Directory.Build.props"
)

// --------------------------------------------------------------------------------------
// Build a NuGet package

Target.create "Package" (fun _ ->
    DotNet.pack id "src"
)

Target.create "PublishNuget" (fun _ ->
    let args = sprintf "push Fable.Elmish.Debugger.%s.nupkg -s nuget.org -k %s" (string release.SemVer) (Environment.environVar "nugetkey")
    let result = DotNet.exec (fun a -> a |> DotNet.Options.withWorkingDirectory "src/bin/Release") "nuget" args
    if (not result.OK) then failwithf "%A" result.Errors
)


// --------------------------------------------------------------------------------------
// Generate the documentation
let gitName = "debugger"
let gitOwner = "elmish"
let gitHome = sprintf "https://github.com/%s" gitOwner

let fakePath = "packages" </> "build" </> "FAKE" </> "tools" </> "FAKE.exe"
let fakeStartInfo script workingDirectory args fsiargs environmentVars =
        (fun (info: ProcStartInfo) ->
        let env =
            seq [
                yield "MSBuild", MSBuild.msBuildExe
                yield "GIT", CommandHelper.gitPath
                yield "FSI", Fake.FSIHelper.fsiPath
            ]
            |> Seq.append environmentVars
            |> Map.ofSeq
        { info with
            FileName = System.IO.Path.GetFullPath fakePath
            Arguments = sprintf "%s --fsiargs -d:FAKE %s \"%s\"" args fsiargs script
            WorkingDirectory = workingDirectory
            Environment = env }
    )

/// Run the given buildscript with FAKE.exe
let executeFAKEWithOutput workingDirectory script fsiargs envArgs =
    let exitCode =
         Process.execRaw
            (fakeStartInfo script workingDirectory "" fsiargs envArgs)
            TimeSpan.MaxValue false ignore ignore
    System.Threading.Thread.Sleep 1000
    exitCode

let copyFiles() =
    let header =
        String.splitStr "\n" """(*** hide ***)
#I "../../src/bin/Release/netstandard2.0"
#I "../../.paket/load/netstandard2.0"
#r "Fable.Core.dll"
#r "Fable.Elmish.dll"

(**
*)"""

    !!"src/*.fs"
    |> Seq.map (fun fn -> File.read fn |> Seq.append header, fn)
    |> Seq.iter (fun (lines,fn) ->
        let fsx = Path.Combine("docs/content",Path.ChangeExtension(fn |> Path.GetFileName, "fsx"))
        lines |> File.writeNew fsx)

// Documentation
let buildDocumentationTarget fsiargs target =
    Trace.trace (sprintf "Building documentation (%s), this could take some time, please wait..." target)
    let exit = executeFAKEWithOutput "docs/tools" "generate.fsx" fsiargs ["target", target]
    if exit <> 0 then
        failwith "generating reference documentation failed"
    ()

let generateHelp fail debug =
    copyFiles()
    Shell.cleanDir "docs/tools/.fake"
    let args =
        if debug then "--define:HELP"
        else "--define:RELEASE --define:HELP"
    try
        buildDocumentationTarget args "Default"
        Trace.traceImportant "Help generated"
    with
    | e when not fail ->
        Trace.traceImportant "generating help documentation failed"

Target.create "GenerateDocs" (fun _ ->
    generateHelp true false
)

Target.create "WatchDocs" (fun _ ->
    use watcher = !! "docs/content/**/*.*" |> ChangeWatcher.run (fun changes ->
         generateHelp true true
    )

    Trace.traceImportant "Waiting for help edits. Press any key to stop."

    System.Console.ReadKey() |> ignore

    watcher.Dispose()
)

// --------------------------------------------------------------------------------------
// Release Scripts

Target.create "ReleaseDocs" (fun _ ->
    let tempDocsDir = "temp/gh-pages"
    Shell.cleanDir tempDocsDir
    Repository.cloneSingleBranch "" (gitHome + "/" + gitName + ".git") "gh-pages" tempDocsDir

    Shell.copyRecursive "docs/output" tempDocsDir true |> Trace.tracefn "%A"
    Staging.stageAll tempDocsDir
    Commit.exec tempDocsDir (sprintf "Update generated documentation for version %s" release.NugetVersion)
    Branches.push tempDocsDir
)

#load "paket-files/build/fsharp/FAKE/modules/Octokit/Octokit.fsx"
open Octokit

Target.create "Release" (fun _ ->
    let user =
        match Environment.environVarOrDefault "github-user" String.Empty with
        | s when not (String.isNullOrWhiteSpace s) -> s
        | _ -> UserInput.getUserInput "Username: "
    let pw =
        match Environment.environVarOrDefault "github-pw" String.Empty with
        | s when not (String.isNullOrWhiteSpace s) -> s
        | _ -> UserInput.getUserPassword "Password: "
    let remote =
        CommandHelper.getGitResult "" "remote -v"
        |> Seq.filter (fun (s: string) -> s.EndsWith("(push)"))
        |> Seq.tryFind (fun (s: string) -> s.Contains(gitOwner + "/" + gitName))
        |> function None -> gitHome + "/" + gitName | Some (s: string) -> s.Split().[0]

    Staging.stageAll ""
    Commit.exec "" (sprintf "Bump version to %s" release.NugetVersion)
    Branches.pushBranch "" remote (Information.getBranchName "")

    Branches.tag "" release.NugetVersion
    Branches.pushTag "" remote release.NugetVersion

    // release on github

    createClient user pw
    |> createDraft gitOwner gitName release.NugetVersion (release.SemVer.PreRelease <> None) release.Notes
    |> releaseDraft
    |> Async.RunSynchronously
)

Target.create "Publish" ignore

// Build order
"Meta"
  ==> "InstallDotNetCore"
  ==> "Clean"
  ==> "Install"
  ==> "Build"
  ==> "Package"

"Build"
  ==> "GenerateDocs"
  ==> "ReleaseDocs"

"Publish"
  <== [ "Build"
        "Package"
        "PublishNuget"
        "ReleaseDocs" ]


// start build
Target.runOrDefault "Build"