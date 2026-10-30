# FN Connect 来源校验验收

已确认旧实现要求 Origin 与后端 Host 匹配（仅允许网关移除端口并要求匹配 Referer），因此代理改写主机名可造成 403。真实 FN Connect 的失败原因和脱敏 Header 特征尚未采集；无法区分实际是 Host 改写、缺失 Referer、null Origin、Fetch Metadata 或 Token 问题。

修复判据：合法 HTTP(S) Origin 且 Sec-Fetch-Site 明确为 same-origin 时允许代理 Host 改写；其他显式值拒绝。缺失 Fetch Metadata 时保留原 Host/Referer 校验。所有写请求仍须通过可信对端、管理员和 CSRF Token 校验。Fetch Metadata 是浏览器来源信号，不作为身份凭据；非浏览器客户端可伪造它，因此仍依赖私有 Socket、网关身份和不可预测 Token。null 或缺失 Origin 不兼容，安全失败关闭。

验证：Windows Release 后端测试 352 项通过；前端构建通过；WSL Linux Release Unix Socket 集成 76 项通过，模拟了 Host 改写、无 Referer、跨站、错误 Token 和非管理员。用户反馈真实 FN Connect 人工实测通过，未提供具体操作和 Header 特征。此次修改尚无真实 LAN 和 Lucky 入口的验收结果。
