using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CtrlG
{
    /// <summary>
    /// 通过 Shell.Application (IShellWindows) 读取资源管理器窗口的当前目录。
    /// 优先使用 C# dynamic（IDispatch 后期绑定）；失败时回退到 IEnumVARIANT 手工枚举。
    /// </summary>
    internal static class ShellCom
    {
        private static object _shellApp;

        private static object ShellApp()
        {
            if (_shellApp != null) return _shellApp;
            Type t = Type.GetTypeFromProgID("Shell.Application");
            if (t == null) return null;
            _shellApp = Activator.CreateInstance(t);
            return _shellApp;
        }

        private struct Entry
        {
            public long Hwnd;       // 顶层资源管理器窗口（多标签时所有 tab 相同）
            public long TabHwnd;    // 标签页自己的 ShellTabWindowClass 子窗口（0=取不到）
            public string Path;
        }

        // ---- 2026-11-03 新增：活动标签页精确识别 ----
        // 每个 IShellWindows 条目通过 IServiceProvider → SID_STopLevelBrowser →
        // IShellBrowser.GetWindow() 可以拿到它对应标签页的 ShellTabWindowClass 子窗口句柄；
        // 顶层窗口下"可见"的那个 ShellTabWindowClass 就是活动标签页。两者一比对，
        // 就能精确知道哪个候选是当前显示的标签，不再依赖会滞后的窗口标题去猜。

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProviderCom
        {
            [PreserveSig]
            int QueryService(ref Guid guidService, ref Guid riid,
                [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
        }

        // IShellBrowser 继承 IOleWindow，vtable 第一格就是 GetWindow——只声明这一个即可
        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowserCom
        {
            [PreserveSig] int GetWindow(out IntPtr phwnd);
        }

        private static readonly Guid SidTopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        private static readonly Guid IidShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");

        /// <summary>取一个 shell window 条目对应的标签页子窗口句柄（取不到返回 0）</summary>
        private static long TabHwndOf(object shellWindow)
        {
            try
            {
                Guid sid = SidTopLevelBrowser;  // ref 实参不能用静态只读字段，拷贝到局部变量
                Guid iid = IidShellBrowser;
                if (shellWindow is IServiceProviderCom sp &&
                    sp.QueryService(ref sid, ref iid, out object sbObj) == 0 &&
                    sbObj is IShellBrowserCom sb &&
                    sb.GetWindow(out IntPtr h) == 0)
                {
                    return (long)h;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>顶层资源管理器窗口下"可见"的 ShellTabWindowClass 子窗口 = 活动标签页</summary>
        private static long ActiveTabHwnd(IntPtr topHwnd)
        {
            if (topHwnd == IntPtr.Zero) return 0;
            IntPtr child = IntPtr.Zero;
            while (true)
            {
                child = Native.FindWindowEx(topHwnd, child, "ShellTabWindowClass", null);
                if (child == IntPtr.Zero) break;
                if (Native.IsWindowVisible(child)) return (long)child;
            }
            return 0;
        }

        public static bool TryGetPath(IntPtr hwnd, string windowTitle, out string path)
        {
            path = null;
            try
            {
                List<Entry> entries = Enumerate();
                var candidates = new List<Entry>();
                foreach (Entry e in entries)
                {
                    if (e.Hwnd == (long)hwnd) candidates.Add(e);
                }
                Util.Log("shellcom probe hwnd=" + (long)hwnd + " title=[" + windowTitle + "] entries=" + entries.Count + " cands=" + candidates.Count);

                List<string> crumbs = TitleCrumbs(windowTitle);

                if (candidates.Count == 1)
                {
                    path = candidates[0].Path;
                }
                else if (candidates.Count > 1)
                {
                    // 选项卡资源管理器：同一 HWND 有多个标签页。
                    // 2026-11-03 修复"跳到最后开的标签"：
                    // ① 首选 = 活动标签精确匹配（可见 ShellTabWindowClass ↔ IShellBrowser.GetWindow）
                    long active = ActiveTabHwnd(hwnd);
                    if (active != 0)
                    {
                        foreach (Entry e in candidates)
                        {
                            if (e.TabHwnd == active) { path = e.Path; break; }
                        }
                        Util.Log("shellcom active tab=" + active + " -> " + (path ?? "<no entry match>"));
                    }
                    // ② 次选 = 标题叶名匹配（活动标签识别不可用时的兜底）
                    if (path == null)
                    {
                        foreach (string crumb in crumbs)
                        {
                            foreach (Entry e in candidates)
                            {
                                if (string.Equals(Leaf(e.Path), crumb, StringComparison.OrdinalIgnoreCase))
                                {
                                    path = e.Path;
                                    break;
                                }
                            }
                            if (path != null) break;
                        }
                    }
                    // ③ 最后才退回"最后一个候选"（原行为）
                    if (path == null) path = candidates[candidates.Count - 1].Path;
                }

                if (string.IsNullOrEmpty(path) && crumbs.Count > 0)
                {
                    foreach (string crumb in crumbs)
                    {
                        foreach (Entry e in entries)
                        {
                            if (string.Equals(Leaf(e.Path), crumb, StringComparison.OrdinalIgnoreCase))
                            {
                                path = e.Path;
                                break;
                            }
                        }
                        if (path != null) break;
                    }
                }

                if (string.IsNullOrEmpty(path) && entries.Count == 1)
                {
                    path = entries[0].Path;
                }

                if (!string.IsNullOrEmpty(path)) Util.Log("shellcom resolved path=" + path);
                return !string.IsNullOrEmpty(path);
            }
            catch (Exception ex)
            {
                Util.Log("shellcom exc: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // ---------- 枚举 ----------

        /// <summary>严格模式路径解析（2026-11-04 粘贴即存文件专用）：
        /// 只接受"前台窗口的活动标签页"的目录，标题猜测/跨窗口匹配/单条目兜底一律不用——
        /// 写文件到错误目录比取不到目录严重得多。识别不了就返回 false。</summary>
        public static bool TryGetPathStrict(IntPtr hwnd, out string path)
        {
            path = null;
            try
            {
                List<Entry> entries = Enumerate();
                var candidates = new List<Entry>();
                foreach (Entry e in entries)
                {
                    if (e.Hwnd == (long)hwnd) candidates.Add(e);
                }
                if (candidates.Count == 0) return false;        // 前台窗口不在 shell 窗口清单里：不猜

                if (candidates.Count == 1)
                {
                    path = candidates[0].Path;                  // 单标签：唯一候选安全
                    return true;
                }

                // 多标签：必须精确命中活动标签
                long active = ActiveTabHwnd(hwnd);
                if (active == 0) return false;
                foreach (Entry e in candidates)
                {
                    if (e.TabHwnd == active) { path = e.Path; return true; }
                }
                return false;                                   // 活动标签识别失败：不猜
            }
            catch (Exception ex)
            {
                Util.Log("shellcom strict exc: " + ex.Message);
                return false;
            }
        }

        /// <summary>在指定资源管理器窗口（多标签时为活动标签页）中选中文件（2026-11-04 粘贴即存文件：
        /// 直接写盘后没有任何可见反馈，用户以为没生效；选中该文件 = 真粘贴同款效果，还会滚动定位）。
        /// 用 IShellFolderViewDual.SelectItem 后期绑定实现。失败返回 false（调用方兜底气泡通知）。</summary>
        public static bool TrySelectItem(IntPtr hwnd, string filePath)
        {
            try
            {
                dynamic app = ShellApp();
                if (app == null) return false;

                string dir = System.IO.Path.GetDirectoryName(filePath);
                string file = System.IO.Path.GetFileName(filePath);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return false;

                long active = ActiveTabHwnd(hwnd);
                foreach (var raw in (IEnumerable)app.Windows)
                {
                    try
                    {
                        dynamic w = raw;
                        long h = ToHwnd(w);
                        if (h != (long)hwnd) continue;
                        // 多标签：只认活动标签页（TabHwnd 取不到时不拦，交由后续候选）
                        long tab = TabHwndOf(raw);
                        if (active != 0 && tab != 0 && tab != active) continue;

                        dynamic folder = app.NameSpace(dir);       // Folder 对象
                        if (folder == null) return false;
                        dynamic item = folder.ParseName(file);     // FolderItem
                        if (item == null) return false;
                        dynamic doc = w.Document;                  // IShellFolderViewDual
                        // SVSI_SELECT(0x1) | SVSI_DESELECTOTHERS(0x4) | SVSI_ENSUREVISIBLE(0x8) | SVSI_FOCUSED(0x10)
                        doc.SelectItem(item, 0x1D);
                        return true;
                    }
                    catch { }
                }
                return false;
            }
            catch (Exception ex)
            {
                Util.Log("shellcom select exc: " + ex.Message);
                return false;
            }
        }

        private static List<Entry> Enumerate()
        {
            dynamic app = ShellApp();
            if (app == null) return new List<Entry>();

            try
            {
                var list = new List<Entry>();
                foreach (var raw in (IEnumerable)app.Windows)
                {
                    try
                    {
                        dynamic w = raw;
                        long h = ToHwnd(w);
                        string p = PathOf(w);
                        if (h != 0 && IsDrivePath(p))
                            list.Add(new Entry { Hwnd = h, TabHwnd = TabHwndOf(raw), Path = p });
                    }
                    catch { }
                }
                if (list.Count > 0) return list;
                Util.Log("shellcom dynamic empty");
            }
            catch (Exception ex)
            {
                Util.Log("shellcom dynamic failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                var list2 = new List<Entry>();
                foreach (var raw in (IEnumerable)app.Windows("ShellBrowserWindow"))
                {
                    try
                    {
                        dynamic w = raw;
                        long h = ToHwnd(w);
                        string p = PathOf(w);
                        if (h != 0 && IsDrivePath(p)) list2.Add(new Entry { Hwnd = h, Path = p });
                    }
                    catch { }
                }
                if (list2.Count > 0)
                {
                    Util.Log("shellcom filtered count=" + list2.Count);
                    return list2;
                }
            }
            catch (Exception ex2)
            {
                Util.Log("shellcom filtered failed: " + ex2.GetType().Name + ": " + ex2.Message);
            }

            return EnumerateRaw(app.Windows);
        }

        private static long ToHwnd(dynamic w)
        {
            try
            {
                object hv = (object)w.HWND;
                if (hv is int i) return i;
                if (hv is long l) return l;
                if (hv != null) return Convert.ToInt64(hv);
            }
            catch { }
            return 0;
        }

        private static string PathOf(dynamic w)
        {
            try
            {
                string p = (string)w.Document.Folder.Self.Path;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            try
            {
                string u = (string)w.LocationURL;
                if (!string.IsNullOrEmpty(u) && u.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    try { return new Uri(u).LocalPath; } catch { }
                }
            }
            catch { }
            return null;
        }

        private static bool IsDrivePath(string p)
        {
            return !string.IsNullOrEmpty(p) && p.Length >= 3 && p[1] == ':' && (p[2] == '\\' || p[2] == '/');
        }

        // 手工通过 IEnumVARIANT 枚举（dynamic 路径不可用时兜底）
        private static List<Entry> EnumerateRaw(dynamic windowsCol)
        {
            var list = new List<Entry>();
            if (windowsCol == null) return list;

            IntPtr pUnk = IntPtr.Zero;
            try
            {
                object raw = (object)windowsCol;
                IEnumVARIANT en = raw as IEnumVARIANT;
                if (en == null)
               {
                    Util.Log("shellcom raw QI failed");
                    return list;
                }

                object shellObj = ShellApp();

                for (int i = 0; i < 200; i++)
                {
                    object[] one = new object[1];
                    int fetched;
                    int hr = en.Next(1, one, out fetched);
                    if (hr != 0 || fetched == 0 || one[0] == null) break;

                    try
                    {
                        object item = one[0];
                        Type itemType = item.GetType();
                        object hv = itemType.InvokeMember("HWND", BindingFlags.GetProperty, null, item, null);
                        long h = hv == null ? 0 : Convert.ToInt64(hv);
                        string p = null;
                        try
                        {
                            object self = itemType.InvokeMember("Folder", BindingFlags.GetProperty, null, item, null);
                            if (self != null)
                            {
                                object selfObj = self.GetType().InvokeMember("Self", BindingFlags.GetProperty, null, self, null);
                                if (selfObj != null) p = selfObj.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, selfObj, null) as string;
                            }
                        }
                        catch { }
                        if (string.IsNullOrEmpty(p))
                        {
                            try
                            {
                                object loc = itemType.InvokeMember("LocationURL", BindingFlags.GetProperty, null, item, null);
                                string u = loc as string;
                                if (!string.IsNullOrEmpty(u) && u.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                                {
                                    try { p = new Uri(u).LocalPath; } catch { }
                                }
                            }
                            catch { }
                        }
                        if (h != 0 && IsDrivePath(p))
                            list.Add(new Entry { Hwnd = h, TabHwnd = TabHwndOf(item), Path = p });
                    }
                    catch { }
                }
                GC.KeepAlive(shellObj);
                Util.Log("shellcom raw entries=" + list.Count);
            }
            catch (Exception ex)
            {
                Util.Log("shellcom raw err 0x" + ex.HResult.ToString("x") + " " + ex.Message);
            }
            finally
            {
                _ = pUnk;
            }
            return list;
        }

        [ComImport, Guid("0000000F-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumVARIANT
        {
            [PreserveSig]
            int Next(int celt, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct, SizeParamIndex = 1)] object[] rgVar, [Out] out int pCeltFetched);
            [PreserveSig] int Skip(int celt);
            void Reset();
            [PreserveSig] int Clone(out IntPtr ppEnum);
        }

        // ---------- 标题叶名 ----------

        /// <summary>把窗口标题拆成候选叶名（Win11 地址面包屑 / 本地化后缀）。</summary>
        private static List<string> TitleCrumbs(string title)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(title)) return res;

            // Win11: "AI ‣ Temp ‣ D" ；Win10: "Temp (D:)" 之类
            string[] parts = title.Split(new[] { " \u2023 " }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts)
            {
                string s = p.Trim();
                int idx = s.LastIndexOf(" - ", StringComparison.Ordinal);
                if (idx > 0) s = s.Substring(0, idx).Trim();
                if (s.Length > 0) res.Add(s);
            }

            // 普通 "叶名 - 资源管理器" 形式
            string whole = title.Trim();
            int dash = whole.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) whole = whole.Substring(0, dash).Trim();
            if (whole.Length > 0 && !res.Contains(whole)) res.Add(whole);

            // 只保留看起来像名称（不含盘符）的条目
            res.RemoveAll(s => s.Length == 0 || (s.Length >= 2 && s[1] == ':' ));
            return res;
        }

        private static string Leaf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string s = path.TrimEnd('\\', '/');
            int bs = s.LastIndexOfAny(new[] { '\\', '/' });
            return bs >= 0 ? s.Substring(bs + 1) : s;
        }
    }
}
