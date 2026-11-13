# Starts the built app with a sample model in dark mode, opens the model diagram,
# and prints screenshots to the log as base64 so the result can be inspected.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$bin = Resolve-Path 'TabularEditor\bin\Release'

# 1. Every assembly the query features need must load.
Add-Type -Path "$bin\Microsoft.AnalysisServices.AdomdClient.dll"
$conn = New-Object Microsoft.AnalysisServices.AdomdClient.AdomdConnection('Data Source=localhost:1')
try { $conn.Open() } catch { Write-Host "ADOMD open failed as expected: $($_.Exception.GetBaseException().GetType().Name): $($_.Exception.GetBaseException().Message)" }

# 1b. Unit checks for the usage analyzer's report scanner and M step generator.
$asm = [Reflection.Assembly]::LoadFrom("$bin\TabularEditor.exe")
$flags = [Reflection.BindingFlags]'NonPublic,Public,Static,Instance'
$append = $asm.GetType('TabularEditor.Modern.UsageAnalyzerForm').GetMethod('AppendStep', $flags)
$q1 = "let`n    Source = Sql.Database(`"srv`", `"db`"),`n    #`"Changed Type`" = Table.TransformColumnTypes(Source, {})`nin`n    #`"Changed Type`""
Write-Host "M-TEST-1:"; Write-Host ($append.Invoke($null, @($q1, 'Removed unused columns', 'Table.RemoveColumns(@PREV, {"a", "b"}, MissingField.Ignore)')))
$q2 = "Sql.Database(`"srv`", `"db`"){[Schema=`"dbo`",Item=`"T`"]}[Data] // comment"
Write-Host "M-TEST-2:"; Write-Host ($append.Invoke($null, @($q2, 'Removed unused columns', 'Table.RemoveColumns(@PREV, {"a"}, MissingField.Ignore)')))
[string]$tmp = Join-Path $env:TEMP 'mk-test.Report'
New-Item -ItemType Directory -Force "$tmp\definition\pages\p1\visuals\v1" | Out-Null
$legacy = @{ sections = @(@{
  filters = '[{"expression":{"Column":{"Expression":{"SourceRef":{"Entity":"Date"}},"Property":"Year"}}}]'
  visualContainers = @(@{ config = '{"singleVisual":{"prototypeQuery":{"Version":2,"From":[{"Name":"s","Entity":"Sales","Type":0},{"Name":"c","Entity":"Customer","Type":0}],"Select":[{"Measure":{"Expression":{"SourceRef":{"Source":"s"}},"Property":"Total Sales"},"Name":"Sales.Total Sales"},{"Aggregation":{"Expression":{"Column":{"Expression":{"SourceRef":{"Source":"c"}},"Property":"City"}},"Function":5}},{"HierarchyLevel":{"Expression":{"Hierarchy":{"Expression":{"SourceRef":{"Source":"c"}},"Hierarchy":"Geo"}},"Level":"Country"}}]}}}' })
}) } | ConvertTo-Json -Depth 10
Set-Content "$tmp\report.json" $legacy -Encoding UTF8
'{"visual":{"query":{"queryState":{"Values":{"projections":[{"field":{"Measure":{"Expression":{"SourceRef":{"Entity":"Sales"}},"Property":"Margin"}},"queryRef":"Sales.Margin"}]}}}}}' | Set-Content "$tmp\definition\pages\p1\visuals\v1\visual.json" -Encoding UTF8
$scanner = [Activator]::CreateInstance($asm.GetType('TabularEditor.Modern.ReportScanner'), $true)
$scanner.GetType().GetMethod('Scan', $flags).Invoke($scanner, [object[]]@([string]$tmp)) | Out-Null
$fields = $scanner.GetType().GetField('Fields', $flags).GetValue($scanner)
$levels = $scanner.GetType().GetField('Levels', $flags).GetValue($scanner)
Write-Host ("SCAN-TEST fields: " + (($fields | ForEach-Object { $_ -replace "`n", '|' } | Sort-Object) -join ', '))
Write-Host ("SCAN-TEST levels: " + (($levels | ForEach-Object { $_ -replace "`n", '|' }) -join ', '))

# 2. Dark theme preference.
$prefDir = Join-Path $env:LOCALAPPDATA 'TabularEditor'
New-Item -ItemType Directory -Force $prefDir | Out-Null
'{"View_DarkTheme":true,"CheckForUpdates":false,"CollectTelemetry":false}' | Set-Content (Join-Path $prefDir 'Preferences.json')

Add-Type -Namespace Win -Name Mouse -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, System.UIntPtr extra);
"@
function Click($x, $y) {
    $ok = [Win.Mouse]::SetCursorPos($x, $y)
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    [Win.Mouse]::mouse_event(0x8001, [uint32]($x * 65535 / $b.Width), [uint32]($y * 65535 / $b.Height), 0, [System.UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
    Write-Host "Click $x $y setpos=$ok cursor=$([System.Windows.Forms.Cursor]::Position) focus=$([Win.Mouse]::GetForegroundWindow())"
    [Win.Mouse]::mouse_event(2, 0, 0, 0, [System.UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    [Win.Mouse]::mouse_event(4, 0, 0, 0, [System.UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
}

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
Write-Host "main window handle: $($proc.MainWindowHandle)"
Click 82 284
[System.Windows.Forms.SendKeys]::SendWait('{RIGHT}{DOWN}{RIGHT}')
Start-Sleep -Seconds 2
Shot 'tree'
Click 47 506
Start-Sleep -Seconds 1
Click 150 650
Start-Sleep -Seconds 2
Shot 'measure'
Click 700 330
[System.Windows.Forms.SendKeys]::SendWait('{END} {+} SU')
Start-Sleep -Seconds 3
Shot 'autocomplete'
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Seconds 1
[System.Windows.Forms.SendKeys]::SendWait('^+d')
Start-Sleep -Seconds 5
Shot 'diagram'
[System.Windows.Forms.SendKeys]::SendWait('%{F4}')
Start-Sleep -Seconds 1
[System.Windows.Forms.SendKeys]::SendWait('^+b')
Start-Sleep -Seconds 4
Shot 'analyzer'
[System.Windows.Forms.SendKeys]::SendWait('%{F4}')
Start-Sleep -Seconds 1
[System.Windows.Forms.SendKeys]::SendWait('^+k')
Start-Sleep -Seconds 5
Shot 'usage'
[System.Windows.Forms.SendKeys]::SendWait('%{F4}')
Start-Sleep -Seconds 1
[System.Windows.Forms.SendKeys]::SendWait('^+q')
Start-Sleep -Seconds 4
[System.Windows.Forms.SendKeys]::SendWait('SUMMARIZECOLUMNS{(} Cust')
Start-Sleep -Seconds 3
Shot 'query'
if ($proc.HasExited) { throw "TabularEditor.exe crashed after opening the diagram" }
Stop-Process -Id $proc.Id -Force
Write-Host 'Smoke test finished'
