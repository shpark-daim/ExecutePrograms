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
    // ── 시작 (fire-and-forget) ─────────────────────────

    /// <summary>지정 디렉터리에서 파일을 실행합니다 (대기 없음).</summary>
    /// <param name="showWindow">true: 창 표시 (GUI/콘솔 기본값), false: 창 숨김</param>
    public Task StartInDirAsync(string directory, string fileName, bool showWindow = true)
    {
        log($"  ▶ 실행: {fileName}");

        var psi = showWindow
            ? new ProcessStartInfo(Path.Combine(directory, fileName))
              {
                  UseShellExecute  = true,
                  WorkingDirectory = directory
              }
            : new ProcessStartInfo(Path.Combine(directory, fileName))
              {
                  UseShellExecute  = false,
                  CreateNoWindow   = true,
                  WorkingDirectory = directory
              };

        Process.Start(psi);
        log($"  프로세스 시작됨");
        return Task.CompletedTask;
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

    // ── 종료 ──────────────────────────────────────────

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
