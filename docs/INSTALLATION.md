# 各平台安装与升级

[返回手册](README.md) · [完整功能配置](CONFIGURATION.md) · [常见问题](TROUBLESHOOTING.md)

## 下载与校验

| 平台 | 发布页 | 主要文件 |
| --- | --- | --- |
| Windows 10 / 11 x64 | [Windows 1.4.0](https://github.com/zxwzywzy/alarm-push-center/releases/tag/v1.4.0) | `AlarmPushCenter-Setup-1.4.0-win-x64.exe` |
| Cudy TR3000 v1 / OpenWrt 24.10 ARM64 | [OpenWrt 1.7.0](https://github.com/zxwzywzy/alarm-push-center/releases/tag/openwrt-v1.7.0) | `alarm-push-center_1.7.0-1_aarch64_cortex-a53.ipk` |
| Android 8.0 及以上 | [手机 App 1.2.0](https://github.com/zxwzywzy/alarm-push-center/releases/tag/mobile-v1.2.0) | `AlarmHub-1.2.0-android.apk` |
| iPhone / iOS 15 及以上 | [手机 App 1.2.0](https://github.com/zxwzywzy/alarm-push-center/releases/tag/mobile-v1.2.0) | `AlarmHub-1.2.0-unsigned.ipa`，需要自行签名 |

下载对应 `.sha256` 文件核对完整性。Windows 可用 `Get-FileHash 文件路径 -Algorithm SHA256`；OpenWrt 可用 `sha256sum -c 文件名.sha256`。源码 ZIP 不是安装程序。

上述新版首次安装完成后，后续可使用[应用内更新入口](AUTO_UPDATES.md)，无需每次重新配置；iPhone 仍需自签。

## Windows 客户端

1. 下载 EXE，先关闭正在运行的旧客户端。
2. 双击安装程序，阅读免责声明、选择目录和快捷方式，再安装。安装包由 Inno Setup 制作，包含 x64 自包含 .NET 运行时，无需另装 .NET。
3. 默认安装在 `%LOCALAPPDATA%\Programs\AlarmPushCenter`，以当前用户权限运行。
4. 从开始菜单“工业报警推送中心”启动。正式配置位于 `%LOCALAPPDATA%\AlarmPushCenter\config.json`；安装包不会导入开发电脑的现场配置。
5. 先添加 Mock 设备、保存配置、启动监控，再配置真实 PLC 和通知渠道。首次启动不会自动监控。
6. Windows 端直接通过本机网络连接随身 WiFi 拨号，不要求先经过路由器。详见[电话配置](PHONE.md)。

升级：备份整个 `%LOCALAPPDATA%\AlarmPushCenter`，退出程序后运行新安装包，使用原目录安装。升级与卸载默认保留独立配置目录。若你曾用 `--config` 指定其他文件，需要备份那个目录，并继续使用同一启动参数。程序安装目录内的 `docs` 附带完整手册。

安装包没有购买商业代码签名证书，Windows 可能显示发布者验证提示；请从项目正式发布页下载并核对 SHA256。

## OpenWrt 插件

### 安装前确认

- 当前 IPK 面向 `aarch64_cortex-a53` / `mediatek/filogic` 的 Cudy TR3000 v1，不能作为所有 OpenWrt 型号的通用包。
- SSH 执行 `ubus call system board`、`opkg print-architecture`、`df -h /overlay` 核对系统、架构与空间。
- 自包含运行时和协议库占用较大，建议至少预留 100 MB 供文件及依赖使用，以实际包占用为准。空间不足时先扩容，不能用 `--force-space` 硬装。
- 路由器必须能访问软件源安装依赖；PLC 所在网段也必须从路由器可达。电脑能连接 PLC，不代表路由器也能连接。

### 新装步骤

1. 下载 IPK 和 SHA256，通过 LuCI“系统 → 软件包 → 上传软件包”上传，或用 SCP 上传到 `/tmp`。
2. SSH 安装示例：

```sh
cd /tmp
opkg update
sha256sum -c alarm-push-center_1.7.0-1_aarch64_cortex-a53.ipk.sha256
opkg install ./alarm-push-center_1.7.0-1_aarch64_cortex-a53.ipk
```

3. 刷新 LuCI，在“服务 → 报警推送中心”打开插件。新装默认不开机启动、不自动采集。
4. 点击“启动插件”，进入配置台，或访问 `http://路由器IP:5080`。
5. 安装终端会显示首次随机管理密码。忘记初始值可通过 SSH 执行 `cat /etc/alarm-push-center/web-password`。该文件只适用于仍采用初始密码的情况；改密后请使用新密码或邮箱找回，不把旧文件内容当作当前密码。
6. 登录后在“系统设置 → 账户与安全”修改密码、配置找回邮箱；添加设备后保存，再手动启动采集。

### 开机启动与采集开关

| 开关 | 作用 |
| --- | --- |
| LuCI：启动 / 停止插件 | 开关网页和后台进程；停止后 5080 与手机连接均不可用 |
| LuCI：开机启动插件 | 路由器重启后是否启动该进程 |
| 配置台：启动 / 停止采集 | 进程运行时是否执行 PLC 采集和机器人交互 |
| 配置台：服务启动后自动采集 | 下次进程启动时是否自动进入采集状态 |
| 设备：启用采集 | 当前设备是否参与监控 |

只希望需要时手动使用：关闭两项自动启动偏好，需要时从 LuCI 启动插件、进入网页后启动采集。

### 升级、备份与卸载

1. 在 SSH 中备份配置并下载到电脑，示例：`tar -czf /tmp/alarm-config-backup.tar.gz /etc/alarm-push-center`。备份包含敏感凭据，不要上传公共仓库。
2. 记录开机开关和采集状态，执行 `/etc/init.d/alarm-push-center stop`。
3. 安装新版 IPK。opkg 出现配置冲突时保留现场配置，不用空模板覆盖。
4. 按原需求启动插件，重新登录并核对设备、规则、手机授权和电话网关。服务重启会中断连接，电话计时重新开始。
5. 如需退回旧版，停止服务，安装原 IPK，恢复升级前匹配版本的配置；不要将新结构配置强行套在旧版上。
6. 卸载用 `opkg remove alarm-push-center`。卸载前先备份；是否保留配置以 opkg 提示和实际文件为准。

诊断命令：

```sh
/etc/init.d/alarm-push-center status
/etc/init.d/alarm-push-center start
/etc/init.d/alarm-push-center stop
logread -e alarm-push-center
```

## 安卓 App

1. 下载 APK，不是 EXE 或 IPK。允许浏览器/文件管理器“安装未知应用”，安装后按需要恢复权限设置。
2. 打开“路由与报警”。首次扫码时允许相机权限。
3. 连接报警插件：在 OpenWrt 插件网页进入“系统设置 → 手机连接”，填写手机可访问的插件地址，生成二维码；手机切换“报警”后扫描。二维码只授权报警插件。
4. 同一部手机无法扫描自己的屏幕时，使用“粘贴连接码”，粘贴完整配对内容，核对地址再确认。
5. 连接路由器：在 App“路由器”页另行添加 OpenWrt 地址、用户名和密码，和报警配对分开。

更新安装保持相同包名和签名，可覆盖旧版保留连接资料。项目构建使用长期安卓签名密钥；不要自行换签后期待覆盖安装。若提示签名冲突，先核对来源，卸载会使本机连接资料丢失。

## iPhone App 与自签

1. 下载 `AlarmHub-1.2.0-unsigned.ipa`。它是未签名原生 IPA，不能直接点开安装，也不是 App Store/TestFlight 包。
2. 用你已有的自签工具和合法签名身份为 IPA 签名，并安装到手机。签名配置、设备授权、到期时间由自签工具和签名身份决定。
3. 如系统要求信任开发者或开启开发者模式，按手机系统提示操作。
4. 首次连接本地路由器时允许“本地网络”权限，扫码允许相机权限。
5. 优先使用 HTTPS 地址。当前原生网络策略额外允许 `http://192.168.1.1`；其他明文地址可能被 iOS 拦截，不能靠修改 App 表单绕过系统网络策略。
6. 报警插件扫码与路由器登录步骤同安卓。签名到期后重新签名，是否保留资料取决于包标识与签名安装方式，重新授权前不要删除网页端仍在使用的其他手机。

没有自签环境时，可先在 Safari 打开插件网页，使用浏览器菜单“添加到主屏幕”作为网页快捷入口；它不会因此获得原生 App 的安全存储、原生扫码或后台通知能力。

## 开发者构建

Windows / OpenWrt 需要 .NET 10 SDK；Inno Setup 6/7 用于 EXE。安卓需要 Node.js、JDK 21 与 Android SDK 36；iOS 需要 macOS/Xcode。

```powershell
.\验证本地代码.ps1
.\打包安装程序.ps1 -Version 1.4.0
.\打包OpenWrt程序.ps1 -Version 1.7.0
cd mobile
npm ci
.\scripts\package-android.ps1
```

安卓脚本读取 `mobile/package.json` 的版本，签名默认保存在 `%LOCALAPPDATA%\AlarmPushCenterBuild\AndroidSigning`，独立于源码和构建缓存。换电脑前备份密钥与密码；丢失后无法用原签名更新。

iOS 在仓库根目录执行 `sh mobile/scripts/build-ios.sh`，或触发 GitHub Actions 的 `Build unsigned iPhone app`。生成路径为 `mobile/ios/App/Unsigned/AlarmHub-<版本>-unsigned.ipa`。安装包位于构建目录 `dist`，不应提交进 Git；正式下载见各平台 Releases。
