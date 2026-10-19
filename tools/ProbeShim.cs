// ProbeShim.cs — which DLLs does the Win7 onnxruntime build actually need?
//
// The architecture doc claims the loader resolves api-ms-win-* through a
// built-in API-set schema rather than the filesystem, so a same-named DLL
// next to the binary would be ignored. That claim is why twenty
// api-ms-win-core-*.dll files sit in ai\win7\. This probe checks it by
// asking the OS loader itself: set the DLL directory to ai\win7 and try to
// load the patched runtime, reporting the Win32 error on failure.
//
// Build: see make_shimprobe.bat
using System;
using System.IO;
using System.Runtime.InteropServices;

static class ProbeShim
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryW(string file);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetDllDirectoryW(string path);

    [DllImport("kernel32.dll")]
    static extern uint GetLastError();

    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: ProbeShim <dir-containing-onnxruntime.dll>");
            return 2;
        }
        string dir = Path.GetFullPath(args[0]);
        string target = Path.Combine(dir, "onnxruntime.dll");
        if (!File.Exists(target))
        {
            Console.WriteLine("MISSING " + target);
            return 2;
        }

        int files = Directory.GetFiles(dir, "*.dll").Length;
        Console.WriteLine("dir       : " + dir);
        Console.WriteLine("dll count : " + files);
        Console.WriteLine("target    : " + target);

        // Point the loader's search path at the shim directory, then load.
        SetDllDirectoryW(dir);
        IntPtr h = LoadLibraryW(target);
        int err = Marshal.GetLastWin32Error();
        if (h != IntPtr.Zero)
        {
            Console.WriteLine("LOAD OK   : the shim set as shipped is sufficient");
            return 0;
        }
        Console.WriteLine("LOAD FAILED: win32 error " + err + " (0x" + err.ToString("X8") + ")");
        Console.WriteLine("  126 = module not found");
        Console.WriteLine("  193 = bad exe format (import entry point missing)");
        return 1;
    }
}