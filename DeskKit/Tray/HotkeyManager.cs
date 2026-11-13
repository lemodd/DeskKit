using System.Windows.Interop;
using MiniPix.Native;

namespace MiniPix.Tray;

/// <summary>Registers system-wide hotkeys via a hidden message window.</summary>
public sealed class HotkeyManager : IDisposable
{
    public const int HK_REGION = 1;
    public const int HK_REGION2 = 2;
    public const int HK_FULL = 3;
    public const int HK_FULL2 = 4;
    public const int HK_PASTE_PIN = 5;
    public const int HK_QUICKG = 6; // DeskKit 合并新增：Ctrl+G 对话框跳转（源 CtrlG）
    public const int HK_TOGGLE_SHELF = 7; // DeskKit 升级阶段3：唤出/隐藏暂存架（键位可配置，默认 F2）
    public const int HK_TAB1 = 8;   // DeskKit 2026-11-12：Alt+1 切「图」标签
    public const int HK_TAB2 = 9;   // Alt+2 切「文」标签
    public const int HK_TAB3 = 10;  // Alt+3 切「设备」标签

    private readonly HwndSource _source;
    private readonly Action<int> _onHotkey;

    public HotkeyManager(Action<int> onHotkey)
    {
        _onHotkey = onHotkey;
        var param = new HwndSourceParameters("MiniPixHotkeyWindow", 0, 0)
        {
            WindowStyle = unchecked((int)0x80000000 /* WS_POPUP */),

        };
        _source = new HwndSource(param);
        _source.AddHook(WndProc);

        // 固定热键（不开放设置）：备用截图/全屏/粘贴贴图/Ctrl+G
        Register(NativeMethods.VK_A, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, HK_REGION2);  // Ctrl+Alt+A
        Register(NativeMethods.VK_C, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, HK_FULL);   // Ctrl+Shift+C
        Register(NativeMethods.VK_C, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, HK_FULL2);    // Ctrl+Alt+C
        Register(NativeMethods.VK_V, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, HK_PASTE_PIN);// Ctrl+Alt+V
        Register(NativeMethods.VK_G, NativeMethods.MOD_CONTROL, HK_QUICKG);                           // Ctrl+G（DeskKit 新增）
        // 可调热键（截图/暂存架/三个标签）由 App 启动后按配置调 ApplyUserHotkeys 注册（2026-11-12 设置页可改）
    }

    /// <summary>按用户配置注册可调热键（截图 / 弹出收回暂存架 / 三个标签）。
    /// 重复调用安全：先反注册再注册。返回是否全部注册成功（失败=键位被其他程序占用或互相冲突）。</summary>
    public bool ApplyUserHotkeys(int capVk, uint capMods, int shelfVk, uint shelfMods,
        int t1Vk, uint t1Mods, int t2Vk, uint t2Mods, int t3Vk, uint t3Mods)
    {
        bool ok = true;
        ok &= ReRegister(HK_REGION, capVk, capMods);
        ok &= ReRegister(HK_TOGGLE_SHELF, shelfVk, shelfMods);
        ok &= ReRegister(HK_TAB1, t1Vk, t1Mods);
        ok &= ReRegister(HK_TAB2, t2Vk, t2Mods);
        ok &= ReRegister(HK_TAB3, t3Vk, t3Mods);
        MiniPix.Core.Log.Write($"hotkeys applied: cap={capVk}/{capMods} shelf={shelfVk}/{shelfMods} " +
                               $"tab1={t1Vk}/{t1Mods} tab2={t2Vk}/{t2Mods} tab3={t3Vk}/{t3Mods} allOk={ok}");
        return ok;
    }

    private bool ReRegister(int id, int vk, uint mods)
    {
        NativeMethods.UnregisterHotKey(_source.Handle, id);   // 未注册过的 id 反注册无害
        if (vk <= 0) return false;
        return Register(vk, mods, id);
    }

    private bool Register(int vk, uint mods, int id)
    {
        if (NativeMethods.RegisterHotKey(_source.Handle, id, mods, (uint)vk))
            return true;
        MiniPix.Core.Log.Write($"hotkey registration failed: id={id} vk={vk} mods={mods}");
        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            _onHotkey(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Ctrl+G 开关（DeskKit 托盘菜单用）：注册/反注册 HK_QUICKG</summary>
    public void SetQuickGEnabled(bool enabled)
    {
        if (enabled) Register(NativeMethods.VK_G, NativeMethods.MOD_CONTROL, HK_QUICKG);
        else NativeMethods.UnregisterHotKey(_source.Handle, HK_QUICKG);
    }

    public void Dispose()
    {
        foreach (var id in new[] { HK_REGION, HK_REGION2, HK_FULL, HK_FULL2, HK_PASTE_PIN, HK_QUICKG, HK_TOGGLE_SHELF, HK_TAB1, HK_TAB2, HK_TAB3 })
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        _source.Dispose();
    }
}
