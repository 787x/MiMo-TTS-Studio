# MiMo TTS Studio

独立的 fnOS 原生语音创作应用。管理员在浏览器编辑中文/英文脚本，选择 MiMo 预置音色、自然语言/导演指导、整体风格与句内标签，生成后试听、下载 WAV 或保存到私有音频库。

本项目不是小米官方产品。PR1 仅支持 `mimo-v2.5-tts`，不包含 VoiceDesign、VoiceClone、录音或 MP3。后端 ASP.NET Core / .NET 10，前端 Vue 3 / TypeScript；FPK 携带 Linux x86-64 自包含运行时，生产不需要 SDK、Node.js、Docker、数据库或 NAS 声卡。

## 构建

构建依赖：.NET 10 SDK、Node.js 24、npm、Python 3、Bash、官方 `fnpack 1.2.3`。从 [fnOS 开发者网站](https://developer.fnnas.com/docs/) 的开发工具页取得适合构建机的 fnpack；Linux x86-64 文件名为 `fnpack-1.2.3-linux-amd64`，赋予执行权限并加入 PATH。不要使用来源不明的同名工具。

Windows + WSL Ubuntu（Windows 已安装 Node.js，WSL 已安装 .NET、Python、fnpack）：

```powershell
./scripts/build.ps1
```

Linux x86-64（所有构建依赖在 Linux 中）：

```bash
bash scripts/build.sh
```

产物为 `artifacts/mimotts.fpk`，暂存目录为 `artifacts/fpk/`。构建会执行 Vue 类型检查与生产打包、`dotnet publish -c Release -r linux-x64 --self-contained true`、许可证收集、图标生成和 `fnpack build`，最后输出 SHA-256。Windows 包装脚本明确使用 WSL 发行版 `Ubuntu`，其他名称可自行修改。Linux 不要复用 Windows 安装的 `node_modules`，先在对应环境运行 `npm ci`。

实际构建环境及设备证据见 [verification/PR1.md](verification/PR1.md)。Manifest 的 `platform=x86` 与 .NET 的 `linux-x64` RID 含义不同。网关最低版本按当前文档声明 `1.1.3100`；本次实际测试系统为 `1.2.0800`，尚未在最小声明版本测试。

## 安装与使用

1. 登录 fnOS 管理员桌面 → 应用中心 → 手动安装 → 从电脑上传 `mimotts.fpk`，选择存储空间并完成安装。更新也通过应用中心上传新版本 FPK。
2. 从桌面的 **MiMo TTS Studio** 图标打开。入口是 `/app/mimotts/`，在 fnOS 同源 iframe 中运行。
3. 点击右上角设置，输入 MiMo API Key。只显示「Key 已配置」，支持更新和清除。请使用 HTTPS 或可信本地网络访问 fnOS；应用沿用系统网关的传输安全。
4. 编辑正文、音色与风格，可选择自然语言、导演模式或两者组合。标签工具在光标/选区前插入标签，保留选区文字、Unicode 文本元素及换行。
5. 点击「预览最终请求」核对 user 风格和每段 assistant 正文，再点击「生成语音」。在线生成可能计费；只有这一操作创建新的 TTS 任务，不会自动重试。
6. 完成后使用浏览器播放器播放/暂停，下载 WAV，或点击保存。试听、下载、保存均复用已生成文件。修改参数后会提示当前展示的是旧结果。

可用预置 Voice ID：`mimo_default`、`冰糖`、`茉莉`、`苏打`、`白桦`、`Mia`、`Chloe`、`Milo`、`Dean`。默认音色随 MiMo 集群变化。自然语言与导演内容进入可选 `user` 消息，正文及标签进入 `assistant`；请求为官方 `/v1/chat/completions` 的非流式 WAV 协议。整体标签按每段重复前缀，唱歌显式添加 `(唱歌)`。这些都在请求预览显示；不暗中添加语速或改写正文。参考 [MiMo 官方接口文档](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/audio/speech-synthesis-v2.5)。

## 数据、安全与边界

| 内容 | 位置 / 规则 |
| --- | --- |
| Key | `TRIM_PKGETC/mimo-key`，目录 0700、文件 0600；权限保护而非加密 |
| 音频与元数据 | `TRIM_PKGVAR/audio/{服务端GUID}/`，按可信 UID 隔离 |
| 任务 | 进程内，最多 100 条状态；重启后任务状态消失，成功资产仍可从音频库访问 |
| 临时资源 | `TRIM_PKGTMP/mimotts-work`；提交暂存目录位于音频目录内以保证同盘原子重命名 |
| Socket | `TRIM_APPDEST/app.sock`，0600；生产只监听该 Socket |
| 日志 | `TRIM_PKGVAR/service.log`，超过 1 MiB 时启动轮换；不记录请求、Key 或上游响应 |

后端同时检查 Socket 对端的 Linux `SO_PEERCRED`（系统 root 网关）及 `X-Trim-Userid/Username/Isadmin`。缺失、非法或非管理员身份拒绝 UI、静态资源和所有 API；任意其他本地用户即使伪造 Header 也不能通过。包用户自己也不能伪装成 root 网关。此假设已在测试系统确认；未来若平台改变网关用户，应用会拒绝访问，需要重新验证适配。root 管理员本来就有系统文件权限，不在此信任边界之外。

所有写请求要求同源 Origin 与不可预测 CSRF token。对 fnOS 去掉 Host 端口的转发行为，额外校验浏览器 Referer 的完整来源。不开 CORS；自定义网关或去掉 Referer 的浏览器策略可能导致写入被拒绝。生产 Release 编译不包含开发免认证开关。

上限：请求体 128 KiB，正文 60000 UTF-8 字节，user 指令 6000 字节，每段正文连同前缀最多 3000 UTF-8 字节；目标 50–3000 个 Unicode 文本元素，默认 3000，最多 80 段。这是保守工程预算，不是 MiMo 官方硬限制。关闭分段时超限直接报错。唱歌歌词尽量保持完整，分段会影响连续性。

全应用同时最多一项生成，单段 3 分钟、整任务 20 分钟；响应 JSON 最多 90 MiB，最终 WAV 最多 64 MiB。取消会传播到 HTTP 请求，并在确认后标为已取消；服务已接收的请求仍可能计费。失败、取消与不完整音频不提交成功资产。支持 PCM 8/16/24/32 位和 IEEE float32、单/双声道 WAV，规范化为单声道 PCM16；段间采样率不同则明确失败，不做未经验证的重采样。

私有音频数据配额 1 GiB、最多 1000 条资产，生成前要求磁盘保留 256 MiB。未保存结果在每次应用启动时清理超过 24 小时的条目；保存后持续保留至主动删除。应用重启后从音频库打开历史结果，不恢复编辑草稿。应用不会自动上传日志、文件、样本或浏览器草稿。

升级脚本不覆盖 Key 与音频。卸载向导默认保留数据：卸载前把 Key 备份至私有 `TRIM_PKGVAR/.retained-config`，防止平台删除 etc；重新安装时仅在没有新 Key 时恢复。选择「确认永久删除」仅删除带本应用归属标记的 `audio` 和 `.retained-config` 子目录，拒绝符号链接，不删除 TRIM 根目录、共享目录或用户文件。元数据、日志和运行锁等平台残余可由系统管理；本应用不扩大清理范围。

## 开发与验证

本地开发使用 Debug 构建，显式设置两项环境变量：

```bash
export ASPNETCORE_ENVIRONMENT=Development MIMO_DEV_LOCAL=1
dotnet run --project backend/Studio.csproj
```

服务仅绑定 `127.0.0.1:5080`，独立数据位于当前工作目录 `.dev/`。先将前端 `dist/` 复制到 `backend/wwwroot/`，或另开终端运行 `npm --prefix frontend run dev` 后访问 Vite 给出的本机地址 `/app/mimotts/`。使用后者时 Origin/Host 须由代理保留；完整网关行为以 Release Socket 测试为准。生产禁止把该模式当作认证替代。

```bash
dotnet run --project tests/Studio.Tests.csproj -c Release
python3 tests/lifecycle.py
sudo python3 tests/production_gateway.py artifacts/fpk/app/server/MiMoStudio
```

实机验收：先安装 FPK，然后把 `tests/fnos_acceptance.py` 复制到指定 fnOS 测试机，以 root 执行。它通过真实私有 Socket 检查权限、范围请求、文件复用与 appcenter 启停，仅创建并清理有明确标签的合成静音测试文件，不调用 MiMo、不更改已有 Key。完整的浏览器登录、桌面 iframe、非管理员会话和在线合成仍应另外验证。

运行时使用 invariant globalization，无 ICU 依赖。Debian 12 测试机已实际启动，并核验 glibc、libstdc++、libgcc、libm、libdl、pthread；真实 HTTPS 合成验证了系统 TLS/证书环境。其他 fnOS 版本与发行版需重新验收，不把 WSL、Mock 或打包成功等同于实机安装。

## 来源与许可

本仓库源代码为独立实现，MIT 授权。只读查阅了 SeleVox 的 `docs/MiMoTTS.md`，没有复制其 GPL 源代码、资源、密钥或用户文件，也没有构建依赖。协议以小米官方文档为依据；FPK 结构依据官方 fnpack 模板、[fnOS 网关文档](https://developer.fnnas.com/docs/core-concepts/gateway-registration/) 及其 [公开文档镜像](https://github.com/ckcoding/fnnas-docs)。打包时收集确切 .NET 运行时和前端依赖的第三方许可到 `server/licenses/`；不要移除后再分发。
