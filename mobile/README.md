# 路由与报警 · 手机 App

安卓与 iPhone 共用一套界面，原生扫码、HTTP 网络访问与安全凭据存储由 Capacitor 提供。

普通用户请看 [安卓 / iPhone 安装指南](../docs/INSTALLATION.md#安卓-app) 和 [每个手机功能的说明](../docs/MOBILE.md)。本页主要面向开发者。

## 两种连接独立

- 路由器：添加地址、OpenWrt 账号与密码。通过 `/ubus` 读取系统、CPU、内存、流量、无线与终端记录。密码不保存，登录会话保存在系统安全存储。
- 报警插件：使用 OpenWrt 1.4.0 或更新版本（推荐 1.7.0），进入“系统设置 → 手机连接”，生成二维码。二维码两分钟有效、仅可使用一次，只授权报警插件。管理员可撤销手机授权。
- 公网使用 HTTPS；内网 HTTP 只允许私有地址。iPhone 优先使用 HTTPS，当前额外允许默认内网地址 `192.168.1.1`。
- 扫码后支持设备状态、采集趋势、历史报警、单设备参数编辑、采集启停。不会通过二维码取得路由器账号。

## 编译

```sh
cd mobile
npm ci
npm test
npm run build
npx cap sync
node scripts/normalize-ios.js
```

安卓需要 JDK 21、Android SDK 36，运行 `android/gradlew assembleDebug` 生成测试 APK。正式发行需使用自己的长期签名密钥。

iPhone 需要 macOS/Xcode。运行 `sh mobile/scripts/build-ios.sh`，生成 `mobile/ios/App/Unsigned/AlarmHub-1.2.0-unsigned.ipa`，随后自签安装。GitHub Actions 的 `Build unsigned iPhone app` 支持相同云构建。未经签名的 IPA 不能直接安装。

## 数据与限制

- 路由器指标每 5 秒更新；流量与 CPU 使用率需至少两次采样。终端记录不等于当前在线数量。
- App 前台轮询，后台暂停，不包含后台驻留和系统推送。
- App 停止不影响路由器上的独立采集服务；但插件网页服务停用后，App 无法连接插件。
- 授权最多 20 台手机，30 天有效。二维码生成及授权撤销仅允许网页管理员会话。修改管理密码会撤销手机授权。
- Android 使用 Keystore AES-GCM，iPhone 使用 Keychain；服务器仅存令牌哈希，不在二维码中包含管理密码。
- 浏览器开发预览不持久保存授权；实际跨域连接需使用原生 App。
- 监控不能替代现场联锁与硬件报警。操作采集启停前核对目标插件和设备。

界面与功能参考 WrtHub 的路由监控定位，代码为独立实现，未复制其源码或素材。
