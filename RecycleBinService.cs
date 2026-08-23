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

    public RecycleBinInfo GetInfo()
    {
        var info = new QueryInfo { Size = Marshal.SizeOf<QueryInfo>() };
        var result = SHQueryRecycleBin(null, ref info);
        return result == 0 ? new RecycleBinInfo(info.Bytes, checked((int)Math.Min(info.Items, int.MaxValue))) : new RecycleBinInfo(0, 0);
    }

    public bool Empty()
    {
        var result = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
        return result == 0;
    }
}
