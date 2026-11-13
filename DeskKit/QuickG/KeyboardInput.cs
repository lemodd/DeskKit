using System;
using System.Runtime.InteropServices;

namespace CtrlG
{
    /// <summary>
    /// 通过 SendInput 向当前前台窗口发送键盘输入（用于把路径“打字”进文件名框）。
    /// </summary>
    internal static class KeyboardInput
    {
        private const uint INPUT_KEYBOARD = 1;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_HOME = 0x24;
        private const ushort VK_END = 0x23;
        private const ushort VK_SHIFT = 10;
        private const ushort VK_DELETE = 0x2E;
        private const ushort VK_RETURN = 0x0D;

        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private static INPUT Key(ushort vk, ushort scan, uint flags)
        {
            var i = new INPUT();
            i.type = INPUT_KEYBOARD;
            i.u.ki.wVk = vk;
            i.u.ki.wScan = scan;
            i.u.ki.dwFlags = flags;
            i.u.ki.time = 0;
            i.u.ki.dwExtraInfo = IntPtr.Zero;
            return i;
        }

        private static void Send(INPUT[] inputs)
        {
            if (inputs == null || inputs.Length == 0) return;
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void VkDown(ushort vk) { Send(new[] { Key(vk, 0, 0) }); }
        private static void VkUp(ushort vk) { Send(new[] { Key(vk, 0, KEYEVENTF_KEYUP) }); }

        /// <summary>以 Unicode 方式输入一个字符（可处理中文、反斜杠等任意字符）。</summary>
        private static void CharDown(char c) { Send(new[] { Key(0, (ushort)c, KEYEVENTF_UNICODE) }); }
        private static void CharUp(char c) { Send(new[] { Key(0, (ushort)c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP) }); }

        public static void SendText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (char c in text)
            {
                CharDown(c);
                CharUp(c);
            }
        }

        public static void Enter()
        {
            VkDown(VK_RETURN);
            VkUp(VK_RETURN);
        }

        /// <summary>全选当前编辑框内容并删除（Ctrl+A + Delete，再配合 Home/Shift+End 兜底）。</summary>
        public static void SelectAllAndClear()
        {
            // Ctrl+A
            VkDown(VK_CONTROL);
            VkDown('A');
            VkUp('A');
            VkUp(VK_CONTROL);

            // Shift+End -> Delete
            VkDown(VK_SHIFT);
            VkDown(VK_HOME);
            VkUp(VK_HOME);
            VkDown(VK_END);
            VkUp(VK_END);
            VkUp(VK_SHIFT);
            VkDown(VK_DELETE);
            VkUp(VK_DELETE);

            // Ctrl+A + Delete 再来一次，保证清空
            VkDown(VK_CONTROL);
            VkDown('A');
            VkUp('A');
            VkUp(VK_CONTROL);
            VkDown(VK_DELETE);
            VkUp(VK_DELETE);
        }
    }
}
