# PR6 — 整体风格标签选择器与自定义风格

日期：2026-10-20（Asia/Shanghai）。基线：最新 main `81ea35d`（PR5 合并后）。最终原生 FPK **0.6.2 / Linux x86-64**。

## 实际修改

- 原整体风格输入框保留原位置；下方新增实时前缀、紧凑已选标签和可展开浏览区。8 类 / 48 个短名称，按名称或类别搜索，点击增删、单项移除、清空，按加入顺序组合。没有搜索结果仍可添加自定义项。
- 自定义名称支持 Enter / 按钮，输入法组词 Enter 不提交；使用单行外观 textarea，防止浏览器单行 input 自动吞掉粘贴的换行。每项 1–64 UTF-16 字符、最多 16 项；拒绝括号、首尾空白、非普通空格的空白、Cc/Cf、无效代理项。不规范化 Unicode，按精确字符串去重。与手动文本重名的完整片段不重复追加，提示回到原输入框编辑。
- 原 Tags API 保持字符串；只在前端维护各模式独立的 `{manual, selected}` 编辑状态，不进请求或磁盘。历史值、手改后的完整值作为不透明的手动文本保留，绝不按空格拆分。手改保留整串当前值并重置结构化标记、明确提示；含内部空格的自定义名在模式切换后仍是同一项。手动文本仍保留既有 200 UTF-16 字符语义，不强制重解析成 64 字 / 16 项。
- 添加前请求 NAS 本机 `/preview`，通过既有 Composition 检查文本、分段数、原子标签及前缀字节预算；不调用 MiMo、不创建 Job。此预算校验不要求先选克隆样本；空正文用本机占位文本检查，真正预览/生成仍严格检查实际正文、样本及全部参数。校验失败不改变选择，草稿改变或组件卸载后丢弃迟到响应。
- `Composition.ValidateTags` 在原 200 字符限制上补齐 Unicode 安全检查，拒绝 Tags 中独立的唱歌别名。预置「唱歌」及自定义 `唱歌` / `sing` / `singing` 使用原 `Singing`；VoiceClone 禁用/拒绝，VoiceDesign 不显示或发送 Tags。原组合 `(唱歌)(清亮)` 保持可用。
- 空标签前缀为空；普通组合为 `(温柔 清亮 上海话)`。每段 assistant 前置相同字符串、计入 3000 UTF-8 字节，不进入 user，不修改正文和句内标签。继续使用原 Job/资产的 `GenerationSummary.Prefix` 和草稿快照，选择、删除、自由编辑实际参数会使旧结果过期。
- 沿用 PR5 两套颜色变量与键盘焦点；预置区最多 240px 高、独立滚动。原字体、版心、双列比例、卡片和播放器设计保持。窄屏原单列网格用 `minmax(0,1fr)` 替代隐含的自动最小尺寸，避免文件控件等固有宽度撑开 iframe；仍为原单列断点，没有调整列比例。
- 未修改 NAS 导出、私有资产格式、凭据、TTS/LLM Provider、WAV/样本处理、AI 创作或生命周期实现。没有新增依赖、持久风格数据库或云同步。

SeleVox 按指定路径只读查阅了五个文件；先核实其 GPLv3 许可证。这里只参考交互行为、安全约束和短分类词汇；Vue 组件、状态转换和后端校验独立实现，没有复制 GPL 源代码、XAML 资源、Key 或样本，构建不依赖 SeleVox。

## 受控验证

| 层级 | 实际结果 |
| --- | --- |
| Windows Release | **247 项通过**；Linux 文件系统用例明确跳过 |
| Linux Release / 非 root | **281 项通过**，包含两个 tmpfs 的原 PR5 跨文件系统、真实 ENOSPC、权限/竞态检查及新增标签测试 |
| 受控 MiMo HTTP | 预置和克隆的每段请求与 Composition 预览逐项相同；Job / 资产摘要前缀相同；WAV、取消、错误、样本快照、播放下载不再次合成的原回归通过 |
| Release Unix Socket | 原 **69 项集成检查通过**，覆盖可信管理员、伪造身份、Origin/CSRF、请求体限制、样本、AI、NAS 导出 |
| Vue | 类型检查 / 生产构建通过；**61 项组件测试**通过（含新增选择器及工作台集成），另 **2 项 Node 轮询测试**通过 |
| CSS / 生命周期 | 原浅色变量保真检查、原 **6 项生命周期检查**通过 |
| FPK | 官方 fnpack、自包含 Linux publish、运行时许可收集、包结构/非 root 权限/网关/API scope/清洁内容检查通过 |

新增覆盖：8 类48项、类别/名称/空结果搜索；顺序、去重、重新选择、单删、清空、Enter；大小写及组合字符的精确保留；普通内部空格、64 UTF-16（含代理对）、16项、200组合长度边界；Cc/Cf、补充平面格式字符、换行、括号、空白、孤立代理项；预算拒绝保持原状态；手动文本和选择器转换；唱歌别名/组合/克隆限制；模式独立草稿、设计无控件、迟到响应、真实生成参数快照过期但不自动创建任务。

复现：

```bash
npm --prefix frontend run build
npm --prefix frontend exec -- vitest run --root frontend --environment happy-dom tests/styles.test.js tests/style-workbench.test.js tests/creative.test.js tests/diagnostics.test.js tests/nas-export.test.js
node --test frontend/tests/polling.test.mjs
dotnet run --project tests/Studio.Tests.csproj -c Release
# 独立 Linux/WSL 受控主机，绝不在真实 NAS 运行挂载测试：
sudo bash tests/nas_filesystems.sh
python3 tests/lifecycle.py
sudo python3 tests/production_gateway.py artifacts/fpk/app/server/MiMoStudio
python3 tests/theme_styles.py
python3 tests/package.py
```

Windows 和 WSL 共用此工作目录，.NET 的 restore / build / tests 串行运行，避免共享 obj 被另一环境改写；构建阶段的许可证断言曾因此中止，串行重新构建后全部通过。未把失败的中间产物用于最终验收。

## 真实 fnOS 与真实 MiMo

测试机 `192.168.3.153`，fnOS 1.2.0800 / x86-64。所有 UI 操作在用户指定的侧边浏览器。应用中心从 PR5 **0.5.1 → 0.6.0 → 0.6.1 → 0.6.2** 原位升级；CLI `install-fpk` 对已安装应用返回 installed 而没有升级，因此没有把该命令当作升级证据。平台未经验证应用提示取得用户确认后继续。

升级前在设备 root 私有临时文件记录 **13 个原配置、历史 WAV、样本及元数据** 的哈希；升级及测试后逐一一致，不输出或提交凭据/凭据哈希。应用仍是包用户运行、0600 私有 Unix Socket，未开启 TCP。

1. **0.6.1 安装版 UI**：实际选择温柔、清亮；搜索无结果时按 Enter 添加上海话，实时输入框、直接前缀及最终请求预览一致。类别搜索、重复点击取消、单删、清空、非法换行拒绝均实际操作；无效添加保持原选择。删项后出现旧参数及预览过期提示，无新合成。
2. **真实预置合成 1 次**：正文「你好，这是整体风格组合的在线验收。愿今天的声音，温柔又清亮。」；前缀 `(温柔 清亮 上海话)`，省略 user。成功文件 `589cdeffc7284b1cae55bf169da7b449`，PCM16 / 单声道 / 24000Hz，180480 帧（7.52秒），361004 bytes。实际点播放后按钮变暂停、随后播放结束；这是浏览器播放验收，不宣称做了音色/方言主观质量评估。
3. 同一预置结果保存、浏览器真实下载、NAS 导出到已有授权 `/vol1/1000/test/PR6-style-combination.wav`。下载、私有源、NAS 目标 SHA-256 均为 `2be928939674ab872ad1ac6b478cc1f4f6c08bb923a0e85294bf7feef18fc217`。没有再次发起合成或改动原文件。
4. **真实 VoiceClone 合成 1 次**：明确选择已有「PR2 温暖女声设计样本」（此前由 MiMo 生成的 6.56 秒 WAV），输入新短正文并选择同一组合。UI 唱歌预置禁用，自定义 `SINGING` 明确拒绝。预览为 VoiceClone 模型、同一前缀；成功文件 `b826315a65284f68b0bc6866a46cafc6`，PCM16 / 单声道 / 24000Hz，207360 帧（8.64秒），保存摘要前缀一致。两个 Job、两个新 WAV，原样本数量仍为 1，原样本文件哈希不变。
5. 云端两次调用发生在 **0.6.1**；**0.6.2 只追加窄屏 CSS 修正，后端 DLL 逐字节相同**。不为版号变化重复收费合成。受控 HTTP 检查实际 JSON；真实云端验收核对浏览器预览、成功 WAV、Job 和保存摘要，不记录上游私密网络内容。
6. 深色 fnOS iframe 实际检查；窄屏使用同一侧边浏览器直接打开真实 `/app/mimotts/` 网关页面，以浏览器视口模拟 **560px / 360px**，实际添加含内部空格和64字符名称，检查换行、删除、Enter、焦点与滚动。单独记录为真实服务 + 视口模拟，**不冒充手机硬件或 fnOS 宿主支持任意窗口缩放**；临时诊断样式和视口设置结束后恢复。
7. `tests/fnos_pr6.py --baseline /tmp/mimotts-pr6-before.json` 在最终安装版真实 Socket 检查两模式每段前缀/预算、非法/别名/设计拒绝、手动空格原样及零任务/资产/样本变更。该脚本 root 模拟可信网关，应用仍以包用户处理；不调用 MiMo、不修改授权或已有数据。

8. 最终 0.6.2 在真实设备执行 stop/status/start/status，得到 stopped/running；重启后再次执行安装版回归，包用户、0600 Socket、原13个文件和外部导出 WAV 均保持。最终前端 JS / CSS 哈希与 FPK 暂存内容逐一一致。最终网关页面无临时 inline style，360px 页面 clientWidth/scrollWidth 均为345px（含滚动条），64字符标签换行后仍相同；560px 也无横向溢出。VoiceDesign 无选择器，返回预置后已选三项及顺序保持。

![fnOS 深色选择器](PR6-fnos-dark.png)
![最终真实网关的560px浅色布局](PR6-narrow-light.png)
![最终360px长标签换行](PR6-narrow-360.png)

## 最终产物与限制

`artifacts/mimotts.fpk`：**0.6.2**，**49,419,265 bytes**，SHA-256 `0f37321cc8c8f567b058e11e634fcbf7b1cd4969a496e42a94f3d386e10862a5`。二进制不提交 Git。

最终包及设备后端 DLL SHA-256：`7714bf64d4322711669c97164cbef0aece7b6fc989373e6183131078e7cbc0f0`。最终前端为 `index-DoIjzKI8.js` / `index-DtJvN_Er.css`。

- 预置目录不是官方能力保证；未逐一试听48项，没有声称标签一定产生特定声学效果。
- 新增安全规则会明确拒绝此前后端未严查的隐藏/控制字符，以及 Tags 中的唱歌别名；不会静默改写。正常手动 Unicode、内部空格及历史资产不迁移。
- 自定义项只随当前草稿存在，刷新不恢复，保持既有草稿生命周期；没有浏览器持久预设。
- AI 候选、VoiceDesign、样本和 NAS 负例主要依靠原受控回归；本次未再次请求真实 LLM/VoiceDesign，也未实机卸载、制造磁盘满或断电、验证最小系统版本/移动硬件。
