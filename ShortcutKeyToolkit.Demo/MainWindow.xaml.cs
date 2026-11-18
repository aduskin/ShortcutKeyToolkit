using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ShortcutKeyToolkit.Demo;

public partial class MainWindow : Window
{
    private static readonly (HotKeyModifiers Modifier, string Label)[] ModifierLabels =
    [
        (HotKeyModifiers.Control, "Ctrl"), (HotKeyModifiers.Alt, "Alt"),
        (HotKeyModifiers.Shift, "Shift"), (HotKeyModifiers.Windows, "Win")
    ];

    private readonly GlobalHotKeyManager _manager;
    private readonly ObservableCollection<BindingRow> _bindings = new();

    public MainWindow()
    {
        InitializeComponent();
        var keys = Enumerable.Range((int)Key.A, 26)
            .Concat(Enumerable.Range((int)Key.D0, 10))
            .Concat(Enumerable.Range((int)Key.F1, 24).Where(key => key != (int)Key.F12))
            .Select(value => (Key)value)
            .Concat([Key.Space, Key.Home, Key.End, Key.Insert, Key.Delete, Key.PageUp, Key.PageDown,
                Key.Left, Key.Up, Key.Right, Key.Down])
            .Select(key => new KeyOption(KeyInterop.VirtualKeyFromKey(key),
                key is >= Key.D0 and <= Key.D9 ? ((int)key - (int)Key.D0).ToString() : key.ToString()))
            .ToArray();
        KeyInput.ItemsSource = keys;
        KeyInput.SelectedItem = keys.First(key => key.Label == "K");
        BindingsGrid.ItemsSource = _bindings;
        _manager = new GlobalHotKeyManager(new DispatcherSynchronizationContext(Dispatcher));
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        BindingsGrid.SelectedItem = null;
        NameInput.Clear();
        NameInput.Focus();
        StatusText.Text = "输入动作名称，选择组合键，然后点击「注册快捷键」。";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var existing = BindingsGrid.SelectedItem as BindingRow;
        var name = existing?.Name ?? NameInput.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "请输入动作名称。";
            NameInput.Focus();
            return;
        }
        if (existing is null && _bindings.Any(item => item.Name == name))
        {
            StatusText.Text = $"「{name}」已存在。请选择其他名称，或选中列表中的该动作进行修改。";
            NameInput.Focus();
            return;
        }

        var key = (KeyOption)KeyInput.SelectedItem;
        var hotKey = ReadHotKey(key);
        var shortcut = string.Join(" + ", ModifierLabels
            .Where(item => (hotKey.Modifiers & item.Modifier) != 0)
            .Select(item => item.Label)
            .Append(key.Label));
        var row = new BindingRow(name, hotKey, shortcut, existing?.HitCount ?? 0);
        var result = _manager.SetHotKey(name, hotKey, () =>
        {
            row.RecordHit();
            StatusText.Text = $"已触发「{row.Name}」（{row.Shortcut}）";
        });
        if (result.Succeeded)
        {
            if (existing is null)
            {
                _bindings.Add(row);
            }
            else
            {
                _bindings[_bindings.IndexOf(existing)] = row;
            }
            BindingsGrid.SelectedItem = row;
        }
        ShowResult(result, $"已保存「{name}」（{shortcut}）");
    }

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        var result = _manager.CheckAvailability(ReadHotKey((KeyOption)KeyInput.SelectedItem),
            (BindingsGrid.SelectedItem as BindingRow)?.Name);
        ShowResult(result, "当前组合可用。保存时会向 Windows 正式注册。");
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BindingRow row })
        {
            var result = _manager.RemoveHotKey(row.Name);
            if (result.Succeeded)
            {
                _bindings.Remove(row);
                if (BindingsGrid.SelectedItem is null)
                {
                    NameInput.Clear();
                }
            }
            ShowResult(result, $"已移除「{row.Name}」");
        }
    }

    private void BindingsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = BindingsGrid.SelectedItem as BindingRow;
        NameInput.IsReadOnly = row is not null;
        SaveButton.Content = row is null ? "注册快捷键" : "保存修改";
        if (row is not null)
        {
            NameInput.Text = row.Name;
            ControlInput.IsChecked = row.HotKey.Modifiers.HasFlag(HotKeyModifiers.Control);
            AltInput.IsChecked = row.HotKey.Modifiers.HasFlag(HotKeyModifiers.Alt);
            ShiftInput.IsChecked = row.HotKey.Modifiers.HasFlag(HotKeyModifiers.Shift);
            WinInput.IsChecked = row.HotKey.Modifiers.HasFlag(HotKeyModifiers.Windows);
            KeyInput.SelectedItem = ((IEnumerable<KeyOption>)KeyInput.ItemsSource)
                .First(key => key.VirtualKey == row.HotKey.VirtualKey);
        }
    }

    private HotKey ReadHotKey(KeyOption key)
    {
        var modifiers = (ControlInput.IsChecked == true ? HotKeyModifiers.Control : HotKeyModifiers.None)
            | (AltInput.IsChecked == true ? HotKeyModifiers.Alt : HotKeyModifiers.None)
            | (ShiftInput.IsChecked == true ? HotKeyModifiers.Shift : HotKeyModifiers.None)
            | (WinInput.IsChecked == true ? HotKeyModifiers.Windows : HotKeyModifiers.None);
        return new HotKey(modifiers, key.VirtualKey);
    }

    private void ShowResult(HotKeyResult result, string successMessage)
    {
        StatusText.Text = result.Status switch
        {
            HotKeyStatus.Success => successMessage,
            HotKeyStatus.InvalidHotKey => "此组合无效，或主键为系统保留键。",
            HotKeyStatus.LocalConflict => $"冲突：此组合已绑定到「{result.ConflictingName}」。",
            HotKeyStatus.SystemConflict => "冲突：此组合已被 Windows 或其他全局热键注册占用。",
            HotKeyStatus.NotFound => "找不到要移除的绑定。",
            HotKeyStatus.CapacityReached => "已达到热键注册数量上限。",
            _ => $"Windows 返回错误 {result.NativeError}。{new Win32Exception(result.NativeError ?? 0).Message}"
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _manager.Dispose();
        base.OnClosed(e);
    }

    private sealed record KeyOption(int VirtualKey, string Label);

    private sealed class BindingRow(string name, HotKey hotKey, string shortcut, int hitCount) : INotifyPropertyChanged
    {
        public string Name { get; } = name;
        public HotKey HotKey { get; } = hotKey;
        public string Shortcut { get; } = shortcut;
        public int HitCount { get; private set; } = hitCount;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void RecordHit()
        {
            HitCount++;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HitCount)));
        }
    }
}
