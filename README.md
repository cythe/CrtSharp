# CrtSharp

Windows 上的多标签 SSH、Telnet 和串口终端客户端，使用 WPF、WebView2、xterm.js 与 SSH.NET。

## 当前功能

- 多标签独立连接
- xterm.js 终端渲染，支持 ANSI/VT/xterm 控制序列和全屏程序
- SSH 密码认证和私钥认证（支持有口令私钥；只保存路径，不保存口令）
- SSH 主机密钥首次确认、持久化及变更阻断
- Telnet 基础选项协商、终端类型和窗口大小协商
- 串口完整参数：波特率、数据位、校验位、停止位、软/硬件流控、DTR 和 RTS
- 本地会话收藏支持编辑、删除和右键菜单；SSH 密码可选用 Windows DPAPI 加密保存
- 会话管理器支持拖动排序，顺序随会话列表一起保存
- 已打开的 SSH/Telnet 标签支持右键复制，在新标签建立相同的独立连接；串口标签禁止复制
- 手动会话日志：按标签开始/停止记录、断线重连继续记录、关闭标签自动收尾
- 日志仅记录终端输出，不记录键盘输入，保存在 `%APPDATA%\\CrtSharp\\Logs`
- 每个会话可配置终端滚屏行数、字体、字号、主题颜色、光标样式和闪烁；外观预览内容可编辑并保存
- 标签增强：高对比选中状态、重命名、整标签拖动排序、连接状态颜色、关闭活动会话确认
- 标签拖出标签栏可创建独立新窗口，并无损迁移现有连接、滚屏缓存和日志
- 标签可跨窗口拖动：拖到其他 CrtSharp 窗口的标签栏按鼠标位置插入，拖走源窗口最后一个标签后源窗口自动关闭回收
- 配置导入/导出：“文件”菜单可导出/导入用户配置（会话、主机指纹、记住的密码、私钥文件），导出文件整体加密（AES-256-GCM + PBKDF2-SHA256 21 万次迭代），导入时需输入密码，按会话 Id 去重合并
- 当前标签滚屏缓存搜索：Ctrl+Shift+F、增量查找、上一个/下一个、匹配计数和大小写选项
- SSH/Telnet/串口断开后可在原标签按 Enter 重连，保留终端滚屏缓存
- 连接成功和切换标签时自动聚焦终端输入
- Ctrl+Shift+C 复制、Ctrl+Shift+V 粘贴；有选区时 Ctrl+C 或右键也可复制
- “终端”菜单提供全选、复制、粘贴，便于提取完整日志

## 构建与运行

```powershell
cd CrtSharp
npm ci
dotnet run
```

发布：

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

会话配置保存在 `%APPDATA%\CrtSharp\sessions.json`，SSH 主机指纹保存在 `%APPDATA%\CrtSharp\known-hosts.json`，配置导入恢复的私钥保存在 `%APPDATA%\CrtSharp\Keys`。

本项目面向 net10.0-windows，需要 .NET 10 SDK 构建、.NET 10 Runtime（含 WindowsDesktop）运行。

## 暂未实现

- SSH Agent/键盘交互认证
- 会话文件夹、代理跳板、端口转发
- 脚本

## 第三方组件

- xterm.js / @xterm/addon-fit：MIT License
- Microsoft WebView2 SDK
- SSH.NET：MIT License
