# ShortcutKeyToolkit

适用于 WPF、WinForms 和 Windows 控制台程序的全局快捷键类库。应用失焦或最小化后，已注册的快捷键仍可触发。类库面向 `net8.0-windows`，在独立线程上处理 Win32 热键消息，无第三方包依赖。

## 功能

- 运行时注册多组快捷键，按动作名称修改或移除绑定。
- 检测当前管理器内的重复绑定和 Windows 报告的全局占用。新组合注册失败时保留原绑定。
- 支持 `Ctrl`、`Alt`、`Shift` 和 `Win` 修饰键，抑制长按时的自动重复通知。
- 可指定回调的 `SynchronizationContext`，方便在界面线程更新控件。释放管理器时注销快捷键。

## Quick Start

### 运行 WPF 示例

在 Windows 上使用 .NET 8 SDK 或可构建 .NET 8 的较新 SDK，并安装 .NET 8 Desktop Runtime。在仓库根目录运行：

```powershell
dotnet run --project ShortcutKeyToolkit.Demo
```

1. 点击「注册快捷键」注册默认的 `Ctrl + Shift + K`。切到其他窗口，按下组合键，切回后查看触发次数。
2. 点击「添加快捷键」，输入新的动作名称并选择另一组按键，再点击「注册快捷键」。列表会同时保留两条绑定。若保留原组合并点击「检测冲突」，可看到占用该组合的动作。也可以用第二个示例实例检测同一组合，观察 Windows 返回的占用结果。
3. 选中已注册的记录，修改组合键并点击「保存修改」。若新组合被占用，原绑定会继续生效。点击对应记录右侧的「移除」可释放组合键。

### 在自己的项目中注册快捷键

在应用的 `.csproj` 中将目标框架设为 `net8.0-windows` 或兼容的更新版本，并添加项目引用。以下路径适用于与类库目录同级的应用，其他目录布局请调整相对路径。

```xml
<ItemGroup>
  <ProjectReference Include="..\ShortcutKeyToolkit\ShortcutKeyToolkit.csproj" />
</ItemGroup>
```

将以下代码放入 Windows 控制台应用的 `Program.cs`。启动后切到其他窗口并按 `Ctrl + Shift + K`，再切回控制台查看输出。按 Enter 退出并释放快捷键。

```csharp
using System;
using ShortcutKeyToolkit;

using var manager = new GlobalHotKeyManager();
var shortcut = new HotKey(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x4B); // VK_K
var result = manager.SetHotKey("quick-search", shortcut, () => Console.WriteLine("打开搜索"));

if (!result.Succeeded)
{
    Console.WriteLine($"注册失败：{result.Status}，Win32 错误码：{result.NativeError}");
    return;
}

Console.WriteLine("已注册 Ctrl + Shift + K。按 Enter 退出。");
Console.ReadLine();
```

### 在 WPF 中更新界面

在窗口类中声明 `private readonly GlobalHotKeyManager _manager;`，在构造函数的 `InitializeComponent()` 之后加入下面的代码，并在窗口关闭时调用 `_manager.Dispose()`。

```csharp
_manager = new GlobalHotKeyManager(new DispatcherSynchronizationContext(Dispatcher));
var shortcut = new HotKey(HotKeyModifiers.Control | HotKeyModifiers.Shift,
    KeyInterop.VirtualKeyFromKey(Key.K));
var result = _manager.SetHotKey("quick-search", shortcut, () => Title = "已触发搜索快捷键");
if (!result.Succeeded)
{
    MessageBox.Show($"注册失败：{result.Status}");
}
```

`DispatcherSynchronizationContext` 位于 `System.Windows.Threading`，`KeyInterop` 和 `Key` 位于 `System.Windows.Input`。其他 UI 框架也可传入对应界面线程的 `SynchronizationContext`。默认回调在线程池执行，可能重叠。回调异常通过 `Trace` 记录。完整接入流程见 [WPF 示例](ShortcutKeyToolkit.Demo/MainWindow.xaml.cs)。

## 常用 API

| API | 用途 |
| --- | --- |
| `SetHotKey(name, hotKey, callback)` | 注册快捷键。使用不同名称注册多组，使用已有名称更新绑定。 |
| `CheckAvailability(hotKey, replacingName)` | 检查组合当前是否可用。编辑时可传入原动作名。 |
| `RemoveHotKey(name)` | 移除指定动作的绑定并释放组合键。 |
| `Dispose()` | 释放该管理器的全部快捷键。 |

动作名称区分大小写。调用方可保存动作名称、修饰键和虚拟键码，并在下次启动时重新注册。移除、改绑或释放管理器后，已进入执行的回调可以继续完成。

## 冲突与系统限制

- `SetHotKey` 的返回值代表实际注册结果。`LocalConflict` 给出当前管理器内占用该组合的动作名称。`SystemConflict` 对应 Windows 的 `ERROR_HOTKEY_ALREADY_REGISTERED`。其他原生注册错误通过 `WindowsError` 和 `NativeError` 返回。
- `CheckAvailability(shortcut, replacingName)` 可在编辑时预查冲突。它临时注册并立即释放组合，只反映检查当时的可用性。正式保存仍需判断 `SetHotKey` 的返回值。
- 每个绑定是一个虚拟键加可选的 `Ctrl`、`Alt`、`Shift`、`Win`。默认抑制长按自动重复。整个注册过程遵循 Windows 的全局热键规则，F12 为系统保留键，部分 Win 组合由系统占用。冲突检测的范围是本管理器和 Windows 全局热键注册，前台应用内部的快捷键及键盘钩子由对应应用处理。无修饰键时，普通字母也可能被全局热键占用。
