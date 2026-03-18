using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ExecutePrograms.Services;

/// <summary>
/// 지정된 디렉터리에서 CMD 명령어들을 순차 실행합니다.
/// 명령어 텍스트는 줄바꿈으로 구분된 여러 줄을 지원합니다.
/// </summary>
public class CommandRunner(Action<string> log)
{
    /// <param name="timeoutMs">0이면 무제한 대기. 양수면 해당 ms 후 프로세스를 강제 종료합니다.</param>
    public async Task RunAsync(string workingDirectory, string commandsText, int timeoutMs = 0)
    {
        var lines = commandsText.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            log($"  > {line}");

            var psi = new ProcessStartInfo("cmd.exe", $"/c {line}")
            {
                WorkingDirectory       = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = true,   // pause 등 대기 방지
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var process = Process.Start(psi)!;
            process.StandardInput.Close();       // stdin EOF → pause 즉시 통과

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            bool timedOut = false;
            if (timeoutMs > 0)
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    try { process.Kill(entireProcessTree: true); } catch { }
                    log($"  ⚠ 타임아웃으로 중단 ({timeoutMs / 1000}초 초과)");
                }
            }
            else
            {
                await process.WaitForExitAsync();
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (!string.IsNullOrWhiteSpace(stdout))
                log($"    {stdout.Trim()}");

            if (!string.IsNullOrWhiteSpace(stderr))
                log($"    [STDERR] {stderr.Trim()}");

            if (!timedOut)
                log($"  완료 (종료 코드: {process.ExitCode})");
        }
    }
}
