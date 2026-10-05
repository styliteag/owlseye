# Builds owlseye for Windows into publish\: owlseye.exe (self-contained: .NET runtime, all managed code and the web
# assets inside) plus the few native libraries it needs next to it (WebView2 loader, WPF). Nothing is extracted to
# %TEMP% at run time; install the folder where only administrators can write (e.g. C:\Program Files\owlseye) if
# owlseye is to run elevated, since the libraries next to the exe are loaded from there.
# With -Version 1.2.3 the exe carries that version, and dist\ gets the release download: owlseye-1.2.3-win-x64.zip
# (the publish folder) and SHA256SUMS.txt.
# Prerequisite on the build machine: .NET 10 SDK. Runs the tests first.
param([switch]$SkipTests, [string]$Version)
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
  if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') {
    throw "Version '$Version' is not of the form 1.2.3 or 1.2.3-rc1"
  }
  if (-not $SkipTests) {
    dotnet test Owlseye.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
  }
  if (Test-Path publish) { Remove-Item publish -Recurse -Force }
  $props = @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=false',
    '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none')
  if ($Version) { $props += "-p:Version=$Version" }
  dotnet publish src/Owlseye.App -c Release -r win-x64 --self-contained @props -o publish
  if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
  # the license (AGPL) and the third-party notices of the bundled runtime go with every copy
  Copy-Item LICENSE, THIRD-PARTY-NOTICES.txt publish
  Write-Host "Done: publish\ (owlseye.exe and its native libraries; copy the whole folder)"
  if ($Version) {
    if (Test-Path dist) { Remove-Item dist -Recurse -Force }
    New-Item -ItemType Directory dist | Out-Null
    $zip = "owlseye-$Version-win-x64.zip"
    # entry by entry with "/" as separator: in Windows PowerShell 5.1, Compress-Archive and ZipFile write backslashes
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $root = (Resolve-Path publish).Path
    $out = [IO.File]::Create("$PWD\dist\$zip")
    $archive = New-Object IO.Compression.ZipArchive($out, [IO.Compression.ZipArchiveMode]::Create)
    try {
      foreach ($f in Get-ChildItem publish -Recurse -File) {
        $name = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
      }
    } finally {
      $archive.Dispose()
      $out.Dispose()
    }
    $hash = (Get-FileHash "dist\$zip" -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$PWD\dist\SHA256SUMS.txt", "$hash  $zip`n")
    Write-Host "Release files: dist\$zip, dist\SHA256SUMS.txt"
  }
} finally {
  Pop-Location
}
