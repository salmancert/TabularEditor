# Starts the built app with a sample model in dark mode, opens the model diagram,
# and prints screenshots to the log as base64 so the result can be inspected.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$bin = Resolve-Path 'TabularEditor\bin\Release'

# 1. Every assembly the query features need must load.
Add-Type -Path "$bin\Microsoft.AnalysisServices.AdomdClient.dll"
$conn = New-Object Microsoft.AnalysisServices.AdomdClient.AdomdConnection('Data Source=localhost:1')
try { $conn.Open() } catch { Write-Host "ADOMD open failed as expected: $($_.Exception.GetBaseException().GetType().Name): $($_.Exception.GetBaseException().Message)" }

# 2. Dark theme preference.
$prefDir = Join-Path $env:LOCALAPPDATA 'TabularEditor'
New-Item -ItemType Directory -Force $prefDir | Out-Null
'{"View_DarkTheme":true,"CheckForUpdates":false,"CollectTelemetry":false}' | Set-Content (Join-Path $prefDir 'Preferences.json')

function Shot($name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
    $p = New-Object System.Drawing.Imaging.EncoderParameters 1
    $p.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality, [long]55)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, $codec, $p)
    $s = [Convert]::ToBase64String($ms.ToArray())
    Write-Host "SHOT-BEGIN $name"
    for ($i = 0; $i -lt $s.Length; $i += 4000) { Write-Host ("SHOT " + $s.Substring($i, [Math]::Min(4000, $s.Length - $i))) }
    Write-Host "SHOT-END $name"
}

$model = Resolve-Path 'TabularEditorTest\AdventureWorks.bim'
$proc = Start-Process "$bin\TabularEditor.exe" -ArgumentList "`"$model`"" -PassThru
Start-Sleep -Seconds 20
if ($proc.HasExited) { throw "TabularEditor.exe exited early with code $($proc.ExitCode)" }
$shell = New-Object -ComObject WScript.Shell
$null = $shell.AppActivate($proc.Id)
Start-Sleep -Seconds 2
Shot 'main'
[System.Windows.Forms.SendKeys]::SendWait('%v')
Start-Sleep -Milliseconds 800
Shot 'viewmenu'
[System.Windows.Forms.SendKeys]::SendWait('e')
Start-Sleep -Seconds 2
[System.Windows.Forms.SendKeys]::SendWait('{DOWN 9}')
Start-Sleep -Seconds 3
Shot 'expanded'
[System.Windows.Forms.SendKeys]::SendWait('^+d')
Start-Sleep -Seconds 5
Shot 'diagram'
if ($proc.HasExited) { throw "TabularEditor.exe crashed after opening the diagram" }
Stop-Process -Id $proc.Id -Force
Write-Host 'Smoke test finished'
