# MVP 审查修复

Status: resolved

来源：`fc2a000...9feefe0` 的 Standards / Spec 审查。保持现有架构和数据库模型。

## 验收要求

- 登录额度按可信客户端 IP 隔离；同一 IP 的 IPv4 / IPv4-mapped IPv6 表示共享额度。
- 只接受回环代理或显式配置的代理转发 IP / 协议；不可信来源不能通过转发头绕过限流。
- 反馈类型只接受 Bug / Suggestion / Other 名称（忽略大小写）；非法数字和组合输入返回 400，且不保存反馈。
- Steam 票据 JSON 语法或结构异常返回 401；资料 JSON 异常不影响已验证登录，保留已有资料。
- 管理列表支持 pageSize，默认 50、上限 200；翻页和筛选保留页面大小。
- 列表和详情展示非空 Steam 头像；状态中文显示使用同一映射。

## 实施与验证

1. 认证：IP 分区、明确代理信任配置及 HTTP 回归。
2. 输入：枚举名称校验、Steam JSON 类型防护及 HTTP 回归。
3. 管理端：分页参数、头像、共享状态显示；验证实际页面行为。
4. 更新部署说明，构建、运行测试并检查差异。

上一轮独立 .NET 复现已确认限流共享、数字类型被接受和 JSON 结构异常的根因；本轮直接将复现场景固化为回归测试，不重复假设与探针阶段。部署文件中用户已有的 Tunnel、端口和运行库修改予以保留。

## 变更文件

- `src/GameFeedback/Program.cs`：按客户端 IP 限流、显式代理信任配置。
- `src/GameFeedback/Services/FeedbackService.cs`、`SteamAuthService.cs`：输入及 JSON 类型校验。
- `src/GameFeedback/Components/Pages/Admin/FeedbackList.razor`、`FeedbackDetail.razor`、`Components/FeedbackStatusDisplay.cs`：分页、头像和共享状态显示。
- `tests/GameFeedback.Tests/AuthRateLimitAndProxyTests.cs`、`FeedbackEndpointTests.cs`、`SteamLoginTests.cs`、`Infrastructure/FakeSteamHandler.cs`：新增回归与传输桩支持。
- `.env.example`、`docker-compose.yml`、`appsettings.example.json`、`README.md` 及相关规格/安全/测试文档：代理配置和行为说明。

## 验证结果（2026-09-08）

- 修复前认证/代理 HTTP 回归：6 项中 3 项失败，复现跨 IP 配额共享与不可信转发头被接受。
- `dotnet build`：0 警告、0 错误。
- `dotnet test --no-build --no-restore`：71/71 通过，使用真实 PostgreSQL Testcontainers 与 Steam HTTP 桩。
- 隔离 Compose 环境：config、build、up、ps 均成功；`/health` 返回 200，未登录访问管理端返回 302。测试容器、卷和镜像已清理。
- HtmlRenderer + 实际组件 + 隔离 PostgreSQL（205 条反馈）：pageSize=10/0/201 分别显示 10/50/200 行；201 的第 2 页显示 5 行；page=0 归一为 1；组合筛选的第 2 页正确显示 10 行。列表和详情有/无头像节点及中文状态均符合预期。
- 浏览器连接不可用，未验证实际按钮点击或图片网络加载；组件渲染验证没有替代这部分交互检查。
- 独立代码复核未发现新的可操作问题；`git diff --check` 通过。无数据库迁移或新依赖。
