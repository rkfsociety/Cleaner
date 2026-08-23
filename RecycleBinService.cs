using System.Runtime.InteropServices;

namespace Cleaner;

public sealed record RecycleBinInfo(long Bytes, int Items);

public sealed class RecycleBinService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref QueryInfo info);

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr owner, string? rootPath, uint flags);

    private const uint NoConfirmation = 0x00000001;
    private const uint NoProgressUi = 0x00000002;
    private const uint NoSound = 0x00000004;

    public RecycleBinInfo GetInfo(IEnumerable<string>? roots = null)
    {
        var totalBytes = 0L;
        var totalItems = 0L;
        foreach (var root in roots ?? [null!])
        {
            var info = new QueryInfo { Size = Marshal.SizeOf<QueryInfo>() };
            if (SHQueryRecycleBin(root, ref info) == 0)
            {
                totalBytes += info.Bytes;
                totalItems += info.Items;
            }
        }

        return new RecycleBinInfo(totalBytes, checked((int)Math.Min(totalItems, int.MaxValue)));
    }

    public bool Empty(IEnumerable<string>? roots = null)
    {
        var success = true;
        foreach (var root in roots ?? [null!])
        {
            var result = SHEmptyRecycleBin(IntPtr.Zero, root, NoConfirmation | NoProgressUi | NoSound);
            success &= result == 0;
        }

        return success;
    }
}
