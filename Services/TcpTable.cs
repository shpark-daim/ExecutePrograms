using System.Runtime.InteropServices;

namespace ExecutePrograms.Services;

/// <summary>
/// OS 의 TCP 리스너 테이블을 PID 별로 읽습니다 (IPv4 + IPv6). 연결을 맺지 않으므로 서버 로그를 오염시키지 않고,
/// 관리자 권한도 필요 없습니다. 호출 한 번이 수 ms 라 5초 폴링에 부담이 없습니다.
/// </summary>
public static class TcpTable
{
    /// <summary>PID → LISTEN 중인 로컬 포트 집합</summary>
    public static Dictionary<int, HashSet<int>> ListeningPortsByPid()
    {
        var result = new Dictionary<int, HashSet<int>>();
        try { ReadTable(AF_INET,  result); } catch { /* 무시 */ }
        try { ReadTable(AF_INET6, result); } catch { /* 무시 */ }
        return result;
    }

    private static void ReadTable(int family, Dictionary<int, HashSet<int>> result)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size <= 0) return;

        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return;

            int count = Marshal.ReadInt32(buf);
            // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, owningPid (각 4바이트)
            // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScopeId, localPort, remoteAddr[16], remoteScopeId, remotePort, state, owningPid
            int rowSize    = family == AF_INET ? 24 : 56;
            int portOffset = family == AF_INET ? 8  : 20;
            int pidOffset  = family == AF_INET ? 20 : 52;

            var row = buf + 4;
            for (int i = 0; i < count; i++, row += rowSize)
            {
                int rawPort = Marshal.ReadInt32(row + portOffset);
                int port    = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF);   // 네트워크 바이트 순서 → 호스트
                int pid     = Marshal.ReadInt32(row + pidOffset);
                if (!result.TryGetValue(pid, out var set)) result[pid] = set = [];
                set.Add(port);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private const int AF_INET  = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
}
