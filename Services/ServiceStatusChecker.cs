using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ExecutePrograms.Services;

/// <summary>
/// xms.exe / xflow.exe service status 명령어로 서비스 상태를 확인합니다.
/// </summary>
public class ServiceStatusChecker(Action<string> log)
{
    // stdout으로 반환되는 정상 상태값
    private static readonly HashSet<string> KnownStates =
    [
        "STOPPED", "START_PENDING", "STOP_PENDING", "RUNNING",
        "CONTINUE_PENDING", "PAUSE_PENDING", "PAUSED"
    ];

    public enum State { Running, Stopped, Pending, NotRegistered, Error }

    public record Result(State Kind, string Raw);

    // ── 상태 조회 ─────────────────────────────────────────

    public async Task<Result> CheckAsync(string workingDir, string exeName)
    {
        var psi = new ProcessStartInfo(Path.Combine(workingDir, exeName), "service status")
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        try
        {
            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var stdout   = (await stdoutTask).Trim();
            int exitCode = proc.ExitCode;

            // stdout에 알려진 상태값이 있으면 우선 사용
            if (KnownStates.Contains(stdout))
            {
                var kind = stdout switch
                {
                    "RUNNING"                              => State.Running,
                    "STOPPED"                              => State.Stopped,
                    _                                      => State.Pending,
                };
                return new(kind, stdout);
            }

            // exit code 기반 상태 — stdout이 비어있거나 알 수 없는 텍스트일 때 진입
            var raw = exitCode switch
            {
                0    => "—",              // stdout 없이 성공 종료 — 형식 불명
                2    => "NotFoundFile",
                5    => "AccessDenied",
                1056 => "RUNNING",        // ERROR_SERVICE_ALREADY_RUNNING
                1060 => "NotRegistered",  // ERROR_SERVICE_DOES_NOT_EXIST
                1062 => "STOPPED",        // ERROR_SERVICE_NOT_ACTIVE
                1072 => "MarkedDelete",
                1073 => "AlreadyExist",
                _    => $"Fail({exitCode})"
            };

            var state = exitCode switch
            {
                1056 => State.Running,
                1060 => State.NotRegistered,
                1062 => State.Stopped,
                _    => State.Error,
            };

            return new(state, raw);
        }
        catch (Exception ex)
        {
            return new(State.Error, ex.Message);
        }
    }

    // ── 멀티라인 전체 조회 ───────────────────────────────

    /// <summary>
    /// stdout의 "name: STATE" 형식 여러 줄을 파싱해 컴포넌트별 결과 목록을 반환합니다.
    /// 파싱 실패 시 CheckAsync 단일 결과로 폴백합니다.
    /// </summary>
    public async Task<List<(string Name, Result Status)>> CheckAllAsync(string workingDir, string exeName)
    {
        var psi = new ProcessStartInfo(Path.Combine(workingDir, exeName), "service status")
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        var items = new List<(string, Result)>();
        try
        {
            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var stdout = (await stdoutTask).Trim();

            foreach (var line in stdout.Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var col = line.IndexOf(':');
                if (col < 0) continue;

                var name     = line[..col].Trim();
                var stateStr = line[(col + 1)..].Trim();

                var kind = stateStr switch
                {
                    "RUNNING"   => State.Running,
                    "STOPPED"   => State.Stopped,
                    "NotExist"  => State.NotRegistered,
                    "START_PENDING" or "STOP_PENDING" or
                    "CONTINUE_PENDING" or "PAUSE_PENDING" or "PAUSED" => State.Pending,
                    _           => State.Error,
                };
                items.Add((name, new Result(kind, stateStr)));
            }

            // 멀티라인 파싱 실패 시 단일 결과 폴백
            if (items.Count == 0)
            {
                var single = await CheckAsync(workingDir, exeName);
                items.Add((Path.GetFileNameWithoutExtension(exeName), single));
            }
        }
        catch (Exception ex)
        {
            items.Add((Path.GetFileNameWithoutExtension(exeName), new Result(State.Error, ex.Message)));
        }
        return items;
    }

    // ── 로그 출력 헬퍼 ────────────────────────────────────

    /// <summary>상태를 확인하고 결과를 로그에 기록합니다.</summary>
    public async Task<Result> LogStatusAsync(string label, string workingDir, string exeName)
    {
        var result = await CheckAsync(workingDir, exeName);

        var (icon, desc) = result.Kind switch
        {
            State.Running       => ("✓", result.Raw),
            State.Stopped       => ("■", result.Raw),
            State.Pending       => ("…", result.Raw),
            State.NotRegistered => ("ℹ", "서비스 미등록"),
            _                   => ("⚠", result.Raw),
        };

        log($"  {icon} {label} 상태: {desc}");
        return result;
    }
}
