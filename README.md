# ThinkBook 16+ 风扇管理

面向 ThinkBook 16+ 2024 的 Windows 风扇曲线与托盘管理工具，以 C# / .NET Framework 编写。

## 适用范围

来自维护者的特定硬件环境；不同年份、BIOS、CPU/GPU 或机型可能拥有不同的 WMI 接口与可写转速范围。本项目不是联想官方软件，不承诺通用兼容。先确认硬件接口和温度读数，再启用手动控制。

## 功能

- CPU/GPU 三分钟滚动均温、九节点转速曲线与温度偏移。
- 接管时 30 秒采样、60 秒线性过渡。
- 瞬时高温保护、转速持续偏离后的重新接管、GPU 读数失败后的恢复重试。
- 独立看门狗与恢复标记、托盘管理、可选登录启动。
- 电池供电和睡眠期间交回固件控制；固定 UTC+8 的 00:00–10:00 暂停手动接管。

温度阈值、转速范围和定时策略包含机型相关的硬编码参数。自动保护不能替代固件保护或硬件验证。

## 构建与运行

Windows x64，系统 .NET Framework 4.x C# 编译器，NVIDIA 温度读取需要可用的 `nvidia-smi`。在 **Windows PowerShell 5.1** 中：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\BuildCurve.ps1
.\FanCurve.exe
```

管理员权限由程序清单申请。普通启动只监控；确认温度、风扇反馈和曲线后，再点击启用。关闭窗口仅隐藏到托盘；退出请用“恢复自动并退出”。配置在首次保存时生成，无需复制维护者的硬件配置。

`curve-settings.json`、恢复标记和日志保存在程序目录，不加入仓库。登录启动由界面注册，移动目录后需重新注册。不附带旧版 CPU 电源计划限制工具。

## 测试

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\RunRegressionTests.ps1
```

回归测试在独立目录检查控制逻辑及界面生命周期，不执行风扇写入。发布准备中的编译/测试不代表新机器上的实机温控验证。

## 隐私、许可与致谢

本项目原创部分采用 [MIT License](LICENSE)。第三方依赖和素材遵守各自许可，详见 [第三方说明](THIRD_PARTY_NOTICES.md)。

感谢 **OpenAI GPT** 与 **DeepSeek** 在开发、排查问题和文档整理中的帮助，详见 [致谢](ACKNOWLEDGEMENTS.md)。本项目为个人工具，与游戏厂商、联想或 AI 模型提供方无官方关联。

真实配置、密钥、日志和截图请留在本机，详见 [隐私说明](PRIVACY.md)。首次发布为源码版本，不包含虚拟环境、游戏文件、驱动或预编译程序。

## 下载可执行版本

从 [Releases](https://github.com/xzk09164418-droid/thinkbook16plus-fan-control/releases) 下载 Windows x64 ZIP 和 SHA256SUMS.txt，解压后先阅读 QUICKSTART.md。无需自行编译；程序未签名，不附带驱动。
