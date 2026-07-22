param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\InterviewGptBridge\InterviewGptBridge.csproj"
$output = Join-Path $root "artifacts\publish\$Runtime"

dotnet restore $project
dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $output

Write-Host "Published executable:"
Write-Host (Join-Path $output "InterviewGptBridge.exe")
