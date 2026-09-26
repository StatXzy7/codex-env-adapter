# Debugger callback for Codex desktop 26.924+.
# Windows starts this while the new ChatGPT.exe is still suspended:
#   powershell -File Inject-CodexTimezone.ps1 -Timezone America/New_York -p <pid> -tid <tid>
#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$timezone = $null
$targetPid = 0
for ($i = 0; $i -lt $args.Count; $i++) {
    if ($args[$i] -eq "-Timezone" -and ($i + 1) -lt $args.Count) { $timezone = [string]$args[$i + 1] }
    if ($args[$i] -eq "-p" -and ($i + 1) -lt $args.Count) { $targetPid = [int]$args[$i + 1] }
}

$logDir = Join-Path $env:LOCALAPPDATA "CodexEnvAdapter"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logPath = Join-Path $logDir "inject-ps.log"

try {
    if ([string]::IsNullOrWhiteSpace($timezone) -or $timezone -notmatch '^[A-Za-z0-9_+\-/]{1,64}$' -or $targetPid -le 0) {
        throw "missing timezone or pid. args=$($args -join ' ')"
    }

    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class CodexTzInject {
  public static void Run(int pid, string timezone) {
    var access = 0x0400u | 0x0010u | 0x0020u | 0x0008u;
    var handle = OpenProcess(access, false, pid);
    if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess");
    try {
      var info = new byte[48];
      int ret;
      int status = NtQueryInformationProcess(handle, 0, info, info.Length, out ret);
      if (status != 0) throw new InvalidOperationException("NtQueryInformationProcess " + status);
      long peb = BitConverter.ToInt64(info, 8);
      long parameters = ReadI64(handle, peb + 0x20);
      long envAddress = ReadI64(handle, parameters + 0x80);
      long envSize = ReadI64(handle, parameters + 0x3F0);
      if (envAddress == 0 || envSize <= 0 || envSize > 1024 * 1024) throw new InvalidOperationException("bad env");
      var current = new byte[envSize];
      int read;
      if (!ReadProcessMemory(handle, new IntPtr(envAddress), current, current.Length, out read) || read != current.Length)
        throw new Win32Exception(Marshal.GetLastWin32Error(), "read env");
      var entries = Parse(current);
      entries.RemoveAll(entry => entry.StartsWith("TZ=", StringComparison.OrdinalIgnoreCase));
      entries.Add("TZ=" + timezone);
      entries.Sort(StringComparer.OrdinalIgnoreCase);
      var block = Build(entries);
      long target = envAddress;
      if (block.Length > current.Length) {
        target = VirtualAllocEx(handle, IntPtr.Zero, new UIntPtr((uint)block.Length), 0x3000, 0x04).ToInt64();
        if (target == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "alloc");
        WriteI64(handle, parameters + 0x80, target);
      }
      int written;
      if (!WriteProcessMemory(handle, new IntPtr(target), block, block.Length, out written) || written != block.Length)
        throw new Win32Exception(Marshal.GetLastWin32Error(), "write env");
      WriteI64(handle, parameters + 0x3F0, block.Length);
      ResumeAll(pid);
    } finally { CloseHandle(handle); }
  }
  static List<string> Parse(byte[] block) {
    var text = Encoding.Unicode.GetString(block);
    var list = new List<string>();
    int start = 0;
    for (int i = 0; i < text.Length; i++) {
      if (text[i] != '\0') continue;
      if (i == start) break;
      list.Add(text.Substring(start, i - start));
      start = i + 1;
    }
    if (list.Count == 0 || list[0].IndexOf('=') < 0) throw new InvalidOperationException("bad env block");
    return list;
  }
  static byte[] Build(List<string> entries) {
    var sb = new StringBuilder();
    foreach (var entry in entries) sb.Append(entry).Append('\0');
    sb.Append('\0');
    return Encoding.Unicode.GetBytes(sb.ToString());
  }
  static long ReadI64(IntPtr handle, long address) {
    var buf = new byte[8];
    int read;
    if (!ReadProcessMemory(handle, new IntPtr(address), buf, 8, out read) || read != 8)
      throw new Win32Exception(Marshal.GetLastWin32Error(), "read");
    return BitConverter.ToInt64(buf, 0);
  }
  static void WriteI64(IntPtr handle, long address, long value) {
    var buf = BitConverter.GetBytes(value);
    int written;
    if (!WriteProcessMemory(handle, new IntPtr(address), buf, 8, out written) || written != 8)
      throw new Win32Exception(Marshal.GetLastWin32Error(), "write");
  }
  static void ResumeAll(int pid) {
    var snapshot = CreateToolhelp32Snapshot(0x4, 0);
    if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "snapshot");
    try {
      var entry = new ThreadEntry();
      entry.dwSize = Marshal.SizeOf(typeof(ThreadEntry));
      if (!Thread32First(snapshot, ref entry)) return;
      do {
        if (entry.th32OwnerProcessID != pid) continue;
        var thread = OpenThread(0x2, false, entry.th32ThreadID);
        if (thread == IntPtr.Zero) continue;
        ResumeThread(thread);
        CloseHandle(thread);
      } while (Thread32Next(snapshot, ref entry));
    } finally { CloseHandle(snapshot); }
  }
  [StructLayout(LayoutKind.Sequential)]
  struct ThreadEntry {
    public int dwSize, cntUsage, th32ThreadID, th32OwnerProcessID, tpBasePri, tpDeltaPri, dwFlags;
  }
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, int size, out int read);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool WriteProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, int size, out int written);
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr VirtualAllocEx(IntPtr handle, IntPtr address, UIntPtr size, uint type, uint protect);
  [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, byte[] info, int length, out int returnLength);
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry entry);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry entry);
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenThread(uint access, bool inherit, int threadId);
  [DllImport("kernel32.dll", SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
}
'@
    [CodexTzInject]::Run($targetPid, $timezone)
    Set-Content -LiteralPath $logPath -Value ("ok pid=$targetPid tz=$timezone") -Encoding UTF8
} catch {
    Set-Content -LiteralPath $logPath -Value ("ERROR " + $_.Exception.ToString()) -Encoding UTF8
}
