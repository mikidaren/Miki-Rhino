# Miki-Rhino

独立的 Rhino 9 Windows AI 建模工作区，基于 Magpie-Rhino 0.4.1 开发候选派生。`SOURCE_SNAPSHOT.json` 记录基础源码的版本和 SHA-256。项目源码公开；包含 IFC 运行环境的完整安装包仍在核对第三方源码和分发许可，尚未公开发行。

## 使用路径

本地测试版可从 Rhino AI 的代理菜单选择 **Miki-Rhino**，也提供 `MikiRhino` 和 `MikiRhinoStandalone` 命令。设置中的「模型连接」打开原生表单，填写 OpenAI-compatible API 基地址、模型 ID 和 API key。API 基地址可包含 `/v1` 或服务自定义前缀。仅填写服务器根地址时补全 `/v1`。远程服务要求 HTTPS，本机回环地址支持 HTTP。允许显式配置无密钥的本机服务。

模型列表读取使用所选地址下的 `/models`，对话使用同一地址下的 `/chat/completions`。用户可直接使用手动填写的模型 ID，模型列表失败不阻断该路径。界面显示所选端点。HTTP 自动跳转和系统代理在模型请求客户端中关闭。更换端点后，继续发送已有对话须确认是否向新地址发送上下文。

### Magpie 路由边界

用户可手动填写 Magpie 暴露的本地兼容端点，并自行提供密钥、模型 ID 或该服务接受的 group ID。界面只展示这个端点实际返回的 `/models` 条目。当前独立版尚未实现 Magpie Agent 连接协议、端点或凭据自动发现，也不保证完整自动组列表、自定义组与策略同步。原 Magpie-Rhino 工程继续独立维护，其原生 Magpie 连接流程由原工程提供。

IFC 操作沿用独立 Python 进程及 `-I -S -B`。设置中的「导出 IFC」使用原生保存对话框调用本地工具。若另一 Rhino 操作正在记录撤销，Miki-Rhino 等待该操作结束后再导出。

## 身份隔离

| 项目 | Miki-Rhino |
| --- | --- |
| 插件 GUID | `20e04364-aa79-4c4c-9d51-830af0ec16c7` |
| 面板 GUID | `e73857e0-e6fe-4390-a509-99a7997f45bb` |
| 命令 GUID | `c56b3e5c-fd1e-4a91-995d-0b5f77bf01e6` |
| 独立面板命令 GUID | `309a7845-50ba-41e4-aaa2-66faa23e38a4` |
| 托管程序集 | `MikiRhino.Plugin`、`MikiRhino.Agent`、`MikiRhino.OpenBim`、`MikiRhino.OpenBim.Rhino` |
| Yak 身份 | `miki-rhino` |
| 配置与执行模式目录 | `%USERPROFILE%/.config/miki-rhino` |
| Native DOM 与 URL | `__rhinoMikiNativeEntry`、`data-rhino-miki-entry`、`rhino-miki://native-entry/` |

为便于对照，上游 C# 文件名与命名空间保留。程序集身份、产品元数据、命令、面板、菜单标记和设置目录分别隔离。两个 OpenBIM 项目仅调整程序集名称，领域源码及 Python worker 保留上游快照。项目内的 BIM 数据协议继续兼容已有模型。

## 密钥

`settings.json` 保存端点、模型和 Windows DPAPI 当前用户加密后的 `protectedApiKey`。每次保存使用临时文件加原子替换。UI 的网页状态仅包含公开端点和模型名，密钥由原生密码输入框处理。配置诊断对密钥脱敏。DPAPI 提供 Windows 用户级保护，同一用户权限下的软件仍有能力访问该用户的数据。移动配置到另一个用户或损坏密文时，需要重新输入密钥。

## 本地构建与检查

公开构建要求支持 .NET 8 目标框架的 SDK。`NuGet.Config` 使用公开的 nuget.org 源，新电脑可以恢复已固定版本的 RhinoCommon 等开发依赖。`packages.lock.json` 记录解析结果，CI 的 `ContinuousIntegrationBuild=true` 开启锁定恢复。正常构建命令为 `pwsh -File scripts/build-local.ps1`，本机已有完整依赖缓存时才额外传入可选的 `-Offline`。

复用已验证的本地 IFC 缓存时，额外传入 `-IfcRuntimePath` 和该缓存目录。脚本在新建的 `dist/local/<id>` 中构建，执行 `Rebuild`、`DebugType=None`、`DebugSymbols=false`，并扫描四个实际托管程序集的 CodeView 路径。`dist/LATEST_BUILD.json` 指向最新完成的候选。完整独立版安装包的 IFC 第三方源码和再分发条件仍在核查。

连接测试运行 `dotnet run --project tests/Connection.Tests`。Agent、Workflow 回归分别位于 `tests/Agent.Tests`、`tests/Workflow.Tests`。浏览器测试运行 `node --test tests/browser/*.test.mjs`，可通过 `PANEL_SCROLL_PLAYWRIGHT` 指向已有 Playwright 包。

`.github/workflows/source-ci.yml` 在 Windows 上配置 .NET 8 与 Node 24，通过公开 NuGet 按锁文件恢复，运行连接、建模、界面和入口共存回归，再构建并检查托管程序集。公开 CI 验证源码与托管构建，不上传尚待审核的 IFC 二进制。本地缓存模式由 `-Offline` 单独选择。

## 原生兼容性与验证边界

Rhino AI 入口采用原生面板的 DOM 兼容层。本机 Rhino 9 测试中，Miki 与 Magpie 同时出现在代理菜单，Miki 返回原生面板后仍可切换到 Magpie；Miki 通过本机模拟的 OpenAI-compatible 服务流式调用并读取 Rhino 文档上下文。浏览器测试覆盖两种注入顺序和忙碌状态。Rhino 官方 UI 或面板生命周期更新仍可能影响兼容层；复杂模型和跨面板并发需要更多验证。

## GitHub Sponsors

`.github/FUNDING.yml` 配置了 `github: mikidaren`。当前公开的 GitHub Sponsors 页面重定向至个人主页，收款能力尚未确认；需要账号所有者完成 GitHub 的赞助账户申请与收款设置。
