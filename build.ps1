# Compila Panoptic.exe con el compilador de C# que trae Windows (.NET Framework 4.8).
# No hace falta instalar nada. Uso:  powershell -ExecutionPolicy Bypass -File build.ps1
param(
    [string]$Out = "$PSScriptRoot\Panoptic.exe",
    [string]$Manifest = "$PSScriptRoot\src\app.manifest",
    [switch]$NoSign
)
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$ico = Join-Path $PSScriptRoot 'src\app.ico'

if (-not (Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing
    $glyphFont = 'Segoe Fluent Icons'
    try { $null = New-Object System.Drawing.FontFamily $glyphFont } catch { $glyphFont = 'Segoe MDL2 Assets' }
    $sizes = 16, 20, 24, 32, 40, 48, 64, 256
    $pngs = foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
        $g.Clear([System.Drawing.Color]::Transparent)
        $r = [single]($s * 0.22); $d = 2 * $r; $w = [single]($s - 1)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
        $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $s, $s), ([System.Drawing.Color]::FromArgb(0, 120, 212)), ([System.Drawing.Color]::FromArgb(0, 90, 158))
        $g.FillPath($brush, $path)
        $font = New-Object System.Drawing.Font $glyphFont, ([single]($s * 0.56)), ([System.Drawing.GraphicsUnit]::Pixel)
        $sf = New-Object System.Drawing.StringFormat ([System.Drawing.StringFormat]::GenericTypographic)
        $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
        $g.DrawString([string][char]0xE9D9, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 0, $s, $s), $sf)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        , $ms.ToArray()
    }
    $fs = [System.IO.File]::Create($ico); $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Close()
}

$sources = Get-ChildItem "$PSScriptRoot\src\*.cs" | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$Out" "/win32icon:$ico" "/win32manifest:$Manifest" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.ServiceProcess.dll /r:Microsoft.CSharp.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "La compilación falló." }

# Firma Authenticode con el certificado de @villargonza (se crea la primera vez en el almacén del usuario).
if (-not $NoSign) {
    $subject = 'CN=@villargonza'
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -FriendlyName '@villargonza (firma de código)' `
            -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(5)
    }
    $sig = $null
    try { $sig = Set-AuthenticodeSignature -FilePath $Out -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com' -ErrorAction Stop } catch { }
    if (-not $sig -or -not $sig.SignerCertificate) { $sig = Set-AuthenticodeSignature -FilePath $Out -Certificate $cert -HashAlgorithm SHA256 }
    Write-Host "Firmado por $($cert.Subject)  huella $($cert.Thumbprint)  sello de tiempo: $([bool]$sig.TimeStamperCertificate)"
}
Write-Host "Listo: $Out"
