using System;
using System.IO;
using System.Threading.Tasks;
using Npgsql;

namespace ExecutePrograms.Services;

/// <summary>
/// PostgreSQL에 연결하여 SQL 파일을 실행합니다.
/// </summary>
public class DbInitializer(Action<string> log)
{
    public async Task RunSqlFileAsync(
        string host, string port,
        string database, string user, string password,
        string sqlFilePath)
    {
        var connStr = $"Host={host};Port={port};Database={database};" +
                      $"Username={user};Password={password}";

        log($"  연결 중... ({host}:{port} / {database})");

        var sql = await File.ReadAllTextAsync(sqlFilePath);

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        log($"  연결 성공. SQL 실행 중...");

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();

        log($"  SQL 실행 완료");
    }
}
