param([switch]$Installers)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$root=Join-Path $repo '.tmp\local-runner'
$stage=Join-Path $root 'app'
New-Item -ItemType Directory -Force $root,$stage,(Join-Path $root 'temp') | Out-Null
$env:TEMP=Join-Path $root 'temp';$env:TMP=$env:TEMP
[Diagnostics.Process]::GetCurrentProcess().PriorityClass='BelowNormal'
$sdk=Join-Path $repo '.cache\distribution-tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { throw 'Project-local .NET 10 SDK is missing.' }
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

public static class LocalBuildIsolation
{
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct Startup { public int cb; public string reserved, desktop, title; public uint x,y,xs,ys,xc,yc,fill,flags; public short show, reservedCount; public IntPtr reserved2, stdin, stdout, stderr; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process, thread; public int pid, tid; }
    [DllImport("user32", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateWindowStation(string name, uint flags, uint access, IntPtr attributes);
    [DllImport("user32")] static extern IntPtr GetProcessWindowStation();
    [DllImport("user32", SetLastError=true)] static extern bool SetProcessWindowStation(IntPtr value);
    [DllImport("user32", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr attributes);
    [DllImport("user32", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool GetUserObjectInformation(IntPtr obj, int index, StringBuilder value, int length, out int needed);
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcess(string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref Startup startup, out ProcessInfo info);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32")] static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32")] static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    [DllImport("kernel32")] static extern bool TerminateProcess(IntPtr handle, uint code);
    [DllImport("user32")] static extern bool CloseDesktop(IntPtr handle);
    [DllImport("user32")] static extern bool CloseWindowStation(IntPtr handle);
    static IntPtr station, desktop;
    static string stationName, desktopName;
    static readonly Dictionary<int,IntPtr> handles=new Dictionary<int,IntPtr>();
    public static string Open()
    {
        var original=GetProcessWindowStation();
        station = CreateWindowStation(null, 0, 0x37f, IntPtr.Zero);
        if(station==IntPtr.Zero) throw new Win32Exception();
        var name = new StringBuilder(256); int needed;
        if(!GetUserObjectInformation(station, 2, name, name.Capacity*2, out needed)) throw new Win32Exception();
        stationName=name.ToString();
        if(stationName.Equals("WinSta0",StringComparison.OrdinalIgnoreCase)) throw new Exception("Refusing interactive station.");
        try {
            if(!SetProcessWindowStation(station)) throw new Win32Exception();
            desktopName="ArkUpdate_"+Guid.NewGuid().ToString("N");
            desktop=CreateDesktop(desktopName,IntPtr.Zero,IntPtr.Zero,0,0x1ff,IntPtr.Zero);
            if(desktop==IntPtr.Zero) throw new Win32Exception();
        } finally { if(!SetProcessWindowStation(original)) throw new Win32Exception(); }
        return stationName+"\\"+desktopName;
    }
    public static int Start(string exe, string arguments)
    {
        var si = new Startup { cb=Marshal.SizeOf(typeof(Startup)), desktop=stationName+"\\"+desktopName, flags=0x81, show=0 };
        ProcessInfo pi;
        if(!CreateProcess(exe,new StringBuilder("\""+exe+"\" "+arguments),IntPtr.Zero,IntPtr.Zero,false,0x08004000,IntPtr.Zero,System.IO.Path.GetDirectoryName(exe),ref si,out pi)) throw new Win32Exception();
        CloseHandle(pi.thread); handles.Add(pi.pid,pi.process); return pi.pid;
    }
    public static int Wait(int pid, uint timeout) { if(WaitForSingleObject(handles[pid],timeout)==258) return int.MinValue; uint code; if(!GetExitCodeProcess(handles[pid],out code)) throw new Win32Exception(); return unchecked((int)code); }
    public static void Stop(int pid) { if(Wait(pid,0)==int.MinValue) TerminateProcess(handles[pid],1); }
    public static void Close() { foreach(var handle in handles.Values) CloseHandle(handle); handles.Clear(); if(desktop!=IntPtr.Zero) CloseDesktop(desktop); if(station!=IntPtr.Zero) CloseWindowStation(station); }
}

'@
$station=[LocalBuildIsolation]::Open()
try {
    if ($Installers) {
        $releasePid=[LocalBuildIsolation]::Start((Get-Command pwsh.exe).Source,('-NoProfile -File "'+(Join-Path $repo 'packaging\Build-Release.ps1')+'"'))
        do { $code=[LocalBuildIsolation]::Wait($releasePid,30000) } while ($code -eq [int]::MinValue)
        if ($code) { throw "Installer build failed ($code); inspect .tmp/release-v0.2.7 logs." }
        Get-Content -LiteralPath (Join-Path $repo '.tmp\release-v0.2.7\PASS.txt')
        return
    }
    & $sdk publish (Join-Path $repo 'ArkCopyBuffer.csproj') -c Release --nologo -o $stage *> (Join-Path $root 'build.log')
    if ($LASTEXITCODE) { Get-Content -LiteralPath (Join-Path $root 'build.log') -Tail 25; throw 'Local publish failed.' }
    $exe=Join-Path $stage 'ArkCopyBuffer.exe'
    $output=Join-Path $root 'checks'
    $testPid=[LocalBuildIsolation]::Start($exe,('--selftest "'+$output+'" en'))
    $code=[LocalBuildIsolation]::Wait($testPid,60000)
    if ($code -ne 0) {
        [LocalBuildIsolation]::Stop($testPid)
        $log=Join-Path $output 'selftest.log'
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 20 }
        throw "Local checks failed: $code"
    }
    $log=Join-Path $output 'selftest.log'
    $count=(Get-Content -LiteralPath $log | Measure-Object -Line).Lines
    $head=(& git -C $repo rev-parse HEAD).Trim()
    $dirty=[bool](& git -C $repo status --porcelain)
    @{pid=$PID;station=$station;exe=$exe;sha256=(Get-FileHash -LiteralPath $exe).Hash;checks=$count;head=$head;workingTreeDirty=$dirty;finished=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'result.json')
    "PASS: $count checks; built the local working copy (uncommitted changes: $dirty)."
    if ($env:GITHUB_STEP_SUMMARY) { "Local Windows build passed $count checks. Working-copy changes included: $dirty. No release was published." | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
} finally { [LocalBuildIsolation]::Close() }
