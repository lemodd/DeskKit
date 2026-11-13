using System;
using System.Windows; // StartupEventArgs 等
using System.Windows.Threading;
using Application = System.Windows.Application;   // WPF Application（避开 WinForms 同名类）
using MessageBox = System.Windows.MessageBox;

namespace DeskKit
{
    /// <summary>
    /// DeskKit 三合一胶水 App：
    ///   QuickG   （源 CtrlG）    ：Ctrl+G 在文件对话框里跳到最近资源管理器目录
    ///   Shelf    （源 ShotShelf）：剪贴板图片/文字自动进暂存架
    ///   Capture  （源 MiniPix）  ：区域/全屏截图进剪贴板、剪贴板贴图 Pin
    ///
    /// 协同关系：MiniPix 截图 → 复制到剪贴板 → ShotShelf 监听器自动收进暂存架，
    /// 两个模块之间不需要任何胶水代码。
    /// </summary>
    public partial class App : Application
    {
        private const string MutexName = "DeskKit.SingleInstance.a3f1c2d9";
        private static Mutex? _mutex;
        private static App? _self;

        private KitTray? _tray;
        private MiniPix.Tray.HotkeyManager? _hotkeys;
        private CtrlG.ExplorerTracker? _tracker;      // QuickG：跟踪最近的资源管理器窗口
        private ShotShelf.MainWindow? _shelf;         // Shelf：暂存架窗口（启动即创建，隐藏）
        private DeskKit.Transfer.TransferService? _transfer; // 2026-11-10：局域网文件传输服务
        // 2026-11-05 持久化：初始值从 config.json 读（原来硬编码 true，托盘关掉重启就丢）
        private bool _quickGEnabled = ShotShelf.AppConfig.Current.QuickGEnabled;
        private bool _lanEnabled = ShotShelf.AppConfig.Current.LanEnabled;

        /// <summary>给 CaptureWindow 等原 MiniPix 代码用的全局气泡通知（原 App.Notifier）</summary>
        public static void Notifier(string message) => _self?._tray?.ShowBalloon(message);

        /// <summary>
        /// DeskKit 修复"截图复制后暂存架出现两张图"：
        /// WPF 的 SetImage 写入 DIB+PNG 双格式会产生两次 WM_CLIPBOARDUPDATE
        /// （ShotShelf handoff 2026-10-26 已记录此现象），原 MiniPix 没有监听器所以无感，
        /// 合并后 ShotShelf 的监听器会收两张。这里在复制期间套用 ShotShelf 的抑制标志。
        /// </summary>
        public static void CopyImageToClipboardSuppressed(System.Windows.Media.Imaging.BitmapSource img)
        {
            if (_self?._shelf != null) _self.RunWithCaptureSuppression(() => MiniPix.Output.ClipboardUtil.SetImage(img));
            else MiniPix.Output.ClipboardUtil.SetImage(img);
        }

        /// <summary>供暂存架工具栏「截图」按钮调用：等同按 F1 触发区域截图（2026-10-30 用户要求）</summary>
        public static void TriggerRegionCapture() => _self?.StartRegionCapture();

        // ================= 2026-11-12 统一菜单（窗口右键 ⇆ 托盘右键同款条目） =================

        /// <summary>Ctrl+G 对话框跳转开关（托盘/窗口右键共用）。</summary>
        public static bool QuickGEnabled => _self?._quickGEnabled ?? false;
        public static void SetQuickG(bool on) => _self?.SetQuickGEnabled(on);

        /// <summary>局域网传输开关（托盘/窗口右键共用）。</summary>
        public static bool LanEnabled => _self?._lanEnabled ?? false;
        public static void SetLan(bool on) => _self?.SetLanEnabled(on);

        /// <summary>全屏截到剪贴板（托盘/窗口右键共用）。</summary>
        public static void CaptureFullNow() => _self?.CaptureFullToClipboard();

        /// <summary>剪贴板内容贴图（托盘/窗口右键共用）。</summary>
        public static void PastePinNow() => _self?.PasteToPin();

        /// <summary>帮助窗口：渲染 exe 旁 Help.md（随发布更新，托盘/窗口右键共用）。</summary>
        public static void ShowHelp() => ShotShelf.HelpWindow.ShowOrActivate(_self?._shelf);

        /// <summary>关于对话框（托盘/窗口右键共用；版本/编译时间来自构建时生成的 BuildInfo）。</summary>
        public static void ShowAbout()
        {
            MessageBox.Show(
                $"DeskKit 桌面工具箱 v{BuildInfo.Version}\n" +
                $"编译时间：{BuildInfo.BuiltAt}\n\n" +
                "· CtrlG —— 快捷跳转（Ctrl+G 跳转对话框按钮）\n" +
                "· ShotShelf —— 剪贴板暂存架\n" +
                "· MiniPix —— 截图 / 贴图 / 标注 / OCR 识文\n" +
                "· QuickG —— 资源管理器粘贴即存文件\n\n" +
                "OCR 引擎：RapidOCR PP-OCRv5（ONNX，本地离线识别）\n\n" +
                "本程序为开源软件（GPL-3.0），源码见 GitHub 发布页。\n" +
                "使用的开源组件：\n" +
                "· RapidOcrNet、PaddleOCR PP-OCRv5 识别模型 —— Apache License 2.0\n" +
                "· ONNX Runtime —— MIT License\n" +
                "· QRCoder —— MIT License\n" +
                "· SkiaSharp —— MIT License\n" +
                "· .NET 8 桌面运行时 —— MIT License",
                "关于 DeskKit",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>贴图 Pin 创建时同步把图收进暂存架（2026-10-30 用户要求）。
        /// 走暂存架的指纹去重，"复制后立刻钉住"不会出现两张。</summary>
        public static void AddImageToShelf(System.Windows.Media.Imaging.BitmapSource img)
        {
            try { _self?._shelf?.AddExternalImage(img); }
            catch (Exception ex) { MiniPix.Core.Log.Write("AddImageToShelf failed: " + ex.Message); }
        }

        /// <summary>抑制暂存架收纳期间执行 action，结束后排空消息队列再解除（同 ShotShelf 防回环机制）</summary>
        private void RunWithCaptureSuppression(Action action)
        {
            if (_shelf == null) { action(); return; }
            _shelf.SuppressCapture = true;
            try { action(); }
            finally
            {
                // 排空消息队列：让迟到的 WM_CLIPBOARDUPDATE 在抑制期内跑完
                Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                _shelf.SuppressCapture = false;
            }
        }

        /// <summary>后台 STA 线程写文本剪贴板（UI 零阻塞）。2026-11-11 为聊天窗复制而建；
        /// 2026-11-12 三修：内核换 Win32 直写 TryCopyTextWin32——WPF OLE 剪贴板内部自带
        /// 10×100ms 重试，被云剪贴板/剪贴板管理器占用时一调就是"卡死数秒"，外层再重试会叠出
        /// 十几秒；且 OLE 数据对象有延迟渲染，写线程退出后粘贴方请求数据会死等。
        /// Win32 直写：OpenClipboard 重试（50ms/次）→ Empty → SetClipboardData，
        /// 成功即数据归系统，无延迟渲染、状态即最终状态（OCR 窗口 2026-11-05 同款方案，零翻车）。</summary>
        public static void CopyTextToClipboard(string text, Action? onFailed, Action? onOk = null)
        {
            var worker = new System.Threading.Thread(() =>
            {
                bool ok = TryCopyTextWin32(text);
                if (!ok)
                {
                    // 兜底：WPF OLE 不带 flush（几乎不会走到；数据由本线程短暂持有）
                    try { Clipboard.SetDataObject(text); ok = true; }
                    catch { }
                }
                // 2026-11-13：_self! 判空（关机竞态下 _self 可能已置 null，! 会 NRE）
                _self?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ok) onOk?.Invoke(); else onFailed?.Invoke();
                }));
            });
            worker.IsBackground = true;
            worker.Start();
        }

        /// <summary>Win32 直写文本剪贴板（2026-11-12，OCR 窗口 TryCopyAll 同款）：
        /// OpenClipboard 重试 20×50ms=1s（每次失败用 GetOpenClipboardWindow 记录占用进程，揪元凶）
        /// → EmptyClipboard → SetClipboardData(CF_UNICODETEXT)，成功后内存归系统所有。</summary>
        public static bool TryCopyTextWin32(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    if (attempt % 5 == 0) LogClipboardBlocker(); // 诊断：谁占着剪贴板
                    System.Threading.Thread.Sleep(50);
                    continue;
                }
                try
                {
                    if (!EmptyClipboard()) return false;

                    IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)((text.Length + 1) * 2));
                    if (hMem == IntPtr.Zero) return false;

                    IntPtr ptr = GlobalLock(hMem);
                    if (ptr == IntPtr.Zero) { GlobalFree(hMem); return false; }
                    try
                    {
                        System.Runtime.InteropServices.Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                        System.Runtime.InteropServices.Marshal.WriteInt16(ptr, text.Length * 2, 0); // 终结符
                    }
                    finally { GlobalUnlock(hMem); }

                    // 成功后内存归系统所有（不可再 GlobalFree）；失败则自己释放
                    if (SetClipboardData(CF_UNICODETEXT, hMem) != IntPtr.Zero) return true;
                    GlobalFree(hMem);
                    return false;
                }
                finally { CloseClipboard(); }
            }
            return false;
        }

        /// <summary>把当前占用剪贴板的窗口/进程记入日志（诊断"复制失败"元凶）</summary>
        private static void LogClipboardBlocker()
        {
            try
            {
                IntPtr w = GetOpenClipboardWindow();
                if (w == IntPtr.Zero) { MiniPix.Core.Log.Write("clipboard blocked by: (no window, transient)"); return; }
                GetWindowThreadProcessId(w, out uint pid);
                string name;
                try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
                catch { name = "?"; }
                MiniPix.Core.Log.Write($"clipboard blocked by pid={pid} {name} hwnd={w}");
            }
            catch { }
        }

        // ---- Win32 剪贴板直写 P/Invoke（2026-11-12，与 OcrResultWindow 同套）----
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool EmptyClipboard();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetOpenClipboardWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        /// <summary>UI 线程写剪贴板 + 重试（2026-11-12）：他方剪贴板管理器（输入法/IM/剪贴板历史）
        /// 会短暂占用导致 CLIPBRD_E_CANT_OPEN，实测暂存架复制文本 100% 失败。
        /// UI 线程不能久等：10 次 × 30ms（与 OCR 窗口 Win32 重试同策略），成功通常第 1~2 次。</summary>
        public static void ClipboardSetWithRetry(DataObject data)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(data, true); // copy=true：程序退出后仍保留
                    return;
                }
                catch (System.Runtime.InteropServices.COMException ex)
                    when ((uint)ex.ErrorCode == 0x800401D0) // CLIPBRD_E_CANT_OPEN
                {
                    if (attempt >= 10) throw; // 300ms 还拿不到就报给上层（红闪反馈）
                    System.Threading.Thread.Sleep(30);
                }
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // ---- 单实例 ----
            _mutex = new Mutex(true, MutexName, out bool first);
            if (!first)
            {
                MessageBox.Show("DeskKit 已经在运行（见系统托盘）。", "DeskKit",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            _self = this;

            // 启动异常不该无声杀掉整个会话
            DispatcherUnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                MiniPix.Core.Log.Write("unhandled: " + args.ExceptionObject);

            // ---- 日志滚动（沿用 CtrlG 的 log.txt + .old 机制，路径已改为 DeskKit） ----
            CtrlG.Util.Roll();
            CtrlG.Util.Log("=== DeskKit started pid=" + Environment.ProcessId + " ===");

            // ---- QuickG：资源管理器窗口跟踪（WinEvent hook，须在 UI 线程装） ----
            try
            {
                _tracker = new CtrlG.ExplorerTracker();
                _tracker.Start();
            }
            catch (Exception ex) { CtrlG.Util.Log("tracker init failed: " + ex); }

            // ---- 粘贴即存文件（2026-11-04 方案B）：Explorer 文件列表里 Ctrl+V 把纯图/纯文字存成文件 ----
            try
            {
                CtrlG.PasteAsFile.Start();
            }
            catch (Exception ex) { CtrlG.Util.Log("paf init failed: " + ex); }

            // ---- Shelf：暂存架窗口（2026-10-30 用户要求：双击启动时直接显示窗口，不再藏进托盘） ----
            try
            {
                _shelf = new ShotShelf.MainWindow();
                _shelf.Show(); // 启动即显示；关闭窗口时 MainWindow 内部会隐藏回托盘
            }
            catch (Exception ex)
            {
                CtrlG.Util.Log("shelf init failed: " + ex);
                MiniPix.Core.Log.Write("shelf init failed: " + ex);
            }

            // ---- 局域网文件传输（2026-11-10）：常开可托盘关闭；首次监听 Windows 防火墙会弹授权，允许一次即可 ----
            if (_lanEnabled)
            {
                try
                {
                    _transfer = DeskKit.Transfer.TransferService.Start();
                }
                catch (Exception ex) { MiniPix.Core.Log.Write("lan init failed: " + ex); }
            }

            // ---- 设备互联客户端（2026-11-12）：记住过服务器地址则自动重连（免配对） ----
            try
            {
                DeskKit.Transfer.LanClient.StartAuto();
            }
            catch (Exception ex) { MiniPix.Core.Log.Write("lanclient init failed: " + ex); }

            // ---- 统一托盘 ----
            try
            {
                _tray = new KitTray(
                    showShelf: ShowShelf,
                    captureRegion: StartRegionCapture,
                    captureFullToClipboard: CaptureFullToClipboard,
                    pasteToPin: PasteToPin,
                    getQuickGEnabled: () => _quickGEnabled,
                    setQuickGEnabled: SetQuickGEnabled,
                    getLanEnabled: () => _lanEnabled,
                    setLanEnabled: SetLanEnabled,
                    exit: ExitApp,
                    // 2026-11-12 统一菜单：托盘也具备窗口右键的全部暂存架操作
                    clearAll: () => _shelf?.ClearAllFromTray(),
                    openSaveDir: () => _shelf?.OpenSaveDirFromTray(),
                    getTopmost: () => _shelf?.IsTopmost ?? false,
                    setTopmost: v => _shelf?.SetTopmostFromTray(v),
                    openSettings: () => _shelf?.OpenSettingsFromTray());
            }
            catch (Exception ex) { MiniPix.Core.Log.Write("tray init failed: " + ex); }

            // ---- 统一热键（MiniPix 的 HwndSource 方案，加了 Ctrl+G） ----
            try
            {
                _hotkeys = new MiniPix.Tray.HotkeyManager(OnHotkey);
                ApplyUserHotkeys();   // 2026-11-12：可调热键（截图/暂存架/标签）按配置注册
            }
            catch (Exception ex) { MiniPix.Core.Log.Write("hotkey init failed: " + ex); }

            MiniPix.Core.Log.Write("startup complete");
            CtrlG.Util.Log("DeskKit startup complete");

            // 2026-11-13 S-H1 修复：系统关机/注销（WM_QUERYENDSESSION）也会触发 WPF 的 Closing，
            // 原先 MainWindow.OnClosing 无条件 e.Cancel 会拦截会话结束（"此应用阻止关机"）。
            // 这里放行：标记真退出并正常走释放链路。
            SessionEnding += (_, _) =>
            {
                MiniPix.Core.Log.Write("session ending: allow window close");
                _shelf?.ExitApp();
            };
        }

        // ================= 热键分发 =================

        private void OnHotkey(int id)
        {
            MiniPix.Core.Log.Write($"hotkey {id}");
            switch (id)
            {
                case MiniPix.Tray.HotkeyManager.HK_REGION:
                case MiniPix.Tray.HotkeyManager.HK_REGION2:
                    StartRegionCapture();
                    break;
                case MiniPix.Tray.HotkeyManager.HK_FULL:
                case MiniPix.Tray.HotkeyManager.HK_FULL2:
                    CaptureFullToClipboard();
                    break;
                case MiniPix.Tray.HotkeyManager.HK_PASTE_PIN:
                    PasteToPin();
                    break;
                case MiniPix.Tray.HotkeyManager.HK_QUICKG:
                    QuickGJump();
                    break;
                case MiniPix.Tray.HotkeyManager.HK_TOGGLE_SHELF:
                    ToggleShelf();
                    break;
                case MiniPix.Tray.HotkeyManager.HK_TAB1:
                    _shelf?.ActivateTabByIndex(0);
                    break;
                case MiniPix.Tray.HotkeyManager.HK_TAB2:
                    _shelf?.ActivateTabByIndex(1);
                    break;
                case MiniPix.Tray.HotkeyManager.HK_TAB3:
                    _shelf?.ActivateTabByIndex(2);
                    break;
            }
        }

        // ================= 2026-11-12 快捷键可设置 =================

        /// <summary>把配置里的用户热键应用到系统（截图 F1 / 弹出收回暂存架 F2 / Alt+1/2/3 标签）。
        /// 启动时调用一次；设置页改动后也调（ShelfSettingsWindow 快捷键页）。返回是否全部注册成功。</summary>
        public bool ApplyUserHotkeys()
        {
            if (_hotkeys == null) return false;
            var c = ShotShelf.AppConfig.Current;
            return _hotkeys.ApplyUserHotkeys(
                c.HotkeyCaptureVk, (uint)c.HotkeyCaptureMods,
                c.HotkeyShelfVk, (uint)c.HotkeyShelfMods,
                c.HotkeyTab1Vk, (uint)c.HotkeyTab1Mods,
                c.HotkeyTab2Vk, (uint)c.HotkeyTab2Mods,
                c.HotkeyTab3Vk, (uint)c.HotkeyTab3Mods);
        }

        // ================= 2026-11-02 升级阶段3：Ctrl+Alt+S 唤出/隐藏暂存架 =================

        /// <summary>Ctrl+Alt+S：非贴边窗口=显示/隐藏；贴边窗口=滑出/滑回切换（细节在 MainWindow 里）</summary>
        private void ToggleShelf() => _shelf?.ToggleShelfViaHotkey();

        // ================= Capture（源 MiniPix） =================

        private bool _busy;

        private void StartRegionCapture()
        {
            if (_busy) return;
            _busy = true;
            // 让热键/修饰键先落定，再让截图遮罩抢焦点
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                try { MiniPix.Capture.CaptureController.StartRegionCapture(); }
                catch (Exception ex) { Report(ex); }
                finally { _busy = false; }
            });
        }

        private void CaptureFullToClipboard()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var (desktop, origin) = MiniPix.Core.CaptureEngine.CaptureVirtualDesktop();
                var monitor = MiniPix.Core.MonitorManager.AtPoint(
                    System.Windows.Forms.Cursor.Position.X, System.Windows.Forms.Cursor.Position.Y);
                var crop = MiniPix.Core.CaptureEngine.Crop(desktop, origin, monitor.PxRect);
                var img = MiniPix.Core.CaptureEngine.WithDpi(crop, monitor.DpiX, monitor.DpiY);
                // 2026-10-30 二次修复：不用抑制，交由暂存架图像指纹去重（同截图"复"按钮）
                MiniPix.Output.ClipboardUtil.SetImage(img);
                _tray?.ShowBalloon("已复制当前屏幕到剪贴板");
            }
            catch (Exception ex) { Report(ex); }
            finally { _busy = false; }
        }

        private void PasteToPin()
        {
            if (!MiniPix.Output.ClipboardUtil.TryGetClipboardImage(out var img) || img == null)
            {
                _tray?.ShowBalloon("剪贴板中没有图片");
                return;
            }
            var p = System.Windows.Forms.Cursor.Position;
            MiniPix.Pin.PinWindow.Show(img, new Point(p.X, p.Y));
        }

        // ================= QuickG（源 CtrlG） =================

        private void QuickGJump()
        {
            CtrlG.Util.Log("hotkey fired enabled=" + _quickGEnabled);
            if (!_quickGEnabled) return;
            try
            {
                string path = _tracker?.GetCurrentPath();
                CtrlG.Util.Log("hotkey path=" + (path ?? "<null>"));
                if (string.IsNullOrEmpty(path)) return; // 没有可用的资源管理器位置，静默忽略

                bool ok = CtrlG.DialogHandler.TryNavigate(path);
                CtrlG.Util.Log("TryNavigate=" + ok);
            }
            catch
            {
                // 任何异常都不应导致程序退出
            }
        }

        private void SetQuickGEnabled(bool enabled)
        {
            _quickGEnabled = enabled;
            // 2026-11-05 持久化：写回 config.json（与 MainWindow 共用 AppConfig.Current 单例，不会互相覆盖）
            ShotShelf.AppConfig.Current.QuickGEnabled = enabled;
            ShotShelf.AppConfig.Current.Save();
            try { _hotkeys?.SetQuickGEnabled(enabled); }
            catch { }
            CtrlG.Util.Log("quickG " + (enabled ? "enabled" : "disabled"));
        }

        // ================= 局域网传输（2026-11-10） =================

        private void SetLanEnabled(bool enabled)
        {
            _lanEnabled = enabled;
            ShotShelf.AppConfig.Current.LanEnabled = enabled;
            ShotShelf.AppConfig.Current.Save();
            if (enabled)
            {
                _transfer = DeskKit.Transfer.TransferService.Start(); // Start 内部已写失败日志
            }
            else
            {
                DeskKit.Transfer.TransferService.StopCurrent();
                _transfer = null;
            }
            MiniPix.Core.Log.Write("lan " + (enabled ? "enabled" : "disabled"));
        }

        // ================= Shelf（源 ShotShelf） =================

        private void ShowShelf()
        {
            if (_shelf == null) return;
            _shelf.Show();
            _shelf.WindowState = WindowState.Normal;
            // 升级阶段3：若正贴边隐藏（只留小边），先滑出保证完整可见
            _shelf.EnsureFullyVisible();
            _shelf.Activate();
        }

        // ================= 退出 / 异常 =================

        private void Report(Exception ex)
        {
            MiniPix.Core.Log.Write("ERROR: " + ex);
            System.Diagnostics.Debug.WriteLine(ex);
            _tray?.ShowBalloon("出错了: " + ex.Message);
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Report(e.Exception);
            e.Handled = true;
        }

        private void ExitApp()
        {
            // 先摘托盘（避免残影），再走暂存架的统一退出路径（释放监听 + Shutdown）
            _tray?.Dispose();
            _tray = null;
            _hotkeys?.Dispose();
            _hotkeys = null;
            CtrlG.PasteAsFile.Stop();   // 摘低级键盘钩子
            _tracker?.Dispose();
            _tracker = null;
            DeskKit.Transfer.LanClient.Instance?.Dispose(); // 断开设备互联客户端轮询（2026-11-12）
            DeskKit.Transfer.TransferService.StopCurrent(); // 停局域网传输监听（2026-11-10）
            _transfer = null;

            if (_shelf != null) { _shelf.ExitApp(); } // 内部会 Close + Application.Shutdown
            else Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _tray?.Dispose();
            _hotkeys?.Dispose();
            _tracker?.Dispose();
            DeskKit.Transfer.LanClient.Instance?.Dispose(); // 兜底：断开设备互联客户端（2026-11-12）
            DeskKit.Transfer.TransferService.StopCurrent(); // 兜底：正常退出路径之外的清理
            _mutex?.Dispose();
            _self = null;
            base.OnExit(e);
        }
    }
}
