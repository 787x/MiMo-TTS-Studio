# CLI / MCP / Agent（0.7.x）

CLI 和 MCP 是同一个自包含 Linux x86-64 程序 `MiMoStudio.Agent`。两者只把命令交给正在运行的 Studio，不包含另一套 TTS、LLM、任务队列或媒体库。普通 NAS 不需要开发 SDK、Python、Node.js。声音始终由浏览器试听，CLI 生成 WAV 不代表播放 NAS 声卡。

## 授权与运行身份

1. 通过 fnOS 桌面以管理员打开 Studio → Key/设置 →「CLI / MCP · Agent 授权」。填写名称，明确勾选权限并创建。页面只显示授权 ID、创建者 fnOS 用户和权限摘要，不返回连接秘密。
2. 服务端生成 256-bit 随机连接秘密，在 `TRIM_PKGVAR/agent/<授权ID>.credential` 保存 0600 凭据文件；`grants.json` 只存秘密的 SHA-256、授权摘要和预设。目录 0700。权限保护不是加密。不要打开、复制或打印这些文件，不要将文件内容放进命令、环境变量、日志或 MCP 配置。
3. 程序在 `TRIM_APPDEST/agent/MiMoStudio.Agent`，默认连接其上级的 `agent.sock`。通过已获管理员许可的受控身份切换，以 **应用包用户**运行 CLI。该用户在测试平台名为 `mimotts`，应核对本机实际包身份；不是当前 SSH 登录者，也不是授权绑定的 fnOS 用户 ID。

示例中 `APP_DEST`、`APP_VAR`、`APP_USER`、`GRANT_ID` 是操作者已核实的安装位置/身份和公开授权 ID，不是秘密。fnOS 管理员可在已有管理 shell 中设置这些非敏感变量：

```bash
AGENT="$APP_DEST/agent/MiMoStudio.Agent"
CREDENTIAL="$APP_VAR/agent/$GRANT_ID.credential"
sudo -u "$APP_USER" -- "$AGENT" get_capabilities --credential-file "$CREDENTIAL"
sudo -u "$APP_USER" -- "$AGENT" --help
```

如果没有可信管理身份切换能力，CLI/MCP 不能访问包用户 Socket；不会自动提权。不要 chmod 放宽 Socket/私有目录、修改宽泛宿主用户组、安装 setuid 或启动 root 常驻进程。应用不自动改动 sudoers。`--credential-file` 和可选 `MIMO_AGENT_CREDENTIAL_FILE` 只包含文件位置，秘密只从经过校验的文件描述符读取。凭据文件必须为当前 UID 所有的普通文件，拒绝软链接及组/其他用户可访问的权限。

Linux 权限与业务权限是两层：服务端核验真实 `SO_PEERCRED` 必须为当前包 UID，另核验授权秘密和每次操作的权限；Owner 只从授权记录的可信 fnOS 创建者取得。任意客户端 Header、UID/Owner 参数均不能替代它。root 的连接也不能直接使用 Agent Socket，需显式切换包身份。原 Web Socket 仍只接受可信 root 网关及管理员 Header，Agent 不伪造 Web 请求。

**包 UID 已被完全控制的恶意进程可以读取私有凭据、API Key 和数据；本设计不宣称防御这种情况。** 普通不同 UID 的应用/SSH 用户会被文件权限及真实对端检查拒绝。管理员授予任意包身份代码执行能力本身属于更高系统信任层，不能用业务授权降低这项风险。

在设置中点击「撤销授权」会删除授权及连接凭据，同时移除该授权自己的 Agent 默认值/命名预设；既有 CLI/MCP 连接之后的新操作也被拒绝。已被服务端接纳的云请求不会自动重试或回滚，已提交 Jobs 继续运行，可以通过原 Web UI 管理。撤销不删除成功资产、样本或 NAS 导出文件。

| scope | 操作 |
| --- | --- |
| read | 能力目录、配置摘要、本地预览、任务/资产/样本查询、CLI 下载、读取默认值/预设 |
| generate | TTS 提交/取消、六类付费 AI 创作、真实 API 检测 |
| manage | 保存私有结果、从 Design 创建样本、CLI 导入、重命名样本 |
| export | 实时 fnOS 授权目录及导出 |
| configure | 修改本授权的默认值和命名预设 |
| delete | 删除本用户私有资产/样本、本授权命名预设 |

每个授权始终含 read。默认只选择 read/generate/manage；export/configure/delete 由管理员另行勾选。Agent 不提供修改授权、读取/修改 API Key、覆盖 Base URL、执行宿主命令、修改权限或通用文件操作工具。

## CLI 输入与输出

以下例子假定 `agent` 是管理员配置的、使用上述正确身份及凭据的命令包装器；不能只给普通 SSH 用户设置凭据环境变量就绕过包权限。

```bash
agent preview_synthesis --text '你好。[轻笑]原文 é👋。' --voice 冰糖 --tags '温柔 清亮 上海话'
agent synthesize_audio --file ./script.txt --preset narration --request-id episode-001 --wait
printf '%s' '标准输入的正文。' | agent synthesize_audio --stdin
agent get_job_status --id JOB_ID --wait --timeout 1200
agent cancel_job --id JOB_ID
```

可使用下划线工具名或对应的连字符形式。输入来源 `--text`、`--file`、`--stdin` 互斥；不会自动读取未声明的 stdin。UTF-8 文件/stdin 最多 60000 字节，严格解码；文件编码签名只移除首个 BOM，不改写正文/换行。输入文件须为当前运行身份所有、0600 的普通文件，拒绝链接/设备/FIFO；应先在明确的受控暂存目录准备文件，不把任意宿主路径转交给 Agent。`--text` 会出现在命令行历史/进程参数，敏感稿件请用文件或 stdin。

`--help` 显示总帮助；`<tool> --help` 显示该操作完整 JSON Schema。所有普通输出都是一个 JSON 对象：

```json
{"success":true,"code":"ok","data":{"job":"服务端JobID","replayed":false}}
```

默认提交后立即退出；`--wait` 轮询同一个 Job，终态为 succeeded 才退出 0。Job 状态包括 running/canceling/succeeded/canceled/failed。等待超时、Ctrl+C、CLI 退出、MCP 断开均不隐式取消成功提交的 TTS；使用 `cancel_job` 明确取消并查询至终态。后台沿用全应用一个 TTS 槽、20 分钟总预算、最多 80 段和 64 MiB 成功 WAV。取消旧 ID 不影响新 Job，其他用户的 Job 不可见。

| 退出码 | code / 意义 |
| --- | --- |
| 0 | ok：操作成功（异步提交尚须查 Job） |
| 2 | invalid_input：参数、文件编码/大小、缺失资源/配置、无效组合 |
| 3 | permission_denied：系统文件/对端/授权/权限拒绝 |
| 4 | service_unavailable / incompatible_service：未运行、连接失败、版本不兼容 |
| 5 | busy：全局槽或本地并发上限 |
| 6 | execution_failed / idempotency_conflict / response_too_large / export_committed_check_target：执行/存储失败或冲突 |
| 7 | timeout / canceled：客户端等待超时、取消 |

错误只输出安全代码，不返回原始上游响应或异常详情。诊断业务结果还需检查 data.state/category，配置就绪不代表在线检测成功。

`requestId` / `--request-id` 仅适用于 TTS 提交，为 1–80 位 ASCII 字母/数字/下划线/连字符。幂等域是**同一个授权、当前后端进程**：相同标识及解析后参数返回同一 Job，不再发起收费请求；不同参数返回 idempotency_conflict。最多保留 256 条记录，满后拒绝新带标识提交，不自动淘汰以导致重复收费。任务状态仍只保留原 Jobs 最近 100 条，因此早期幂等命中可能返回已不可查询的旧 Job ID；资产仍在库中。**重启/升级后记录消失**；网络断开时不得把新的 requestId 当作自动重试。无标识时不提供去重保证。

## 三模式、标签和预设

```bash
agent synthesize_audio --text '你好。[轻笑]欢迎回来。' --voice 冰糖 --style '温柔自然' \
 --mode combined --character 主持人 --scene 电台 --direction 清晰讲述 \
 --tags '温柔 清亮' --split true --segment-characters 200 --wait
agent synthesize_audio --text '春风轻轻吹来。' --singing true --split false --wait
agent synthesize_audio --synthesis-mode design --voice-description '成熟温暖的成年女声，普通话清晰。' \
 --text '你好，这是新声音的设计试音。' --wait
agent create_sample_from_asset --id DESIGN_ASSET_ID --name '电台样本'
agent synthesize_audio --synthesis-mode clone --source-sample-id SAMPLE_ID \
 --text '克隆模式的新正文。' --tags '温柔' --wait
```

所有模式复用 `Composition.Plan` 和原 MiMo/WAV/提交链。Preset 支持音色、自然风格、导演、整体及句内标签、唱歌、长文本分段。Design 必须有描述，仅单次最多 3000 UTF-8 字节，不接受 Tags/唱歌/预置音色；Clone 必须选择自有样本 ID，支持适用风格/导演/标签/长文本，拒绝唱歌、指定预置音色和设计描述。不支持文本优化或隐式改写。整体 Tags 是与 PR6 相同的**单一自由字符串**，不是按空格重新解析的数组；唱歌使用独立布尔字段。`list_style_presets` 返回与 Web 相同的 8 类48项浏览目录，不限制合法自由风格名称。

`preview_synthesis` 返回真实 model、user、assistant、prefix、segments 及有效参数（text 字段为空，正文在预览分段）；不调用云、不上传样本、不创建 Job。只有预览和明确 AI 候选工具返回必要创作文本，普通任务/资产列表不返回正文、指导、标题或音频。

```bash
agent set_agent_defaults --style '温柔自然' --split true --segment-characters 200
agent get_agent_defaults
agent save_preset --name narration --voice 冰糖 --tags '清亮'
agent list_presets
agent get_preset --name narration
agent synthesize_audio --text '本次正文。' --preset narration --style '' --wait
agent set_agent_defaults --parameters '{"style":null,"segmentCharacters":null}'
agent set_agent_defaults --clear true
agent delete_preset --name narration
```

优先级逐字段为：本次显式参数 → 指定命名预设 → 本授权 Agent 默认值 → 后端安全默认值。未提供才继承；空字符串是显式空值，null 重置为该字段后端默认值（style/tags/导演/描述为 `""`，sourceSampleId 为 null，voice=mimo_default，mode=natural，synthesisMode=preset，singing=false，split=true，segmentCharacters=3000）。`clear=true` 移除全部默认字段，再合并本次 parameters。save_preset 完整替换指定预设；同一字段不能同时经 `--parameters` 和独立 flag 传入。

允许字段：synthesisMode、voice、voiceDescription、sourceSampleId、style、mode、character、scene、direction、tags、singing、split、segmentCharacters。拒绝 Key、URL、正文、完整参考音频及路径字段。模式切换不会悄悄丢弃继承字段；不兼容组合/删除样本会在生成前明确失败，需显式清空或使用合适预设。部分预设可只保存适用字段；最终合并参数仍必须通过 Composition。

每授权最多100个命名预设，名称1–64位字母/数字/空格/下划线/连字符；整个 grants.json 最多256 KiB、最多64授权。单后端进程使用同一锁防止丢失更新，并以0600临时文件、flush、原子改名保存。Web 草稿不成为 Agent 默认值，保存 Agent 预设不修改浏览器编辑内容。

## 完整工具表

CLI 与 MCP 共用下表名称/Schema/执行层。以下 `{parameters}` 均为上述严格字段对象，`text` 为本次正文/创作需求，`preset` 可选；未知字段、重复 JSON 字段、类型错误均拒绝。CLI 独立参数以 camelCase 对应连字符选项，如 sampleId → `--sample-id`。

| 工具 | 参数 | 权限 |
| --- | --- | --- |
| get_capabilities | 无 | read |
| list_voices / list_style_presets / list_inline_tags / get_service_status | 无 | read |
| get_agent_defaults / list_presets | 无 | read |
| get_preset | name | read |
| save_preset | name, parameters | configure |
| delete_preset | name | delete |
| set_agent_defaults | parameters?, clear? | configure |
| preview_synthesis | text, parameters?, preset? | read |
| synthesize_audio | text, parameters?, preset?, requestId? | generate |
| list_jobs / list_audio_assets / list_voice_samples | offset?（0–1000） | read |
| get_job_status / get_audio_asset / get_voice_sample | id | read |
| cancel_job | id | generate |
| generate_voice_description / generate_style_instruction / generate_director_plan / generate_script / generate_lyrics / annotate_inline_tags | text?, parameters?, preset? | generate |
| create_sample_from_asset / rename_voice_sample | id, name | manage |
| delete_voice_sample / delete_audio_asset | id | delete |
| save_audio_asset | id | manage |
| list_nas_export_targets | 无 | export |
| export_audio_to_nas | id, directoryId, fileName | export |
| check_service_configuration / probe_service | service=preset/design/clone/llm, sampleId? | read / generate |
| import_sample（仅 CLI） | name, format=wav/mp3, `--input FILE` | manage |
| download_asset（仅 CLI） | id, `--output NEW_FILE` | read |

列表每页最多100条，返回 items/nextOffset，使用 offset 获取后续页；预设列表最多100名，NAS目录查询受原平台响应体上限和请求时限约束。音频元数据包含格式、大小、时长；详情另含采样率。`save_audio_asset` 是原私有库保存标记，`download_asset` 是写到明确的 CLI 本地输出，`export_audio_to_nas` 是原 fnOS NAS 授权导出；它们都复用已有 WAV，均不再合成。

六类 AI 复用已保存的 LLM URL、模型、Thinking、Streaming、Key，以及 `Llm.Generate`/`Creative.Prompt`/`Validate`。音色描述需求在 parameters.voiceDescription（design），风格需求在 style，导演需求在 character/scene/direction，写稿/歌词/标签需求在 text。歌词要求 preset + singing=true。导演返回 text/role/scene/direction；其他返回 text 候选。标签工具严格只插入目录标签，拒绝改写原文或拆开 Unicode；不完整 SSE/截断/失败不算有效候选。不会改 Web 草稿、自动 TTS 或上传样本。

检测 clone 必须明确 sampleId，不能自动寻找/上传其他样本。check 只做离线检查，probe 才发送一次固定测试请求且可能计费；结果沿用 Diagnostics 的 state/category/reason/configuration。普通服务状态不返回 URL、Key 或私密路径；API Key 更新继续在 Web 设置完成。

```bash
agent generate_voice_description --synthesis-mode design --voice-description '温暖低沉男声'
agent generate_director_plan --mode director --character 旁白 --scene 夜晚 --direction '舒缓讲述'
agent annotate_inline_tags --text '你好。今天阳光很好。'
agent check_service_configuration --service clone --sample-id SAMPLE_ID
agent probe_service --service clone --sample-id SAMPLE_ID
agent save_audio_asset --id ASSET_ID
agent download_asset --id ASSET_ID --output ./new-result.wav
agent import_sample --input ./reference.wav --name '授权参考' --format wav
agent list_nas_export_targets
agent export_audio_to_nas --id ASSET_ID --directory-id TARGET_ID --file-name narration.wav
```

导入复用完整 WAV/MP3 检查，最多7,400,000原始字节、0.2–120秒、归属和原子存储；只有后续 clone 生成才上传样本。CLI 文件也必须属于包运行身份且0600。下载最多64 MiB，0600同目录随机暂存、完整接收/同步后不覆盖发布，失败清理暂存。输出路径由显式 CLI 操作者负责选择，不通过 MCP 传入。MCP 不暴露上传、下载、任意文件路径或音频 Base64；首版不支持远程 MCP 文件导入，请用受控 CLI 暂存或原 Web 上传。

NAS 工具只接受实时查询得到的目录 ID（重启后失效），不接受目录路径。继续由 PR5 `NasExport` 检查平台实时授权、可信 fnOS Owner ACL、包用户写入权限、路径/inode、O_TMPFILE、无覆盖原子提交。返回 ID/文件名/字节数，隐藏宿主绝对路径。如果返回 export_committed_check_target，完整文件已提交但目录同步未确认，应先到目标目录核实，不能盲目重试。已导出 WAV 由用户管理，撤权、删除私有资产或卸载不会删除它。

## MCP stdio 与 SSH

程序使用官方 [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) **2.2.0**，标准 stdin/stdout 换行 JSON-RPC，版本协商由 SDK 处理。不是 LSP Content-Length 帧。实现核对了 [MCP 规范](https://modelcontextprotocol.io/specification/) 和该已发布 SDK 的 API；没有为未协商的扩展声明支持。工具提供具体参数 Schema、readOnly/destructive/openWorld 注释、structuredContent、isError。tools/list 按授权过滤，每次 tools/call 仍在服务端再次校验。Cloud 工具描述说明上传/费用，但权限不依赖描述文字。

在已经具备正确包身份的本机会话中，标准客户端配置：

```json
{
  "mcpServers": {
    "mimo-studio": {
      "command": "/REPLACE_APP_DEST/agent/MiMoStudio.Agent",
      "args": ["mcp", "--credential-file", "/REPLACE_APP_VAR/agent/REPLACE_GRANT_ID.credential"]
    }
  }
}
```

Windows/Linux 客户端可通过受信任 SSH 在 NAS 启动 stdio 进程：

```json
{
  "mcpServers": {
    "mimo-studio-nas": {
      "command": "ssh",
      "args": ["-T", "NAS_SSH_ALIAS", "sudo", "-n", "-u", "REPLACE_APP_USER", "--", "/REPLACE_APP_DEST/agent/MiMoStudio.Agent", "mcp", "--credential-file", "/REPLACE_APP_VAR/agent/REPLACE_GRANT_ID.credential"]
    }
  }
}
```

前提是 SSH key/host key 已由管理员配置，并且该 SSH 身份**已有可信、受控的非交互包身份切换能力**。上例 sudo -n 不弹出密码提示；没有该前提会明确失败，不能假设 fnOS 普通 SSH 用户天然可访问包私有 Socket。应用不会创建宽泛免密 sudo 规则。管理员如配置窄权限包装器，应使用不可由普通调用者改写的固定程序、固定包用户、固定凭据位置，不给任意命令/参数提权；具体规则由设备管理员审计。

不要使用 ssh -t/交互 shell/profile 欢迎输出，也不要在 MCP stdout 打印密码提示、调试日志或 JSON 之外的信息。SSH stderr 可独立收集安全日志，不混入 stdout；SSH EOF/客户端断开使 MCP 进程退出，已提交 TTS 在 NAS 后台继续。不要将密码、API Key、授权秘密或参考音频放进配置。密码式 SSH/sudo 如需交互，应由可信会话管理器提供独立安全输入，不能写进配置或让其从 MCP 协议 stdin 读取；未建立这种通道时不支持普通远程 MCP 配置。

实测 SSH 证据和前提见 [PR7 验证记录](../verification/PR7.md)。未加入 HTTP/SSE/公网/LAN MCP 监听，也没有“不支持就开放端口”的回退。

## 协议、资源和生命周期边界

Agent IPC v1 是专用0600 Unix Socket，一连接一请求，4个固定服务 worker、最多4个 MCP 业务调用；请求头 JSON 最多128 KiB，响应最多256 KiB，MCP JSON-RPC 每条输入最多128 KiB。SDK结果同时含文本与结构化对象，整体输出仍有界（约为IPC响应的两倍加协议元数据）。默认 JSON 编码转义及风格字段会计入请求体上限，正文上限与整个请求体上限必须同时满足；不会静默截断。

认证/读取头预算5秒，操作总预算4分钟；导入另有7.4MB受限二进制帧，下载按已验证长度最多64MiB分块传输。CLI连接超时5秒，`--timeout` 总预算1–1200秒（默认1200）；后端仍有独立操作/Jobs/LLM/检测/NAS导出时限。上传/下载失败无自动重试。服务协议不兼容返回 incompatible_service，需要更新客户端与服务。旧服务没有 agent.sock 会返回 service_unavailable。应用停止/升级清理 Agent Socket，下次启动重建；只有新启动的服务接纳请求。

Agent 私有持久状态位于 TRIM_PKGVAR/agent，升级保留；保留数据卸载保留此目录，重装后重新收紧权限；确认删除数据的卸载仅删除本应用私有 agent/audio/samples/保留配置目录。正常保留不会改变旧 TTS/LLM Key、样本、资产或 NAS 授权；服务重启仍沿用原有进程内 Jobs 历史消失的边界。
