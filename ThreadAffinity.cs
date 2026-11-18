using System.Runtime.InteropServices;

namespace deltalag;

internal static class ThreadAffinity
{
    internal static bool TryPinCurrentThread(int threadIndex)
    {
        int processorCount = Environment.ProcessorCount;
        if (processorCount <= 0)
            return false;

        int coreIndex = ((threadIndex % processorCount) + processorCount) % processorCount;

        if (OperatingSystem.IsWindows())
        {
            int maskBits = IntPtr.Size * 8;
            if ((uint)coreIndex >= (uint)maskBits)
                return false;

            Thread.BeginThreadAffinity();
            try
            {
                nuint mask = (nuint)1 << coreIndex;
                if (SetThreadAffinityMask(GetCurrentThread(), mask) != 0)
                    return true;
            }
            catch
            {
                // Graceful fallback if P/Invoke fails
            }

            Thread.EndThreadAffinity();
            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            const int cpuSetBytes = 128; // sizeof(cpu_set_t) = 1024 bits
            if ((uint)coreIndex >= cpuSetBytes * 8)
                return false;

            Thread.BeginThreadAffinity();
            try
            {
                byte[] mask = new byte[cpuSetBytes];
                mask[coreIndex >> 3] = (byte)(1 << (coreIndex & 7));
                if (sched_setaffinity(0, (nuint)cpuSetBytes, mask) == 0)
                    return true;
            }
            catch
            {
                // Graceful fallback if P/Invoke fails
            }

            Thread.EndThreadAffinity();
            return false;
        }

        return false;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint SetThreadAffinityMask(nint hThread, nuint dwThreadAffinityMask);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setaffinity(int pid, nuint cpusetsize, byte[] mask);
}
