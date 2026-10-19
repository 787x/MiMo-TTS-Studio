# PR2 验证记录

日期：2026-10-19（Asia/Shanghai）。基线为最新 `main` 的 PR1 合并提交 `0d118bb`，保留 PR1 的真实设备、云端和文件证据。开发分支 `codex/pr2-voice-library`。

## 交付与边界

- 在现有 Composition/Mimo/Jobs/Storage 上扩展三模式，普通请求缺省仍是预置模式，旧资产缺省仍按预置结果读取。
- VoiceDesign：必填描述进入 user、播报原文进入 assistant，单次短试音，不带 voice，显式关闭 `audio.optimize_text_preview`。本版拒绝开启优化；无唱歌或整体标签专属控件，不将多次随机设计伪装成固定音色。
- VoiceClone：后端从所属用户的样本 ID 取得完整快照，构造正确 MIME 的 Data URL；后续改名/删除不会改变运行中任务。原有分段、取消、响应上限、WAVE 校验及原子提交共用。
- 持久样本库：独立副本、命名/改名/试听/选择/删除、来源及音频实际元数据。生成结果和样本共用 1 GiB / 1000 条限额。默认卸载保留，确认删除只清理归属标记下的已知私有子目录，并拒绝 symlink。
- 普通 API 仍为 128 KiB。只有样本上传路由放宽为 7,400,000 字节，先鉴权/Origin/CSRF，再有界流读取与实际格式校验；最多两个样本处理、30 秒超时。
- MP3 使用随包分发的 NLayer 3.0.0（MIT），检查完整连续 Layer III 帧并逐帧解码，检查有 CRC 的帧，不依赖 NAS 上的 ffmpeg/NAudio 或 Windows 音频组件。许可见 `third_party/NLayer.LICENSE`。
- 三模式各自保留编辑草稿与结果槽，结果展示实际模式和参数；修复组件卸载后 in-flight finally 再安排轮询的问题。

## 已执行的构建与受控测试

| 层级 | 命令 / 实际结果 |
| --- | --- |
| 前端 | `npm --prefix frontend run build`：vue-tsc 与 Vite 8.3.4 生产构建成功 |
| .NET | WSL Ubuntu SDK 10.0.112，Release 与 linux-x64 自包含 publish 成功，0 构建警告 |
| 受控协议/文件/任务 | `dotnet run --project tests/Studio.Tests.csproj -c Release`：**99 项检查通过**（保留 PR1 的 49 项） |
| Release 接入 | `sudo python3 tests/production_gateway.py artifacts/fpk/app/server/MiMoStudio`：**37 项检查通过** |
| 生命周期 | `python3 tests/lifecycle.py`：**6 项检查通过** |
| 前端定时器 | `node --test frontend/tests/polling.test.mjs`：**2 项通过**，包含请求未完成时卸载、空闲 timer 卸载 |
| 依赖审计 | npm audit：0 vulnerabilities；`dotnet list ... package --vulnerable --include-transitive`：当前源未发现易受攻击依赖 |
| FPK | fnpack 1.2.3 实际打包成功；`python tests/package.py` 校验架构、网关、脚本权限、ELF/自包含、NLayer 与依赖许可、无 Key/半成品/Windows 项目依赖 |

99 项检查验证三模型 JSON 差异、显式不优化/不改正文、不支持控制字段拒绝、WAV 与 MP3 MIME/时长、空/超限/截断/坏帧/CRC/伪装格式、MP3 MPEG2 stereo VBR + ID3/Xing、Data URL 大小预算、PR1 无新字段的 JSON、源资产删除与重启后独立样本可用、样本归属、改名/删除与运行中多段任务的快照竞争、取消/失败零成功资产、样本共享磁盘配额和参数摘要无 Key/Data URL。

37 项接入检查运行真正 Release 二进制，包用户监听真实 Unix Socket，root 测试客户端模拟可信网关。验证普通 JSON 128 KiB 限制、192044-byte 合法 WAV 上传、chunked MP3 上传、错误格式/空/超限上传、样本 CRUD 与归属、下载复用、Origin/CSRF、路径穿越、无 Header/非管理员/非 root 对端拒绝、Release 无法打开开发免认证开关。没有接入真实云端，不以此替代设备会话或云端合成。

生命周期检查包含新增 samples 的默认保留、删除分支和 symlink 拒绝；删除不影响无关目录。它是临时目录测试，尚不能等同于 fnOS GUI 卸载选择实测。

## 浏览器本地验证

在仅绑定 localhost 的显式 Debug 开发服务上，通过真实浏览器操作验证：

- 预置模式输入 Unicode/换行与风格，切到 VoiceDesign 输入独立描述和正文，再切到 Clone 编辑新正文；回切后前两种草稿保持原值。
- Design 不展示预置音色、唱歌或整体标签控件；Clone 不展示唱歌，明确显示参考音频将随生成上传云端。
- 使用原创正弦波 MP3 通过真实文件选择器上传，样本显示 MP3、1.04 秒、实际大小，并自动选择到 Clone；现有 Clone 草稿未覆盖。
- 未配置开发 Key，生成保持禁用；上传/选择/预览没有触发云端合成。

这部分是本地浏览器 UI 证据，不是 fnOS 或真实 MiMo 的完成证据。

![本地三模式和样本 UI 验证](PR2-local-workspace.png)

## 真实 fnOS / MiMo 状态

测试机仍运行 PR1 `0.1.3`。已记录升级前 Key 的私有哈希及既有 WAV 哈希（未打印 Key 或 Key 哈希），并将 PR2 FPK 上传应用中心。系统弹出第三方应用声明，包含使用者承担设备损坏/数据丢失风险的条款，已请求用户确认；**尚未接受声明或执行原位升级**。

因此，此时尚未把以下项目记为通过：PR1 → PR2 原位升级、真实设备的新增样本操作、真实 VoiceDesign/VoiceClone/Design → Clone 在线流程。`tests/fnos_pr2.py` 已准备用于指定设备，验证升级版本/Key/WAV 哈希、真实托管 MP3 解码、样本 API、普通与上传 Body 上限、重启保留；它不会更改 Key 或发起云端请求。

## 当前产物

FPK **0.2.0**，Linux x86-64，自包含；Manifest `platform=x86`。

路径 `artifacts/mimotts.fpk`，SHA-256：`24249c06c2be61917c3881f7592e497919e988bed9e07a5565a6e0a16fe5c803`。二进制不提交 Git，可按 README 重建。

另验证了发布明确排除本地 `.dev/` 状态与临时 `backend/wwwroot` 预览内容；在存在本地样本后重新 publish 的服务 DLL 与包内 DLL SHA-256 一致，包内无样本元数据或凭据。

## 尚未验证 / 局限

- 应用中心第三方条款确认及其后的原位升级/真实云端流程仍待用户确认后执行，不能将上传 FPK 当作已完成安装。
- 独立非管理员 fnOS 登录会话、GUI 卸载确认删除、最小声明系统版本、外部 HTTPS/FN Connect 接入仍未实测。
- 无 CRC 的 MP3 中，无法保证检出仍可合法解码的所有位变化；free-format、改变采样率/声道或不识别尾部数据会拒绝。按帧计算的时长包含编码延迟/填充。
- 极限大小/持续并发的 CPU/RAM 压测、NAS 断电及磁盘耗尽未实测；已有请求、时长、并发、响应和容量保护。
