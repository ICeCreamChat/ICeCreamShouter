# ICeCream Shouter

ICeCream Shouter 是面向学校使用的云端远程喊话系统。教师用手机或电脑浏览器登录，在校内外都能向指定班级发送文字或教师原声；教室里的 Windows 电脑按“普通、重要、紧急”显示不同提醒。正式环境推荐部署到国内腾讯云服务器，Cloudflare 版本保留为可选方案。

本项目由 ICeCreamChat 维护，是基于 [OpenRemoteShouter](https://github.com/YU322142/OpenRemoteShouter) 产品构想进行大幅重构和二次开发的云端版本。云端架构、账号与权限、服务器中继、数据库、自动更新、教师网页和 Windows 教室端均围绕学校实际使用场景重新设计。第三方来源与许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 已实现功能

- 最高管理员固定账号为 `ICe`，仅 `ICe` 能创建、改名、停用班级。
- 支持单个创建班级，也可一键创建“高一（1）班”至“高一（20）班”等连续班级。
- `ICe` 可创建不限人数的教师账号，并给每位教师分配一个或多个班级。
- 教师只能查看和操作自己获授权的班级。
- 教师端是响应式网页，支持手机和电脑、快捷短语、发送确认、在线状态和震动反馈。
- `ICe` 可维护全校公共禁言时间表；时段内发送前会醒目提醒，教师可取消或确认后继续发送。
- 普通通知只在右下角静音显示；重要通知显示顶部横幅并播放声音；紧急通知全屏显示并播放声音。
- 教室端支持 Windows 10/11、系统托盘、开机启动、断网指数退避重连和单实例运行。
- 新喊话会立即关闭上一条弹窗，并停止上一条文字 TTS 或教师原声后播放最新内容。
- 教师端支持按住录音、试听和重录；教室端播放原声，播放完成后服务端自动删除临时音频，历史记录只保留元数据。
- 语音喊话只允许选择“重要”或“紧急”，避免把普通静音通知误用为语音消息。
- 状态区分教室离线、已发送、已收到、已展示和处理失败。
- 保存喊话历史，支持按班级筛选和 CSV 导出；默认不自动删除。
- 教室绑定使用 15 分钟一次性绑定码，设备令牌使用 Windows DPAPI 加密后保存在 EXE 同目录的 `config.json`。
- `ICe` 可查看、停用和重新启用已绑定的教室设备；停用会立即断开设备。
- 所有账号可修改自己的密码，管理员可重置教师密码。

## 系统组成

- `server/`：腾讯云国内版 Python、SQLite 和 WebSocket 服务端。
- `cloud/`：可选的 Cloudflare Workers、D1 和 Durable Objects 云端服务。
- `web_controller/index.html`：与云端同域部署的单文件教师/管理网页。
- `Receiver/`：Avalonia/.NET 8 编写的 Windows 教室接收端。
- `deploy-tencent-server.bat`：腾讯云 Ubuntu 服务器一键部署入口。
- `deploy-cloud.bat`：Windows 云端部署入口。
- `build.bat`：Windows 单文件 EXE 构建入口。
- `prepare-delivery.bat`：生成管理员交付包，预置云端地址和教师端快捷方式。
- `prepare-admin-package.bat`：一键构建 EXE 并生成完整交付包。
- `docs/DEPLOYMENT_ZH.md`：面向第一次部署者的完整中文操作手册。
- `docs/完整操作流程.md`：从零开始的中文操作步骤，适合管理员和第一次使用者。

## 最短使用路径

1. 按 [完整操作流程](docs/完整操作流程.md) 把腾讯云服务器重装为 Ubuntu Server 24.04 LTS。
2. 双击 `deploy-tencent-server.bat`，自动安装服务器、HTTPS、WebSocket、数据库并生成交付包。
3. 用 `ICe` 登录，创建班级、教师账号并分配班级。
4. 把 `dist\delivery\教室端` 文件夹复制到每间教室电脑，双击 EXE 后只输入一次绑定码。
5. 教师双击 `dist\delivery\教师端.url`，登录后选择班级发送喊话。

学校端不需要安装 Node.js、.NET、Python，也不需要运行任何 `.bat` 或命令行脚本。`build.bat`、`deploy-cloud.bat`、`prepare-delivery.bat` 和 `prepare-admin-package.bat` 只由管理员在准备系统时使用。

## 本地检查

```powershell
npm install
npm run typecheck
dotnet build Receiver\Receiver.csproj -c Release
```

接收端改为 C#/.NET 是为了直接使用 Windows 原生长连接和 SAPI，提升托盘、开机启动及语音中断的稳定性，因此本云端版本不需要 Python，也没有 `requirements.txt`。

## 许可与说明

ICeCreamChat 对本项目新增和修改的代码采用仓库根目录的 MIT 许可证。项目所包含或衍生自第三方开源项目的部分，继续遵循 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 中列出的原始许可。腾讯云国内版的喊话内容及账号数据只存储在你自己的腾讯云服务器中；Cloudflare 可选版的数据存储在你自己的 Cloudflare 账户中。
