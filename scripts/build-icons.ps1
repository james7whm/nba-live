# Run with: pwsh -STA -File scripts/build-icons.ps1
# Native WPF vector source; no graphics packages or network access required.
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run this script with pwsh -STA -File scripts/build-icons.ps1.'
}
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskSource = Join-Path $taskRoot 'src\NbaLive\Assets\AppIcon.xaml'
$taskXml = [IO.File]::ReadAllText($taskSource)
$taskImage = [Windows.Markup.XamlReader]::Parse($taskXml)
$taskSmallImage = $taskImage.Clone()
# At tray sizes, omit the curved seams to avoid a dense, blurry center.
$taskSmallImage.Drawing.Children.RemoveAt(3)
$taskImage.Freeze()
$taskSmallImage.Freeze()

function ConvertTo-IconPng([int]$Size) {
    $taskVisual = [Windows.Media.DrawingVisual]::new()
    $taskContext = $taskVisual.RenderOpen()
    try {
        $taskSelectedImage = if ($Size -le 24) { $taskSmallImage } else { $taskImage }
        $taskContext.DrawImage($taskSelectedImage, [Windows.Rect]::new(0,0,$Size,$Size))
    }
    finally { $taskContext.Close() }
    $taskBitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($Size,$Size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $taskBitmap.Render($taskVisual)
    $taskEncoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $taskEncoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($taskBitmap))
    $taskStream = [IO.MemoryStream]::new()
    try { $taskEncoder.Save($taskStream); return ,$taskStream.ToArray() }
    finally { $taskStream.Dispose() }
}

$taskSizes = @(16,20,24,32,40,48,64,128,256)
$taskFrames = @($taskSizes | ForEach-Object { ,(ConvertTo-IconPng $_) })
$taskIconPath = Join-Path $taskRoot 'src\NbaLive\Assets\Tray.ico'
$taskStream = [IO.File]::Create($taskIconPath)
$taskWriter = [IO.BinaryWriter]::new($taskStream)
try {
    $taskWriter.Write([uint16]0)
    $taskWriter.Write([uint16]1)
    $taskWriter.Write([uint16]$taskSizes.Count)
    $taskOffset = 6 + 16 * $taskSizes.Count
    for ($taskIndex=0; $taskIndex -lt $taskSizes.Count; $taskIndex++) {
        $taskDimension = if ($taskSizes[$taskIndex] -eq 256) { 0 } else { $taskSizes[$taskIndex] }
        $taskWriter.Write([byte]$taskDimension)
        $taskWriter.Write([byte]$taskDimension)
        $taskWriter.Write([byte]0)
        $taskWriter.Write([byte]0)
        $taskWriter.Write([uint16]1)
        $taskWriter.Write([uint16]32)
        $taskWriter.Write([uint32]$taskFrames[$taskIndex].Length)
        $taskWriter.Write([uint32]$taskOffset)
        $taskOffset += $taskFrames[$taskIndex].Length
    }
    foreach ($taskFrame in $taskFrames) { $taskWriter.Write([byte[]]$taskFrame) }
}
finally { $taskWriter.Dispose() }
[IO.File]::WriteAllBytes((Join-Path $taskRoot 'docs\app-icon.png'), (ConvertTo-IconPng 256))
$taskDecoder = [Windows.Media.Imaging.IconBitmapDecoder]::new([Uri]::new($taskIconPath),
    [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
    [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
if ($taskDecoder.Frames.Count -ne $taskSizes.Count) { throw 'ICO frame verification failed.' }
Write-Output "Generated icon frames: $($taskSizes -join ', ') px"
