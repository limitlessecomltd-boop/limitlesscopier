using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace LTC.App.Services;

/// <summary>
/// Detects the broker IP that an MT5 terminal is currently talking to. Walks the
/// Windows TCP table via iphlpapi.dll (<c>GetExtendedTcpTable</c>), filters rows
/// owned by <c>terminal64.exe</c>, and returns the active broker connection.
///
/// Strategy:
/// 1. Find every PID named "terminal64".
/// 2. Get the TCP table with PID info.
/// 3. Keep rows where (state == Established) AND (owning PID is terminal64).
/// 4. Drop loopback / private-LAN rows; what's left is the broker socket.
///
/// Notes / assumptions:
/// - We assume only ONE terminal64.exe is running. If there are multiple, the
///   detector still works but may pick the wrong one — the UI tells the user
///   to close other windows.
/// - We don't filter by port (443/444/1950/etc). MT5 uses random broker ports,
///   so we filter by "non-loopback, non-RFC1918" instead.
/// </summary>
public sealed class Mt5IpDetector
{
    /// <summary>
    /// One-shot detection. Returns null if no terminal64.exe is running OR no
    /// suitable TCP connection is found.
    /// </summary>
    public DetectionResult Detect()
    {
        var pids = FindTerminal64Pids();
        if (pids.Count == 0)
            return new DetectionResult(false, null, null, "Terminal64.exe is not running. Please open MT5 and log in.");
        if (pids.Count > 1)
            return new DetectionResult(false, null, null,
                $"Multiple MT5 terminals are running ({pids.Count}). Please close all but one — the one you want to add.");

        var pid = pids[0];

        IPEndPoint[] remotes;
        try
        {
            remotes = GetEstablishedRemotesForPid(pid);
        }
        catch (Exception ex)
        {
            return new DetectionResult(false, null, null, $"Could not read TCP table: {ex.Message}");
        }

        // Drop loopback and private-LAN addresses — MT5's broker connection is
        // always a public IP. (We also drop IPv6 link-local for the same reason.)
        var brokerCandidates = remotes
            .Where(ep => IsBrokerIp(ep.Address))
            .ToList();

        if (brokerCandidates.Count == 0)
            return new DetectionResult(false, null, null,
                "MT5 is running but no broker connection is established yet. Try logging in again, or wait a few seconds.");

        // If multiple broker IPs (unusual — proxy?), prefer the most-common port (443).
        var pick = brokerCandidates.OrderByDescending(c => c.Port == 443 ? 1 : 0).First();
        return new DetectionResult(true, pick.Address.ToString(), pick.Port, null);
    }

    private static List<int> FindTerminal64Pids()
    {
        var pids = new List<int>();
        foreach (var proc in Process.GetProcessesByName("terminal64"))
        {
            try { pids.Add(proc.Id); }
            catch { /* tolerate */ }
            finally { proc.Dispose(); }
        }
        return pids;
    }

    private static bool IsBrokerIp(IPAddress addr)
    {
        if (IPAddress.IsLoopback(addr)) return false;
        var bytes = addr.GetAddressBytes();
        if (bytes.Length != 4) return false;          // ignore IPv6 for simplicity
        // RFC1918 private ranges
        if (bytes[0] == 10) return false;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return false;
        if (bytes[0] == 192 && bytes[1] == 168) return false;
        // Link-local
        if (bytes[0] == 169 && bytes[1] == 254) return false;
        return true;
    }

    // ----------- Win32 interop: GetExtendedTcpTable -----------
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwOutBufLen, bool sort,
        int ipVersion, int tblClass, int reserved);

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint MIB_TCP_STATE_ESTAB = 5;

    private static IPEndPoint[] GetEstablishedRemotesForPid(int pid)
    {
        int bufSize = 0;
        // First call: get required size.
        GetExtendedTcpTable(IntPtr.Zero, ref bufSize, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

        var ptr = Marshal.AllocHGlobal(bufSize);
        try
        {
            var rc = GetExtendedTcpTable(ptr, ref bufSize, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (rc != 0) throw new Win32Exception((int)rc);

            int rowCount = Marshal.ReadInt32(ptr);
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            var results = new List<IPEndPoint>();

            // First DWORD is the count; the rows follow.
            var rowPtr = ptr + 4;
            for (int i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr + (i * rowSize));
                if (row.owningPid != (uint)pid) continue;
                if (row.state != MIB_TCP_STATE_ESTAB) continue;

                var addr = new IPAddress(BitConverter.GetBytes(row.remoteAddr));
                // remotePort is in network byte order — high byte first
                var port = ((row.remotePort & 0xFF) << 8) | ((row.remotePort >> 8) & 0xFF);
                results.Add(new IPEndPoint(addr, (int)port));
            }
            return results.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}

/// <summary>Outcome of a single Detect() call.</summary>
public sealed record DetectionResult(bool Success, string? Ip, int? Port, string? Reason);
