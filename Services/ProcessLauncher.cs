using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ExecutePrograms.Services;

/// <summary>
/// 프로그램 시작 / 종료를 담당합니다.
/// 런처가 띄운 프로세스는 핸들을 보관해 언제 죽든 종료 코드와 함께 기록합니다.
/// </summary>
/// <param name="log">실행 로그</param>
/// <param name="eventLog">종료 같은 생애 이벤트 로그. null 이면 log 를 사용</param>
public class ProcessLauncher(Action<string> log, Action<string>? eventLog = null)
{
    private const int TailLines = 20;

    /// <summary>런처가 띄워 아직 살아 있는 프로세스. 키는 확장자 없는 파일명.</summary>
    public static readonly ConcurrentDictionary<string, Process> Owned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>런처가 직접 종료를 요청한 이름. Exited 에서 "요청된 종료"와 "사망"을 구분한다.</summary>
    private static readonly ConcurrentDictionary<string, byte> ExpectedExits = new(StringComparer.OrdinalIgnoreCase);

    // ── 시작 ──────────────────────────────────────────────

    /// <summary>
    /// 지정 디렉터리에서 파일을 실행합니다.
    /// 2초 후 생존 여부를 로그하고, 이후 종료되면 Exited 이벤트로 종료 코드를 기록합니다.
    /// </summary>
    /// <param name="showWindow">true: 창 표시, false: 창 숨김 + stdout/stderr 수집 (마지막 20줄은 비정상 종료 시 덤프)</param>
    public async Task StartInDirAsync(string directory, string fileName, bool showWindow = true)
    {
        log($"  ▶ 실행: {fileName}");
        var name = Path.GetFileNameWithoutExtension(fileName);

        var psi = new ProcessStartInfo(Path.Combine(directory, fileName))
        {
            UseShellExecute        = showWindow,
            CreateNoWindow         = !showWindow,
            WorkingDirectory       = directory,
            RedirectStandardOutput = !showWindow,   // 숨김일 때만 캡처 — 파이프 버퍼가 막히지 않도록 계속 소비한다
            RedirectStandardError  = !showWindow,
        };

        // EnableRaisingEvents 는 Start 전에 걸어야 2초 안의 즉사도 놓치지 않는다
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tail = new ConcurrentQueue<string>();
        proc.Exited += async (_, _) =>
        {
            await Task.Delay(300);   // 파이프에 남은 마지막 출력이 tail 에 들어올 시간
            OnExited(proc, name, fileName, tail);
        };

        try
        {
            if (!proc.Start())
            {
                log($"  ❌ {fileName} — 프로세스를 시작할 수 없습니다");
                proc.Dispose();
                return;
            }
        }
        catch (Exception ex)
        {
            log($"  ❌ {fileName} — 시작 실패: {ex.Message}");
            proc.Dispose();
            return;
        }

        ExpectedExits.TryRemove(name, out _);   // 이전 종료 요청 흔적 제거
        Owned[name] = proc;

        if (!showWindow)
        {
            _ = PumpAsync(proc.StandardError,  fileName, tail, logLines: true);
            _ = PumpAsync(proc.StandardOutput, fileName, tail, logLines: false);
        }

        // 2초 후 생존 체크 — 이미 죽었으면 Exited 핸들러가 기록한다
        await Task.Delay(2000);
        if (!proc.HasExited)
            log($"  ✓ {fileName} 실행 중 (PID {proc.Id})");
    }

    /// <summary>스트림을 끝까지 읽으며 마지막 N줄을 보관합니다. stderr 는 즉시 로그에도 흘립니다.</summary>
    private async Task PumpAsync(StreamReader reader, string fileName, ConcurrentQueue<string> tail, bool logLines)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                tail.Enqueue(line);
                while (tail.Count > TailLines) tail.TryDequeue(out _);
                if (logLines) log($"  ⚠ [{fileName}] {line}");
            }
        }
        catch { /* 프로세스 종료 시 스트림 닫힘 — 정상 */ }
    }

    private void OnExited(Process proc, string name, string fileName, ConcurrentQueue<string> tail)
    {
        Owned.TryRemove(name, out _);
        int code;
        try { code = proc.ExitCode; } catch { code = -1; }
        bool expected = ExpectedExits.TryRemove(name, out _);
        var write = eventLog ?? log;

        if (expected)
            write($"  ■ {fileName} 종료 (런처 요청, 코드 {code})");
        else if (code == 0)
            write($"  ■ {fileName} 종료됨 (코드 0)");
        else
        {
            write($"  ❌ {fileName} 비정상 종료 (코드 {code})");
            if (!tail.IsEmpty)
            {
                log($"  ── {fileName} 마지막 출력 ──");
                foreach (var l in tail) log($"    {l}");
            }
        }
        proc.Dispose();
        OwnedProcessExited?.Invoke();
    }

    /// <summary>런처가 띄운 프로세스가 종료될 때마다 발생. 상태 폴링을 즉시 깨우는 데 쓴다.</summary>
    public static event Action? OwnedProcessExited;

    /// <summary>start.bat 등 BAT 파일을 비동기(fire-and-forget)로 실행합니다.</summary>
    public Task StartBatAsync(string directory, string batFile)
    {
        log($"  ▶ BAT 실행: {batFile}");

        var psi = new ProcessStartInfo("cmd.exe", $"/c \"{Path.Combine(directory, batFile)}\"")
        {
            UseShellExecute  = false,
            CreateNoWindow   = true,
            WorkingDirectory = directory
        };

        Process.Start(psi);
        log($"  프로세스 시작됨");
        return Task.CompletedTask;
    }

    // ── 종료 ──────────────────────────────────────────────

    /// <summary>프로세스 이름으로 실행 중인 모든 인스턴스를 강제 종료하고 최대 3초 종료를 기다립니다.</summary>
    public Task KillProcessByNameAsync(string processName)
    {
        var name  = Path.GetFileNameWithoutExtension(processName);
        var procs = Process.GetProcessesByName(name);

        if (procs.Length == 0)
        {
            log($"  ℹ {processName} — 실행 중인 프로세스 없음");
            return Task.CompletedTask;
        }

        ExpectedExits[name] = 1;   // Exited 로그가 "비정상 종료"로 찍히지 않도록

        foreach (var p in procs)
        {
            try
            {
                int pid = p.Id;
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);   // 바로 재시작할 때 포트·파일 잠금이 풀릴 시간
                log($"  ✓ {processName} 종료 완료 (PID {pid})");
            }
            catch (Exception ex)
            {
                log($"  ⚠ {processName} 종료 실패: {ex.Message}");
            }
            finally { p.Dispose(); }
        }

        return Task.CompletedTask;
    }
}
