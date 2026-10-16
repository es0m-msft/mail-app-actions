param(
    [Parameter(Mandatory)][ValidatePattern('^[a-p]{32}$')][string]$ExtensionId,
    [ValidateSet('win-arm64', 'win-x64', 'win-x86')][string]$Runtime = 'win-arm64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$destination = Join-Path $env:LOCALAPPDATA 'EdgeGoogleActions\host'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
dotnet publish (Join-Path $root 'native\NativeHost\NativeHost.csproj') -c Release -r $Runtime --self-contained true -o $destination --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Native host publish failed.' }
$manifest = @{
    name = 'com.edge_google_actions.bridge'
    description = 'Google Actions for Edge native bridge'
    path = Join-Path $destination 'EdgeGoogleActions.NativeHost.exe'
    type = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
}
$manifestPath = Join-Path $destination 'host.json'
$manifest | ConvertTo-Json | Set-Content $manifestPath -Encoding utf8
$key = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.edge_google_actions.bridge'
New-Item -Path $key -Force | Out-Null
Set-Item -Path $key -Value $manifestPath
Write-Host "Native host registered for $ExtensionId. Reload the extension in Edge."
Write-Host "Import your Desktop OAuth client: & '$destination\EdgeGoogleActions.NativeHost.exe' --import-client '<downloaded-client.json>'"
