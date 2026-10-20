# ELC 电量监视

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-8.0--windows-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

读取 2.4G 无线外设的电量，托盘用**带进度弧的圆环图标**显示：弧长表示还剩多少，颜色从红到绿表示状态。

界面与图标规范见 [`design/DESIGN.md`](design/DESIGN.md)，可交互的视觉稿见 [`design/design-system.html`](design/design-system.html)。

---

## 功能

| 功能 | 说明 |
|---|---|
| 托盘圆环图标 | 一个设备一个图标；**弧长 + 颜色双重编码**，暗轨道随主题切换 |
| 悬停提示 | 显示设备名、电量，并提示可双击刷新 |
| 单击 / 双击 | 单击打开主界面，**双击立即刷新**并播放图标旋转动画 |
| 定时刷新 | 默认 5 分钟，可在设置里改（最小 30 秒）；后台刷新不播动画 |
| 低电量预警 | 两级阈值（默认 20% / 10%）弹窗 + 提示音，可换自定义 wav |
| 主题 | 浅色 / 深色 / 跟随系统，运行中实时切换 |
| 关闭行为可选 | 关闭窗口时"最小化到托盘"或"完全退出" |
| 开机自启 | 写 HKCU Run 键，静默启动进托盘 |
| 驱动跳转 | 卡片上"驱动设置"按钮直达厂商网页驱动 |
| 主窗口列表 | 每台设备一张卡片；进度条上标出你设的预警线位置 |

设计上刻意避开的坑：
- **不装内核驱动、不注入进程、不常驻服务**，所以不会和外设厂商软件打架
- HID 句柄用 `FILE_SHARE_READ | FILE_SHARE_WRITE` 共享打开，只发查询命令，不改设备配置，可以和 Armoury Crate 同时运行
- 同一设备的命令串行执行（HID 命令队列本来就不能并发，否则会读到错位的应答）

---

## 下载

到 [Releases](https://github.com/rain1ly/elc-battery/releases/latest) 下载 `ELCBattery.exe` —— 单文件，双击即用，不需要装 .NET。

第一次运行会有 SmartScreen 蓝色警告（未签名程序都会有），点「更多信息 → 仍要运行」。

---

## 直接使用

`publish\` 下有两个版本：

- **ELCBattery-light**（约 150 KB + 少量 dll）— 需要机器上有 .NET 8 桌面运行时
- **ELCBattery-standalone**（约 66 MB）— 单文件，双击即用，不需要装任何东西

**发给别人只发 `publish\ELCBattery-standalone\ELCBattery.exe` 这一个文件**就够了（已实测：单独复制到空目录可正常启动）。对方会看到 SmartScreen 警告（未签名），点「更多信息 → 仍要运行」。

程序数据写在 `%APPDATA%\ELC\`（配置 + 日志），跟 exe 放在哪个目录无关。

---

## 目录结构

```
elc/
├── src/
│   ├── ElcBattery.App/      WPF 界面、托盘、主题
│   │   ├── Themes/          Light.xaml / Dark.xaml（token）+ Controls.xaml（样式）
│   │   ├── Models/          DeviceVm（卡片视图模型）、AppConfig
│   │   ├── Services/        Config / Theme / AutoStart / AppLog
│   │   ├── Tray/            IconRenderer（图标绘制）、TrayIconManager
│   │   └── Views/           AlertWindow（低电量弹窗）
│   └── ElcBattery.Core/     HID 枚举、报告描述符解析、各品牌 Provider
├── design/
│   ├── DESIGN.md            设计规范（token / 组件 / 图标规格）
│   ├── design-system.html   可切换主题的视觉稿
│   └── icons/               生成的图标与动画帧
├── tools/
│   ├── IconPreview/         图标预览生成器（C#），与 IconRenderer 同算法
│   └── *.py                 设备协议探测脚本（纯标准库）
├── config/devices.json      设备协议资料库
└── publish/                 两个发布版本
```

---

## 支持哪些设备

> 本节记录的协议来自**对设备实际通信的观察**（HID 报告的收发），以及厂商公开的网页驱动与社区资料，**不包含任何厂商的代码、固件或二进制文件**。协议是实现互操作所必需的功能性信息。
>
> ROG、Armoury Crate 为华硕（ASUS）的商标，MCHOSE 为迈从的商标，此处仅用于说明兼容性。本项目为独立开发，与上述厂商无隶属、授权或合作关系。

### 已适配

**ROG 龙鳞 2 Ace**（以及所有走 SpeedNova 协议的 ROG 无线外设）

协议是从华硕官方 GearLink 网页驱动的前端 bundle 里还原出来的，不是猜的：

```
发送  WriteFile 到 0xFF01 接口（不能用 HidD_SetOutputReport，见下）
      整条报告 64 字节： [reportId=3][0x12][0x07][0x00][0x00] + 59×0x00
      Report ID 必须从设备动态问出来（HidP_GetValueCaps，不能写死）

接收  从 0xFF01 接口自身的 Input 读（不是 0xFFC1！）
      报文：[0x03][0x12][0x07][0x00][0x00][电量][...]
      [5]    电量百分比（0-100，直接取值）
      [7]    低电量阈值
      [8..9] 电压（小端 u16，单位 mV）
      [10]   ==1 表示充电中
```

**两个实测踩坑点（2026-10-20 验证）**：

1. **必须用 `WriteFile`，不能用 `HidD_SetOutputReport`**。后者对这条命令返回 `true` 像成功了，但设备根本不响应；换成 `WriteFile` 发同样 64 字节，立刻收到电量。
2. **应答从 0xFF01 接口自己回来**，Report ID 与发送时相同（=3）。0xFFC1 事件接口上只有 `0x12 0x01` 的周期性状态事件，等它是等不到的（这是我最初判断错的地方）。

实测样本：`44%` + `3798 mV`，与 Armoury Crate 一致。

### 迈从 MCHOSE（`VID_5253`）

实测型号 **A7 Pro（PID 1021，RealTek 主控）**，协议极简——不用发命令，直接读 Feature Report：

```
HidD_GetFeature(reportId = 0x14)   →   14 3C 3C 00 00 ...
                                       ↑  ↑  ↑
                                    回显  60 60   ← byte[1] = 电量
```

- 配置接口：顶层 `usagePage 0xFF01`，Feature Report 长度 65
- `byte[1]` 是电量，`byte[2]` 同值；读到超出 0–100 就判定为不支持，宁可显示 `--` 也不给错数字
- 同集合上 `0x11` 是状态缓冲、`0x12` 是 DPI/配置回显
- **适配范围**：按 VID `5253` 匹配。老一代 MCHOSE（`VID_3837`）是另一套协议（发 `0x11` 命令、从输入报文 `0x13` 读 `0xE2` 前缀），**未实现**——没有实物验证过的东西不写进代码

**狼蛛 AULA** —— 网页驱动（`heo.aulacn.com`）的 JS 做了混淆，且没有实物可验证，暂时不做。拿到鼠标后按上面的方法扫一次即可。

### 自动识别

实现了标准 HID Battery Strength 的设备（`usagePage 0x0006 / usage 0x0020`）**零配置直接可用**。不少国产 2.4G 外设走这一套。

### 不支持：觅声 天蝎座（Misiom-Scorpio，`VID_3206 / PID_7015`）

程序**不会列出**这个耳机 —— 它不通过 HID 上报电量，用户态读不到，列出来也只是个占位。

排查结论（省得以后重复踩）：

- 5 个 HID 接口全试过：不主动上报、Feature Report 全不可读、`HidD_GetInputReport` 只回设备名字符串
- 电量走 **USB 厂商控制传输**：`bmRequestType=0xC0, bRequest=0x87` → 应答 `[0x87, percent]`（实测 `0x5B`=91，与官方软件显示一致）
- 但该请求发给 USB 设备本身，MI_00 被厂商内核驱动 `shengmang-misiom-service` 独占，没有 WinUSB 接口 → **用户态发不出去**
- 官方软件经 KS 节点 `...\{6994ad04-...}\xmosctl` 转发；复刻需逆向其私有 IOCTL（`0x4708xx`，`METHOD_NEITHER`，入参含用户态指针），有驱动 bugcheck 风险，故不实现

---

## 添加新设备

1. 打开主界面 → 设置 → **查看设备报告**
2. 会导出一份 `elc-hid-report.txt` 并用记事本打开，里面是本机所有 HID 设备的接口、usage page、报告描述符树
3. 找到你的设备，看它的 vendor 集合用的是什么 usage page、有没有 Output report

如果设备用的是标准 Battery Strength（0x0006/0x0020），程序已经自动支持了，不用做任何事。

如果是厂商私有协议，需要在 `src/ElcBattery.Core/Devices/` 下新建一个 Provider：

```csharp
public sealed class MyBrandProvider : IBatteryProvider
{
    public string Id => "my-brand";
    public string DisplayName => "某品牌无线";
    public string? DriverUrl => "https://...";

    public bool Matches(DeviceGroup g) => g.VendorId == 0x1234;

    public async Task<BatteryInfo> ReadAsync(DeviceGroup g, CancellationToken ct)
    {
        // 用 HidDevice.Open + SendOutputReport / ReadInputReportAsync
    }
}
```

然后在 `DeviceRegistry` 构造函数里 `new MyBrandProvider()`，重启即可。

---

## 排查工具（tools/）

程序读不到电量时，用这几个脚本定位问题（需要 Python 3.9+，纯标准库，无第三方依赖）：

| 脚本 | 用途 |
|---|---|
| `python tools/hid_scan.py` | **先跑这个**——列出本机所有 HID 设备，判定是标准电池（零配置可用）还是要逆向厂商协议 |
| `python tools/hid_scan.py 5253` | 只看某个 VID 的接口细节 |
| `python tools/probe_rog.py` | 列出带 0xFF01/0xFFC1 厂商集合的华硕设备 |
| `python tools/sweep_rog.py` | 盲扫 Report ID 1..12，看接收器对哪个 ID 有反应 |
| `python tools/verify_rog.py` | 端到端发一次读电量命令并解析结果 |
| `python tools/sniff_rog.py` | 同时监听所有 vendor 接口，确定应答从哪个接口回来 |
| `python tools/try_send_methods.py` | 对比 `WriteFile` 与 `HidD_SetOutputReport` 哪个真能收到应答 |
| `python tools/mchose_sweep.py` | 扫 Feature Report 找电量字段（迈从就是这么定位的） |
| `python tools/mchose_test.py` | 对迈从配置口发一次查询并打印原始报文 |

图标相关：

```bash
dotnet run --project tools/IconPreview -- design/icons
```

用与 `IconRenderer` **完全相同的绘制算法**生成 `design/icons/preview.png`（深浅托盘对比）与 `design/icons/spinner/` 下的动画帧，并打印每个图标的覆盖率与主色用于自检。改图标参数先看这里，确认后再同步回 `IconRenderer`。

**实测记录（2026-10-20，本机）**：

- 接收器 8 个 HID 接口：`0xFF01`(in/out 64B)、`0xFFC1`(in 21B)、`0xFFC2`(in 21B)、`0xFFC0`(in 21B)、`0xFF02`、`0xFF00`、以及标准键盘/鼠标集合
- **Report ID = 3**：盲扫 1..12 时只有 ID 3 的 `SetOutputReport` 返回成功，其余全部被拒
- 发送命令成功，但 8 个接口同时监听 0 条回传 —— 当时鼠标处于关机/休眠，接收器不会代替鼠标回答电量

⚠️ 所以：**读电量要求鼠标处于开机状态**。鼠标关机时接收器仍在，但电量查不到，程序会显示 `–` 并标注未连接。

---

## 构建

```bash
# 需要 .NET 8 SDK
dotnet build ELC.sln -c Release

# 轻量版（依赖 .NET 8 桌面运行时）
dotnet publish src/ElcBattery.App -c Release -r win-x64 --self-contained false -o publish/ELCBattery-light

# 独立版（单文件，零依赖）
dotnet publish src/ElcBattery.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/ELCBattery-standalone
```

配置和日志位置：`%APPDATA%\ELC\config.json`

---

## 内存占用

托盘程序要一直挂着，占用越小越好。实测（私有内存，即任务管理器里的"专用工作集"）：

| 版本 | 窗口打开时 | 藏进托盘后 |
|---|---|---|
| **light**（框架依赖，150 KB） | 58 MB | **58 MB** |
| standalone（自包含，66 MB） | 193 MB | 161 MB |

**结论：自己用的话跑 light 版，省掉三分之二。** 差别不在程序代码，而在打包方式 —— self-contained 版把整个 .NET 运行时打进 exe，每个进程独占一份；light 版用机器上已装的运行时，那部分页面是共享的，不计入本进程的私有内存。

light 版要求机器上装了 **.NET 8 桌面运行时**（`Microsoft.WindowsDesktop.App 8.0.x`）。用 `dotnet --list-runtimes` 确认；没有的话就用 standalone，或者装一个。

已经做过的优化：

- **窗口藏进托盘后真正销毁**（`ReleaseMainWindow`）：WPF 窗口连同视觉树要占二三十 MB，而它可能一整天都用不上一次。实测 standalone 版关闭窗口后 193 → 161 MB。代价是下次打开要重建，约一两百毫秒。
- **关掉并发 GC**（`ConcurrentGarbageCollection=false`）：省一条常驻 GC 线程及其堆。代价是 GC 时短暂暂停，但程序绝大多数时间在后台，感知不到。
- **IPC 改用线程池等待句柄**：原来为"第二个实例叫醒窗口"开了一条常驻线程，换成 `ThreadPool.RegisterWaitForSingleObject` 后省掉一个线程（1MB 栈 + 内核对象）。

⚠️ **别开 `InvariantGlobalization`**。它本可以省下 ICU 全球化数据约 20–30 MB，但 XAML 默认 `xml:lang="en-us"`，绑定转换值时要查这个区域性，Invariant 模式下不存在，会直接抛 `XamlParseException` 崩在启动阶段（2026-10-20 实测踩过）。真要用，得同时给所有绑定指定 `ConverterCulture`，代价不值当。

没做的：用 `SetProcessWorkingSetSize` 把工作集"修剪"小 —— 那只是把页面换出到磁盘，数字好看，下次访问反而更慢，属于自欺欺人。

## 已知限制

- **只支持 Windows**（Win10 1809+；窗口圆角由 DWM 提供，Win11 上效果最好）
- 接收器在线但设备关机时，图标只剩灰轨道环、不画弧，主窗口标注"未读取到电量"
- 私有协议的设备只能逐个型号适配，没有万能的自动识别
- 电量读取依赖设备在 2.4G 通道上真的上报；如果某个耳机只在底座或蓝牙模式下才给电量，2.4G 模式就是读不到，这不是程序能绕过的
