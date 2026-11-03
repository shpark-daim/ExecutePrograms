using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ExecutePrograms.Services;

/// <summary>
/// Windows 서비스 관리자(SCM)와 프로세스 테이블을 직접 조회해
/// 서비스 모드·EXE 모드·기타 프로그램의 상태를 같은 형식(<see cref="ServiceStatusChecker.Result"/>)으로 돌려줍니다.
/// xms.exe / xflow.exe 를 거치지 않으므로 PID 와 마지막 종료 코드까지 얻을 수 있고 관리자 권한이 필요 없습니다.
/// </summary>
public static class ScmQuery
{
    // ── Windows 서비스 ────────────────────────────────────

    /// <summary>서비스 이름으로 SCM 상태를 조회합니다. 미등록이면 NotRegistered.</summary>
    public static ServiceStatusChecker.Result QueryService(string serviceName)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            return new(ServiceStatusChecker.State.Error, $"SCM({Marshal.GetLastWin32Error()})");
        try
        {
            var svc = OpenService(scm, serviceName, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return err switch
                {
                    1060 => new(ServiceStatusChecker.State.NotRegistered, "NotExist"),
                    5    => new(ServiceStatusChecker.State.Error, "AccessDenied"),
                    _    => new(ServiceStatusChecker.State.Error, $"Fail({err})"),
                };
            }
            try
            {
                var ssp  = new SERVICE_STATUS_PROCESS();
                uint size = (uint)Marshal.SizeOf<SERVICE_STATUS_PROCESS>();
                if (!QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, ref ssp, size, out _))
                    return new(ServiceStatusChecker.State.Error, $"Fail({Marshal.GetLastWin32Error()})");

                var (kind, raw) = ssp.dwCurrentState switch
                {
                    1 => (ServiceStatusChecker.State.Stopped, "STOPPED"),
                    2 => (ServiceStatusChecker.State.Pending, "START_PENDING"),
                    3 => (ServiceStatusChecker.State.Pending, "STOP_PENDING"),
                    4 => (ServiceStatusChecker.State.Running, "RUNNING"),
                    5 => (ServiceStatusChecker.State.Pending, "CONTINUE_PENDING"),
                    6 => (ServiceStatusChecker.State.Pending, "PAUSE_PENDING"),
                    7 => (ServiceStatusChecker.State.Pending, "PAUSED"),
                    _ => (ServiceStatusChecker.State.Error,   $"State({ssp.dwCurrentState})"),
                };
                int? pid  = ssp.dwProcessId != 0 ? (int)ssp.dwProcessId : null;
                // 1066 = ERROR_SERVICE_SPECIFIC_ERROR → 서비스 고유 코드가 실제 값
                int  code = ssp.dwWin32ExitCode == 1066 ? (int)ssp.dwServiceSpecificExitCode : (int)ssp.dwWin32ExitCode;
                return new(kind, raw, pid, code);
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>
    /// xms.toml / xflow.toml 의 [[Service.Runs]] 블록에서 서비스 이름 목록을 읽습니다.
    /// 파일이 없거나 블록이 없으면 빈 목록.
    /// </summary>
    public static List<string> ReadServiceNames(string tomlPath)
    {
        var names = new List<string>();
        if (!File.Exists(tomlPath)) return names;
        try
        {
            bool inRuns = false;
            foreach (var rawLine in File.ReadLines(tomlPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith('['))
                {
                    inRuns = line.Replace(" ", "") == "[[Service.Runs]]";
                    continue;
                }
                if (!inRuns) continue;
                var m = NameLine.Match(line);
                if (m.Success && !names.Contains(m.Groups[1].Value))
                    names.Add(m.Groups[1].Value);
            }
        }
        catch { /* 읽기 실패 → 빈 목록 */ }
        return names;
    }

    private static readonly Regex NameLine = new(@"^Name\s*=\s*""([^""]+)""", RegexOptions.Compiled);

    /// <summary>
    /// 데몬이 실제로 쓰는 `urls` 를 데몬과 같은 순서로 겹쳐 읽어 리슨 포트 목록을 돌려줍니다.
    /// Daim.Xflow.Options 의 AddTomlFileWithOverride 와 동일한 순서 — 뒤에 읽는 파일이 우선:
    ///   {name}.toml → 그 파일의 Override 체인 → {name}.{환경}.toml → 그 파일의 Override 체인
    /// 환경은 ASPNETCORE_ENVIRONMENT, 없으면 Production. 선언이 없으면 빈 배열 (→ 준비 상태 판단 안 함).
    /// </summary>
    public static int[] ReadExpectedPorts(string dir, string name)
    {
        if (string.IsNullOrWhiteSpace(dir)) return [];
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.IsNullOrWhiteSpace(env)) env = "Production";

        string? urls = null;
        foreach (var file in new[] { $"{name}.toml", $"{name}.{env}.toml" })
            foreach (var path in OverrideChain(Path.Combine(dir, file)))
                urls = ReadTopLevelKey(path, "urls") ?? urls;

        if (string.IsNullOrWhiteSpace(urls)) return [];
        var ports = new List<int>();
        foreach (var u in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // "http://*:9100", "http://0.0.0.0:9005" 같은 와일드카드 호스트도 Uri 로 읽히게 치환
            var norm = u.Replace("://*", "://localhost").Replace("://+", "://localhost").Replace("://0.0.0.0", "://localhost");
            if (Uri.TryCreate(norm, UriKind.Absolute, out var uri) && uri.Port > 0 && !ports.Contains(uri.Port))
                ports.Add(uri.Port);
        }
        return [.. ports];
    }

    /// <summary>파일 → 그 파일의 Override → 그 Override 의 Override … 순서 (존재하는 파일만, 최대 5단계, 순환 방지)</summary>
    private static IEnumerable<string> OverrideChain(string path)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? cur = path;
        for (int depth = 0; depth < 5 && cur != null && File.Exists(cur) && seen.Add(cur); depth++)
        {
            yield return cur;
            var ov = ReadTopLevelKey(cur, "Override");
            cur = ov == null ? null : Path.GetFullPath(ov, Path.GetDirectoryName(cur)!);
        }
    }

    /// <summary>첫 테이블 헤더([...]) 앞의 최상위 `key = "value"` 값. 없으면 null.</summary>
    private static string? ReadTopLevelKey(string path, string key)
    {
        try
        {
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.StartsWith('[')) break;                       // 최상위 구간 끝
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0 || !line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                var m = QuotedValue.Match(line[(eq + 1)..].Trim());
                return m.Success ? m.Groups[1].Value : null;
            }
        }
        catch { /* 읽기 실패 → 없음 */ }
        return null;
    }

    private static readonly Regex QuotedValue = new(@"^""([^""]*)""", RegexOptions.Compiled);

    // ── 프로세스 ──────────────────────────────────────────

    /// <summary>프로세스 이름(확장자 제외)으로 생존 여부와 PID 를 조회합니다.</summary>
    public static ServiceStatusChecker.Result QueryProcess(string processName)
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName(processName); }
        catch { return new(ServiceStatusChecker.State.Error, "Fail"); }

        try
        {
            if (procs.Length == 0) return new(ServiceStatusChecker.State.Stopped, "STOPPED");
            return new(ServiceStatusChecker.State.Running, "RUNNING", procs[0].Id);
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    // ── P/Invoke ──────────────────────────────────────────

    private const uint SC_MANAGER_CONNECT      = 0x0001;
    private const uint SERVICE_QUERY_STATUS    = 0x0004;
    private const int  SC_STATUS_PROCESS_INFO  = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel,
        ref SERVICE_STATUS_PROCESS buffer, uint bufSize, out uint bytesNeeded);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
