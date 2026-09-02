$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$name = $args[0]
$out = $args[1]

$ns = 'root\virtualization\v2'
$vmms = Get-WmiObject -Namespace $ns -Query "SELECT * FROM Msvm_VirtualSystemManagementService"
$vm = Get-WmiObject -Namespace $ns -Query "SELECT * FROM Msvm_ComputerSystem WHERE ElementName='$name'"
$r = $vmms.GetVirtualSystemThumbnailImage($vm, 768, 1024)
if ($r.ReturnValue -ne 0) { throw "WMI returned $($r.ReturnValue)" }
# ImageData arrives as a decoded byte[] (4-byte header + 16bpp pixels)
$bytes = if ($r.ImageData -is [byte[]]) { $r.ImageData } else { [Convert]::FromBase64String($r.ImageData) }
$w = 768; $h = 1024
$pixLen = $w * $h * 2
if ($bytes.Length -lt $pixLen) { throw "payload too small: $($bytes.Length)" }

# wrap payload (skip 4-byte WMI header) in a minimal 16bpp BI_RGB bottom-up BMP
$pix = New-Object byte[] $pixLen
[Array]::Copy($bytes, 4, $pix, 0, $pixLen)
$headerSize = 54
$fileSize = $headerSize + $pixLen
$bmpBytes = New-Object byte[] $fileSize
# BITMAPFILEHEADER
[BitConverter]::GetBytes([uint16]0x4D42).CopyTo($bmpBytes, 0)        # 'BM'
[BitConverter]::GetBytes([uint32]$fileSize).CopyTo($bmpBytes, 2)
[BitConverter]::GetBytes([uint32]$headerSize).CopyTo($bmpBytes, 10)  # pixel offset
# BITMAPINFOHEADER
[BitConverter]::GetBytes([uint32]40).CopyTo($bmpBytes, 14)
[BitConverter]::GetBytes([int32]$w).CopyTo($bmpBytes, 18)
[BitConverter]::GetBytes([int32](-$h)).CopyTo($bmpBytes, 22)           # negative = top-down (WMI payload order)
[BitConverter]::GetBytes([uint16]1).CopyTo($bmpBytes, 26)
[BitConverter]::GetBytes([uint16]16).CopyTo($bmpBytes, 28)
[BitConverter]::GetBytes([uint32]$pixLen).CopyTo($bmpBytes, 34)
[Array]::Copy($pix, 0, $bmpBytes, $headerSize, $pixLen)

$tmp = [IO.Path]::ChangeExtension($out, '.bmp')
[IO.File]::WriteAllBytes($tmp, $bmpBytes)
$img = [System.Drawing.Image]::FromFile($tmp)
$img.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$img.Dispose()
Remove-Item $tmp
"saved $out ($([IO.File]::ReadAllBytes($out).Length) bytes)"
