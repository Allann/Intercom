[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assetsDirectory = Join-Path $PSScriptRoot 'Assets'
New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null

function New-IntercomLogo([string] $Path, [int] $Width, [int] $Height) {
    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::FromArgb(255, 246, 239, 219))
        $margin = [Math]::Max(2, [int]([Math]::Min($Width, $Height) * 0.12))
        $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 37, 67, 64), [Math]::Max(2, $margin / 3))
        $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 221, 93, 67))
        try {
            $graphics.FillEllipse($brush, $margin, $margin, $Width - (2 * $margin), $Height - (2 * $margin))
            $graphics.DrawEllipse($pen, $margin, $margin, $Width - (2 * $margin), $Height - (2 * $margin))
            $graphics.DrawLine($pen, [int]($Width * 0.35), [int]($Height * 0.5), [int]($Width * 0.65), [int]($Height * 0.5))
        }
        finally {
            $pen.Dispose()
            $brush.Dispose()
        }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

New-IntercomLogo (Join-Path $assetsDirectory 'Square44x44Logo.png') 44 44
New-IntercomLogo (Join-Path $assetsDirectory 'Square150x150Logo.png') 150 150
New-IntercomLogo (Join-Path $assetsDirectory 'Wide310x150Logo.png') 310 150
New-IntercomLogo (Join-Path $assetsDirectory 'StoreLogo.png') 50 50
