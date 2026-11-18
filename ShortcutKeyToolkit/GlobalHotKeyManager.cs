using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static ShortcutKeyToolkit.NativeMethods;

namespace ShortcutKeyToolkit;

/// <summary>
/// Manages Windows global hotkeys on a dedicated message thread.
/// </summary>
public sealed class GlobalHotKeyManager : IDisposable
{
    private const int MaxId = 0xBFFF;
    private readonly SynchronizationContext? _callbackContext;
    private readonly Thread _thread;
    private readonly uint _threadId;
    private readonly object _gate = new();
    private readonly Queue<(Func<HotKeyResult> Run, TaskCompletionSource<HotKeyResult> Completion)> _commands = new();
    private bool _disposed;
    private Exception? _loopFailure;

    // Accessed only from the message thread.
    private readonly Dictionary<string, Registration> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Registration> _byId = new();
    private readonly HashSet<int> _nativeIds = new();
    private int _lastId;

    /// <param name="callbackContext">Target context for callbacks. Null queues callbacks to the thread pool.</param>
    public GlobalHotKeyManager(SynchronizationContext? callbackContext = null)
    {
        _callbackContext = callbackContext;
        var ready = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => RunMessageLoop(ready))
        {
            IsBackground = true,
            Name = "Global hotkey messages"
        };
        _thread.Start();
        _threadId = ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Registers or replaces a named hotkey. A rejected replacement leaves the existing binding active.
    /// </summary>
    public HotKeyResult SetHotKey(string name, HotKey hotKey, Action callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(callback);
        return Invoke(() => SetCore(name, hotKey, callback));
    }

    /// <summary>
    /// Releases a named hotkey.
    /// </summary>
    public HotKeyResult RemoveHotKey(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Invoke(() => _byName.TryGetValue(name, out var registration)
            ? RemoveCore(registration)
            : new(HotKeyStatus.NotFound));
    }

    /// <summary>
    /// Tests a combination with a temporary registration. The result can change immediately.
    /// </summary>
    public HotKeyResult CheckAvailability(HotKey hotKey, string? replacingName = null) => Invoke(() =>
    {
        var result = Validate(hotKey, replacingName);
        if (!result.Succeeded || (replacingName is not null
            && _byName.TryGetValue(replacingName, out var current) && current.HotKey == hotKey))
        {
            return result;
        }

        result = RegisterNative(hotKey, out var id);
        return result.Succeeded ? UnregisterNative(id) : result;
    });

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                if (_loopFailure is null)
                {
                    PostMessage(StopMessage);
                }
                _disposed = true;
            }
        }
        _thread.Join();
    }

    private HotKeyResult Invoke(Func<HotKeyResult> command)
    {
        var completion = new TaskCompletionSource<HotKeyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loopFailure is not null)
            {
                throw new InvalidOperationException("The hotkey message loop stopped.", _loopFailure);
            }
            PostMessage(RunCommandMessage);
            _commands.Enqueue((command, completion));
        }
        return completion.Task.GetAwaiter().GetResult();
    }

    private void PostMessage(uint message)
    {
        if (!PostThreadMessage(_threadId, message, 0, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private void RunMessageLoop(TaskCompletionSource<uint> ready)
    {
        try
        {
            // PeekMessage creates the thread queue before callers can post to it.
            PeekMessage(out _, 0, 0, 0, 0);
            ready.SetResult(GetCurrentThreadId());

            int result;
            while ((result = GetMessage(out var message, 0, 0, 0)) > 0)
            {
                if (message.Id == StopMessage)
                {
                    return;
                }
                if (message.Id == RunCommandMessage)
                {
                    RunCommand();
                }
                else if (message.Id == HotKeyMessage && _byId.TryGetValue((int)message.WParam, out var registration))
                {
                    var modifiers = (HotKeyModifiers)((int)message.LParam & (int)HotKey.AllModifiers);
                    var virtualKey = ((int)message.LParam >> 16) & 0xFFFF;
                    if (registration.HotKey == new HotKey(modifiers, virtualKey))
                    {
                        QueueCallback(registration);
                    }
                }
            }

            if (result < 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        catch (Exception error)
        {
            ready.TrySetException(error);
            lock (_gate)
            {
                _loopFailure = error;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (!_disposed && _loopFailure is null)
                {
                    _loopFailure = new InvalidOperationException("The hotkey message loop exited unexpectedly.");
                }
                while (_commands.TryDequeue(out var command))
                {
                    command.Completion.SetException(_loopFailure ?? new ObjectDisposedException(nameof(GlobalHotKeyManager)));
                }
            }
            foreach (var registration in _byName.Values)
            {
                registration.Active = false;
            }
            foreach (var id in _nativeIds)
            {
                UnregisterHotKey(0, id);
            }
            _byName.Clear();
            _byId.Clear();
            _nativeIds.Clear();
        }
    }

    private void RunCommand()
    {
        (Func<HotKeyResult> Run, TaskCompletionSource<HotKeyResult> Completion) command;
        lock (_gate)
        {
            if (!_commands.TryDequeue(out command))
            {
                return;
            }
        }
        try
        {
            command.Completion.SetResult(command.Run());
        }
        catch (Exception error)
        {
            command.Completion.SetException(error);
        }
    }

    private HotKeyResult SetCore(string name, HotKey hotKey, Action callback)
    {
        var result = Validate(hotKey, name);
        if (!result.Succeeded)
        {
            return result;
        }

        _byName.TryGetValue(name, out var previous);
        if (previous is not null && previous.HotKey == hotKey)
        {
            previous.Active = false;
            Store(new(previous.Id, name, hotKey, callback));
            return new(HotKeyStatus.Success);
        }

        result = RegisterNative(hotKey, out var id);
        if (!result.Succeeded)
        {
            return result;
        }

        if (previous is not null)
        {
            result = RemoveCore(previous);
            if (!result.Succeeded)
            {
                // If the rollback fails too, the native ID remains tracked for disposal.
                UnregisterNative(id);
                return result;
            }
        }

        Store(new(id, name, hotKey, callback));
        return new(HotKeyStatus.Success);
    }

    private void Store(Registration registration)
    {
        _byName[registration.Name] = registration;
        _byId[registration.Id] = registration;
    }

    private HotKeyResult RemoveCore(Registration registration)
    {
        var result = UnregisterNative(registration.Id);
        if (result.Succeeded)
        {
            registration.Active = false;
            _byName.Remove(registration.Name);
            _byId.Remove(registration.Id);
        }
        return result;
    }

    private HotKeyResult Validate(HotKey hotKey, string? replacingName)
    {
        if (!hotKey.IsValid)
        {
            return new(HotKeyStatus.InvalidHotKey);
        }

        foreach (var registration in _byName.Values)
        {
            if (registration.HotKey == hotKey && registration.Name != replacingName)
            {
                return new(HotKeyStatus.LocalConflict, ConflictingName: registration.Name);
            }
        }
        return new(HotKeyStatus.Success);
    }

    private HotKeyResult RegisterNative(HotKey hotKey, out int id)
    {
        id = 0;
        if (_nativeIds.Count == MaxId)
        {
            return new(HotKeyStatus.CapacityReached);
        }

        do
        {
            _lastId = _lastId % MaxId + 1;
        } while (_nativeIds.Contains(_lastId));

        id = _lastId;
        if (!RegisterHotKey(0, id, (uint)hotKey.Modifiers | NoRepeat, (uint)hotKey.VirtualKey))
        {
            var error = Marshal.GetLastPInvokeError();
            return new(error == HotKeyAlreadyRegistered ? HotKeyStatus.SystemConflict : HotKeyStatus.WindowsError, error);
        }
        _nativeIds.Add(id);
        return new(HotKeyStatus.Success);
    }

    private HotKeyResult UnregisterNative(int id)
    {
        if (!UnregisterHotKey(0, id))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != HotKeyNotRegistered)
            {
                return new(HotKeyStatus.WindowsError, error);
            }
        }
        _nativeIds.Remove(id);
        return new(HotKeyStatus.Success);
    }

    private void QueueCallback(Registration registration)
    {
        // A custom context can run Post inline. Keep caller code off the message thread in either case.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (_callbackContext is null)
                {
                    InvokeCallback(registration);
                }
                else
                {
                    _callbackContext.Post(_ => InvokeCallback(registration), null);
                }
            }
            catch (Exception error)
            {
                Trace.TraceError("Could not dispatch hotkey '{0}': {1}", registration.Name, error);
            }
        });
    }

    private static void InvokeCallback(Registration registration)
    {
        if (!registration.Active)
        {
            return;
        }
        try
        {
            registration.Callback();
        }
        catch (Exception error)
        {
            Trace.TraceError("Hotkey callback '{0}' failed: {1}", registration.Name, error);
        }
    }

    private sealed record Registration(int Id, string Name, HotKey HotKey, Action Callback)
    {
        public volatile bool Active = true;
    }
}