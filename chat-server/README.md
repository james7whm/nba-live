# 聊天室服务：本机预览

此目录目前只用于本机联调，不是可直接对公网开放的正式服务。程序默认只监听 `127.0.0.1`，不修改已有网站或系统配置。

在仓库根目录运行：

```powershell
python chat-server/server.py --database "$env:TEMP\nba-live-chat-preview.sqlite3" --port 8765
```

在另一个 PowerShell 窗口中，以预览配置启动桌面端：

```powershell
$env:NBALIVE_CHAT_ENDPOINT = 'http://127.0.0.1:8765/'
.\.tools\dotnet\dotnet.exe run --project src/NbaLive/NbaLive.csproj -c Release -f net10.0-windows -- --demo
```

客户端仅允许本机回环地址使用 HTTP；公网连接需要有效的 HTTPS。当前使用临时身份，重启后重新创建，聊天消息保存在服务端数据库中。

测试：

```powershell
python -m unittest discover -s chat-server -p test_server.py -v
.\.tools\dotnet\dotnet.exe run --project tests-chat/NbaLive.ChatTests.csproj -c Release
```

正式部署前还需要完成服务限流、消息管理、存储策略、HTTPS 和运维验证。不要把预览服务直接映射到公网。
