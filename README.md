# HuaweiPodsWin

HuaweiPodsWin 是 HuaweiPods 的 Windows 桌面移植版，使用 C#、WinUI 3 原生 XAML 控件和 Windows App SDK，通过 Bluetooth Classic RFCOMM 与耳机通信。本仓库 forked from [Nshpiter/HuaweiPods](https://github.com/Nshpiter/HuaweiPods)，沿用上游 GPL-3.0 许可并保留协议、机型和资源的来源说明。

## 使用

1. 在 Windows 的蓝牙设置中配对耳机。
2. 完整解压 x64 发行包，打开其中的 `HuaweiPods.exe`。发行包包含 .NET 和 Windows App SDK 运行时。
3. 应用在没有耳机控制连接时每 10 秒检查 Windows 已连接的蓝牙设备，并尝试读取名称含 `HUAWEI` 的设备。也可在左侧“设备”中手动连接。耳机改名后可打开“显示其他蓝牙设备”开关，尝试自动识别型号，或在连接前手动选择型号。
4. 首页显示官方设备图片、左右耳和充电盒电量，并提供耳机控制。点按“关闭降噪”“透传”或“降噪”即可切换模式，向下滚动可查看当前机型的其他设置。暂时读不到充电盒电量时，打开盒盖后回到“设备”刷新状态。
5. 应用会记住成功连接过的耳机。保持应用运行，耳机再次连上 Windows 蓝牙时，鼠标所在屏幕上方会显示 Mica 弹窗，显示官方图片、耳机名称和本次连接读到的电量。弹窗从屏幕上边缘外用 360 毫秒三次缓出飞入并淡入，自飞入首帧开始计时 3 秒，再用 300 毫秒三次缓入向上飞出并淡出。位移随渲染帧更新，移动过程中保持窗口尺寸不变；首次布局不消耗飞入时间。连续通知先用 130 毫秒淡出旧内容，再淡入新内容；电量刷新不重启动画或延长计时。关闭系统动画时直接显示和隐藏。
6. 在左侧“设置”打开“开机自启”后，当前 Windows 用户登录时会启动 HuaweiPods。默认关闭。移动应用目录后，可关闭再开启以更新启动路径。

目标系统为 Windows 10 2004（19041）及以上。窗口以 11:16 的竖版比例等比缩放，Windows 11 使用系统 Mica 背景。

## 已移植的功能

- 枚举 Windows 已配对设备并建立耳机控制连接，自动识别型号，或按设备记住手动指定的型号。
- 轮询 Windows 已连接的 HUAWEI 设备，通过协议确认后自动读取。手动断开的设备会在下一次被检测到蓝牙断开后重新参与自动连接。
- 实时监听 Windows 蓝牙连接状态，对已记住的耳机显示重连弹窗。应用启动时已经在线的设备仍由名称扫描处理。
- 读取左右耳和充电盒电量，分别放在三个 88 DIP 高度的紧凑方格中。眼镜显示左右镜腿电量。
- 根据型号显示主动降噪、透传、降噪级别及 FreeBuds 3 降噪方向。模式通过首页分段开关直接切换，级别和方向在切换后单独应用。
- 音效预设选择后立即应用并读取耳机确认，无需再点应用按钮。佩戴检测、低时延及重连时恢复低时延使用原生开关，失败时恢复已确认状态。
- 按当前机型显示空间音频及场景、高清音质、音量自适应、掉落提醒、头动控制、单耳降噪、防风噪、双设备连接或自动切换、充电盒提示音、语音控制及耳塞材质等设置。不支持的设置隐藏。
- 按机型提供双击、三击、捏按、长按和滑动等手势设置。只支持写入而不能回读的控制会明确标为“上次发送”。
- 读取当前均衡器和耳机已保存的曲线。FreeBuds SE 4 ANC、6i、7、7i、Pro 5 和 FreeArc 支持编辑自定义音效 1 的十段增益。耳机回读匹配名称与增益后才显示保存成功。
- FreeBuds 7i 可读取耳机最近上报的配对设备并发送移除请求。设备逐条返回，刷新保留先前记录，未确认的移除不会显示为成功。

型号目录对应上游的 20 个型号。实际连接及可用控制项取决于耳机固件、机型和 Windows 能发现的 RFCOMM 服务。FreeBuds 3 的电量依赖旧版 HFP 通道，Windows 版目前支持该机型的降噪控制。FreeBuds 3、FreeBuds Pro 4 等没有对应控制的状态回读时，界面显示上次发送的状态。

FreeClip 2 的自定义均衡器写入在上游依赖 Android 官方组件，Windows 版提供曲线读取并隐藏编辑入口。缺少经过验证的直接写入协议时，同样只提供读取。Android / HyperOS 系统集成、账号服务及固件升级尚未移植。实际蓝牙音质也取决于 Windows 和驱动支持的编码。

## 本地官方图片

发行包内置从华为官方资源目录预取的 198 张原始 PNG，覆盖 17 个型号和 68 个配色或资源变体。应用通过型号和配色匹配本地图片，并校验 SHA-256。配色缺失时优先显示该型号的白色图片，其次显示第一张通过校验的图片。也可在“设备”手动选择已收录的官方配色。

预取脚本沿用上游的官方目录查询、资源包大小校验和图片选择流程。Eyewear 2 的 `0A`、`0B` 及 FreeArc 的 `13` 资源包与官方声明大小相差超过上游允许的 1%，因此未收录。FreeBuds Pro 4、初代 FreeClip 和初代 Eyewear 缺少经上游验证的官方目录编号。

```powershell
python .\tools\PrefetchArtwork\prefetch.py
python .\tools\PrefetchArtwork\prefetch.py --verify
```

## 构建与验证

在 Windows 上使用 .NET 8 SDK。可打开 `HuaweiPods.sln`，也可在项目目录运行以下命令。

```powershell
dotnet build .\HuaweiPods\HuaweiPods.csproj -c Release -p:Platform=x64
dotnet run --project .\tools\ProtocolSmoke\ProtocolSmoke.csproj -c Release
dotnet run --project .\tools\AdvancedSettingsSmoke\AdvancedSettingsSmoke.csproj -c Release
dotnet run --project .\tools\StartupSmoke\StartupSmoke.csproj -c Release
dotnet publish .\HuaweiPods\HuaweiPods.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o .\artifacts\HuaweiPods-win-x64
dotnet publish .\HuaweiPods\HuaweiPods.csproj -c Release -p:Platform=ARM64 -r win-arm64 --self-contained true -o .\artifacts\HuaweiPodsWin-arm64
```

发布目录需要整体分发。ARM64 可将发布参数中的 `x64` 和 `win-x64` 替换为 `ARM64` 和 `win-arm64`。

协议冒烟测试使用上游录制的报文验证帧分片、校验、机型识别、电量、降噪、音效预设、佩戴检测、低时延、手势、均衡器和配对列表。高级设置测试覆盖查询、写入、状态回读及型号能力约束。开发环境已验证 x64 构建，并以模拟传输验证状态回填、失败回滚和会话隔离。华为耳机实机收发仍待验证。

## 上游与许可证

协议、机型数据和图标来自 Nshpiter 的 [HuaweiPods](https://github.com/Nshpiter/HuaweiPods)，移植依据为 [dc5b9d1](https://github.com/Nshpiter/HuaweiPods/tree/dc5b9d14e9030be95b178699d674d1a3217e1e6d)。本项目沿用 [GPL-3.0](LICENSE)。设置和关于图标源自 Miuix，详见 [Apache-2.0 许可证](HuaweiPods/Assets/Interface/LICENSE-Miuix.txt)。官方设备图片源自华为官方资源服务。

侧栏采用 [Yuandl.ThemeUI](https://github.com/lixiaoliyubo/Yuandl.ThemeUI) 的组件结构和选中样式，使用 WinUI 3 原生控件移植。详见 [MIT 许可证](HuaweiPods/Assets/Interface/LICENSE-Yuandl.ThemeUI.txt)。

首页设备卡片、设置行和重连弹窗的排版参考 [OpenEGo Hub](https://github.com/NihilDigit/openego-hub) 的配件页及键盘连接提示。

重连弹窗动画移植自 OpenEGo Hub 的[笔连接提示实现](https://github.com/NihilDigit/openego-hub/blob/c3cab6b87dcffb71a3be00be88365ee479ef1d9a/Tools/EGoTouchSettings/NotificationWindow.xaml.cpp)，保留耳机的横向排版和 Mica 背景。详见 [MIT 许可证](HuaweiPods/Assets/Interface/LICENSE-OpenEGo-Hub.txt)。
