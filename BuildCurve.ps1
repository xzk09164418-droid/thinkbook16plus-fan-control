param([string]$OutputPath = (Join-Path $PSScriptRoot 'FanCurve.exe'))
$ErrorActionPreference='Stop'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

$references=@('System.dll','System.Core.dll','System.Xml.dll','Microsoft.CSharp.dll','System.Drawing.dll','System.Management.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Windows.Forms.DataVisualization.dll')
$parameters=@('/nologo','/target:winexe','/platform:x64',('/out:'+(Join-Path $PSScriptRoot 'FanCurve.exe')),('/win32manifest:'+(Join-Path $PSScriptRoot 'FanCurve.manifest')))
$parameters[3]='/out:'+$outputPath
$parameters+=('/win32icon:'+(Join-Path $PSScriptRoot 'assets\fan.ico'))
$parameters+=('/resource:'+(Join-Path $PSScriptRoot 'assets\fan.ico')+',FanCurve.Icon')
foreach($reference in $references){$parameters+=('/r:'+$reference)}
$parameters+=(Join-Path $PSScriptRoot 'FanCurve.cs')
& $compiler @parameters
if($LASTEXITCODE -ne 0){throw 'Build failed'}
Write-Output "Built $outputPath"
