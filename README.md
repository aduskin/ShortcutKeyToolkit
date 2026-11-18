# Windows 全局快捷键管理

`ShortcutKeyToolkit` 是面向 Windows 桌面程序的 .NET 8 类库，没有第三方包依赖。类库在独立线程上调用 Win32 `RegisterHotKey` 并处理 `WM_HOTKEY`，可用于 WPF、WinForms 或有交互式桌面会话的控制台程序。窗口失焦或最小化后，已注册的热键仍可触发。相关行为见 [RegisterHotKey 文档](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-registerhotkey)。

## 运行 WPF 示例

在 Windows 上安装 .NET 8 SDK 或可构建 .NET 8 的较新 SDK。运行示例需要 .NET 8 Desktop Runtime。

```powershell
dotnet build ShortcutKeyToolkit.sln
dotnet run --project ShortcutKeyToolkit.Demo
```

1. 点击「注册快捷键」注册默认的 `Ctrl + Shift + K`。切到其他窗口，按下组合键，切回后查看触发次数。
2. 点击「添加快捷键」，输入新的动作名称并选择另一组按键，再点击「注册快捷键」。列表会同时保留两条绑定。若保留原组合并点击「检测冲突」，可看到占用该组合的动作。也可以用第二个示例实例检测同一组合，观察 Windows 返回的占用结果。
3. 选中已注册的记录，修改组合键并点击「保存修改」。若新组合被占用，原绑定会继续生效。点击对应记录右侧的「移除」可释放组合键。

## 在其他项目中引用

将 `ShortcutKeyToolkit/ShortcutKeyToolkit.csproj` 添加为项目引用。建议为每个应用创建一个长期使用的 `GlobalHotKeyManager`，在应用退出时调用 `Dispose()`。动作名称由调用方定义，使用区分大小写的稳定标识。同名保存执行改绑；各应用可自行保存动作名称、修饰键和虚拟键码，并在启动时重新注册。

```csharp
using ShortcutKeyToolkit;

using var manager = new GlobalHotKeyManager();
var shortcut = new HotKey(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x4B); // K
var result = manager.SetHotKey("quick-search", shortcut, () => Console.WriteLine("打开搜索"));

if (!result.Succeeded)
{
    Console.WriteLine($"{result.Status}, Win32={result.NativeError}");
    return;
}

Console.ReadLine(); // 保持应用和管理器存活
```

在 WPF 中，从界面线程创建 `new GlobalHotKeyManager(new DispatcherSynchronizationContext(Dispatcher))`，并用 `KeyInterop.VirtualKeyFromKey` 将 WPF 的 `Key` 转为 Win32 虚拟键码。其他 UI 框架可传入其界面线程的 `SynchronizationContext`，或由回调自行切换到 UI 线程。默认回调在线程池执行，可能重叠。回调异常通过 `Trace` 记录。移除、改绑或释放管理器后，已进入执行的回调可以继续完成。

## 冲突与系统限制

- `SetHotKey` 的返回值代表实际注册结果。`LocalConflict` 给出当前管理器内占用该组合的动作名称。`SystemConflict` 对应 Windows 的 `ERROR_HOTKEY_ALREADY_REGISTERED`。其他原生注册错误通过 `WindowsError` 和 `NativeError` 返回。
- `CheckAvailability(shortcut, replacingName)` 可在编辑时预查冲突。它临时注册并立即释放组合，只反映检查当时的可用性。正式保存仍需判断 `SetHotKey` 的返回值。
- 每个绑定是一个虚拟键加可选的 `Ctrl`、`Alt`、`Shift`、`Win`。默认抑制长按自动重复。整个注册过程遵循 Windows 的全局热键规则，F12 为系统保留键，部分 Win 组合由系统占用。冲突检测的范围是本管理器和 Windows 全局热键注册，前台应用内部的快捷键及键盘钩子由对应应用处理。无修饰键时，普通字母也可能被全局热键占用。
