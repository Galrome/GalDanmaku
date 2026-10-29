using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GalDanmaku.App.Windows;

public static class WindowCatalog
{
    public static IReadOnlyList<WindowTarget> Enumerate()
    {
        var windows = new List<WindowTarget>();
        bool succeeded = NativeMethods.EnumWindows((handle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(handle) || NativeMethods.IsCloaked(handle) ||
                ((long)NativeMethods.GetExtendedStyle(handle) & NativeMethods.ToolWindowStyle) != 0)
            {
                return true;
            }

            uint threadId = NativeMethods.GetWindowThreadProcessId(handle, out uint processId);
            if (processId == Environment.ProcessId || processId == 0)
            {
                return true;
            }

            var title = new StringBuilder(1024);
            NativeMethods.GetWindowText(handle, title, title.Capacity);
            if (!string.IsNullOrWhiteSpace(title.ToString()))
            {
                windows.Add(new WindowTarget(handle, processId, threadId, ReadProcessName(processId), title.ToString()));
            }

            return true;
        }, nint.Zero);

        if (!succeeded)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取窗口列表。");
        }

        return windows.OrderBy(window => window.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string ReadProcessName(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "进程已退出";
        }
        catch (InvalidOperationException)
        {
            return "进程已退出";
        }
        catch (Win32Exception)
        {
            return "进程名称不可读";
        }
    }
}
