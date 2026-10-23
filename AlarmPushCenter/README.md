# C# 项目

完整使用说明、协议参数、验证方式和功能边界见上一级 [README.md](../README.md)。

从项目根目录启动：

```powershell
dotnet run --project .\AlarmPushCenter\AlarmPushCenter.csproj -- --config .\config.json
```

离线检查：

```powershell
dotnet run --project .\AlarmPushCenter\AlarmPushCenter.csproj -- --self-test
```

正式安装包用根目录 `打包安装程序.ps1` 生成；框架依赖的开发发布可使用 `发布.ps1`。根目录 `启动.cmd` 从源码运行并使用根目录配置，安装版的配置目录独立。完整手册见 [docs](../docs/README.md)。
