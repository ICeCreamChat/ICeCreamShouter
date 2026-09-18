param(
    [string]$BaseUrl = 'http://127.0.0.1:8787',
    [string]$BootstrapToken = 'LOCAL_E2E_BOOTSTRAP_2026_STRONG',
    [string]$ReceiverPath = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
Set-Location (Split-Path -Parent $PSScriptRoot)
if (-not $ReceiverPath) {
    $ReceiverPath = Join-Path (Get-Location) 'dist\receiver\ICeCreamShouter.exe'
}

function New-ApiClient {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseCookies = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.BaseAddress = [Uri]$BaseUrl
    return $client
}

function Invoke-Api {
    param(
        [System.Net.Http.HttpClient]$Client,
        [string]$Method,
        [string]$Path,
        [object]$Body = $null,
        [string]$Csrf = ''
    )
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Path)
    if ($Csrf) { [void]$request.Headers.TryAddWithoutValidation('X-CSRF-Token', $Csrf) }
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 10 -Compress
        $request.Content = [System.Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
    }
    try {
        $response = $Client.SendAsync($request).GetAwaiter().GetResult()
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($response.Headers.Contains('Set-Cookie')) {
            $setCookie = @($response.Headers.GetValues('Set-Cookie')) | Select-Object -First 1
            if ($setCookie -match '(^|;\s*)(crs_session=[^;]*)') {
                [void]$Client.DefaultRequestHeaders.Remove('Cookie')
                [void]$Client.DefaultRequestHeaders.TryAddWithoutValidation('Cookie', $Matches[2])
            }
        }
        $data = if ($content) { $content | ConvertFrom-Json } else { $null }
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Data = $data; Raw = $content }
    }
    finally {
        $request.Dispose()
    }
}

function New-TestWav {
    $sampleRate = 16000
    $sampleCount = $sampleRate
    $dataBytes = $sampleCount * 2
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::ASCII, $true)
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
        $writer.Write([int](36 + $dataBytes))
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVE'))
        $writer.Write([Text.Encoding]::ASCII.GetBytes('fmt '))
        $writer.Write([int]16)
        $writer.Write([int16]1)
        $writer.Write([int16]1)
        $writer.Write([int]$sampleRate)
        $writer.Write([int]($sampleRate * 2))
        $writer.Write([int16]2)
        $writer.Write([int16]16)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
        $writer.Write([int]$dataBytes)
        for ($index = 0; $index -lt $sampleCount; $index++) { $writer.Write([int16]0) }
        $writer.Flush()
        return $stream.ToArray()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Invoke-VoiceApi {
    param(
        [System.Net.Http.HttpClient]$Client,
        [string]$ClassId,
        [string]$Csrf
    )
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, '/api/shouts')
    [void]$request.Headers.TryAddWithoutValidation('X-CSRF-Token', $Csrf)
    $multipart = [System.Net.Http.MultipartFormDataContent]::new()
    $multipart.Add([System.Net.Http.StringContent]::new($ClassId), 'classId')
    $multipart.Add([System.Net.Http.StringContent]::new('info'), 'alertLevel')
    $multipart.Add([System.Net.Http.StringContent]::new('5'), 'displayDurationSec')
    $audio = [System.Net.Http.ByteArrayContent]::new((New-TestWav))
    $audio.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('audio/wav')
    $multipart.Add($audio, 'audio', 'voice.wav')
    $request.Content = $multipart
    try {
        $response = $Client.SendAsync($request).GetAwaiter().GetResult()
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Data = ($content | ConvertFrom-Json); Raw = $content }
    }
    finally {
        $request.Dispose()
    }
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Wait-Until {
    param([scriptblock]$Condition, [int]$Seconds = 15, [string]$Failure = 'Condition timed out.')
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $value = & $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 350
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

$admin = New-ApiClient
$teacher = New-ApiClient
$anonymous = New-ApiClient
$receiverProcess = $null
$configPath = Join-Path (Split-Path -Parent $ReceiverPath) 'config.json'
$className = '"\u9ad8\u4e00\uff081\uff09\u73ed"' | ConvertFrom-Json
$gradeTwo = '"\u9ad8\u4e8c"' | ConvertFrom-Json

try {
    Assert-True (Test-Path -LiteralPath $ReceiverPath) 'The published receiver EXE does not exist.'
    Assert-True (-not (Test-Path -LiteralPath $configPath)) 'A receiver config.json already exists; refusing to overwrite it.'

    $bootstrap = Invoke-Api $anonymous GET '/api/bootstrap'
    Assert-True ($bootstrap.Status -eq 200 -and $bootstrap.Data.required) 'A fresh local database should require bootstrap.'

    $wrongBootstrap = Invoke-Api $anonymous POST '/api/bootstrap' @{ bootstrapToken = 'wrong'; password = 'AdminPass2026!' }
    Assert-True ($wrongBootstrap.Status -eq 403) 'An incorrect bootstrap token must be rejected.'

    $createdAdmin = Invoke-Api $anonymous POST '/api/bootstrap' @{ bootstrapToken = $BootstrapToken; password = 'AdminPass2026!' }
    Assert-True ($createdAdmin.Status -eq 200) 'ICe bootstrap failed.'

    $adminLogin = Invoke-Api $admin POST '/api/auth/login' @{ username = 'ICe'; password = 'AdminPass2026!' }
    Assert-True ($adminLogin.Status -eq 200 -and $adminLogin.Data.user.role -eq 'superadmin') 'ICe login failed.'
    $adminCsrf = [string]$adminLogin.Data.user.csrfToken

    $class = Invoke-Api $admin POST '/api/classes' @{ name = $className } $adminCsrf
    Assert-True ($class.Status -eq 201) 'Administrator could not create a class.'
    $classId = [string]$class.Data.id

    $batch = Invoke-Api $admin POST '/api/classes/batch' @{ grade = $gradeTwo; start = 1; end = 2 } $adminCsrf
    Assert-True ($batch.Status -eq 200 -and $batch.Data.created.Count -eq 2) 'Batch class creation failed.'

    $teacherCreated = Invoke-Api $admin POST '/api/teachers' @{
        username = 'teacher_e2e'; displayName = 'E2E Teacher'; password = 'TeacherPass2026!'; classIds = @($classId)
    } $adminCsrf
    Assert-True ($teacherCreated.Status -eq 201) 'Teacher account creation failed.'

    $teacherLogin = Invoke-Api $teacher POST '/api/auth/login' @{ username = 'teacher_e2e'; password = 'TeacherPass2026!' }
    Assert-True ($teacherLogin.Status -eq 200 -and $teacherLogin.Data.user.role -eq 'teacher') 'Teacher login failed.'
    $teacherCsrf = [string]$teacherLogin.Data.user.csrfToken

    $teacherClassAttempt = Invoke-Api $teacher POST '/api/classes' @{ name = 'Forbidden Class' } $teacherCsrf
    Assert-True ($teacherClassAttempt.Status -eq 403) 'A teacher was incorrectly allowed to create a class.'

    $teacherClasses = Invoke-Api $teacher GET '/api/classes'
    Assert-True ($teacherClasses.Status -eq 200 -and $teacherClasses.Data.classes.Count -eq 1 -and $teacherClasses.Data.classes[0].id -eq $classId) 'Teacher class scoping is incorrect.'

    $offlineShout = Invoke-Api $teacher POST '/api/shouts' @{
        classId = $classId; text = 'Offline status test'; alertLevel = 'info'; ttsVolume = 1; ttsSpeed = 0; displayDurationSec = 5
    } $teacherCsrf
    Assert-True ($offlineShout.Status -eq 202 -and $offlineShout.Data.status -eq 'offline') 'An offline classroom should return offline.'

    $codeResponse = Invoke-Api $admin POST "/api/classes/$classId/enrollment-code" $null $adminCsrf
    Assert-True ($codeResponse.Status -eq 200 -and $codeResponse.Data.code) 'Enrollment code generation failed.'
    $enrollment = Invoke-Api $anonymous POST '/api/device/enroll' @{ code = $codeResponse.Data.code; deviceName = 'Local E2E Receiver' }
    Assert-True ($enrollment.Status -eq 200 -and $enrollment.Data.deviceToken) 'Device enrollment failed.'

    $reuse = Invoke-Api $anonymous POST '/api/device/enroll' @{ code = $codeResponse.Data.code; deviceName = 'Duplicate enrollment' }
    Assert-True ($reuse.Status -ge 400) 'An enrollment code was incorrectly accepted twice.'

    $config = @{
        Endpoint = $BaseUrl; DeviceId = [string]$enrollment.Data.deviceId; DeviceToken = [string]$enrollment.Data.deviceToken
        ClassId = $classId; ClassName = $className; DeviceName = 'Local E2E Receiver'; StartWithWindows = $false
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($configPath, $config, [Text.UTF8Encoding]::new($false))
    $receiverProcess = Start-Process -FilePath $ReceiverPath -PassThru

    [void](Wait-Until -Seconds 20 -Failure 'The receiver did not become online.' -Condition {
        $status = Invoke-Api $admin GET "/api/classes/$classId/status"
        return $status.Status -eq 200 -and $status.Data.online
    })

    $firstShout = Invoke-Api $teacher POST '/api/shouts' @{
        classId = $classId; text = 'First long announcement that should be interrupted.'; alertLevel = 'warning'; ttsVolume = 1; ttsSpeed = -2; displayDurationSec = 20
    } $teacherCsrf
    Assert-True ($firstShout.Status -eq 202 -and $firstShout.Data.status -eq 'sent') 'First online shout was not sent.'

    [void](Wait-Until -Seconds 10 -Failure 'The first shout did not reach displayed state.' -Condition {
        $history = Invoke-Api $teacher GET "/api/history?classId=$classId&limit=20"
        return @($history.Data.history | Where-Object { $_.id -eq $firstShout.Data.id -and $_.status -eq 'displayed' }).Count -eq 1
    })

    $secondShout = Invoke-Api $teacher POST '/api/shouts' @{
        classId = $classId; text = 'Second latest announcement'; alertLevel = 'urgent'; ttsVolume = 1; ttsSpeed = 2; displayDurationSec = 5
    } $teacherCsrf
    Assert-True ($secondShout.Status -eq 202 -and $secondShout.Data.status -eq 'sent') 'Second online shout was not sent.'

    [void](Wait-Until -Seconds 10 -Failure 'The second shout did not reach displayed state.' -Condition {
        $history = Invoke-Api $teacher GET "/api/history?classId=$classId&limit=20"
        return @($history.Data.history | Where-Object { $_.id -eq $secondShout.Data.id -and $_.status -eq 'displayed' }).Count -eq 1
    })

    $voiceShout = Invoke-VoiceApi $teacher $classId $teacherCsrf
    Assert-True ($voiceShout.Status -eq 202 -and $voiceShout.Data.status -eq 'sent') 'Voice shout was not sent.'
    [void](Wait-Until -Seconds 10 -Failure 'The receiver did not download and finish the voice shout.' -Condition {
        $history = Invoke-Api $teacher GET "/api/history?classId=$classId&limit=20"
        return @($history.Data.history | Where-Object { $_.id -eq $voiceShout.Data.id -and $_.status -eq 'displayed' }).Count -eq 1
    })

    $devices = Invoke-Api $admin GET '/api/devices'
    $device = @($devices.Data.devices | Where-Object { $_.id -eq $enrollment.Data.deviceId })[0]
    Assert-True ($devices.Status -eq 200 -and $null -ne $device) 'Enrolled device is missing from administration.'

    $disabled = Invoke-Api $admin PATCH "/api/devices/$($enrollment.Data.deviceId)" @{ enabled = $false } $adminCsrf
    Assert-True ($disabled.Status -eq 200) 'Device disable failed.'
    [void](Wait-Until -Seconds 10 -Failure 'Disabled device remained online.' -Condition {
        $status = Invoke-Api $admin GET "/api/classes/$classId/status"
        return $status.Status -eq 200 -and -not $status.Data.online
    })

    $historyFinal = Invoke-Api $teacher GET "/api/history?classId=$classId&limit=20"
    Assert-True ($historyFinal.Status -eq 200 -and $historyFinal.Data.history.Count -ge 3) 'Shout history is incomplete.'

    Write-Host 'E2E PASS: bootstrap, RBAC, class creation, teacher authorization, offline status, enrollment, receiver WebSocket, interruption, voice download/playback, ACKs, history, and device disable.' -ForegroundColor Green
}
finally {
    if ($receiverProcess -and -not $receiverProcess.HasExited) { Stop-Process -Id $receiverProcess.Id -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $configPath) { Remove-Item -LiteralPath $configPath -Force }
    $admin.Dispose(); $teacher.Dispose(); $anonymous.Dispose()
}
