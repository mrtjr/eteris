# Gera a marca do Éteris: o ícone da aplicação (.ico com vários tamanhos) e PNG grandes para o README e o instalador.
# O desenho é o do logótipo da própria app: um anel «íris» (violeta, ciano, verde, rosa) à volta de um núcleo luminoso,
# o éter à volta de um centro, num quadrado escuro de cantos redondos.
# Uso (Windows PowerShell 5.1, que corre em STA):  powershell -File build\brand\make-icon.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$assets = Join-Path $root "src\Eteris.Browser\Assets"
$docs = Join-Path $root "docs\brand"
New-Item -ItemType Directory -Force $assets, $docs | Out-Null

function New-Color([string]$hex) { [System.Windows.Media.ColorConverter]::ConvertFromString($hex) }

function Render-Icon([int]$size) {
    $s = [double]$size
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()

    # Fundo: quadrado escuro com cantos redondos e uma luz violeta suave no canto superior.
    $bg = New-Object System.Windows.Media.LinearGradientBrush((New-Color "#1A1730"), (New-Color "#07070D"), 90)
    $radius = $s * 0.23
    $dc.DrawRoundedRectangle($bg, $null, [System.Windows.Rect]::new(0, 0, $s, $s), $radius, $radius)
    if ($size -ge 48) {
        $halo = New-Object System.Windows.Media.RadialGradientBrush
        $halo.GradientStops.Add((New-Object System.Windows.Media.GradientStop((New-Color "#558B5CF6"), 0)))
        $halo.GradientStops.Add((New-Object System.Windows.Media.GradientStop((New-Color "#008B5CF6"), 1)))
        $dc.PushClip([System.Windows.Media.RectangleGeometry]::new([System.Windows.Rect]::new(0, 0, $s, $s), $radius, $radius))
        $dc.DrawEllipse($halo, $null, [System.Windows.Point]::new(($s * 0.3), ($s * 0.2)), ($s * 0.75), ($s * 0.6))
        $dc.Pop()
    }

    # Anel íris.
    $iris = New-Object System.Windows.Media.LinearGradientBrush
    $iris.StartPoint = New-Object System.Windows.Point(0, 0)
    $iris.EndPoint = New-Object System.Windows.Point(1, 1)
    foreach ($stop in @(@("#8B5CF6", 0), @("#22D3EE", 0.4), @("#34D399", 0.7), @("#F472B6", 1))) {
        $iris.GradientStops.Add((New-Object System.Windows.Media.GradientStop((New-Color $stop[0]), $stop[1])))
    }
    $ringWidth = [Math]::Max(2.0, $s * 0.105)
    $ringRadius = $s * 0.30
    $center = [System.Windows.Point]::new(($s / 2), ($s / 2))
    $dc.DrawEllipse($null, (New-Object System.Windows.Media.Pen($iris, $ringWidth)), $center, $ringRadius, $ringRadius)

    # Núcleo luminoso, com brilho nos tamanhos grandes.
    if ($size -ge 32) {
        $glow = New-Object System.Windows.Media.RadialGradientBrush
        $glow.GradientStops.Add((New-Object System.Windows.Media.GradientStop((New-Color "#9922D3EE"), 0)))
        $glow.GradientStops.Add((New-Object System.Windows.Media.GradientStop((New-Color "#0022D3EE"), 1)))
        $dc.DrawEllipse($glow, $null, $center, ($s * 0.2), ($s * 0.2))
    }
    $coreRadius = [Math]::Max(1.6, $s * 0.085)
    $dc.DrawEllipse((New-Object System.Windows.Media.SolidColorBrush((New-Color "#F5F3FF"))), $null, $center, $coreRadius, $coreRadius)

    # Satélite rosa na órbita (só onde se distingue).
    if ($size -ge 32) {
        $angle = -0.78
        $sat = [System.Windows.Point]::new(($center.X + $ringRadius * [Math]::Cos($angle)), ($center.Y + $ringRadius * [Math]::Sin($angle)))
        $dc.DrawEllipse((New-Object System.Windows.Media.SolidColorBrush((New-Color "#0B0D13"))), $null, $sat, ($s * 0.085), ($s * 0.085))
        $dc.DrawEllipse((New-Object System.Windows.Media.SolidColorBrush((New-Color "#F472B6"))), $null, $sat, ($s * 0.055), ($s * 0.055))
    }

    $dc.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    return , $stream.ToArray()
}

# .ico com entradas PNG (suportado desde o Windows Vista).
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = @{}
foreach ($size in $sizes) { $images[$size] = Render-Icon $size }
$ico = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($ico)
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($size in $sizes) {
    $dim = if ($size -ge 256) { 0 } else { $size }
    $writer.Write([Byte]$dim); $writer.Write([Byte]$dim); $writer.Write([Byte]0); $writer.Write([Byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$images[$size].Length); $writer.Write([UInt32]$offset)
    $offset += $images[$size].Length
}
foreach ($size in $sizes) { $writer.Write($images[$size]) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets "eteris.ico"), $ico.ToArray())

foreach ($size in 512, 1024) { [System.IO.File]::WriteAllBytes((Join-Path $docs "eteris-$size.png"), (Render-Icon $size)) }
[System.IO.File]::WriteAllBytes((Join-Path $docs "eteris-32.png"), $images[32])
[System.IO.File]::WriteAllBytes((Join-Path $docs "eteris-16.png"), $images[16])
"eteris.ico: $($sizes.Count) tamanhos, $([Math]::Round($ico.Length / 1KB)) KB"
