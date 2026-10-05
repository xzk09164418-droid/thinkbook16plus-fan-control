$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$sizes=@(16,24,32,48,64,128,256)
$images=@()
foreach($size in $sizes) {
 $b=New-Object Drawing.Bitmap($size,$size)
 $g=[Drawing.Graphics]::FromImage($b)
 $g.SmoothingMode='AntiAlias'
 $g.ScaleTransform(($size/256.0),($size/256.0))
 $dark=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(24,53,78))
 $blue=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(38,173,222))
 $white=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(235,249,255))
 $g.FillEllipse($dark,4,4,248,248)
 for($i=0;$i -lt 3;$i++) {
  $state=$g.Save(); $g.TranslateTransform(128,128); $g.RotateTransform(($i*120))
  $path=New-Object Drawing.Drawing2D.GraphicsPath
  $path.AddBezier(0,-17,-64,-42,-72,-98,-21,-101)
  $path.AddBezier(-21,-101,37,-105,68,-55,17,-5)
  $path.AddBezier(17,-5,10,-13,5,-17,0,-17)
  $path.CloseFigure(); $g.FillPath($blue,$path); $path.Dispose(); $g.Restore($state)
 }
 $g.FillEllipse($dark,99,99,58,58); $g.FillEllipse($white,109,109,38,38)
 $ms=New-Object IO.MemoryStream
 $b.Save($ms,[Drawing.Imaging.ImageFormat]::Png)
 $images+=,($ms.ToArray())
 if($size -eq 256){$b.Save((Join-Path $PSScriptRoot 'fan.png'),[Drawing.Imaging.ImageFormat]::Png)}
 $ms.Dispose(); $g.Dispose(); $b.Dispose(); $dark.Dispose(); $blue.Dispose(); $white.Dispose()
}
$w=New-Object IO.BinaryWriter([IO.File]::Create((Join-Path $PSScriptRoot 'fan.ico')))
try {
 $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
 $offset=6+16*$sizes.Count
 for($i=0;$i -lt $sizes.Count;$i++) {
  $w.Write([byte]($sizes[$i]%256)); $w.Write([byte]($sizes[$i]%256))
  $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset); $offset+=$images[$i].Length
 }
 foreach($bytes in $images){$w.Write([byte[]]$bytes)}
} finally {$w.Dispose()}
