using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using Microsoft.Win32.SafeHandles;
using GalDanmaku.Core.Diagnostics;

namespace GalDanmaku.App.Windows;

public static class WindowCatalog
{
    // Resolve only on explicit binding, never in the tracker's 50 ms polling loop.
    public static WindowTarget ReadProcessIdentity(WindowTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureWindowIdentity(target);
        using SafeProcessHandle process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, target.ProcessId);
        string? path = null;
        string? error = null;
        if (process.IsInvalid)
        {
            error = $"无法读取进程路径：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        }
        else
        {
            // Windows extended-length paths can contain up to 32767 characters.
            var buffer = new StringBuilder(32768);
            uint size = (uint)buffer.Capacity;
            if (NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size))
                path = buffer.ToString();
            else
                error = $"无法读取进程路径：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        }

        // A window may disappear or change owner while the process is being queried.
        EnsureWindowIdentity(target);
        RuntimeLog.Write(path is null ? LogLevel.Warning : LogLevel.Information, "Window", path is null ? "ProcessIdentityUnavailable" : "ProcessIdentityRead",
            new { hwnd = $"0x{target.Handle:X}", pid = target.ProcessId, threadId = target.ThreadId,
                executableName = path is null ? null : Path.GetFileName(path), executablePath = path, error });
        return target with
        {
            ProcessName = path is not null ? Path.GetFileNameWithoutExtension(path) : target.ProcessName,
            ExecutablePath = path,
            ProcessIdentityError = error,
        };
    }

    private static void EnsureWindowIdentity(WindowTarget target)
    {
        if (!NativeMethods.IsWindow(target.Handle) ||
            NativeMethods.GetWindowThreadProcessId(target.Handle, out uint processId) != target.ThreadId ||
            processId != target.ProcessId)
            throw new InvalidOperationException("所选窗口已关闭或身份发生变化，请刷新列表后重试。");
    }

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
