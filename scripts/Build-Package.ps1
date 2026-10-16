param(
    [ValidateSet('arm64', 'x64', 'x86')][string]$Architecture = 'arm64',
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string]$CertificateThumbprint
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$output = Join-Path $root "artifacts\$Architecture"
$stage = Join-Path $output 'package'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
dotnet publish (Join-Path $root 'native\NativeHost\NativeHost.csproj') -c Release -r "win-$Architecture" --self-contained true -o (Join-Path $output 'native-host') --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Native host publish failed.' }
dotnet publish (Join-Path $root 'native\ActionProvider\ActionProvider.csproj') -c Release -r "win-$Architecture" --self-contained true -o $stage --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Action provider publish failed.' }
[xml]$manifest = Get-Content (Join-Path $root 'packaging\AppxManifest.xml')
$manifest.Package.Identity.ProcessorArchitecture = $Architecture
$manifest.Save((Join-Path $stage 'AppxManifest.xml'))
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
foreach ($item in @(@('StoreLogo.png', 50), @('Logo150.png', 150), @('Logo44.png', 44))) {
    $size = [int]$item[1]
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::FromArgb(0, 88, 164))
        $font = [Drawing.Font]::new('Segoe UI', [single]($size * 0.5), [Drawing.FontStyle]::Bold)
        try { $graphics.DrawString('G', $font, [Drawing.Brushes]::White, [single]($size * 0.15), [single]0) }
        finally { $font.Dispose() }
        $bitmap.Save((Join-Path $assets $item[0]), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$sdk = Get-ChildItem $sdkRoot -Directory | Where-Object Name -Match '^10\.' |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk) { throw 'Windows SDK with MakeAppx and SignTool is required.' }
$tools = Join-Path $sdk.FullName 'x64'
$package = Join-Path $output 'EdgeGoogleActions.msix'
$packOutput = & (Join-Path $tools 'makeappx.exe') pack /d $stage /p $package /o 2>&1
$packOutput | Set-Content (Join-Path $output 'makeappx.log')
if ($LASTEXITCODE -ne 0) { throw "MSIX packaging failed: $($packOutput -join [Environment]::NewLine)" }
if ($CertificateThumbprint) {
    & (Join-Path $tools 'signtool.exe') sign /fd SHA256 /sha1 $CertificateThumbprint $package
    if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed. Import its private key into CurrentUser\My.' }
}
Write-Host "Package: $package"
Compress-Archive -Path (Join-Path $root 'extension\*') -DestinationPath (Join-Path $output 'edge-extension.zip') -Force
Write-Host "Extension: $(Join-Path $output 'edge-extension.zip')"
if (!$CertificateThumbprint) { Write-Host 'Before installation, sign with a trusted certificate whose subject is CN=EdgeGoogleActionsDev.' }
