# Runs the UI smoke test (smoke.mjs) against an exe.
#   ./tests/e2e/smoke-ci.ps1 publish/owlseye.exe
# The test drives the app through WebView2's remote debugging port (WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS). An elevated
# owlseye drops WEBVIEW2_* on purpose (security review, finding 1), and WebView2 ignores them when elevated anyway.
# GitHub's Windows runners run every job as an administrator with UAC off, so there the test runs as a temporary
# standard user, created for the run and deleted afterwards.
param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference = 'Stop'

$exePath = (Resolve-Path $Exe).Path
$script = Join-Path $PSScriptRoot 'smoke.mjs'
$node = (Get-Command node).Source
$me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  & $node $script $exePath
  exit $LASTEXITCODE
}

Write-Host "Running elevated: the smoke test runs as a temporary standard user"
$name = 'owlsmoke'
$pw = ConvertTo-SecureString ('Ow1!' + [guid]::NewGuid().ToString('N')) -AsPlainText -Force
New-LocalUser -Name $name -Password $pw -PasswordNeverExpires -AccountNeverExpires -Description 'owlseye smoke test' | Out-Null
$code = 1
try {
  # read and run: the exe and its libraries, the test script, node
  foreach ($dir in @((Split-Path $exePath), $PSScriptRoot, (Split-Path $node))) {
    icacls $dir /grant "${name}:(OI)(CI)RX" /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed for $dir" }
  }
  # Start-Process -Credential hands on this process's environment, so TEMP would be the administrator's: give the
  # test a folder of its own (smoke.mjs puts the app's data there)
  $base = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
  $work = Join-Path $base "owlsmoke-$PID"
  New-Item -ItemType Directory $work | Out-Null
  icacls $work /grant "${name}:(OI)(CI)M" /Q | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "icacls failed for $work" }
  $env:TEMP = $work
  $env:TMP = $work
  # where smoke.mjs puts a screenshot if it fails (the workflow uploads it)
  $env:SMOKE_ARTIFACTS = Join-Path $base 'smoke-artifacts'
  New-Item -ItemType Directory $env:SMOKE_ARTIFACTS -Force | Out-Null
  icacls $env:SMOKE_ARTIFACTS /grant "${name}:(OI)(CI)M" /Q | Out-Null
  $out = Join-Path $base "owlseye-smoke-$PID.out"
  $err = "$out.err"
  $cred = New-Object Management.Automation.PSCredential("$env:COMPUTERNAME\$name", $pw)
  # CreateProcessWithLogonW: same desktop, the user's profile loaded
  $p = Start-Process -FilePath $node -ArgumentList @("`"$script`"", "`"$exePath`"") -Credential $cred -LoadUserProfile `
    -WorkingDirectory $PSScriptRoot -RedirectStandardOutput $out -RedirectStandardError $err -Wait -PassThru
  Get-Content $out, $err -ErrorAction SilentlyContinue | Write-Host
  $code = $p.ExitCode
} finally {
  Remove-LocalUser -Name $name -ErrorAction SilentlyContinue
}
exit $code
