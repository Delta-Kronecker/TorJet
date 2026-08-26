// tun-helper.cs - builds to tun-helper.exe (compile with build-start-tor.ps1).
// TorJet Core License v1.0 (see LICENSE). Using this Core in another program
// requires the mandatory attribution of https://github.com/Delta-Kronecker/TorJet.
// Elevated supervisor for TorJet TUN mode. The tunnel itself is an Xray-core
// TUN instance (data\xray-tun.json, written by the launcher): it captures all
// system traffic, resolves DNS through tor (hijacked port 53), excludes
// tor.exe/xray.exe by process, and blocks UDP. This helper only supervises:
// spawn elevated, watch health, tear down cleanly.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TunHelper
{
    internal static class Program
    {
        private static readonly string AppDir =
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        private static readonly string DataDir = ResolveDataDir();
        private static readonly string StateFile = Path.Combine(DataDir, "tun-state.txt");
        private static readonly string StopFile = Path.Combine(DataDir, "tun-stop.txt");
        private static readonly string ResultFile = Path.Combine(DataDir, "tun-result.txt");
        private static readonly string XrayExe = Path.Combine(DataDir, "xray.exe");
        private static readonly string XrayTunConfig = Path.Combine(DataDir, "xray-tun.json");
        private static readonly string WintunDll = Path.Combine(DataDir, "wintun.dll");

        // The launcher knows the data directory for sure; prefer it when the
        // helper is started by the launcher.
        private static string ResolveDataDir()
        {
            string env = Environment.GetEnvironmentVariable("TUN_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(env) &&
                File.Exists(Path.Combine(env, "xray.exe")) &&
                File.Exists(Path.Combine(env, "wintun.dll")))
                return env;
            string d1 = Path.Combine(AppDir, "data");
            if (File.Exists(Path.Combine(d1, "xray.exe"))) return d1;
            if (File.Exists(Path.Combine(AppDir, "xray.exe"))) return AppDir;
            return d1;
        }

        private const string TunName = "TorJetTun";

        // Only one keeper may run at a time: a second "on" (e.g. the user
        // pressing T twice in a row) used to race the first one.
        private static readonly Mutex TunMutex = new Mutex(true, @"Local\TorJetTunHelper");

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint WintunDeleteAdapterFn([MarshalAs(UnmanagedType.LPWStr)] string name);

        private static bool WintunDeleteAdapterByName(string name)
        {
            try
            {
                IntPtr h = LoadLibrary(Path.Combine(DataDir, "wintun.dll"));
                if (h == IntPtr.Zero) return false;
                IntPtr fn = GetProcAddress(h, "WintunDeleteAdapter");
                if (fn == IntPtr.Zero) return false;
                var del = (WintunDeleteAdapterFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(WintunDeleteAdapterFn));
                return del(name) == 0;
            }
            catch { return false; }
        }

        private static string ReadState()
        {
            try { if (File.Exists(StateFile)) return File.ReadAllText(StateFile); }
            catch { }
            return "";
        }

        private static void WriteState(string content)
        {
            try { File.WriteAllText(StateFile, content, new UTF8Encoding(false)); } catch { }
        }

        private static string GetStateValue(string state, string key)
        {
            foreach (string raw in state.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith(key + "=", StringComparison.Ordinal))
                    return line.Substring(key.Length + 1);
            }
            return "";
        }

        private static string ReadResult()
        {
            try { if (File.Exists(ResultFile)) return File.ReadAllText(ResultFile).Trim(); }
            catch { }
            return "";
        }

        private static void WriteResult(string text)
        {
            try { File.WriteAllText(ResultFile, text, new UTF8Encoding(false)); } catch { }
        }

        // ---- relay routes ----------------------------------------------------
        // Xray's TUN captures everything, including tor's own connections to
        // guards/bridges — that would deadlock the core. Mainline Xray has no
        // process-based routing, so tor's destinations (every relay IP in the
        // cached consensus + every bridge IP) get /32 routes via the physical
        // gateway, which take precedence over the TUN's default route.
        private const uint NO_ERROR = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct SockaddrInet
        {
            public ushort si_family;
            public ushort si_port;
            public uint si_flowinfo;
            public uint si_addr0;
            public uint si_addr1;
            public uint si_addr2;
            public uint si_addr3;
            public uint si_scope_id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IpAddressPrefix
        {
            public SockaddrInet Prefix;
            public byte PrefixLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibIpForwardRow2
        {
            public ulong InterfaceLuid;
            public uint InterfaceIndex;
            public IpAddressPrefix DestinationPrefix;
            public SockaddrInet NextHop;
            public byte SitePrefixLength;
            public uint ValidLifetime;
            public uint PreferredLifetime;
            public uint Metric;
            public uint Protocol;
            public byte Loopback;
            public byte AutoconfigureAddress;
            public byte Publish;
            public byte Immortal;
            public uint Age;
            public uint Origin;
        }

        [DllImport("netioapi.dll")]
        private static extern uint SetIpForwardEntry2(ref MibIpForwardRow2 row);
        [DllImport("netioapi.dll")]
        private static extern uint DeleteIpForwardEntry2(ref MibIpForwardRow2 row);

        [StructLayout(LayoutKind.Sequential)]
        private struct MibIpForwardRow
        {
            public uint dest;
            public uint mask;
            public uint policy;
            public uint nextHop;
            public int ifIndex;
            public uint type;
            public uint proto;
            public uint age;
            public uint nextHopAs;
            public uint metric1, metric2, metric3, metric4, metric5;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetBestRoute(uint dest, uint source, out MibIpForwardRow row);

        private static uint IpToUInt(string ip)
        {
            byte[] b = IPAddress.Parse(ip).GetAddressBytes();
            return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        }

        private static string UIntToIp(uint v)
        {
            return string.Format("{0}.{1}.{2}.{3}", (v >> 24) & 255, (v >> 16) & 255,
                                 (v >> 8) & 255, v & 255);
        }

        private static SockaddrInet SockFromUInt(uint v)
        {
            SockaddrInet s = new SockaddrInet();
            s.si_family = 2; // AF_INET
            s.si_addr0 = v;
            return s;
        }

        private static bool AddRelayRoute(string ip, int ifIndex, uint nextHop)
        {
            MibIpForwardRow2 r = new MibIpForwardRow2();
            r.InterfaceIndex = (uint)ifIndex;
            r.DestinationPrefix.Prefix = SockFromUInt(IpToUInt(ip));
            r.DestinationPrefix.PrefixLength = 32;
            r.NextHop = SockFromUInt(nextHop);
            r.SitePrefixLength = 0;
            r.ValidLifetime = 0xFFFFFFFF;
            r.PreferredLifetime = 0xFFFFFFFF;
            r.Protocol = 3; // NETMGMT
            return SetIpForwardEntry2(ref r) == NO_ERROR;
        }

        private static bool DeleteRelayRoute(string ip, int ifIndex, uint nextHop)
        {
            MibIpForwardRow2 r = new MibIpForwardRow2();
            r.InterfaceIndex = (uint)ifIndex;
            r.DestinationPrefix.Prefix = SockFromUInt(IpToUInt(ip));
            r.DestinationPrefix.PrefixLength = 32;
            r.NextHop = SockFromUInt(nextHop);
            return DeleteIpForwardEntry2(ref r) == NO_ERROR;
        }

        private static bool IsPublicIpv4(string s)
        {
            IPAddress a;
            if (!IPAddress.TryParse(s, out a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = a.GetAddressBytes();
            if (b[0] == 0 || b[0] == 10 || b[0] == 127) return false;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] >= 224) return false;
            return true;
        }

        // Relay IPs from the cached consensus (microdesc + full) and every
        // bridge list in data\bridges.
        private static HashSet<string> ParseRelayIps()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            string bridgesDir = Path.Combine(DataDir, "bridges");
            foreach (string f in new[]
            {
                Path.Combine(DataDir, "data", "cached-microdesc-consensus"),
                Path.Combine(DataDir, "data", "cached-consensus")
            })
            {
                if (!File.Exists(f)) continue;
                try
                {
                    foreach (string raw in File.ReadAllLines(f))
                    {
                        if (!raw.StartsWith("r ")) continue;
                        string[] parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 5; i <= 6 && i < parts.Length; i++)
                            if (IsPublicIpv4(parts[i])) set.Add(parts[i]);
                    }
                }
                catch { }
            }
            if (Directory.Exists(bridgesDir))
            {
                try
                {
                    foreach (string f in Directory.GetFiles(bridgesDir, "*.txt"))
                    {
                        foreach (string raw in File.ReadAllLines(f))
                        {
                            foreach (Match m in Regex.Matches(raw, @"\b(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\b"))
                                if (IsPublicIpv4(m.Groups[1].Value)) set.Add(m.Groups[1].Value);
                        }
                    }
                }
                catch { }
            }
            return set;
        }

        private static bool TorUp(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    using (TcpClient c = new TcpClient())
                    {
                        IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, 9050, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(800) && c.Connected) return true;
                    }
                }
                catch { }
                Thread.Sleep(200);
            }
            return false;
        }

        private static void Run(string file, string args)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch { }
        }

        // IPv6 kill switch: the tunnel is IPv4-only, so IPv6 traffic would
        // bypass it (or hang the browser). Toggled while tunnelled.
        private static void SetIp6Binding(bool enable)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command " +
                        (enable ? "Enable" : "Disable") +
                        "-NetAdapterBinding -ComponentID ms_tcpip6 -Name *",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch { }
        }

        private static bool AdapterUp()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = "interface show interface",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using (Process p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return outp.IndexOf(TunName, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        private static void KillPid(string pid)
        {
            int n;
            if (!string.IsNullOrEmpty(pid) && int.TryParse(pid, out n) && n > 0)
            {
                try { Process.GetProcessById(n).Kill(); } catch { }
            }
        }

        private static void Teardown()
        {
            string st = ReadState();
            KillPid(GetStateValue(st, "pid"));
            Thread.Sleep(500);
            // relay /32 routes reference the physical gateway — remove first
            string gwStr = GetStateValue(st, "physGw");
            string ifStr = GetStateValue(st, "physIf");
            string relays = GetStateValue(st, "relays");
            int pif;
            int.TryParse(ifStr, out pif);
            if (pif > 0 && gwStr.Length > 0 && relays.Length > 0)
            {
                uint gw = IpToUInt(gwStr);
                foreach (string ip in relays.Split(','))
                {
                    string t = ip.Trim();
                    if (t.Length == 0) continue;
                    DeleteRelayRoute(t, pif, gw);
                }
            }
            WintunDeleteAdapterByName(TunName);
            SetIp6Binding(true);
            Run("ipconfig.exe", "/flushdns");
            WriteState("status=off");
            try { if (File.Exists(StopFile)) File.Delete(StopFile); } catch { }
        }

        private static int RunKeeper()
        {
            WriteResult("enabling...");
            if (!File.Exists(XrayExe)) { WriteResult("error: xray.exe not found in " + DataDir); return 1; }
            if (!File.Exists(WintunDll)) { WriteResult("error: wintun.dll not found in " + DataDir); return 1; }
            if (!File.Exists(XrayTunConfig)) { WriteResult("error: xray-tun.json not found (reconnect from TorJet)"); return 1; }
            if (!TorUp(2000)) { WriteResult("error: tor SOCKS (127.0.0.1:9050) is not up"); return 1; }
            if (GetStateValue(ReadState(), "status") == "on") { WriteResult("error: TUN is already on"); return 1; }

            // Kill orphaned xray.exe from earlier attempts first: a stale
            // holder wedges wintun ("Failed to register rings") and every
            // later attempt times out. The only xray.exe on the system is
            // ours, so the path match is safe.
            foreach (Process p in Process.GetProcessesByName("xray"))
            {
                try
                {
                    if (p.MainModule.FileName.Equals(XrayExe, StringComparison.OrdinalIgnoreCase))
                        p.Kill();
                }
                catch { }
            }
            Thread.Sleep(300);

            // clean leftovers from a previous (crashed) run
            KillPid(GetStateValue(ReadState(), "pid"));
            WintunDeleteAdapterByName(TunName);
            SetIp6Binding(false);

            // physical gateway for the relay /32 routes (TUN is not up yet,
            // so the best route to 0/0 IS the physical default)
            MibIpForwardRow phys;
            if (GetBestRoute(0, 0, out phys) != NO_ERROR)
            {
                WriteResult("error: no IPv4 default route");
                return 1;
            }
            uint physGw = phys.nextHop;
            int physIf = phys.ifIndex;

            Process xray;
            try
            {
                xray = Process.Start(new ProcessStartInfo
                {
                    FileName = XrayExe,
                    Arguments = "run -c \"" + XrayTunConfig + "\"",
                    WorkingDirectory = DataDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex)
            {
                WriteResult("error: cannot start xray: " + ex.Message);
                return 1;
            }

            // wait for the adapter (Xray autoRoute sets the default route itself)
            bool up = false;
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(500);
                try { xray.Refresh(); if (xray.HasExited) break; } catch { break; }
                if (AdapterUp() && TorUp(300)) { up = true; break; }
            }
            if (!up)
            {
                try { xray.Kill(); } catch { }
                WintunDeleteAdapterByName(TunName);
                SetIp6Binding(true);
                WriteResult("error: TUN adapter did not come up (is xray-tun.json valid?)");
                return 1;
            }

            // tor must reach its guards/bridges outside the tunnel
            var relays = ParseRelayIps();
            int relayOk = 0;
            foreach (string ip in relays)
                if (AddRelayRoute(ip, physIf, physGw)) relayOk++;

            WriteState("status=on\r\npid=" + xray.Id +
                       "\r\nphysGw=" + UIntToIp(physGw) +
                       "\r\nphysIf=" + physIf +
                       "\r\nrelays=" + string.Join(",", relays));
            WriteResult("on: xray TUN active (" + relayOk + "/" + relays.Count +
                        " relay routes, DNS hijacked, UDP blocked)");
            Run("ipconfig.exe", "/flushdns");

            int torDown = 0;
            try
            {
                while (true)
                {
                    Thread.Sleep(4000);
                    if (File.Exists(StopFile)) break;
                    try { xray.Refresh(); if (xray.HasExited) break; } catch { break; }
                    if (TorUp(1500)) torDown = 0; else torDown++;
                    if (torDown >= 4) break;
                }
            }
            finally
            {
                try { xray.Kill(); } catch { }
                Thread.Sleep(500);
                Teardown();
                WriteResult("off");
            }
            return 0;
        }

        private static int Main(string[] args)
        {
            string arg = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            if (arg == "on")
            {
                bool acquired;
                try { acquired = TunMutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    WriteResult("error: TUN is already on (another instance is running)");
                    return 1;
                }
                try { return RunKeeper(); }
                finally { try { TunMutex.ReleaseMutex(); } catch { } }
            }
            if (arg == "off")
            {
                Teardown();
                WriteResult("off");
                return 0;
            }
            WriteResult("status=" + (GetStateValue(ReadState(), "status") == "on" ? "on" : "off"));
            return 0;
        }
    }
}
