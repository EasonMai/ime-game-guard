using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ImeGameGuard;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "ImeGameGuard.Singleton", out var created);
        if (!created)
            return;

        ApplicationConfiguration.Initialize();
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var config = AppConfig.Load(configPath);
        if (args.Length > 0 && File.Exists(args[0]))
        {
            var selected = Path.GetFileName(args[0]);
            if (!config.GameProcesses.Contains(selected, StringComparer.OrdinalIgnoreCase))
            {
                config.GameProcesses.Add(selected);
                config.Save(configPath);
            }
        }
        Application.Run(new GuardTrayContext(config, configPath));
    }
}

internal sealed class AppConfig
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalMs { get; set; } = 250;
    public bool MatchFullscreenWindows { get; set; } = true;
    public double MinimumFullscreenCoverage { get; set; } = 0.90;
    public bool DisableImeContext { get; set; } = true;
    public bool BlockImeHotkeys { get; set; } = true;
    public bool OnlineGameListEnabled { get; set; } = true;
    public string OnlineGameListUrl { get; set; } = GameProcessCatalog.DefaultUrl;
    public int OnlineGameListRefreshHours { get; set; } = 168;
    public string OnlineGameListFile { get; set; } = "gameprocessesdb.json";
    public List<string> GameProcesses { get; set; } = new();
    public List<string> ExcludeProcesses { get; set; } = new()
    {
        "explorer.exe", "dwm.exe", "ApplicationFrameHost.exe", "TextInputHost.exe",
        "SearchHost.exe", "SearchApp.exe", "ShellExperienceHost.exe"
    };

    [JsonIgnore]
    public string? LastLoadError { get; private set; }

    public static AppConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                var defaults = new AppConfig();
                defaults.Save(path);
                return defaults;
            }

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            loaded.GameProcesses ??= new List<string>();
            loaded.ExcludeProcesses ??= new List<string>();
            if (string.IsNullOrWhiteSpace(loaded.OnlineGameListUrl))
                loaded.OnlineGameListUrl = GameProcessCatalog.DefaultUrl;
            if (string.IsNullOrWhiteSpace(loaded.OnlineGameListFile))
                loaded.OnlineGameListFile = "gameprocessesdb.json";
            return loaded;
        }
        catch (Exception ex)
        {
            return new AppConfig { LastLoadError = ex.Message };
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}

internal sealed class GuardTrayContext : ApplicationContext
{
    private readonly string _configPath;
    private AppConfig _config;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly GameProcessCatalog _catalog;
    private readonly ForegroundGameGuard _guard;
    private readonly ImeHotkeyBlocker? _hotkeyBlocker;
    private readonly GuardForm _form;

    public GuardTrayContext(AppConfig config, string configPath)
    {
        _config = config;
        _configPath = configPath;
        _catalog = new GameProcessCatalog(_config, AppContext.BaseDirectory);
        _guard = new ForegroundGameGuard(_config, _catalog);
        _hotkeyBlocker = _config.BlockImeHotkeys ? new ImeHotkeyBlocker(_guard) : null;
        _form = new GuardForm(_config, _configPath, _catalog, _guard, Reload);
        _form.Show();

        _statusItem = new ToolStripMenuItem("状态：正在监视") { Enabled = false };
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "游戏输入法守卫",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _tray.DoubleClick += (_, _) => OpenConfig();

        _timer = new System.Windows.Forms.Timer { Interval = Math.Clamp(_config.PollIntervalMs, 50, 2000) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _ = RefreshOnlineListAsync();

        if (!string.IsNullOrWhiteSpace(_config.LastLoadError))
            _tray.ShowBalloonTip(4000, "配置读取失败", _config.LastLoadError, ToolTipIcon.Warning);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开配置文件", null, (_, _) => OpenConfig());
        menu.Items.Add("重新加载配置", null, (_, _) => Reload());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        return menu;
    }

    private void Tick()
    {
        var state = _config.Enabled ? _guard.Update() : GuardState.Disabled;
        _statusItem.Text = state switch
        {
            GuardState.Protected => "状态：已禁用当前游戏输入法",
            GuardState.GameDetected => "状态：已检测到游戏",
            GuardState.Disabled => "状态：已暂停",
            _ => "状态：正在监视"
        };
    }

    private void Reload()
    {
        var oldBlock = _config.BlockImeHotkeys;
        _guard.Restore();
        _config = AppConfig.Load(_configPath);
        _guard.UpdateConfig(_config);
        _form.SyncConfig(_config);
        if (oldBlock != _config.BlockImeHotkeys)
            _tray.ShowBalloonTip(2500, "配置已加载", "输入法热键设置将在重启程序后生效。", ToolTipIcon.Info);
        _timer.Interval = Math.Clamp(_config.PollIntervalMs, 50, 2000);
        _ = RefreshOnlineListAsync();
    }

    private async Task RefreshOnlineListAsync()
    {
        try
        {
            var result = await _catalog.RefreshIfNeededAsync();
            if (result.Updated)
                _tray.ShowBalloonTip(2500, "游戏列表已更新", $"已加载 {_catalog.Count:N0} 个进程名。", ToolTipIcon.Info);
            else if (result.Failed)
                _tray.ShowBalloonTip(3500, "游戏列表更新失败", result.Message, ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(3500, "游戏列表更新失败", ex.Message, ToolTipIcon.Warning);
        }
    }

    private void OpenConfig()
    {
        _form.Show();
        _form.Activate();
        return;
        /*
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", _configPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(3000, "无法打开配置", ex.Message, ToolTipIcon.Error);
        }
        */
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _guard.Restore();
        _hotkeyBlocker?.Dispose();
        _form.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}

internal enum GuardState { Monitoring, GameDetected, Protected, Disabled }

internal sealed class GuardForm : Form
{
    private readonly AppConfig _config;
    private readonly string _configPath;
    private readonly GameProcessCatalog _catalog;
    private readonly ForegroundGameGuard _guard;
    private readonly Action _reload;
    private readonly Label _selectedLabel;
    private readonly Label _statusLabel;
    private readonly TextBox _processBox;
    private readonly CheckBox _fullscreenBox;
    private readonly CheckBox _hotkeysBox;
    private string? _selectedPath;

    public void SyncConfig(AppConfig config)
    {
        _processBox.Text = string.Join(Environment.NewLine, config.GameProcesses);
        _fullscreenBox.Checked = config.MatchFullscreenWindows;
        _hotkeysBox.Checked = config.BlockImeHotkeys;
        RefreshView();
    }

    public GuardForm(AppConfig config, string configPath, GameProcessCatalog catalog, ForegroundGameGuard guard, Action reload)
    {
        _config = config;
        _configPath = configPath;
        _catalog = catalog;
        _guard = guard;
        _reload = reload;
        Text = "游戏输入法守卫";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(620, 410);
        Size = new Size(720, 470);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        var title = new Label { Text = "游戏输入法守卫", Font = new Font("Segoe UI", 18, FontStyle.Bold), AutoSize = true, Location = new Point(24, 20) };
        var subtitle = new Label { Text = "游戏运行时自动保持英文输入状态", AutoSize = true, Location = new Point(26, 58), ForeColor = Color.DimGray };
        _statusLabel = new Label { Text = "状态：正在监视", AutoSize = false, Width = 650, Height = 32, Location = new Point(24, 92), ForeColor = Color.DarkGreen, Font = new Font("Segoe UI", 11, FontStyle.Bold) };
        _selectedLabel = new Label { Text = "尚未选择游戏可执行文件", AutoSize = false, Width = 650, Height = 42, Location = new Point(24, 142), BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(8), AutoEllipsis = true };
        var browse = new Button { Text = "选择游戏可执行文件...", Width = 190, Height = 34, Location = new Point(24, 195) };
        browse.Click += (_, _) => SelectExecutable();
        var apply = new Button { Text = "应用补丁", Width = 140, Height = 34, Location = new Point(224, 195) };
        apply.Click += (_, _) => ApplySelection();
        var launch = new Button { Text = "启动游戏", Width = 140, Height = 34, Location = new Point(374, 195) };
        launch.Click += (_, _) => LaunchGame();

        var processTitle = new Label { Text = "当前保护的进程", AutoSize = true, Location = new Point(24, 250) };
        _processBox = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 650, Height = 72, Location = new Point(24, 274), ReadOnly = true };
        _processBox.Text = string.Join(Environment.NewLine, _config.GameProcesses);
        _fullscreenBox = new CheckBox { Text = "自动识别全屏和无边框窗口", AutoSize = true, Location = new Point(24, 360), Checked = _config.MatchFullscreenWindows };
        _fullscreenBox.CheckedChanged += (_, _) => { _config.MatchFullscreenWindows = _fullscreenBox.Checked; _config.Save(_configPath); _reload(); };
        _hotkeysBox = new CheckBox { Text = "拦截常用输入法切换键", AutoSize = true, Location = new Point(300, 360), Checked = _config.BlockImeHotkeys };
        _hotkeysBox.CheckedChanged += (_, _) => { _config.BlockImeHotkeys = _hotkeysBox.Checked; _config.Save(_configPath); MessageBox.Show(this, "重启程序后输入法热键设置生效。", "配置已保存", MessageBoxButtons.OK, MessageBoxIcon.Information); };
        var refresh = new Button { Text = "重新加载配置", Width = 120, Height = 30, Location = new Point(554, 398) };
        refresh.Click += (_, _) => { _reload(); RefreshView(); };
        Controls.AddRange(new Control[] { title, subtitle, _statusLabel, _selectedLabel, browse, apply, launch, processTitle, _processBox, _fullscreenBox, _hotkeysBox, refresh });
        Shown += (_, _) => RefreshView();
    }

    private void SelectExecutable()
    {
        using var dialog = new OpenFileDialog { Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*", Title = "Select a game executable" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _selectedPath = dialog.FileName;
            _selectedLabel.Text = _selectedPath;
        }
    }

    private void ApplySelection()
    {
        if (string.IsNullOrWhiteSpace(_selectedPath) || !File.Exists(_selectedPath))
        {
            MessageBox.Show(this, "请先选择一个存在的游戏可执行文件。", "游戏输入法守卫", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var process = Path.GetFileName(_selectedPath);
        if (!_config.GameProcesses.Contains(process, StringComparer.OrdinalIgnoreCase))
            _config.GameProcesses.Add(process);
        _config.Save(_configPath);
        _reload();
        RefreshView();
        MessageBox.Show(this, $"已将 {process} 加入保护进程列表。", "补丁已应用", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void LaunchGame()
    {
        if (string.IsNullOrWhiteSpace(_selectedPath) || !File.Exists(_selectedPath))
        {
            MessageBox.Show(this, "请先选择一个存在的游戏可执行文件。", "游戏输入法守卫", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(_selectedPath) { WorkingDirectory = Path.GetDirectoryName(_selectedPath), UseShellExecute = true });
    }

    private void RefreshView()
    {
        var state = _config.Enabled ? _guard.Update() : GuardState.Disabled;
        _statusLabel.Text = state switch
        {
            GuardState.Protected => "状态：已禁用当前游戏输入法",
            GuardState.GameDetected => "状态：已检测到游戏",
            GuardState.Disabled => "状态：已暂停",
            _ => $"状态：正在监视（数据库 {_catalog.Count:N0} 条）"
        };
        _processBox.Text = string.Join(Environment.NewLine, _config.GameProcesses);
    }
}

internal sealed class ForegroundGameGuard
{
    private AppConfig _config;
    private readonly GameProcessCatalog _catalog;
    private IntPtr _window;
    private IntPtr _savedImeContext;
    private bool _detached;
    private bool _gameActive;

    public ForegroundGameGuard(AppConfig config, GameProcessCatalog catalog)
    {
        _config = config;
        _catalog = catalog;
    }

    public void UpdateConfig(AppConfig config)
    {
        _config = config;
        _catalog.UpdateConfig(config);
    }

    public bool IsGameActive => _gameActive;

    public GuardState Update()
    {
        var hwnd = Native.GetForegroundWindow();
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root != IntPtr.Zero)
            hwnd = root;
        if (hwnd == IntPtr.Zero)
        {
            Restore();
            return GuardState.Monitoring;
        }

        if (hwnd != _window)
        {
            Restore();
            _window = hwnd;
        }

        if (!GameWindowDetector.IsGame(hwnd, _config, _catalog.Patterns))
        {
            Restore();
            return GuardState.Monitoring;
        }

        _gameActive = true;

        if (_config.DisableImeContext)
        {
            if (!_detached)
                DisableForWindow(hwnd);
            return _detached ? GuardState.Protected : GuardState.GameDetected;
        }

        SetEnglishMode(hwnd);
        return GuardState.GameDetected;
    }

    private void DisableForWindow(IntPtr hwnd)
    {
        SetEnglishMode(hwnd);
        var previous = Native.ImmAssociateContext(hwnd, IntPtr.Zero);
        if (previous != IntPtr.Zero)
        {
            _savedImeContext = previous;
            _detached = true;
        }
    }

    private static void SetEnglishMode(IntPtr hwnd)
    {
        var himc = Native.ImmGetContext(hwnd);
        if (himc == IntPtr.Zero)
            return;

        try
        {
            Native.ImmSetOpenStatus(himc, false);
            if (Native.ImmGetConversionStatus(himc, out var conversion, out var sentence))
            {
                conversion &= ~(Native.IME_CMODE_NATIVE | Native.IME_CMODE_FULLSHAPE);
                Native.ImmSetConversionStatus(himc, conversion, sentence);
            }
        }
        finally
        {
            Native.ImmReleaseContext(hwnd, himc);
        }
    }

    public void Restore()
    {
        if (_detached && _window != IntPtr.Zero && Native.IsWindow(_window))
            Native.ImmAssociateContext(_window, _savedImeContext);
        _savedImeContext = IntPtr.Zero;
        _detached = false;
        _gameActive = false;
        _window = IntPtr.Zero;
    }
}

internal static class GameWindowDetector
{
    public static bool IsGame(IntPtr hwnd, AppConfig config, IReadOnlyList<string> onlineProcesses)
    {
        if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero)
            return false;

        var processName = GetProcessName(hwnd);
        if (config.ExcludeProcesses.Any(x => WildcardMatch(processName, x)))
            return false;
        if (config.GameProcesses.Any(x => WildcardMatch(processName, x)))
            return true;
        if (onlineProcesses.Any(x => WildcardMatch(processName, x)))
            return true;
        return config.MatchFullscreenWindows && IsFullscreen(hwnd, config.MinimumFullscreenCoverage);
    }

    private static string GetProcessName(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName + ".exe";
        }
        catch { return string.Empty; }
    }

    private static bool WildcardMatch(string value, string pattern)
    {
        value = value.Trim();
        pattern = pattern.Trim();
        if (pattern.Length == 0) return false;
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool IsFullscreen(IntPtr hwnd, double minimumCoverage)
    {
        if (!Native.GetWindowRect(hwnd, out var windowRect)) return false;
        var monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return false;
        var monitorArea = Math.Max(1L, (long)(info.rcMonitor.Right - info.rcMonitor.Left) * (info.rcMonitor.Bottom - info.rcMonitor.Top));
        var intersection = Rectangle.Intersect(
            new Rectangle(windowRect.Left, windowRect.Top, windowRect.Right - windowRect.Left, windowRect.Bottom - windowRect.Top),
            new Rectangle(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top));
        var area = (long)Math.Max(0, intersection.Width) * Math.Max(0, intersection.Height);
        return area / (double)monitorArea >= Math.Clamp(minimumCoverage, 0.5, 1.0);
    }
}

internal sealed class ImeHotkeyBlocker : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint VK_SPACE = 0x20;
    private const uint VK_CONTROL = 0x11;
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private const uint VK_LSHIFT = 0xA0;
    private const uint VK_RSHIFT = 0xA1;
    private const uint VK_MENU = 0x12;
    private readonly ForegroundGameGuard _guard;
    private readonly Native.LowLevelKeyboardProc _callback;
    private readonly HashSet<uint> _down = new();
    private readonly HashSet<uint> _suppressed = new();
    private readonly IntPtr _hook;

    public ImeHotkeyBlocker(ForegroundGameGuard guard)
    {
        _guard = guard;
        _callback = HookCallback;
        _hook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, _callback, Native.GetModuleHandle(null), 0);
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            var vk = data.vkCode;
            var keyUp = message is WM_KEYUP or WM_SYSKEYUP;
            if (!keyUp) _down.Add(vk); else _down.Remove(vk);

            var shouldBlock = _guard.IsGameActive && IsImeHotkey(vk);
            if (shouldBlock) _suppressed.Add(vk);
            if (keyUp && _suppressed.Remove(vk)) shouldBlock = true;
            if (shouldBlock) return (IntPtr)1;
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool IsImeHotkey(uint vk)
    {
        if (vk == VK_SPACE && (_down.Contains(VK_CONTROL) || _down.Contains(VK_LWIN) || _down.Contains(VK_RWIN)))
            return true;
        if ((vk == VK_LSHIFT || vk == VK_RSHIFT) && _down.Contains(VK_MENU))
            return true;
        if (vk == VK_MENU && (_down.Contains(VK_LSHIFT) || _down.Contains(VK_RSHIFT)))
            return true;
        return false;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _down.Clear();
        _suppressed.Clear();
    }
}

internal static class Native
{
    public const int GW_OWNER = 4;
    public const uint GA_ROOT = 2;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int IME_CMODE_NATIVE = 0x0001;
    public const int IME_CMODE_FULLSHAPE = 0x0008;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("imm32.dll")] public static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")] public static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
    [DllImport("imm32.dll")] public static extern bool ImmSetOpenStatus(IntPtr hIMC, bool open);
    [DllImport("imm32.dll")] public static extern bool ImmGetConversionStatus(IntPtr hIMC, out int conversion, out int sentence);
    [DllImport("imm32.dll")] public static extern bool ImmSetConversionStatus(IntPtr hIMC, int conversion, int sentence);
    [DllImport("imm32.dll")] public static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? moduleName);
}
