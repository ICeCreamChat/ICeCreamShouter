$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

function Read-Endpoint {
    $savedPath = Join-Path (Get-Location) 'dist\delivery\cloud-url.txt'
    if (Test-Path -LiteralPath $savedPath) {
        $saved = (Get-Content -LiteralPath $savedPath -Raw).Trim()
        if ($saved) { return $saved.TrimEnd('/') }
    }

    $entered = Read-Host '请输入部署完成后显示的系统网址（例如 https://icecreamchat.top）'
    $entered = $entered.Trim().TrimEnd('/')
    $parsed = $null
    if (-not [Uri]::TryCreate($entered, [UriKind]::Absolute, [ref]$parsed) -or $parsed.Scheme -ne [Uri]::UriSchemeHttps) {
        throw '网址无效。请使用部署完成后显示的 https:// 正式地址。'
    }
    return $entered
}

$receiver = Join-Path (Get-Location) 'dist\receiver\ICeCreamShouter.exe'
if (-not (Test-Path -LiteralPath $receiver)) {
    throw '找不到 dist\receiver\ICeCreamShouter.exe。请先在管理员电脑运行 build.bat。'
}

$endpoint = Read-Endpoint
$receiverVersion = ''
$receiverVersionMatch = Select-String -LiteralPath (Join-Path (Get-Location) 'Receiver\Receiver.csproj') -Pattern '<Version>([^<]+)</Version>'
if ($receiverVersionMatch) { $receiverVersion = $receiverVersionMatch.Matches[0].Groups[1].Value }
$delivery = Join-Path (Get-Location) 'dist\delivery'
$classroom = Join-Path $delivery '教室端'
New-Item -ItemType Directory -Force -Path $classroom | Out-Null
Remove-Item -LiteralPath (Join-Path $classroom 'CloudRemoteShouter.exe') -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath $receiver -Destination (Join-Path $classroom 'ICeCreamShouter.exe') -Force
Set-Content -LiteralPath (Join-Path $delivery 'cloud-url.txt') -Value $endpoint -Encoding utf8
Set-Content -LiteralPath (Join-Path $classroom 'cloud-url.txt') -Value $endpoint -Encoding utf8

$shortcut = @"
[InternetShortcut]
URL=$endpoint
IconFile=$endpoint/favicon.svg
IconIndex=0
"@
Set-Content -LiteralPath (Join-Path $delivery '教师端.url') -Value $shortcut -Encoding ascii

$classroomReadme = @'
教室电脑一步使用

1. 把这个文件夹复制到教室电脑的固定位置，例如 D:\ICeCreamShouter。
2. 双击 ICeCreamShouter.exe。
3. 在 ICe 管理网页的“班级管理”中生成该班的一次性绑定码，并在窗口中输入。
4. 点击“绑定并开始接收”。以后电脑开机后会自动在后台接收喊话。

每个绑定码只能使用一次，15 分钟后失效。不要把 config.json 或绑定码发到公开群聊。

以后更新

管理员更新服务器后，教室端会在后台自动检查并安装新版。也可以右键系统托盘图标，点击“检查更新”，或在设置窗口点击同名按钮。
更新不会删除本机 config.json、班级绑定或开机自启动设置。更新过程中程序会自动重启，通常不需要重新绑定。

通知声音

普通通知只在右下角显示，不播放声音。重要通知显示顶部横幅并播放声音，紧急通知全屏显示并播放声音。
'@
Set-Content -LiteralPath (Join-Path $classroom '一步使用说明.txt') -Value $classroomReadme -Encoding utf8

$teacherReadme = @'
教师端一步使用

双击同目录的“教师端.url”，在浏览器中输入管理员分配的账号和密码即可使用。
手机可在浏览器中打开同一网址，再选择“添加到主屏幕”。

普通通知只显示静音弹窗；重要和紧急通知会播放声音。语音喊话只能使用重要或紧急。
禁言上课时间内，网页会在发送前提醒；取消后教室端什么都不会出现，确认“仍然发送”后才会正常送达。
'@
Set-Content -LiteralPath (Join-Path $delivery '教师端使用说明.txt') -Value $teacherReadme -Encoding utf8

$adminReadme = @'
ICeCream Shouter 交付包

教室端：把“教室端”文件夹复制到每间教室电脑，双击 EXE，只需输入一次管理员生成的绑定码。
教师端：双击“教师端.url”，登录后即可喊话。

管理员保留本文件夹，不要公开发送部署初始化密钥、账号密码或教室端 config.json。
最高管理员 ICe 可在网页“禁言时间”中维护全校公共时间表，教师只能查看。
'@
Set-Content -LiteralPath (Join-Path $delivery '交付说明.txt') -Value $adminReadme -Encoding utf8

Write-Host ''
Write-Host '交付包已生成：' -ForegroundColor Green
Write-Host "  $delivery"
if ($receiverVersion) { Write-Host "已包含教室端版本：$receiverVersion" }
Write-Host '教室电脑只需复制“教室端”文件夹并双击 EXE。'
Write-Host '教师只需双击“教师端.url”并登录。'
