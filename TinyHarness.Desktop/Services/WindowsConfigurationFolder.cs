using System.Runtime.InteropServices;

namespace TinyHarness.Desktop.Services;

/// <summary>
///     <para>通过 Windows Shell 打开配置目录，复用已有的文件夹窗口。</para>
///     <para>Opens the configuration directory through Windows Shell, reusing an existing folder window.</para>
/// </summary>
internal static class WindowsConfigurationFolder
{
    /// <summary>
    ///     <para>在后台线程打开配置文件所在目录；目录不存在时创建，Shell 失败向调用方报告。</para>
    ///     <para>Opens the config file's directory on a worker thread, creating it if missing and reporting Shell failures.</para>
    /// </summary>
    internal static Task OpenAsync(string configPath) => Task.Run(() => Open(configPath));

    private static void Open(string configPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("此操作需要 Windows 资源管理器。");
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath))
                     ?? throw new InvalidOperationException("无法确定配置文件所在目录。");
        Directory.CreateDirectory(directory);
        Marshal.ThrowExceptionForHR(CoInitializeEx(nint.Zero, 0));
        var folder = nint.Zero;
        var items  = nint.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(directory, nint.Zero, out folder, 0, out _));
            // 空的相对 PIDL 指向目录自身，避免零项选择打开其父目录；交由 Shell 复用已打开的目录。
            items = Marshal.AllocCoTaskMem(nint.Size + sizeof(short));
            Marshal.WriteIntPtr(items, items + nint.Size);
            Marshal.WriteInt16(items         + nint.Size, 0);
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(folder, 1, items, 0));
        }
        finally
        {
            Marshal.FreeCoTaskMem(items);
            Marshal.FreeCoTaskMem(folder);
            CoUninitialize();
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint concurrencyModel);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(
        string name, nint bindingContext, out nint itemIdList, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(nint folder, uint count, nint items, uint flags);
}
