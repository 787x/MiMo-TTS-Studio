# MiMo-TTS-Studio

## Project Goal

MiMo-TTS-Studio 是独立于 Windows SeleVox 的飞牛 fnOS 原生应用。面向 NAS 浏览器提供 MiMo 语音创作，包括预置音色、风格与句内标签、导演模式、长文本分段、音频生成、试听和文件管理；后续逐步加入 VoiceDesign、VoiceClone 等能力。

每个 PR 以自己的任务描述为实现范围；本文件描述长期有效的工程与安全边界，不要求提前实现未来功能。首要发布目标是可在 fnOS Linux x86-64 上实际安装运行的原生 `.fpk`，不是 Docker、Windows 桌面程序或单纯 Web Demo。

## Reference and Independence

- Windows 原项目（若开发环境可访问）：`D:\Projects\SeleVox`。**只读参考**其 MiMo API、SSE、分段、Unicode/标签规则、音频校验、创作 UI 和测试。不要修改、格式化、提交或运行会改写该目录的命令。
- 新项目独立维护源代码、配置、Git 历史、依赖、测试、版本与 FPK 产物；不能通过硬编码原项目路径、项目引用、符号链接或子模块才能构建。
- 按需参考原有 `AGENTS.md`、`docs/MiMoTTS.md`、相关代码和验证记录，优先理解已验证行为，不盲目复制依赖 Windows 的实现。
- 若复制/派生原仓库源代码，核实其许可证及第三方依赖授权，保留必要版权和许可信息；不能因为是本地自用就忽略再分发义务。禁止复制 API Key、用户数据或真实克隆样本。

## Technology and Platform

- 主要目标：fnOS 原生应用规范、官方 `fnpack`、Linux x86-64；按 `fnpack create` 模板和当前平台文档确定 Manifest、权限、资源和生命周期文件，不能虚构或照搬已过时字段。
- 建议使用 .NET 10 ASP.NET Core Web API + Vue 3/TypeScript 前端；可采用仓库已确立的等效技术，不因建议而做无谓重构。Linux 发布产物应自包含，并验证目标 fnOS 所需的系统库。
- 默认通过 fnOS 统一网关和仅本机可访问的 Unix Socket 提供 Web UI/API；稳定前缀、桌面入口和 Socket 名称要一致。不得默默改成对 LAN/公网开放的无认证端口。
- 尊重 `TRIM_APPDEST`、`TRIM_PKGETC`、`TRIM_PKGVAR`、`TRIM_PKGTMP` 等实际支持的 fnOS 路径变量；开发模式可显式指定独立路径，但发布时不能写死 `/vol1`、开发机路径或用户 Home。
- 不引入 Docker、root 常驻服务、独立数据库、队列服务或 Node.js 生产时常驻进程，除非某个 PR 明确要求且有必要。

## Security and Privacy Invariants

1. **服务端持有凭据。** MiMo API Key 只能由后端管理，保存在应用私有配置目录并收紧权限；不写入前端、浏览器存储、日志、下载文件或仓库。设置界面只展示是否配置，支持更新与清除。
5. **数据生命周期清楚。** 私有配置、生成结果和临时文件分离。只在成功校验后提交生成结果；取消和失败不产生“成功”文件；升级不丢数据，卸载清理仅针对本应用有确认归属的目录和文件。

## Functional and Engineering Boundaries

- 合成与播放/下载分离：TTS 返回音频数据；浏览器试听/保存重用该成功结果，不重复合成。NAS 的浏览器试听不依赖 NAS 声卡。
- 预置音色、VoiceDesign、VoiceClone 是不同的 MiMo 请求模式；对适用参数和流式能力按实际官方协议处理，不把某个模型的能力硬套给另外两个模型。
- 保留用户输入原文与 Unicode 文本元素边界。整体风格标签、句内标签、导演模式的最终请求内容必须可核对；不隐式改写朗读文本或加入标签。
- 长文本以可配置分段为目标，同时遵守后端 UTF-8 字节、请求和响应体限制；合并 WAVE 时不能简单连接多个文件头。浏览器可先使用完整 WAV 结果，不强制首版实时 PCM 播放。
- 长任务应支持取消、有限并发与状态查询；旧任务、旧网络回调不得覆盖新任务结果。不要把 HTTP 请求关闭或 UI 状态改变当作真实取消完成。
- 优先少量清晰的模块；不为了未来功能预设通用插件平台、微服务、数据库或大型抽象层。可以按实际需要从 SeleVox 提取纯跨平台代码，但不把 WPF、WinRT、WASAPI、Windows Credential Manager 或本地热键引入 Linux 后端。

## Verification and Delivery

- 验证力度与本 PR 风险相称：执行相关构建、协议/文件/权限测试与 FPK 打包检查，并尽可能在真实 fnOS 设备完成安装、启动、状态、停止、重启和 Web 访问验证。
- **不得混淆证据：** Linux/WSL 运行、`fnpack build` 成功、模拟 MiMo HTTP、真实 MiMo 合成和真实 fnOS 安装属于不同层级。没有设备、Key 或工具时注明未验证，不得报成实测通过。
- 优先验证核心正反例：成功生成合法 WAV；试听/下载不重新计费；未授权访问被拒；Key 不泄漏；错误/取消不遗留半成品；网关子路径下 UI/API 正常；升级/卸载不误删数据。
- 每个 PR 最终只需简洁报告：实际修改、实际执行的验证结果、被环境阻塞或仍有风险的事项。遵循仓库现有 README、测试和适用的 Skills 文档，不新增无价值模板。
