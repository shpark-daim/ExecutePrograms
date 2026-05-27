using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ExecutePrograms.Services;

/// <summary>
/// 프로그램 시작 / 종료를 담당합니다.
/// </summary>
public class ProcessLauncher(Action<string> log)
{
    // ── 시작 ──────────────────────────────────────────────

    /// <summary>
    /// 지정 디렉터리에서 파일을 실행합니다.
    /// 2초 후 생존 여부를 확인하고, 숨김 프로세스는 stderr를 백그라운드로 수집합니다.
    /// </summary>
    /// <param name="showWindow">true: 창 표시, false: 창 숨김 + stderr 수집</param>
    public async Task StartInDirAsync(string directory, string fileName, bool showWindow = true)
    {
        log($"  ▶ 실행: {fileName}");

        var psi = new ProcessStartInfo(Path.Combine(directory, fileName))
        {
            UseShellExecute       = showWindow,
            CreateNoWindow        = !showWindow,
            WorkingDirectory      = directory,
            RedirectStandardError = !showWindow,  // 숨김일 때만 캡처 (파이프 버퍼 막힘 방지)
        };

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            log($"  ❌ {fileName} — 시작 실패: {ex.Message}");
            return;
        }

        if (proc == null)
        {
            log($"  ❌ {fileName} — 프로세스를 시작할 수 없습니다");
            return;
        }

        // 숨김 프로세스: stderr 백그라운드 수집 (오류 즉시 로그 + 파이프 버퍼 소비)
        if (!showWindow)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await proc.StandardError.ReadLineAsync()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            log($"  ⚠ [{fileName}] {line}");
                    }
                }
                catch { /* 프로세스 종료 시 스트림 닫힘 — 정상 */ }
            });
        }

        // 2초 후 생존 체크
        await Task.Delay(2000);

        if (proc.HasExited)
            log($"  ❌ {fileName} — 즉시 종료됨 (종료 코드: {proc.ExitCode})");
        else
            log($"  ✓ {fileName} 실행 중 (PID {proc.Id})");
    }

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

    /// <summary>프로세스 이름으로 실행 중인 모든 인스턴스를 강제 종료합니다.</summary>
    public Task KillProcessByNameAsync(string processName)
    {
        var name  = Path.GetFileNameWithoutExtension(processName);
        var procs = Process.GetProcessesByName(name);

        if (procs.Length == 0)
        {
            log($"  ℹ {processName} — 실행 중인 프로세스 없음");
            return Task.CompletedTask;
        }

        foreach (var p in procs)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                log($"  ✓ {processName} 종료 완료 (PID {p.Id})");
            }
            catch (Exception ex)
            {
                log($"  ⚠ {processName} 종료 실패: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }
}
