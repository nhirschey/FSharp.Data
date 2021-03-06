// --------------------------------------------------------------------------------------
// FAKE build script
// --------------------------------------------------------------------------------------

#r "paket: groupref fake //"

#if !FAKE
#load ".fake/build.fsx/intellisense.fsx"
#r "netstandard"
#endif

open System
open System.IO
open Fake.Core
open Fake.DotNet
open Fake.DotNet.NuGet
open Fake.DotNet.Testing
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Core.TargetOperators
open Fake.Tools.Git

Environment.CurrentDirectory <- __SOURCE_DIRECTORY__
let (!!) includes = (!! includes).SetBaseDirectory __SOURCE_DIRECTORY__

// --------------------------------------------------------------------------------------
// Information about the project to be used at NuGet and in AssemblyInfo files
// --------------------------------------------------------------------------------------

let project = "FSharp.Data"
let authors = ["Tomas Petricek"; "Gustavo Guerra"; "Colin Bull"]
let summary = "Library of F# type providers and data access tools"
let description = """
  The FSharp.Data library (FSharp.Data.dll) contains type providers and utilities to access
  common data formats in your F# applications and scripts. It contains F# type providers for working with
  structured file formats (CSV, HTML, JSON and XML) and helpers for parsing CSV, HTML and JSON files and for sending HTTP requests."""
let tags = "F# fsharp data typeprovider WorldBank CSV HTML CSS JSON XML HTTP linqpad-samples"

let gitOwner = "fsprojects"
let gitHome = "https://github.com/" + gitOwner
let gitName = "FSharp.Data"

let desiredSdkVersion = (DotNet.getSDKVersionFromGlobalJson ())
let mutable sdkPath = None
let getSdkPath() = (defaultArg sdkPath "dotnet")
let installed =
  try
    DotNet.getVersion id <> null
  with _ -> false

printfn "Desired .NET SDK version = %s" desiredSdkVersion
printfn "DotNetCli.isInstalled() = %b" installed

let getPathForSdkVersion (sdkVersion) =
  DotNet.install (fun v -> { v with Version = DotNet.Version sdkVersion }) (DotNet.Options.Create ())
  |> fun o -> o.DotNetCliPath

if installed then
    let installedSdkVersion = DotNet.getVersion id
    printfn "The installed default .NET SDK version reported by FAKE's 'DotNetCli.getVersion()' is %s" installedSdkVersion
    if installedSdkVersion <> desiredSdkVersion then
        match Environment.environVar "CI" with
        | null ->
            if installedSdkVersion > desiredSdkVersion then
                printfn "*** You have .NET SDK version '%s' installed, assuming it is compatible with version '%s'" installedSdkVersion desiredSdkVersion
            else
                printfn "*** You have .NET SDK version '%s' installed, we expect at least version '%s'" installedSdkVersion desiredSdkVersion
        | _ ->
            printfn "*** The .NET SDK version '%s' will be installed (despite the fact that version '%s' is already installed) because we want precisely that version in CI" desiredSdkVersion installedSdkVersion
            sdkPath <- Some (getPathForSdkVersion desiredSdkVersion)
    else
        sdkPath <- Some (getPathForSdkVersion installedSdkVersion)
else
    printfn "*** The .NET SDK version '%s' will be installed (no other version was found by FAKE helpers)" desiredSdkVersion
    sdkPath <- Some (getPathForSdkVersion desiredSdkVersion)

// Read release notes & version info from RELEASE_NOTES.md
let release = ReleaseNotes.load "RELEASE_NOTES.md"

let bindir = "./bin"

let isAppVeyorBuild = Environment.environVar "APPVEYOR" <> null
let isAppVeyorBuildTag = Environment.environVar "APPVEYOR_REPO_TAG" <> null
let appVeyorTagName = Environment.environVar "APPVEYOR_REPO_TAG_NAME"
let nugetVersion =
    if isAppVeyorBuild then
        if not isAppVeyorBuildTag then
            sprintf "%s-a%s" release.NugetVersion (DateTime.UtcNow.ToString "yyMMddHHmm")
        else
            if appVeyorTagName  <> release.NugetVersion then
                printfn "mismatch between tag '%s' and RELEASE_NOTES.md version '%s" appVeyorTagName release.NugetVersion
            release.NugetVersion
    else release.NugetVersion

Target.create "AppVeyorBuildVersion" (fun _ ->
    Shell.Exec("appveyor", sprintf "UpdateBuild -Version \"%s\"" nugetVersion) |> ignore
)

// --------------------------------------------------------------------------------------
// Generate assembly info files with the right version & up-to-date information

Target.create "AssemblyInfo" <| fun _ ->
    for file in !! "src/AssemblyInfo*.fs" do
        let replace (oldValue:string) newValue (str:string) = str.Replace(oldValue, newValue)
        let title =
            Path.GetFileNameWithoutExtension file
            |> replace "AssemblyInfo" "FSharp.Data"
        let versionSuffix =".0"
        let version = release.AssemblyVersion + versionSuffix
        AssemblyInfoFile.createFSharp file
           [ AssemblyInfo.Title title
             AssemblyInfo.Product project
             AssemblyInfo.Description summary
             AssemblyInfo.Version version
             AssemblyInfo.FileVersion version]

// --------------------------------------------------------------------------------------
// Clean build results

Target.create "Clean" <| fun _ ->
    // have to clean netcore output directories because they corrupt the full-framework outputs
    seq {
        yield bindir
        yield! !!"**/bin"
        yield! !!"**/obj"
    } |> Shell.cleanDirs

Target.create "CleanDocs" <| fun _ ->
    Shell.cleanDirs ["docs/output"]

let internetCacheFolder = Environment.GetFolderPath(Environment.SpecialFolder.InternetCache)

Target.create "CleanInternetCaches" <| fun _ ->
    Shell.cleanDirs [ internetCacheFolder @@ "DesignTimeURIs"
                      internetCacheFolder @@ "WorldBankSchema"
                      internetCacheFolder @@ "WorldBankRuntime"]

// --------------------------------------------------------------------------------------
// Build library & test projects

let testProjs =
    [ "tests/FSharp.Data.DesignTime.Tests/FSharp.Data.DesignTime.Tests.fsproj"
      "tests/FSharp.Data.Tests.CSharp/FSharp.Data.Tests.CSharp.csproj"
      "tests/FSharp.Data.Tests/FSharp.Data.Tests.fsproj"
      "tests/FSharp.Data.Reference.Tests/FSharp.Data.Reference.Tests.fsproj"  ]

let buildProjs =
    [ "FSharp.Data.sln" ]

let setSdkPathAndVerbose (c: DotNet.Options) =
  { c with
      DotNetCliPath = getSdkPath ()
      CustomParams = Some ("/v:n /p:SourceLinkCreate=true /p:Version=" + nugetVersion) }

let logResults label lines =
  lines
  |> String.concat "\n\t"
  |> Trace.tracefn "%s:\n\t%s" label

Target.create "Build" <| fun _ ->
    for proj in buildProjs do
      DotNet.build (fun o -> { o with Common = setSdkPathAndVerbose o.Common
                                      Configuration = DotNet.BuildConfiguration.Release }) proj
    
Target.create "Pack" <| fun _ ->
    for proj in buildProjs do
      DotNet.pack (fun o -> { o with Common = setSdkPathAndVerbose o.Common
                                     Configuration = DotNet.BuildConfiguration.Release }) proj
    

Target.create "BuildTests" <| fun _ ->
  for testProj in testProjs do
        DotNet.build (fun o -> { o with Common = setSdkPathAndVerbose o.Common
                                        Configuration = DotNet.BuildConfiguration.Release }) testProj

Target.create "RunTests" <| fun _ ->
    for testProj in testProjs do
        DotNet.test (fun p -> { p with Common = setSdkPathAndVerbose p.Common
                                       Configuration = DotNet.BuildConfiguration.Release }) testProj

// --------------------------------------------------------------------------------------
// Build a NuGet package

Target.create "NuGet" <| fun _ ->
    // Format the release notes
    let releaseNotes = release.Notes |> String.concat "\n"
    NuGet.NuGetPack (fun p ->
        { p with
            Authors = authors
            Project = project
            Summary = summary
            Description = description
            Version = nugetVersion
            ReleaseNotes = releaseNotes
            Tags = tags
            OutputPath = "bin"
            AccessKey = Environment.environVarOrDefault "nugetkey" ""
            Publish = Environment.hasEnvironVar "nugetkey"
            Dependencies = [] })
        "nuget/FSharp.Data.nuspec"

// --------------------------------------------------------------------------------------
// Generate the documentation
Target.create "GenerateDocs" (fun _ ->
    Shell.cleanDir ".fsdocs"
    DotNet.exec id "fsdocs" ("build --properties Configuration=Release --eval --clean --parameters fsdocs-package-version " + nugetVersion) |> ignore
)

// --------------------------------------------------------------------------------------
// Release Scripts
let publishFiles what branch fromFolder toFolder =
    let tempFolder = "temp/" + branch
    Shell.cleanDir tempFolder
    Repository.cloneSingleBranch "" (gitHome + "/" + gitName + ".git") branch tempFolder
    Repository.fullclean tempFolder
    Shell.copyRecursive fromFolder (tempFolder + "/" + toFolder) true |> Trace.tracefn "%A"
    Staging.stageAll tempFolder
    Commit.exec tempFolder <| sprintf "Update %s for version %s" what release.NugetVersion
    Branches.push tempFolder

// note: doc release now done by github action, this is left in case we want to switch back to manuak
// release
Target.create "ReleaseDocsManually" <| fun _ ->
    publishFiles "generated documentation" "gh-pages" "output" ""

Target.create "Release" ignore


// --------------------------------------------------------------------------------------
// Help

Target.create "Help" <| fun _ ->
    printfn ""
    printfn "  Please specify the target by calling 'build -t <Target>'"
    printfn ""
    printfn "  Targets for building:"
    printfn "  * Build"
    printfn "  * BuildTests"
    printfn "  * RunTests"
    printfn "  * GenerateDocs"
    printfn "  * NuGet (creates package only, doesn't publish)"
    printfn "  * All (calls previous 5)"
    printfn ""
    printfn "  Targets for releasing (requires write access to the 'https://github.com/fsharp/FSharp.Data.git' repository):"
    printfn "  * Release (calls All)"
    printfn "  * ReleaseDocsManually (note: doc release now done by github action)"
    printfn ""
    printfn "  Other targets:"
    printfn "  * CleanInternetCaches"
    printfn ""

Target.create "All" ignore

"Clean" ==> "AssemblyInfo" ==> "Build"
"Build" ==> "CleanDocs" ==> "GenerateDocs" ==> "All"
"Build" ==> "NuGet" ==> "All"
"Build" ==> "All"
"Build" ==> "BuildTests" ==> "RunTests" ==> "All"
"All" ==> "Release"

Target.runOrDefaultWithArguments "Help"
