# Run with: pwsh -STA -File scripts/build-icons.ps1 [-PreviewPath artifacts/icon-preview.png]
# Native WPF vector source; no graphics packages or network access required.
param([string]$PreviewPath = '')
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with pwsh -STA.' }
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskArtwork = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $taskRoot 'src\NbaLive\Assets\AppIcon.xaml')))
foreach ($taskKey in @('Application','ApplicationSmall','TrayLight','TrayDark')) { $taskArtwork[$taskKey].Freeze() }

function ConvertTo-IconPng([int]$Size, [string]$Variant) {
    $taskVisual = [Windows.Media.DrawingVisual]::new()
    $taskContext = $taskVisual.RenderOpen()
    try {
        $taskSelected = if ($Variant -eq 'Application' -and $Size -le 24) { 'ApplicationSmall' } else { $Variant }
        $taskContext.DrawImage($taskArtwork[$taskSelected], [Windows.Rect]::new(0,0,$Size,$Size))
    } finally { $taskContext.Close() }
    $taskBitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($Size,$Size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $taskBitmap.Render($taskVisual)
    $taskEncoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $taskEncoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($taskBitmap))
    $taskStream = [IO.MemoryStream]::new()
    try { $taskEncoder.Save($taskStream); return ,$taskStream.ToArray() } finally { $taskStream.Dispose() }
}

$taskSizes = @(16,20,24,32,40,48,64,128,256)
function Write-Icon([string]$Variant, [string]$Name) {
    $taskFrames = @($taskSizes | ForEach-Object { ,(ConvertTo-IconPng $_ $Variant) })
    $taskPath = Join-Path $taskRoot "src\NbaLive\Assets\$Name"
    $taskWriter = [IO.BinaryWriter]::new([IO.File]::Create($taskPath))
    try {
        $taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]$taskSizes.Count)
        $taskOffset = 6 + 16 * $taskSizes.Count
        for ($taskIndex=0; $taskIndex -lt $taskSizes.Count; $taskIndex++) {
            $taskDimension = if ($taskSizes[$taskIndex] -eq 256) { 0 } else { $taskSizes[$taskIndex] }
            $taskWriter.Write([byte]$taskDimension); $taskWriter.Write([byte]$taskDimension)
            $taskWriter.Write([byte]0); $taskWriter.Write([byte]0)
            $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32)
            $taskWriter.Write([uint32]$taskFrames[$taskIndex].Length); $taskWriter.Write([uint32]$taskOffset)
            $taskOffset += $taskFrames[$taskIndex].Length
        }
        foreach ($taskFrame in $taskFrames) { $taskWriter.Write([byte[]]$taskFrame) }
    } finally { $taskWriter.Dispose() }
    $taskDecoder = [Windows.Media.Imaging.IconBitmapDecoder]::new([Uri]::new($taskPath),
        [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    if ($taskDecoder.Frames.Count -ne $taskSizes.Count) { throw "$Name frame count mismatch." }
    for ($taskIndex=0; $taskIndex -lt $taskSizes.Count; $taskIndex++) {
        $taskFrame = $taskDecoder.Frames[$taskIndex]
        if ($taskFrame.PixelWidth -ne $taskSizes[$taskIndex] -or $taskFrame.PixelHeight -ne $taskSizes[$taskIndex]) { throw "$Name frame dimensions mismatch." }
        $taskDecoded = [Windows.Media.Imaging.FormatConvertedBitmap]::new($taskFrame, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
        $taskPixels = [byte[]]::new($taskFrame.PixelWidth * $taskFrame.PixelHeight * 4)
        $taskDecoded.CopyPixels($taskPixels, $taskFrame.PixelWidth * 4, 0)
        foreach ($taskCorner in @(3, ($taskFrame.PixelWidth * 4 - 1), ($taskPixels.Length - $taskFrame.PixelWidth * 4 + 3), ($taskPixels.Length - 1))) {
            if ($taskPixels[$taskCorner] -ne 0) { throw "$Name has an opaque canvas corner." }
        }
    }
    Write-Output "$Name verified: $($taskSizes -join ', ') px"
}
Write-Icon 'Application' 'Tray.ico'
Write-Icon 'TrayLight' 'TrayLight.ico'
Write-Icon 'TrayDark' 'TrayDark.ico'
[IO.File]::WriteAllBytes((Join-Path $taskRoot 'docs\app-icon.png'), (ConvertTo-IconPng 256 'Application'))

if ($PreviewPath) {
    $taskVisual = [Windows.Media.DrawingVisual]::new()
    $taskContext = $taskVisual.RenderOpen()
    function Draw-Label([string]$Text, [double]$X, [double]$Y, [double]$Size, [Windows.Media.Brush]$Ink) {
        $taskText = [Windows.Media.FormattedText]::new($Text, [Globalization.CultureInfo]::GetCultureInfo('zh-CN'),
            [Windows.FlowDirection]::LeftToRight, [Windows.Media.Typeface]::new('Microsoft YaHei UI'), $Size, $Ink, 1.0)
        $taskContext.DrawText($taskText, [Windows.Point]::new($X,$Y))
    }
    try {
        $taskInk = [Windows.Media.BrushConverter]::new().ConvertFromString('#303840')
        $taskContext.DrawRectangle([Windows.Media.Brushes]::WhiteSmoke, $null, [Windows.Rect]::new(0,0,920,500))
        Draw-Label '聊天气泡 × 篮球' 40 26 28 $taskInk
        Draw-Label '正式图标 · 原生矢量绘制' 42 72 13 $taskInk
        $taskContext.DrawImage($taskArtwork['Application'], [Windows.Rect]::new(50,128,280,280))
        Draw-Label '桌面 / 程序 / 窗口' 92 425 15 $taskInk
        for ($taskRow=0; $taskRow -lt 2; $taskRow++) {
            $taskY = 150 + $taskRow * 160
            $taskVariant = if ($taskRow -eq 0) { 'TrayLight' } else { 'TrayDark' }
            $taskBackground = [Windows.Media.BrushConverter]::new().ConvertFromString($(if ($taskRow -eq 0) { '#FFFFFF' } else { '#303840' }))
            Draw-Label $(if ($taskRow -eq 0) { '浅色任务栏' } else { '深色任务栏' }) 410 ($taskY - 27) 13 $taskInk
            $taskContext.DrawRoundedRectangle($taskBackground, $null, [Windows.Rect]::new(395,$taskY,480,74), 10, 10)
            $taskX = 418
            foreach ($taskSize in @(16,20,24,32,48)) {
                $taskBytes = ConvertTo-IconPng $taskSize $taskVariant
                $taskStream = [IO.MemoryStream]::new($taskBytes)
                try {
                    $taskDecoder = [Windows.Media.Imaging.PngBitmapDecoder]::new($taskStream, [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
                    $taskContext.DrawImage($taskDecoder.Frames[0], [Windows.Rect]::new($taskX,($taskY + (74-$taskSize)/2),$taskSize,$taskSize))
                } finally { $taskStream.Dispose() }
                Draw-Label "$taskSize px" ($taskX - 2) ($taskY + 83) 11 $taskInk
                $taskX += 85
            }
        }
    } finally { $taskContext.Close() }
    $taskBitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(920,500,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $taskBitmap.Render($taskVisual)
    $taskEncoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $taskEncoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($taskBitmap))
    $taskDestination = [IO.Path]::GetFullPath($PreviewPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskDestination)) | Out-Null
    $taskStream = [IO.File]::Create($taskDestination)
    try { $taskEncoder.Save($taskStream) } finally { $taskStream.Dispose() }
    Write-Output "Preview: $taskDestination"
}
