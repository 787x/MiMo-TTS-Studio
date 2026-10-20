# MiMo TTS Studio

独立的 fnOS 原生语音创作应用。管理员在浏览器编辑中文/英文脚本，使用预置音色、VoiceDesign 或 VoiceClone，配置自然语言/导演指导及适用标签，生成后试听、下载 WAV 或保存到私有音频库。VoiceDesign 的成功结果可以独立保存成持久克隆样本。

本项目不是小米官方产品。不包含录音、云端训练、音色市场或 MP3 导出；MP3 仅可作为上传参考样本。后端 ASP.NET Core / .NET 10，前端 Vue 3 / TypeScript；FPK 携带 Linux x86-64 自包含运行时与纯托管 MP3 解码器，生产不需要 SDK、Node.js、ffmpeg、Docker、数据库或 NAS 声卡。

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

实际构建环境及设备证据见 [PR1](verification/PR1.md) 和 [PR2](verification/PR2.md) 验证记录。Manifest 的 `platform=x86` 与 .NET 的 `linux-x64` RID 含义不同。网关最低版本按当前文档声明 `1.1.3100`；实际测试系统为 `1.2.0800`，尚未在最小声明版本测试。

## 安装与使用

1. 登录 fnOS 管理员桌面 → 应用中心 → 手动安装 → 从电脑上传 `mimotts.fpk`，选择存储空间并完成安装。更新也通过应用中心上传新版本 FPK。
2. 从桌面的 **MiMo TTS Studio** 图标打开。入口是 `/app/mimotts/`，在 fnOS 同源 iframe 中运行。
3. 点击右上角设置，输入 MiMo API Key。只显示「Key 已配置」，支持更新和清除。请使用 HTTPS 或可信本地网络访问 fnOS；应用沿用系统网关的传输安全。
4. 编辑正文、音色与风格，可选择自然语言、导演模式或两者组合。标签工具在光标/选区前插入标签，保留选区文字、Unicode 文本元素及换行。
5. 点击「预览最终请求」核对 user 风格和每段 assistant 正文，再点击「生成语音」。在线生成可能计费；只有这一操作创建新的 TTS 任务，不会自动重试。
6. 完成后使用浏览器播放器播放/暂停，下载 WAV、保存到私有音频库，或导出到已授权的 NAS 文件夹。这些操作均复用已生成文件。修改参数后会提示当前展示的是旧结果。

## 整体风格选择器（0.6.2）

在原「整体风格标签」输入框下可展开「浏览预置 / 添加自定义风格」，按名称或类别搜索 8 类、48 个选项。点击添加、再次点击取消；已选标签可逐项移除、清空，按选择先后组合。无匹配结果时仍可输入自定义名称，按 Enter（输入法组词期间不提交）或点击「添加风格」。选择器和原自由输入共用现有 `Tags` 字符串，不新增服务或持久预设库。

选择「温柔」「清亮」再添加「上海话」后，输入框为 `温柔 清亮 上海话`，旁边实时显示 `(温柔 清亮 上海话)`。每段 assistant 使用同一前缀，计入原有 3000 UTF-8 字节预算；不写入 user 或改写播报正文及句内标签。选择动作只调用 NAS 本机预览接口做 Composition 预算检查，不调用 MiMo、不创建 Job。预览、生成 Job 和资产参数摘要沿用同一后端前缀逻辑。修改实际参数后，旧音频按原快照机制显示为旧结果。

自定义名称为 1–64 个 UTF-16 字符（如 Emoji 通常占两个），选择器最多 16 项；组合字符串仍最多 200 个 UTF-16 字符。拒绝首尾空白、括号、换行、控制字符、隐藏格式控制字符及不成对代理项；允许内部普通空格及合法 Unicode。名称精确区分大小写，不做 Unicode 规范化：`News`/`news`、`é`/`é` 是不同名称。超长或增加前缀会使分段预算超限时，添加失败且原有选择不变。后端同样对自由 Tags 实施安全字符和组合长度检查，不使用预置白名单。

自由输入保留全部原文，不按空格拆成名称。旧值或手工改写的完整值显示为「手动文本（整体保留）」；用户改写上方输入框会清除结构化已选标记，将当前完整字符串转成手动文本，并提示转换。之后的新选择追加在其后；移除只删除选择器追加的完整名称。手动部分中的重名片段不重复追加，需回到完整输入框编辑。含内部空格的自定义名不会因切换模式而拆散。手动文本仍按既有 200 字符规则保留，不被新选择器的每项 64/16 项限制强制重解析。

预置音色和 VoiceClone 各保留自己的标签草稿及选择顺序；VoiceDesign 不显示选择器、不发送 Tags。特殊预设「唱歌」与自定义的 `唱歌`/`sing`/`singing`（英文不区分大小写）只切换现有预置音色唱歌状态，不进入 Tags；VoiceClone 禁用并拒绝这些别名。自由输入 Tags 中的独立唱歌别名会明确报错，提示改用开关。唱歌和普通风格原有组合 `(唱歌)(清亮)` 保持可用，不引入重复前缀。

分类只是浏览辅助，不保证每个音色对每个风格有相同效果；最终音频以实际 MiMo 返回为准。自定义名称、搜索和编辑草稿不持久保存。浅色/深色沿用既有变量，预置区独立滚动，不调整工作台字体、列比例或播放器。实际构建、fnOS 升级、浏览器及在线验证见 [PR6](verification/PR6.md)。

## 导出到 NAS（0.5.1）

当前结果和私有音频库的每条 WAV 均提供「导出到 NAS」。在确认面板中选择服务端列出的已授权可写目录、核对或修改文件名，再点击「确认导出 WAV 到 NAS」。默认名称只有时间戳和随机标识；同名文件明确报错，绝不覆盖。成功后显示目标目录、文件名和字节数，再次导出会生成新默认名称。浏览器「下载」保存到电脑，NAS「导出」复制到所选 NAS 目录。

在支持的 fnOS 宿主内，「选择并授权文件夹」使用官方 `@trimjs/web-app` **0.5.0** 的 `pickSharedFile`；「前往 fnOS 应用设置授权目录」使用 `openAppSetting`。也可手动进入「系统设置 → 应用 → MiMo TTS Studio → 访问权限」（应用中心的应用设置也可到达），添加已有目录并设置为「读写」，返回后刷新。授权使用平台原生 ACL；不创建 `data-share`，不添加自定义用户组，不改目标目录权限。若平台要求重启，请在应用中心停止、启动后重新打开面板。

NAS 导出需要官方目录开放 API（fnOS **1.2.0401+**）；实测 **1.2.0800**。保留原安装最低版本 `1.1.3100`，较旧系统或缺少宿主桥接时显示手动授权步骤；缺少后端开放 API 时导出明确不可用，既有创作/私有音频功能不依赖它。没有使用启动时 `TRIM_DATA_ACCESSIBLE_PATHS` 作为永久权限：后端通过系统 Unix Socket、服务端 `TRIM_API_TOKEN` 实时查询 `trim.file.getSharedAccessibleFolders`，并按可信网关 UID 调用 `trim.file.checkUserACL`，同时检验包用户实际写入能力。`config/resource` 仅新增 `trim.file.sharedAccess` 与 `trim.file.userAcl`，Manifest 开启目录授权及微应用桥接。

仅复制当前用户的完整私有 WAV，不创建 TTS Job，不调用 MiMo，不改变 Saved、源资产或克隆样本。目录 ID 在重启后失效，需要刷新。所有接口沿用管理员、可信网关与 Origin/CSRF 校验；不接受客户端绝对路径或子目录。目录逐级用 `openat` / `O_NOFOLLOW` 打开，父目录使用 `O_PATH`，提交前重新查询授权并核对目录设备号/inode。应用无法通过软链接或相邻路径混入其他目录。

复制最多 64 MiB、单槽不排队、总预算两分钟、64 KiB 有界缓冲；私有源与目标可在不同文件系统。目标使用 Linux `O_TMPFILE` 匿名暂存，长度一致并同步后以 `linkat` 原子发布完整 WAV；真实 EEXIST 保证不覆盖。取消、进程退出或复制失败会由内核回收未发布临时文件，无需扫描/清理用户目录。不支持这些语义的文件系统明确失败。如果完整文件已提交但后续目录同步失败，界面提示先核实目标文件，避免重复导出；不会尝试删除已提交文件。平台授权查询和文件提交不是跨系统事务，已完成的导出不受随后撤权影响。

导出结果归用户目录管理；撤权、删除私有源资产、停止或卸载应用均不会清理它们。升级不迁移历史数据。验证及平台限制见 [PR5](verification/PR5.md)。

可用预置 Voice ID：`mimo_default`、`冰糖`、`茉莉`、`苏打`、`白桦`、`Mia`、`Chloe`、`Milo`、`Dean`。默认音色随 MiMo 集群变化。自然语言与导演内容进入可选 `user` 消息，正文及标签进入 `assistant`；请求为官方 `/v1/chat/completions` 的非流式 WAV 协议。整体标签按每段重复前缀，唱歌显式添加 `(唱歌)`。这些都在请求预览显示；不暗中添加语速或改写正文。参考 [MiMo 官方接口文档](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/audio/speech-synthesis-v2.5)。

## 服务检测与主题（0.4.3）

右上角设置中的「服务检测 · MiMo / LLM」分别展示预置音色、VoiceDesign、VoiceClone 和 LLM。打开面板只进行本机配置预检查；「配置就绪」不代表服务可用。点击对应的「服务检测」才向该服务发送一次固定短文本，不读取工作台稿件，不重试或更换服务。

「获取 / 管理 API Key」左侧及独立 LLM Key 输入行右侧提供「检测服务」快捷按钮，与面板共用结果。按钮默认普通配色，成功后显示绿色「检测成功」、失败后显示红色「检测失败」；配置更改后恢复普通配色。检测期间禁用重复点击，面板仍提供取消入口。

预置检测固定使用「冰糖」；VoiceDesign 使用固定短音色描述。VoiceClone 默认不选择样本，只有明确选择自己的已有样本并点击检测才上传该参考音频。三种模型复用生产请求与完整 WAV/PCM 校验，校验结果仅在内存使用，不生成任务、音频资产或新样本。LLM 复用已保存的地址、模型、Key、Thinking 和 Streaming，严格验证正常结束的非空最终回答；推理、部分 SSE、缺少 `[DONE]` 或截断不算成功。

检测全应用最多一项，不排队，独立取消和 90 秒总预算；取消按钮等待服务端执行结束确认。已有 TTS / AI 任务使用原有独立状态，不会被检测取消。已发出的请求仍可能计费。配置、凭据或样本变化会使旧结果过期，未保存的相关设置会阻止检测。面板打开时每 2 秒只比较本机配置快照，不发起云端探测；关闭面板或页面请求取消，迟到响应会被丢弃。结果不持久化，成功仅代表这一次请求。

导航栏的「深色 / 浅色」即时切换配色，保持现有布局、字体和尺寸。默认浅色，偏好只存放在本浏览器的 `mimo-theme` 项中；刷新和重新打开 iframe 后保持。存储不可用时回退浅色，不影响创作。不会在浏览器持久保存凭据或编辑内容。构建、实机及真实云端证据见 [PR4 验证记录](verification/PR4.md)。

## VoiceDesign、VoiceClone 与声音样本

三种创作模式保留各自的当前编辑内容。结果展示真实生成模式和生成时参数；切换模式不会把其他模式迟到的结果标成当前模式的结果。刷新后可从音频库重新打开结果，编辑草稿不持久保存。

| 模式 | 官方请求与本应用边界 |
| --- | --- |
| 预置音色 | `mimo-v2.5-tts`，`audio.voice` 为官方预置 ID，沿用 PR1 语义 |
| VoiceDesign | `mimo-v2.5-tts-voicedesign`，音色描述及选用的自然语言/导演指导组成必填 user；无 `audio.voice`，显式 `audio.optimize_text_preview=false`，保留 assistant 原文；不开放唱歌或整体标签控件 |
| VoiceClone | `mimo-v2.5-tts-voiceclone`，使用服务端样本 ID，后端构造 WAV `data:audio/wav;base64,...` 或 MP3 `data:audio/mpeg;base64,...`；支持自然语言/导演指导、整体与句内标签；不发送优化或唱歌参数 |

所有模式都是非流式 WAV，使用同一任务/取消/结果管线。VoiceDesign 最多单段 3000 UTF-8 字节，不会拆成多次随机设计；超限时请先设计短样本，再使用 VoiceClone。不同设计请求不保证同一声音。本版不提供文本优化选项，API 也拒绝 `optimizeTextPreview=true`。

Design → Clone：填写音色描述和试音正文 → 生成并试听 → 在结果区命名、点击「保存为克隆样本并切换」→ 修改克隆模式的新正文/风格 → 生成。保存动作复制已经成功的 WAV，不再次调用 MiMo；删除原设计结果、24 小时结果清理或重启都不影响样本。转换要求音频符合下述参考范围，超限会禁用并说明原因，不截断音频。这是本地音频复用，未向云端永久注册 Voice ID。

声音样本库支持上传 WAV/MP3、显示实际格式/大小/时长、试听、选择、重命名及删除。上传只保存到 NAS；**仅点击 VoiceClone 生成时，所选完整参考音频与本次正文/指导才发送至小米云端，可能计费**。请仅使用自己有权使用的样本，建议清晰单人语音。已启动任务持有完整参考快照，任务期间删除或改名不会改变后续分段；新任务不能选择已删除的样本。

参考音频上限为 **7,400,000 原始字节、0.2–120 秒**，编码后连同 Data URL 前缀严格小于官方 10 MB 限制。时长是本应用资源保护范围，不是 MiMo 官方强制时长；官方支持数秒短样本。WAV 使用现有完整 RIFF/PCM 校验；MP3 检查完整连续 MPEG Layer III 帧、CRC（若存在），并用 NLayer 3.0.0 逐帧解码，拒绝截断、坏帧、异常采样、格式伪装或中途改变采样率/声道。支持 MPEG 1/2/2.5、CBR/VBR 和 ID3；不支持 free-format 或带不识别尾部数据的 MP3。MP3 时长按完整解码帧计数，包含编码延迟/填充，与浏览器显示可能有少量差异。没有 CRC 的压缩音频不能保证识别所有仍可解码的位变化。

## AI 辅助创作（0.3.2）

设置中独立配置 OpenAI-Compatible Chat Completions 的 Base URL（包括 `/v1`，不带结尾斜线）、模型、Key、SSE 与 Thinking。官方便捷选项使用 `https://api.xiaomimimo.com/v1` 和 `mimo-v2.6-flash`；可填写服务实际支持的其他 LLM 模型。TTS 三模型保持原协议。仅官方精确地址允许管理员明确勾选复用 TTS Key；自定义服务不会继承它。换地址时清除原独立 Key，需为新地址重新配置。

支持 Thinking：不发送、MiMo `thinking.type=enabled/disabled`、`enable_thinking=true/false`、`reasoning_effort=low/medium/high`。管理员应按模型选择，服务不支持时明确报错，不降级或重试。不发送工具、联网搜索或 TTS audio 参数。官方协议见 [MiMo Chat Completions 文档](https://mimo.mi.com/docs/zh-CN/api/chat/openai-api)。

VoiceDesign 的音色描述、各模式自然语言风格、导演三栏、播报稿、预置唱歌歌词与适用模式句内标签均提供 AI 操作。先在对应输入框写需求，点击 AI 后在候选区比较原文，主动应用；也可放弃或恢复应用前内容。导演三栏同时应用/恢复。应用后继续编辑会阻止恢复覆盖；请求期间编辑、切换模式或样本、取消及关闭页面都会使旧响应失效。AI 创作不生成、播放或改动 WAV；应用后仍需另行点击「生成语音」，旧音频按已有规则显示为历史结果。

标签操作只允许插入工具栏目录中的标签，服务端独立验证原文、标点、空格、换行、Unicode 文本元素及原有方括号表达式完整保留。改稿、非法标签、拆分组合字符/Emoji/CRLF 均拒绝。导演返回严格 JSON 三个非空字符串；所有结果通过实际 Composition 字节/分段预算，不自动截断。

LLM 配置和凭据放在 `TRIM_PKGETC/llm-private` 私有凭据容器（0600，目录 0700），API 仅返回非敏感设置及配置状态，权限保护不是加密。与 TTS Key 一起参与保留卸载/重装恢复；升级不覆盖。不得把该文件当作普通可分享设置导出。只发送当前操作必需的文本，不发送样本、音频或其他资产。

默认只允许外部 HTTPS，连接时解析并校验实际 IP，禁止重定向及系统代理，防止 DNS 变化绕过私网限制。管理员可明确启用 HTTP/私有网络兼容服务；界面显示明文凭据与稿件的传输风险。API 普通请求上限仍为 128 KiB；LLM 响应最多 1 MiB、总时限 3 分钟、连接 15 秒、首响应/读取空闲 30 秒，全应用最多一个 AI 请求。不自动重试，取消已送达上游的请求仍可能计费。SSE 需正常 `stop`、`[DONE]`、完整事件和连接结束，推理字段不会成为正文；中断、空回答、截断、拒绝或工具调用均不产生可应用候选。没有持久 AI 历史或多轮聊天。

构建与实机证据、限制见 [PR3 验证记录](verification/PR3.md)。前端候选状态测试：`npm --prefix frontend exec -- vitest run --root frontend --environment happy-dom tests/creative.test.js`。

## 数据、安全与边界

| 内容 | 位置 / 规则 |
| --- | --- |
| Key | `TRIM_PKGETC/mimo-key`，目录 0700、文件 0600；权限保护而非加密 |
| 音频与元数据 | `TRIM_PKGVAR/audio/{服务端GUID}/`，按可信 UID 隔离 |
| 持久参考样本 | `TRIM_PKGVAR/samples/{服务端GUID}/`，独立音频与元数据，按可信 UID 隔离 |
| 任务 | 进程内，最多 100 条状态；重启后任务状态消失，成功资产仍可从音频库访问 |
| 临时资源 | `TRIM_PKGTMP/mimotts-work`；提交暂存目录位于音频目录内以保证同盘原子重命名 |
| Socket | `TRIM_APPDEST/app.sock`，0600；生产只监听该 Socket |
| 日志 | `TRIM_PKGVAR/service.log`，超过 1 MiB 时启动轮换；不记录请求、Key 或上游响应 |

后端同时检查 Socket 对端的 Linux `SO_PEERCRED`（系统 root 网关）及 `X-Trim-Userid/Username/Isadmin`。缺失、非法或非管理员身份拒绝 UI、静态资源和所有 API；任意其他本地用户即使伪造 Header 也不能通过。包用户自己也不能伪装成 root 网关。此假设已在测试系统确认；未来若平台改变网关用户，应用会拒绝访问，需要重新验证适配。root 管理员本来就有系统文件权限，不在此信任边界之外。

所有写请求要求同源 Origin 与不可预测 CSRF token。对 fnOS 去掉 Host 端口的转发行为，额外校验浏览器 Referer 的完整来源。不开 CORS；自定义网关或去掉 Referer 的浏览器策略可能导致写入被拒绝。生产 Release 编译不包含开发免认证开关。

上限：普通 API 请求体仍为 128 KiB；只有经同样鉴权/CSRF 检查的 `POST /api/samples/upload` 放宽为 7,400,000 字节，使用有界原始文件流、最多两个并行样本处理、30 秒超时，不信任扩展名/MIME 或 Content-Length。正文 60000 UTF-8 字节，user 指令 6000 字节，每段正文连同前缀最多 3000 UTF-8 字节；目标 50–3000 个 Unicode 文本元素，默认 3000，最多 80 段。这是保守工程预算，不是 MiMo 官方硬限制。关闭分段时超限直接报错。唱歌歌词尽量保持完整，分段会影响连续性。

全应用同时最多一项生成，单段 3 分钟、整任务 20 分钟；响应 JSON 最多 90 MiB，最终 WAV 最多 64 MiB。取消会传播到 HTTP 请求，并在确认后标为已取消；服务已接收的请求仍可能计费。失败、取消与不完整音频不提交成功资产。支持 PCM 8/16/24/32 位和 IEEE float32、单/双声道 WAV，规范化为单声道 PCM16；段间采样率不同则明确失败，不做未经验证的重采样。

生成音频与样本合计配额 1 GiB、最多 1000 条，提交前在同一存储锁中检查配额与磁盘保留 256 MiB 空间。未保存生成结果在每次应用启动时清理超过 24 小时的条目；已保存结果与声音样本持续保留至主动删除。应用重启后从库中打开历史结果，不恢复编辑草稿。应用不会自动上传日志、样本或浏览器草稿。参考音频内容、Data URL 和 Key 不进入任务/资产 JSON 摘要或日志。

升级脚本不覆盖 Key、PR1 历史音频或样本，旧元数据缺少模式字段时按预置模式读取。卸载向导默认保留数据：卸载前把 Key 备份至私有 `TRIM_PKGVAR/.retained-config`，防止平台删除 etc；重新安装时仅在没有新 Key 时恢复。选择「确认永久删除」仅删除带本应用归属标记的 `audio`、`samples` 和 `.retained-config` 子目录，拒绝符号链接，不删除 TRIM 根目录、共享目录或用户文件。元数据、日志和运行锁等平台残余可由系统管理；本应用不扩大清理范围。

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
node --test frontend/tests/polling.test.mjs
python3 tests/theme_styles.py
npm --prefix frontend exec -- vitest run --root frontend --environment happy-dom tests/creative.test.js tests/diagnostics.test.js
npm --prefix frontend exec -- vitest run --root frontend --environment happy-dom tests/nas-export.test.js
python3 tests/package.py
```

NAS 文件系统负例在独立 Linux / WSL 测试机以 root 执行 `bash tests/nas_filesystems.sh`：脚本拒绝已有 `/vol98765`，在自己创建的目录挂载两个临时 tmpfs，再以非 root 身份运行测试，覆盖跨文件系统复制和真实 ENOSPC，退出时卸载并清理自身测试目录。**不要在真实 NAS 上运行该受控挂载测试**；真实 fnOS ACL 需在平台应用设置与文件管理器中验收。

实机验收：先安装 FPK，然后把 `tests/fnos_acceptance.py` 复制到指定 fnOS 测试机，以 root 执行。它通过真实私有 Socket 检查权限、范围请求、文件复用与 appcenter 启停，仅创建并清理有明确标签的合成静音测试文件，不调用 MiMo、不更改已有 Key。完整的浏览器登录、桌面 iframe、非管理员会话和在线合成仍应另外验证。

PR2 的样本验收可将 `tests/fnos_pr2.py` 和 `tests/fixtures/tone.mp3` 复制到指定测试机，以 root 运行 `python3 /tmp/fnos_pr2.py /tmp/tone.mp3`。脚本检查上传、解析、归属和重启保留，拒绝在已有生成任务运行时开始重启测试；只清理自己创建的测试样本，不发起云端合成。原位升级的哈希比较可额外传入升级前记录的私有比较文件，其字段格式见脚本；该文件含 Key 哈希，保持 0600，验收后删除，不提交仓库。

运行时使用 invariant globalization，无 ICU 依赖。Debian 12 测试机已实际启动，并核验 glibc、libstdc++、libgcc、libm、libdl、pthread；真实 HTTPS 合成验证了系统 TLS/证书环境。其他 fnOS 版本与发行版需重新验收，不把 WSL、Mock 或打包成功等同于实机安装。

## 来源与许可

本仓库源代码为独立实现，MIT 授权。只读查阅了 SeleVox 的 `docs/MiMoTTS.md` 及 PR6 指定的风格选择器和测试作为行为参考；分类名称和短标签词汇用于相同功能的浏览辅助。Vue 交互和后端安全检查独立实现，没有复制其 GPL 源代码、界面资源、密钥或用户文件，也没有构建依赖。协议以小米官方文档为依据；FPK 结构依据官方 fnpack 模板、[fnOS 网关文档](https://developer.fnnas.com/docs/core-concepts/gateway-registration/) 及其 [公开文档镜像](https://github.com/ckcoding/fnnas-docs)。打包时收集确切 .NET 运行时和前端依赖的第三方许可到 `server/licenses/`；不要移除后再分发。

MP3 解码依赖 [NLayer 3.0.0](https://github.com/naudio/NLayer/tree/v3.0.0)，MIT 许可与上游版权保留在 `third_party/NLayer.LICENSE`，构建时随 FPK 分发；不引入 NAudio 或 Windows 播放依赖。测试中的 MP3 为原创正弦波，不是真实个人声音。
