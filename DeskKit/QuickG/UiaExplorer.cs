using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace CtrlG
{
    /// <summary>
    /// 通过 UI Automation 读取资源管理器窗口的地址栏（当前路径）。
    /// </summary>
    internal static class UiaExplorer
    {
        private static readonly Regex PathRegex =
            new Regex(@"^[a-zA-Z]:\\(?:[^\\/:*?""<>|]*\\?)*$", RegexOptions.Compiled);

        private static bool IsDrivePath(string v)
        {
            if (string.IsNullOrEmpty(v) || v.Length < 3) return false;
            char d = v[0];
            if (!((d >= 'A' && d <= 'Z') || (d >= 'a' && d <= 'z'))) return false;
            return v[1] == ':' && (v[2] == '\\' || v[2] == '/');
        }

        public static bool TryGetPath(IntPtr hwnd, out string path)
        {
            path = null;
            AutomationElement root = null;
            try
            {
                root = AutomationElement.FromHandle(hwnd);
                if (root == null) return false;

                // 1) 首选：地址栏 Edit，AutomationId="TextBox"（Win10/11 地址栏固定 ID）
                var editCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                var edits = root.FindAll(TreeScope.Descendants, editCond);
                foreach (AutomationElement e in edits)
                {
                    object p;
                    if (!e.TryGetCurrentPattern(ValuePattern.Pattern, out p)) continue;
                    var vp = (ValuePattern)p;
                    string v = vp.Current.Value;
                    if (!IsDrivePath(v)) continue;
                    if (!PathRegex.IsMatch(v)) continue;
                    path = v;
                    return true;
                }

                // 2) 次选：Address 组合框/编辑框，用名称匹配
                var comboCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox);
                var combos = root.FindAll(TreeScope.Descendants, comboCond);
                foreach (AutomationElement e in combos)
                {
                    object p;
                    if (!e.TryGetCurrentPattern(ValuePattern.Pattern, out p)) continue;
                    string v = ((ValuePattern)p).Current.Value;
                    if (!IsDrivePath(v)) continue;
                    if (!PathRegex.IsMatch(v)) continue;
                    path = v;
                    return true;
                }
            }
            catch
            {
                // UIA 调用失败
            }
            finally
            {
                try { if (root != null) root = null; } catch { }
            }
            return false;
        }
    }
}
