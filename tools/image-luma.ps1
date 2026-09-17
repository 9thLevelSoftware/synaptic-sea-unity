<#
.SYNOPSIS
  Mean luminance of PNG crops, for calibrating Unity renders against Godot reference captures.

.DESCRIPTION
  For each image and each crop rectangle (x,y,w,h in pixels, top-left origin) prints the mean sRGB Rec.709 luma
  (0..255), the mean linear luminance (0..1), and the mean RGB. Pixels within -BackgroundTolerance of the
  -Background colour can be excluded so empty clear-colour areas do not dilute the comparison.

.EXAMPLE
  pwsh tools/image-luma.ps1 -Images a.png,b.png -Crops '0,0,1920,1080','600,300,400,300'
  pwsh tools/image-luma.ps1 -Images a.png -Background '13,13,18' -BackgroundTolerance 6
#>
param(
    [Parameter(Mandatory)] [string[]]$Images,
    [string[]]$Crops = @(),
    [string]$Background,
    [int]$BackgroundTolerance = 4
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# `pwsh -File` passes "a,b" as one string; accept both forms.
$Images = @($Images | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Crops = @($Crops | ForEach-Object { $_ -split ';' } | Where-Object { $_ })
$bg = $null
if ($Background) { $bg = $Background.Split(',') | ForEach-Object { [int]$_ } }

Add-Type -TypeDefinition @'
using System;
public static class LumaMath {
    static double ToLinear(double c) { c /= 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
    public static double[] Stats(byte[] px, int stride, int x0, int y0, int w, int h, int[] bg, int tol) {
        double luma = 0, lin = 0, r = 0, g = 0, b = 0; long n = 0;
        for (int y = y0; y < y0 + h; y++) {
            int row = y * stride;
            for (int x = x0; x < x0 + w; x++) {
                int i = row + x * 4;
                int B = px[i], G = px[i + 1], R = px[i + 2];
                if (bg != null && Math.Abs(R - bg[0]) <= tol && Math.Abs(G - bg[1]) <= tol && Math.Abs(B - bg[2]) <= tol) continue;
                luma += 0.2126 * R + 0.7152 * G + 0.0722 * B;
                lin += 0.2126 * ToLinear(R) + 0.7152 * ToLinear(G) + 0.0722 * ToLinear(B);
                r += R; g += G; b += B; n++;
            }
        }
        if (n == 0) return new double[] { 0, 0, 0, 0, 0, 0 };
        return new double[] { luma / n, lin / n, r / n, g / n, b / n, n };
    }
}
'@

foreach ($path in $Images) {
    $full = (Resolve-Path $path).Path
    $bmp = [System.Drawing.Bitmap]::new($full)
    try {
        $rect = [System.Drawing.Rectangle]::new(0, 0, $bmp.Width, $bmp.Height)
        $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $bytes = [byte[]]::new($data.Stride * $bmp.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
        $bmp.UnlockBits($data)
        $list = if ($Crops.Count -gt 0) { $Crops } else { @("0,0,$($bmp.Width),$($bmp.Height)") }
        foreach ($c in $list) {
            $p = $c.Split(',') | ForEach-Object { [int]$_ }
            $s = [LumaMath]::Stats($bytes, $data.Stride, $p[0], $p[1], $p[2], $p[3], $bg, $BackgroundTolerance)
            '{0} crop={1} luma={2:F2} linear={3:F4} rgb=({4:F1},{5:F1},{6:F1}) pixels={7}' -f (Split-Path $full -Leaf), $c, $s[0], $s[1], $s[2], $s[3], $s[4], $s[5]
        }
    }
    finally { $bmp.Dispose() }
}
