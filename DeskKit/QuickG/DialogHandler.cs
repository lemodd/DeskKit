using System;
using System.Threading;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace CtrlG
{
    /// <summary>
    /// 找到前台的“打开/另存为”对话框，把资源管理器目录填入文件名框并确认，
    /// 实现类似 Listary 的 Ctrl+G 快速跳转。
    /// </summary>
    internal static class DialogHandler
    {
        private static readonly string[] DialogWindowClasses = { "#32770", "Any" };

        // 通用文件对话框“文件名”控件的常见 AutomationId（传统 #32770 与 Vista+ IFileDialog）
        private static readonly string[] FileNameEditIds =
            { "1148", "1070", "42306", "42307", "42305", "42304", "43000", "1136" };

        private static readonly Regex FileNameLabelRx =
            new Regex(@"(file\s*name|文件名)", RegexOptions.IgnoreCase);

        private static readonly Regex OpenSaveButtonRx =
            new Regex(@"^(&)?(save|open|select folder|保存|打开|选择|确定|ok)", RegexOptions.IgnoreCase);

        /// <summary>
        /// 尝试把对话框导航到 folderPath。若前台不是文件对话框则返回 false。
        /// </summary>
        public static bool TryNavigate(string folderPath)
        {
            try
            {
                IntPtr dialogHwnd = FindDialogWindow();
                if (dialogHwnd == IntPtr.Zero) { Util.Log("no dialog; fgcls=" + Native.ClassName(Native.GetForegroundWindow())); return false; }
                Util.Log("dialog hwnd=" + dialogHwnd + " cls=" + Native.ClassName(dialogHwnd));

                AutomationElement dialog;
                try { dialog = AutomationElement.FromHandle(dialogHwnd); }
                catch { return false; }
                if (dialog == null) return false;

                AutomationElement edit = FindFileNameEdit(dialog);
                if (edit == null) { Util.Log("no filename edit"); return false; }
                Util.Log("edit id=" + edit.Current.AutomationId + " name=" + edit.Current.Name);

                string nav = EnsureTrailingSeparator(folderPath);

                bool accepted = TrySetViaValuePattern(edit, nav) || TypeIntoEdit(edit, nav);
                Util.Log("set text accepted=" + accepted);

                try { edit.SetFocus(); } catch { }
                Thread.Sleep(30);
                KeyboardInput.Enter();
                Util.Log("enter sent");
                return accepted;
            }
            catch
            {
                return false;
            }
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.TrimEnd('\\', '/') + "\\";
        }

        private static bool ReadbackMatches(AutomationElement edit, string expected)
        {
            try
            {
                object p;
                if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out p)) return false;
                string v = ((ValuePattern)p).Current.Value;
                if (v == null) return false;
                v = v.TrimEnd('\\', '/');
                string e2 = expected.TrimEnd('\\', '/');
                return string.Equals(v, e2, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool TrySetViaValuePattern(AutomationElement edit, string text)
        {
            try
            {
                object p;
                if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out p)) return false;
                ((ValuePattern)p).SetValue(text);
                Thread.Sleep(60);
                return ReadbackMatches(edit, text);
            }
            catch { return false; }
        }

        private static bool TypeIntoEdit(AutomationElement edit, string text)
        {
            try
            {
                try { edit.SetFocus(); } catch { }
                Thread.Sleep(60);
                KeyboardInput.SelectAllAndClear();
                Thread.Sleep(30);
                KeyboardInput.SendText(text);
                Thread.Sleep(80);
                bool ok = ReadbackMatches(edit, text);
                Util.Log("typed readback=" + ok);
                return ok;
            }
            catch { return false; }
        }

        // ---------- 窗口定位 ----------

        private static bool IsDialogClass(string cls)
        {
            return Array.Exists(DialogWindowClasses, c => c == cls);
        }

        private static IntPtr FindDialogWindow()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return IntPtr.Zero;

            // 1) 从前台窗口向上走：自身 → 父/属主 → 根
            for (IntPtr h = fg; h != IntPtr.Zero; h = Native.GetAncestor(h, Native.GA_PARENT))
            {
                if (IsDialogClass(Native.ClassName(h))) return h;
                IntPtr root = Native.GetAncestor(h, Native.GA_ROOT);
                if (root == h) break;
            }

            // 2) 属主根窗口（GA_ROOTOWNER）
            IntPtr top = Native.GetAncestor(fg, Native.GA_ROOTOWNER);
            if (top != IntPtr.Zero && IsDialogClass(Native.ClassName(top))) return top;

            // 3) 根窗口内部找对话框子窗口（Chrome/Edge 等把对话框嵌在自己窗口里）
            if (top != IntPtr.Zero)
            {
                IntPtr found = IntPtr.Zero;
                Native.EnumChildWindows(top, (h, l) =>
                {
                    if (IsDialogClass(Native.ClassName(h))) { found = h; return false; }
                    return true;
                }, IntPtr.Zero);
                if (found != IntPtr.Zero) return found;
            }

            if (IsDialogClass(Native.ClassName(fg))) return fg;
            return IntPtr.Zero;
        }

        // ---------- “文件名”编辑框定位 ----------

        private static AutomationElement FindFileNameEdit(AutomationElement dialog)
        {
            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));

            AutomationElementCollection elements;
            try { elements = dialog.FindAll(TreeScope.Descendants, condition); }
            catch { return null; }

            AutomationElement byId = null;
            AutomationElement byLabel = null;
            AutomationElement byParentId = null;
            AutomationElement fallback = null;

            foreach (AutomationElement e in elements)
            {
                string id, name;
                try
                {
                    id = e.Current.AutomationId;
                    name = e.Current.Name;
                }
                catch { continue; }

                if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(name)) continue;

                if (Array.Exists(FileNameEditIds, x => x == id))
                {
                    if (byId == null) byId = e;
                    continue;
                }

                if (name != null && FileNameLabelRx.IsMatch(name))
                {
                    if (byLabel == null) byLabel = e;
                    continue;
                }

                // 传统 #32770：Edit 子控件的 AutomationId 是 "0"，但其祖先 ComboBox/ComboBoxEx32 带 id
                string ancestorId = GetAncestorAutomationId(e, 2);
                if (ancestorId != null && Array.Exists(FileNameEditIds, x => x == ancestorId))
                {
                    if (byParentId == null) byParentId = e;
                    continue;
                }

                if (fallback == null) fallback = e;
            }

            AutomationElement chosen = byId ?? byLabel ?? byParentId;

            // 若命中控件是 ComboBox，取其内部 Edit 或直接使用它自身
            if (chosen != null)
            {
                try
                {
                    if (chosen.Current.ControlType == ControlType.ComboBox)
                    {
                        var inner = chosen.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                        if (inner != null) chosen = inner;
                    }
                }
                catch { }
                return chosen;
            }

            return fallback;
        }

        private static string GetAncestorAutomationId(AutomationElement e, int maxLevels)
        {
            try
            {
                AutomationElement cur = e;
                for (int i = 0; i < maxLevels; i++)
                {
                    cur = TreeWalker.ControlViewWalker.GetParent(cur);
                    if (cur == null) return null;
                    string id = cur.Current.AutomationId;
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
            catch { }
            return null;
        }

        // ---------- 填值 + 点击默认按钮 ----------

        private static void InvokeDefaultButton(AutomationElement dialog)
        {
            AutomationElement buttons = null;
            try
            {
                buttons = dialog.FindFirst(TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "1")));
            }
            catch { }

            if (buttons == null)
            {
                try
                {
                    var all = dialog.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    foreach (AutomationElement b in all)
                    {
                        string name;
                        try { name = b.Current.Name; } catch { continue; }
                        if (string.IsNullOrEmpty(name)) continue;
                        string clean = name.Replace("&", string.Empty).Trim(' ', '\t');
                        if (OpenSaveButtonRx.IsMatch(clean) && clean.Length < 30)
                        {
                            buttons = b;
                            break;
                        }
                    }
                }
                catch { }
            }

            if (buttons == null) return;

            try
            {
                object p;
                if (buttons.TryGetCurrentPattern(InvokePattern.Pattern, out p))
                {
                    ((InvokePattern)p).Invoke();
                }
            }
            catch { }
        }
    }
}
