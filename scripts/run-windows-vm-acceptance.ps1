param(
    [Parameter(Mandatory=$true)][string]$Root,
    [Parameter(Mandatory=$true)][string]$PayloadUri,
    [Parameter(Mandatory=$true)][string]$ManifestSha256
)
$ErrorActionPreference = 'Stop'
$ApiKeyByteCount = 32
$ReadinessAttempts = 120
$ReadinessDelayMilliseconds = 250
$ExitWaitMilliseconds = 10000
if ($Root -notmatch '^C:\\Temp\\exec-server-acceptance-[a-zA-Z0-9-]+$') { throw 'Invalid private test directory.' }
if (Test-Path -LiteralPath $Root) { throw 'Refusing an existing test directory.' }
New-Item -ItemType Directory -Path $Root | Out-Null
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true,$false)
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
$rule = New-Object Security.AccessControl.FileSystemAccessRule($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
$acl.AddAccessRule($rule)
Set-Acl -LiteralPath $Root -AclObject $acl
$manifestPath = Join-Path $Root 'manifest.json'
Invoke-WebRequest -UseBasicParsing -Uri ($PayloadUri + '/manifest.json') -OutFile $manifestPath
if ((Get-FileHash -Algorithm SHA256 $manifestPath).Hash -ne $ManifestSha256) { throw 'Manifest hash mismatch.' }
$manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
foreach ($name in @('exec-server.exe','exec-server-fixture.exe','exec-server-harness.exe')) {
    $destination = Join-Path $Root $name
    Invoke-WebRequest -UseBasicParsing -Uri ($PayloadUri + '/' + $name) -OutFile $destination
    if ((Get-FileHash -Algorithm SHA256 $destination).Hash -ne $manifest.$name) { throw ('Payload hash mismatch: ' + $name) }
}
$serverDir = Join-Path $Root 'server'
New-Item -ItemType Directory -Path $serverDir | Out-Null
Move-Item -LiteralPath (Join-Path $Root 'exec-server.exe') -Destination $serverDir
$keyBytes = New-Object byte[] $ApiKeyByteCount
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($keyBytes)
$rng.Dispose()
$key = [Convert]::ToBase64String($keyBytes)
$keyFile = Join-Path $Root 'api.key'
[IO.File]::WriteAllText($keyFile, $key)
$listener = New-Object System.Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$url = 'http://127.0.0.1:' + $port
$env:EXECSERVER_API_KEY = $key
$env:ExecServer__SpoolDirectory = Join-Path $Root 'spool'
$env:ExecServer__LogDirectory = Join-Path $Root 'logs'
$env:ExecServer__MemoryThresholdBytes = '1024'
$env:ExecServer__InlineBytes = '4096'
$service = $null
try {
    $service = Start-Process -FilePath (Join-Path $serverDir 'exec-server.exe') -ArgumentList @('--urls',$url) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $Root 'server.stdout.jsonl') -RedirectStandardError (Join-Path $Root 'server.stderr.txt')
    $env:EXECSERVER_API_KEY = $null
    $ready = $false
    for ($attempt = 0; $attempt -lt $ReadinessAttempts; $attempt++) {
        $service.Refresh()
        if ($service.HasExited) { throw 'Server exited before readiness.' }
        try {
            $status = Invoke-RestMethod -Uri ($url + '/v1/status') -Headers @{Authorization=('Bearer ' + $key)} -TimeoutSec 2
            $ready = $true
            break
        } catch { Start-Sleep -Milliseconds $ReadinessDelayMilliseconds }
    }
    if (-not $ready) { throw 'Server readiness deadline exceeded.' }
    $harness = Join-Path $Root 'exec-server-harness.exe'
    & $harness --url $url --token-file $keyFile --fixture (Join-Path $Root 'exec-server-fixture.exe') --include-shutdown | Set-Content -Encoding UTF8 (Join-Path $Root 'harness.json')
    $result = $LASTEXITCODE
    if (-not $service.WaitForExit($ExitWaitMilliseconds)) { throw 'Server did not exit after drain.' }
    $os = Get-CimInstance Win32_OperatingSystem
    $receipt = [ordered]@{
        os = $os.Caption; version = $os.Version; build = $os.BuildNumber
        root = $Root; serverPid = $service.Id; serverExitCode = $service.ExitCode
        harnessExitCode = $result; hashes = $manifest
        harness = (Get-Content -Raw (Join-Path $Root 'harness.json') | ConvertFrom-Json)
    }
    $receipt | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 (Join-Path $Root 'receipt.json')
    $receipt | ConvertTo-Json -Depth 20 -Compress
    if ($result -ne 0 -or $service.ExitCode -ne 0) { exit 1 }
} finally {
    $env:EXECSERVER_API_KEY = $null
    if ($null -ne $service) {
        $service.Refresh()
        if (-not $service.HasExited) {
            # This object is the process started above; never signal a reused PID.
            if ($service.MainModule.FileName -ne (Join-Path $serverDir 'exec-server.exe')) { throw 'Service identity changed.' }
            $service.Kill()
            $service.WaitForExit()
        }
        $service.Dispose()
    }
}
