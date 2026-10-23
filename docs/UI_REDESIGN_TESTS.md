# 界面改版验证记录

日期：2026-10-23。范围：OpenWrt 全部网页、Windows 主要工作区、现有安卓／iPhone 共享页面。下表是预览阶段记录；用户确认发布后追加了文末的 Release 构建验证。本轮未部署现场路由器。

## 本地预览

- 网页工作台：`http://127.0.0.1:5113/`
- 截图总览：`http://127.0.0.1:5113/__review/review.html`
- 登录演示：`http://127.0.0.1:5113/?preview=login`，仅该隔离演示接受密码 `demo`。
- 手机 App 开发预览：`http://127.0.0.1:5175/?preview`

以上地址仅在本机预览进程运行时有效。演示密码不对应路由器或实际报警插件。精选演示截图在 `docs/images/current/`。完整截图、浏览器脚本及 JSON 为本机测试产物，发布整理时归档在源码目录外，不参与产品安装。

## 已通过

| 检查 | 结果 | 边界 |
| --- | --- | --- |
| OpenWrt 浏览器流程 | 82 项通过 | 隔离的内存接口，1440 / 768 / 390px |
| 手机 App 浏览器流程 | 17 项通过 | Vite 开发预览，390 / 412 / 768px |
| Web UI 单元测试 | 13 项通过 | 图表、字典导入与搜索、请求错误、重新登录保留草稿 |
| 手机单元测试 | 2 项通过 | 参数类型和配对二维码边界 |
| Windows 离线断言 | 130 项通过 | 包含导航、设备参数、配置、规则及本地模拟通信 |
| Windows 电话模块检查 | 10 项通过 | 本地模拟网关，未实际呼叫 |
| Windows 与 OpenWrt Debug 构建 | 均通过 | 两者均零警告、零错误；没有运行发布／打包脚本 |

网页流程覆盖：登录成功和失败、密码显隐、脱敏找回邮箱、当前设备草稿、只保存当前设备、历史每页十条、通知通道切换、五个系统分区、邮箱参数展开、空状态添加入口。确认框同时检查默认取消焦点、Tab 焦点限制、关闭后焦点恢复和取消不拨号。全部主要页面检查整体横向溢出；表格允许在容器内部滚动。

手机开发预览覆盖四个入口，确认无未捕获脚本错误，并阻止访问本地以外地址。Windows 另外渲染 1440px 和最小 1120px 窗口，并通过实际 Mock 采集运行约 31 秒生成曲线截图；没有用静态数字冒充现场数据。

## 可复跑的项目检查

在仓库根目录运行：

```powershell
dotnet build AlarmPushCenter/AlarmPushCenter.csproj --no-restore
dotnet build AlarmPushCenter.OpenWrt/AlarmPushCenter.OpenWrt.csproj --no-restore
dotnet AlarmPushCenter/bin/Debug/net10.0-windows/AlarmPushCenter.dll --self-test
node --test AlarmPushCenter.OpenWrt/tests/ui-core.test.cjs AlarmPushCenter.OpenWrt/tests/telemetry-ui.test.cjs AlarmPushCenter.OpenWrt/tests/web-flow.test.cjs
npm --prefix mobile test
```

浏览器脚本使用本机已有的 Playwright 与 Chromium，测试文件和详细逐项结果保存在本机截图目录。该目录没有进入 Git；它包含本轮检查产物而非应用运行必需内容。

## Release 构建与发布检查

- `验证本地代码.ps1`：Windows/OpenWrt Release 均零警告、零错误；14 项网页/API 测试、130 条核心离线断言、10 项桌面电话检查、22 项邮箱找回、41 项电话检查、2 项手机单元测试通过。所有外部设备与拨号使用隔离模拟接口。
- Windows 1.3.0：自包含 win-x64 发布产物再执行自检，通过后由 Inno Setup 生成 EXE，包含免责声明和完整文档。
- OpenWrt 1.6.0：生成 linux-musl-arm64 IPK，核对包版本、ARM64 ELF、LuCI 入口、启动脚本、网页资源和无现场凭据的模板。构建使用裁剪，真实路由器运行仍需现场升级后确认。
- 手机 1.1.0：安卓 Release APK 使用原长期密钥签名并通过 APK v2/v3 校验；iPhone 在 macOS GitHub Actions 构建未签名 IPA，结果以对应发布页和云构建记录为准。
- 文档：执行 `node tools/check-docs.cjs` 检查本地链接；各安装文件附 SHA256。

## 尚未验证

未在实体路由器上升级运行，未连接现场 PLC 或随身 WiFi，未发送机器人消息、验证码或实际拨号。手机仅测试共享 Web 页面，未做 Android／iOS 真机、原生扫码及系统安全区域实测。当前截图与本地预览用于先确认布局和操作；不能替代发布前的设备联调。
