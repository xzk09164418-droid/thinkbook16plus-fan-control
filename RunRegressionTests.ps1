$ErrorActionPreference='Stop'
$testDir=Join-Path $PSScriptRoot 'review-tests'
New-Item -ItemType Directory -Path $testDir -Force | Out-Null
if(Test-Path -LiteralPath (Join-Path $testDir 'curve-recovery.json')) {throw 'Test directory contains a recovery marker; test cancelled.'}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$references=@('System.dll','System.Core.dll','System.Xml.dll','Microsoft.CSharp.dll','System.Drawing.dll','System.Management.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Windows.Forms.DataVisualization.dll')
$testExe=Join-Path $testDir 'RegressionTests.exe'
$parameters=@('/nologo','/target:exe','/platform:x64','/main:RegressionTests',('/out:'+$testExe))
$parameters+=('/resource:'+(Join-Path $PSScriptRoot 'assets\fan.ico')+',FanCurve.Icon')
foreach($reference in $references){$parameters+=('/r:'+$reference)}
$parameters+=@(Join-Path $PSScriptRoot 'FanCurve.cs'; Join-Path $PSScriptRoot 'RegressionTests.cs')
& $compiler @parameters
if($LASTEXITCODE -ne 0){throw 'Test build failed'}
& $testExe
if($LASTEXITCODE -ne 0){throw 'Regression tests failed'}
Get-Content -LiteralPath (Join-Path $testDir 'regression-test.txt')
