using System.Runtime.InteropServices;

namespace GatewayPulse.Core;

public sealed class MemoryCandidate
{
    public IntPtr Address { get; set; }
    public int Value { get; set; }
    public bool LooksLikeArray { get; set; }
}

public sealed class ProcessMemoryReader : IDisposable
{
    private readonly IntPtr _handle;

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;

    public ProcessMemoryReader(int pid)
    {
        _handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("OpenProcess failed");
    }

    public bool TryReadInt32(IntPtr address, out int value)
    {
        value = 0;
        byte[] buffer = new byte[4];

        if (!ReadProcessMemory(_handle, address, buffer, buffer.Length, out var read) || read.ToInt64() != 4)
            return false;

        value = BitConverter.ToInt32(buffer, 0);
        return true;
    }

    public List<MemoryCandidate> FindInt32Candidates(HashSet<int> expectedValues, int maxCandidates)
    {
        var results = new List<MemoryCandidate>();
        IntPtr address = IntPtr.Zero;

        while (results.Count < maxCandidates &&
               VirtualQueryEx(_handle, address, out var mbi, (UIntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != UIntPtr.Zero)
        {
            if (IsReadable(mbi))
                ScanRegionForExpected(mbi.BaseAddress, mbi.RegionSize, expectedValues, results, maxCandidates);

            long next = mbi.BaseAddress.ToInt64() + unchecked((long)mbi.RegionSize.ToUInt64());
            if (next <= address.ToInt64()) break;
            address = new IntPtr(next);
        }

        return results;
    }

    /// <summary>
    /// Scan committed readable memory for Int32 values in an HF Hz range.
    /// Array-like neighborhoods are skipped so candidate capacity is not spent on config tables.
    /// </summary>
    public List<MemoryCandidate> FindInt32InRange(int minHz, int maxHz, int maxCandidates)
    {
        if (minHz > maxHz)
            (minHz, maxHz) = (maxHz, minHz);

        var results = new List<MemoryCandidate>();
        IntPtr address = IntPtr.Zero;

        while (results.Count < maxCandidates &&
               VirtualQueryEx(_handle, address, out var mbi, (UIntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != UIntPtr.Zero)
        {
            if (IsReadable(mbi))
                ScanRegionForRange(mbi.BaseAddress, mbi.RegionSize, minHz, maxHz, results, maxCandidates);

            long next = mbi.BaseAddress.ToInt64() + unchecked((long)mbi.RegionSize.ToUInt64());
            if (next <= address.ToInt64()) break;
            address = new IntPtr(next);
        }

        return results;
    }

    private static bool IsReadable(MEMORY_BASIC_INFORMATION mbi)
    {
        var protect = mbi.Protect & 0xff;
        return mbi.State == MEM_COMMIT &&
               (mbi.Protect & PAGE_GUARD) == 0 &&
               protect != PAGE_NOACCESS;
    }

    private void ScanRegionForExpected(
        IntPtr baseAddress,
        UIntPtr regionSize,
        HashSet<int> expectedValues,
        List<MemoryCandidate> results,
        int maxCandidates)
    {
        const int chunkSize = 64 * 1024;
        long size = unchecked((long)regionSize.ToUInt64());
        long offset = 0;

        while (offset < size && results.Count < maxCandidates)
        {
            int toRead = (int)Math.Min(chunkSize, size - offset);
            byte[] buffer = new byte[toRead];
            var addr = new IntPtr(baseAddress.ToInt64() + offset);

            if (ReadProcessMemory(_handle, addr, buffer, buffer.Length, out var bytesRead) && bytesRead.ToInt64() >= 4)
            {
                int limit = (int)bytesRead.ToInt64() - 4;

                for (int i = 0; i <= limit && results.Count < maxCandidates; i += 4)
                {
                    int value = BitConverter.ToInt32(buffer, i);
                    if (!expectedValues.Contains(value)) continue;

                    bool looksLikeArray = TrimodeFrequencyHeuristics.LooksLikeFrequencyArray(buffer, i, expectedValues);

                    results.Add(new MemoryCandidate
                    {
                        Address = new IntPtr(addr.ToInt64() + i),
                        Value = value,
                        LooksLikeArray = looksLikeArray
                    });
                }
            }

            offset += toRead;
        }
    }

    private void ScanRegionForRange(
        IntPtr baseAddress,
        UIntPtr regionSize,
        int minHz,
        int maxHz,
        List<MemoryCandidate> results,
        int maxCandidates)
    {
        const int chunkSize = 64 * 1024;
        long size = unchecked((long)regionSize.ToUInt64());
        long offset = 0;

        while (offset < size && results.Count < maxCandidates)
        {
            int toRead = (int)Math.Min(chunkSize, size - offset);
            byte[] buffer = new byte[toRead];
            var addr = new IntPtr(baseAddress.ToInt64() + offset);

            if (ReadProcessMemory(_handle, addr, buffer, buffer.Length, out var bytesRead) && bytesRead.ToInt64() >= 4)
            {
                int limit = (int)bytesRead.ToInt64() - 4;

                for (int i = 0; i <= limit && results.Count < maxCandidates; i += 4)
                {
                    int value = BitConverter.ToInt32(buffer, i);
                    if (!TrimodeFrequencyHeuristics.IsPlausibleHfHz(value, minHz, maxHz))
                        continue;

                    // Skip frequency tables so capacity is reserved for live-looking cells.
                    if (TrimodeFrequencyHeuristics.LooksLikeFrequencyArrayInRange(buffer, i, minHz, maxHz))
                        continue;

                    results.Add(new MemoryCandidate
                    {
                        Address = new IntPtr(addr.ToInt64() + i),
                        Value = value,
                        LooksLikeArray = false
                    });
                }
            }

            offset += toRead;
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
            CloseHandle(_handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
