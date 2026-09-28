using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace SubtitleMaster.Helpers;

public class ProcessExecutionOptions
{
    public required string FileName { get; set; }
    public string? Arguments { get; set; }
    public List<string>? ArgumentList { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? StandardInput { get; set; }
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
    public Action<string>? OnOutputLine { get; set; }
    public Action<string>? OnErrorLine { get; set; }
}

public class ProcessExecutionResult
{
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Native Win32 process execution helper using DETACHED_PROCESS and CREATE_NO_WINDOW.
/// Completely suppresses console allocation, preventing Windows 11 Windows Terminal
/// Console Broker from flickering or flashing any black console window.
/// </summary>
public static class SilentProcessRunner
{
    private const uint DETACHED_PROCESS = 0x00000008;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_HIDE = 0;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static async Task<ProcessExecutionResult> RunAsync(ProcessExecutionOptions options, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };

        // Create anonymous pipes for stdout, stderr, and stdin
        if (!CreatePipe(out var outRead, out var outWrite, ref sa, 0))
            throw new InvalidOperationException($"CreatePipe stdout failed: {Marshal.GetLastWin32Error()}");

        if (!CreatePipe(out var errRead, out var errWrite, ref sa, 0))
        {
            outRead.Dispose();
            outWrite.Dispose();
            throw new InvalidOperationException($"CreatePipe stderr failed: {Marshal.GetLastWin32Error()}");
        }

        SafeFileHandle? inRead = null;
        SafeFileHandle? inWrite = null;
        if (options.StandardInput != null)
        {
            if (!CreatePipe(out inRead, out inWrite, ref sa, 0))
            {
                outRead.Dispose();
                outWrite.Dispose();
                errRead.Dispose();
                errWrite.Dispose();
                throw new InvalidOperationException($"CreatePipe stdin failed: {Marshal.GetLastWin32Error()}");
            }
            SetHandleInformation(inWrite, HANDLE_FLAG_INHERIT, 0);
        }

        // Ensure the reading ends are not inherited by child process
        SetHandleInformation(outRead, HANDLE_FLAG_INHERIT, 0);
        SetHandleInformation(errRead, HANDLE_FLAG_INHERIT, 0);

        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf<STARTUPINFO>();
        si.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
        si.wShowWindow = SW_HIDE;
        si.hStdOutput = outWrite.DangerousGetHandle();
        si.hStdError = errWrite.DangerousGetHandle();
        if (inRead != null)
        {
            si.hStdInput = inRead.DangerousGetHandle();
        }

        // Build command line
        string commandLine = BuildCommandLine(options.FileName, options.Arguments, options.ArgumentList);
        uint creationFlags = CREATE_NO_WINDOW;

        // Apply custom environment variables if any
        if (options.EnvironmentVariables != null)
        {
            foreach (var kvp in options.EnvironmentVariables)
            {
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
            }
        }

        string? workDir = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? null : options.WorkingDirectory;

        bool created = CreateProcess(
            null,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            true,
            creationFlags,
            IntPtr.Zero,
            workDir,
            ref si,
            out PROCESS_INFORMATION pi);

        // Close write ends in the parent so EOF is triggered when child finishes
        outWrite.Dispose();
        errWrite.Dispose();
        inRead?.Dispose();

        if (!created)
        {
            int err = Marshal.GetLastWin32Error();
            outRead.Dispose();
            errRead.Dispose();
            inWrite?.Dispose();
            throw new InvalidOperationException($"CreateProcess failed (Win32 error {err}) for command: {commandLine}");
        }

        // Write stdin if provided
        if (inWrite != null && options.StandardInput != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using (var writer = new StreamWriter(new FileStream(inWrite, FileAccess.Write), new UTF8Encoding(false)))
                    {
                        await writer.WriteAsync(options.StandardInput);
                        await writer.FlushAsync();
                    }
                }
                catch { }
            });
        }

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        var encoding = new UTF8Encoding(false);

        var readOutTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(new FileStream(outRead, FileAccess.Read), encoding);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                stdoutBuilder.AppendLine(line);
                options.OnOutputLine?.Invoke(line);
            }
        }, cancellationToken);

        var readErrTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(new FileStream(errRead, FileAccess.Read), encoding);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                stderrBuilder.AppendLine(line);
                options.OnErrorLine?.Invoke(line);
            }
        }, cancellationToken);

        var waitExitTask = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                uint waitRes = WaitForSingleObject(pi.hProcess, 100);
                if (waitRes == 0) // WAIT_OBJECT_0
                {
                    break;
                }
                await Task.Delay(50, cancellationToken);
            }
        }, cancellationToken);

        try
        {
            await Task.WhenAll(readOutTask, readErrTask, waitExitTask);
        }
        catch (OperationCanceledException)
        {
            try
            {
                TerminateProcess(pi.hProcess, 1);
                var p = Process.GetProcessById(pi.dwProcessId);
                p.Kill(entireProcessTree: true);
            }
            catch { }
            throw;
        }

        GetExitCodeProcess(pi.hProcess, out uint exitCode);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);

        sw.Stop();

        return new ProcessExecutionResult
        {
            ExitCode = (int)exitCode,
            StandardOutput = stdoutBuilder.ToString(),
            StandardError = stderrBuilder.ToString(),
            Duration = sw.Elapsed
        };
    }

    private static string BuildCommandLine(string fileName, string? args, List<string>? argList)
    {
        var sb = new StringBuilder();
        sb.Append(QuoteIfNeeded(fileName));

        if (!string.IsNullOrEmpty(args))
        {
            sb.Append(' ').Append(args);
        }

        if (argList != null && argList.Count > 0)
        {
            foreach (var arg in argList)
            {
                sb.Append(' ').Append(QuoteIfNeeded(arg));
            }
        }

        return sb.ToString();
    }

    private static string QuoteIfNeeded(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (!arg.Contains(' ') && !arg.Contains('\t') && !arg.Contains('"') && !arg.Contains('\n') && !arg.Contains('\r'))
        {
            return arg;
        }
        return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
