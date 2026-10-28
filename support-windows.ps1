param([switch]$Open, [switch]$Restart, [switch]$Stop)

$ErrorActionPreference = 'Stop'
[System.Threading.Thread]::CurrentThread.CurrentUICulture = [System.Globalization.CultureInfo]::GetCultureInfo('en-US')
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$showEvent = [System.Threading.EventWaitHandle]::new($false, 'AutoReset', 'Local\PrintAgentSupportShow')
$stopEvent = [System.Threading.EventWaitHandle]::new($false, 'AutoReset', 'Local\PrintAgentSupportStop')
$workerStopEvent = [System.Threading.EventWaitHandle]::new($false, 'AutoReset', 'Local\PrintAgentWorkerStop')
$directory = Split-Path -Parent $MyInvocation.MyCommand.Path
$workerPath = Join-Path $directory 'PrintAgent.exe'
$settingsPath = Join-Path $directory 'appsettings.json'
$logPath = Join-Path $directory 'Logs\printagent.log'
$downloadsPath = Join-Path $directory 'Downloads'
$worker = $null
$nextWorkerStart = [DateTime]::MinValue
$workerErrorShown = $false

function Find-Worker {
    $sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    $running = Get-CimInstance Win32_Process -Filter "Name = 'PrintAgent.exe'" |
        Where-Object { $_.ExecutablePath -eq $workerPath -and $_.SessionId -eq $sessionId } | Select-Object -First 1
    if ($running) { return [System.Diagnostics.Process]::GetProcessById($running.ProcessId) }
    return $null
}

function Stop-Worker {
    [void]$workerStopEvent.Set()
    if (-not $script:worker) { return }
    try {
        if (-not $script:worker.HasExited -and -not $script:worker.WaitForExit(10000)) {
            $script:worker.Kill()
            [void]$script:worker.WaitForExit(5000)
        }
    }
    catch { [void][System.Windows.Forms.MessageBox]::Show("Unable to stop PrintAgent: $($_.Exception.Message)", 'PrintAgent') }
    finally { $script:worker.Dispose(); $script:worker = $null }
}

if ($Restart -or $Stop) { [void]$stopEvent.Set() }
$owned = $false
$mutex = [System.Threading.Mutex]::new($true, 'Local\PrintAgentSupport', [ref]$owned)
if (-not $owned -and ($Restart -or $Stop)) {
    try { $owned = $mutex.WaitOne(20000) }
    catch [System.Threading.AbandonedMutexException] { $owned = $true }
}
if ($Stop) {
    $worker = Find-Worker
    Stop-Worker
}
if ($Stop -or -not $owned) {
    if ($Open -and -not $Stop) { [void]$showEvent.Set() }
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
    $showEvent.Dispose()
    $stopEvent.Dispose()
    $workerStopEvent.Dispose()
    return
}
[void]$stopEvent.WaitOne(0)

function Start-Worker {
    if ($script:worker) { $script:worker.Dispose(); $script:worker = $null }
    $script:worker = Find-Worker
    if ($script:worker) { Stop-Worker }
    if (-not (Test-Path -LiteralPath $workerPath)) { throw 'PrintAgent.exe was not found.' }
    [void]$workerStopEvent.WaitOne(0)
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($workerPath)
    $startInfo.Arguments = "--parent-pid $PID"
    $startInfo.WorkingDirectory = $directory
    $startInfo.UseShellExecute = $false
    $script:worker = [System.Diagnostics.Process]::Start($startInfo)
    if (-not $script:worker) { throw 'Unable to start PrintAgent.exe.' }
}

if ($Restart) {
    $worker = Find-Worker
    if ($worker) { Stop-Worker }
}

function Open-Downloads {
    try {
        [void][System.IO.Directory]::CreateDirectory($downloadsPath)
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new($downloadsPath)
        $startInfo.UseShellExecute = $true
        [void][System.Diagnostics.Process]::Start($startInfo)
    }
    catch { [void][System.Windows.Forms.MessageBox]::Show("Unable to open downloads: $($_.Exception.Message)", 'PrintAgent') }
}

$form = New-Object System.Windows.Forms.Form
$form.Text = 'PrintAgent - Support'
$form.SetBounds(0, 0, 380, 225)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false

$label = New-Object System.Windows.Forms.Label
$label.Text = 'Medpointe port (1-65535):'
$label.SetBounds(20, 20, 320, 20)
$form.Controls.Add($label)

$portBox = New-Object System.Windows.Forms.TextBox
$portBox.SetBounds(20, 45, 150, 24)
$form.Controls.Add($portBox)
try { $portBox.Text = [string]((Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).MedpointePort) }
catch { $portBox.Text = '' }

function Show-Settings {
    try { $portBox.Text = [string]((Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).MedpointePort) }
    catch { $portBox.Text = '' }
    if (-not $form.Visible) { $form.Show() }
    $form.WindowState = 'Normal'
    $form.Activate()
}
$form.Add_FormClosing({ param($sender, $eventArgs) $eventArgs.Cancel = $true; $form.Hide() })

$saveButton = New-Object System.Windows.Forms.Button
$saveButton.Text = 'Save port'
$saveButton.SetBounds(185, 43, 155, 28)
$saveButton.Add_Click({
    $port = 0
    if (-not [int]::TryParse($portBox.Text.Trim(), [ref]$port) -or $port -lt 1 -or $port -gt 65535) {
        [void][System.Windows.Forms.MessageBox]::Show('Enter a port between 1 and 65535.', 'PrintAgent')
        return
    }

    $tempPath = "$settingsPath.new"
    $backupPath = "$settingsPath.bak"
    try {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        if ($null -eq $settings.PSObject.Properties['MedpointePort']) { throw 'MedpointePort is missing from appsettings.json.' }
        $settings.MedpointePort = $port
        [System.IO.File]::WriteAllText($tempPath, ($settings | ConvertTo-Json -Depth 100), [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::Replace($tempPath, $settingsPath, $backupPath)
        [void][System.Windows.Forms.MessageBox]::Show('Port saved. PrintAgent will apply the change automatically.', 'PrintAgent')
    }
    catch { [void][System.Windows.Forms.MessageBox]::Show("Unable to save port: $($_.Exception.Message)", 'PrintAgent') }
    finally { Remove-Item -LiteralPath $tempPath,$backupPath -ErrorAction SilentlyContinue }
})
$form.Controls.Add($saveButton)

$logsButton = New-Object System.Windows.Forms.Button
$logsButton.Text = 'View logs'
$logsButton.SetBounds(20, 95, 150, 30)
$logsForm = New-Object System.Windows.Forms.Form
$logsForm.Text = 'PrintAgent - Recent logs'
$logsForm.SetBounds(0, 0, 800, 500)
$logsForm.StartPosition = 'CenterScreen'
$logsText = New-Object System.Windows.Forms.TextBox
$logsText.Multiline = $true
$logsText.ReadOnly = $true
$logsText.ScrollBars = 'Both'
$logsText.WordWrap = $false
$logsText.Dock = 'Fill'
$logsText.Font = [System.Drawing.Font]::new('Consolas', 9)
$logsForm.Controls.Add($logsText)
$pauseButton = New-Object System.Windows.Forms.Button
$pauseButton.Text = 'Pause'
$pauseButton.Dock = 'Bottom'
$logsForm.Controls.Add($pauseButton)
$logVersion = ''
$logsTimer = [System.Windows.Forms.Timer]::new()
$logsTimer.Interval = 1000

function Refresh-Logs {
    try {
        $file = Get-Item -LiteralPath $logPath -ErrorAction Stop
        $version = "$($file.Length):$($file.LastWriteTimeUtc.Ticks)"
        if ($version -eq $script:logVersion) { return }
        $logsText.Text = (Get-Content -LiteralPath $logPath -Tail 600 -ErrorAction Stop) -join "`r`n"
        $script:logVersion = $version
    }
    catch {
        if ($script:logVersion -eq 'missing') { return }
        $logsText.Text = 'No logs are available yet.'
        $script:logVersion = 'missing'
    }
    $logsText.SelectionStart = $logsText.TextLength
    $logsText.ScrollToCaret()
}
$logsTimer.Add_Tick({ Refresh-Logs })
$pauseButton.Add_Click({
    if ($logsTimer.Enabled) { $logsTimer.Stop(); $pauseButton.Text = 'Resume' }
    else { $script:logVersion = ''; Refresh-Logs; $logsTimer.Start(); $pauseButton.Text = 'Pause' }
})
$logsForm.Add_FormClosing({ param($sender, $eventArgs) $eventArgs.Cancel = $true; $logsForm.Hide(); $logsTimer.Stop() })

function Show-Logs {
    if (-not $logsForm.Visible) {
        $script:logVersion = ''
        Refresh-Logs
        $pauseButton.Text = 'Pause'
        $logsTimer.Start()
        $logsForm.Show()
    }
    $logsForm.WindowState = 'Normal'
    $logsForm.Activate()
}
$logsButton.Add_Click({ Show-Logs })
$form.Controls.Add($logsButton)

$downloadsButton = New-Object System.Windows.Forms.Button
$downloadsButton.Text = 'Open downloads'
$downloadsButton.SetBounds(185, 95, 155, 30)
$downloadsButton.Add_Click({ Open-Downloads })
$form.Controls.Add($downloadsButton)

$closeButton = New-Object System.Windows.Forms.Button
$closeButton.Text = 'Close'
$closeButton.SetBounds(20, 140, 320, 30)
$closeButton.Add_Click({ $form.Close() })
$form.Controls.Add($closeButton)

$context = [System.Windows.Forms.ApplicationContext]::new()
$menu = [System.Windows.Forms.ContextMenuStrip]::new()
$menu.Items.Add('Configure port').Add_Click({ Show-Settings })
$menu.Items.Add('View logs').Add_Click({ Show-Logs })
$menu.Items.Add('Open downloads').Add_Click({ Open-Downloads })
$menu.Items.Add('Exit PrintAgent').Add_Click({ $context.ExitThread() })
$tray = [System.Windows.Forms.NotifyIcon]::new()
$tray.Icon = [System.Drawing.SystemIcons]::Application
$tray.Text = 'PrintAgent'
$tray.ContextMenuStrip = $menu
$tray.Add_MouseDoubleClick({ Show-Settings })
$tray.Visible = $true

$timer = [System.Windows.Forms.Timer]::new()
$timer.Interval = 300
$timer.Add_Tick({
    if ($stopEvent.WaitOne(0)) { $context.ExitThread(); return }
    if ($showEvent.WaitOne(0)) { Show-Settings }
    if (($null -eq $script:worker -or $script:worker.HasExited) -and [DateTime]::UtcNow -ge $script:nextWorkerStart) {
        try { Start-Worker; $script:workerErrorShown = $false }
        catch {
            if (-not $script:workerErrorShown) { $tray.ShowBalloonTip(5000, 'PrintAgent', "Unable to start the Worker: $($_.Exception.Message)", 'Error') }
            $script:workerErrorShown = $true
        }
        $script:nextWorkerStart = [DateTime]::UtcNow.AddSeconds(10)
    }
})
$timer.Start()
try {
    $initialPort = 0
    if ($Open -or -not [int]::TryParse($portBox.Text, [ref]$initialPort) -or $initialPort -lt 1 -or $initialPort -gt 65535) { Show-Settings }
    [System.Windows.Forms.Application]::Run($context)
}
finally {
    $timer.Stop()
    Stop-Worker
    $logsTimer.Stop()
    $tray.Visible = $false
    $tray.Dispose()
    $menu.Dispose()
    $logsForm.Dispose()
    $form.Dispose()
    $mutex.ReleaseMutex()
    $mutex.Dispose()
    $showEvent.Dispose()
    $stopEvent.Dispose()
    $workerStopEvent.Dispose()
}
