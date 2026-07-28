param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\InterviewGptBridge\InterviewGptBridge.csproj"
$licenseProject = Join-Path $root "src\InterviewGptLicenseTool\InterviewGptLicenseTool.csproj"
$output = Join-Path $root "artifacts\publish\$Runtime"
$licenseOutput = Join-Path $root "artifacts\publish\$Runtime-license-tool"

dotnet restore $project
dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $output

dotnet restore $licenseProject
dotnet publish $licenseProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $licenseOutput

Write-Host "Published executable:"
Write-Host (Join-Path $output "InterviewGptBridge.exe")
Write-Host "Published license generator:"
Write-Host (Join-Path $licenseOutput "InterviewGptLicenseTool.exe")
