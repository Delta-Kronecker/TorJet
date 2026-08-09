// tun-helper.cs - builds to tun-helper.exe (compile with build-start-tor.ps1).
// Elevated Windows helper for TorBoost TUN mode. Compiled as winexe: no console.
//
// TUN mode routes ALL system IPv4 traffic through the local tor by creating a
// Wintun adapter (via tun2socks.exe + wintun.dll), moving the default route into
// it, pointing system DNS at tor's DNSPort (127.0.0.1:53) and adding per-relay
// routes on the physical interface so tor's own relay connections bypass the
// tunnel. Without those relay routes tor's outbound would loop back into itself
// (tun2socks loopback problem with a local SOCKS proxy).
//
// Usage:
//   tun-helper.exe on     - enable TUN, then keep running. Stops (teardown) when
//                           data\tun-stop.txt appears, tun2socks exits, or tor
//                           SOCKS goes down.
//   tun-helper.exe off    - disable TUN from data\tun-state.txt (best effort).
//   tun-helper.exe status - write data\tun-result.txt with the current TUN state.
//
// Files (next to this exe):
//   data\tun2socks.exe  data\wintun.dll  data\torrc (patched)  data\data\ (state/log)
//   data\tun-state.txt  data\tun-stop.txt data\tun-result.txt
//
// Routes are added with the modern NetIO API (SetIpForwardEntry2, netioapi.dll)
// when it is available; otherwise we fall back to the legacy iphlpapi API
// (CreateIpForwardEntry), which exists on every Windows. Both are fast enough
// for the several-thousand relay routes.
//
// Ports default to the launcher's 9050/9051 but can be overridden with the
// TUN_SOCKS_PORT / TUN_CTRL_PORT environment variables (used for testing).
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
        private static readonly string DataDir = Path.Combine(AppDir, "data");
        private static readonly string StateFile = Path.Combine(DataDir, "tun-state.txt");
        private static readonly string StopFile = Path.Combine(DataDir, "tun-stop.txt");
        private static readonly string ResultFile = Path.Combine(DataDir, "tun-result.txt");
        private static readonly string Torrc = Path.Combine(DataDir, "torrc");
        private static readonly string TunExe = Path.Combine(DataDir, "tun2socks.exe");
        private static readonly string WintunDll = Path.Combine(DataDir, "wintun.dll");
        private static readonly string Consensus1 = Path.Combine(DataDir, "data", "cached-microdesc-consensus");
        private static readonly string Consensus2 = Path.Combine(DataDir, "data", "cached-consensus");
        private static readonly string BridgesDir = Path.Combine(DataDir, "bridges");
        private static readonly string ControlPassword = "newway-j7DJPvxLaS1H";

        private const string TunName = "TorBoostTun";
        private const string TunAddr = "10.0.0.1";
        private const string TunMask = "255.255.255.0";
        private const string DnsMarker = "DNSPort 127.0.0.1:53";

        private static readonly int SocksPort = GetEnvPort("TUN_SOCKS_PORT", 9050);
        private static readonly int CtrlPort = GetEnvPort("TUN_CTRL_PORT", 9051);

        // Only one keeper may run at a time: a second "on" (e.g. the user pressing
        // T twice in a row) used to race the first one, creating a second adapter
        // named "TorBoostTun 1" that the fixed-name netsh calls then mis-targeted,
        // which made the default-route step fail and left the state file empty.
        private static readonly Mutex TunMutex = new Mutex(true, @"Local\TorBoostTunHelper");

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

        private static void KillStaleTun2Socks()
        {
            try
            {
                string dir = DataDir.TrimEnd('\\');
                foreach (Process p in Process.GetProcessesByName("tun2socks"))
                {
                    try
                    {
                        if (p.MainModule.FileName.TrimEnd('\\').StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                            p.Kill();
                    }
                    catch { }
                }
            }
            catch { }
        }

        private const uint NO_ERROR = 0;
        private const uint MIB_IPPROTO_NETMGMT = 3;
        private const uint INFINITE_LIFE = 0xFFFFFFFF;
        private const ushort AF_INET = 2;

        private static readonly bool UseNetio =
            File.Exists(Path.Combine(Environment.SystemDirectory, "netioapi.dll"));

        // --- NetIO (netioapi.dll) structures for the modern route API ---
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

        // --- legacy iphlpapi struct/API (not used unless netioapi is missing) ---
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

        [DllImport("iphlpapi.dll")]
        private static extern uint GetBestRoute(uint dest, uint source, out MibIpForwardRow row);
        [DllImport("iphlpapi.dll")]
        private static extern uint CreateIpForwardEntry(ref MibIpForwardRow row);
        [DllImport("iphlpapi.dll")]
        private static extern uint DeleteIpForwardEntry(ref MibIpForwardRow row);

        private static int GetEnvPort(string name, int def)
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(name);
                int p;
                if (!string.IsNullOrEmpty(v) && int.TryParse(v, out p) && p > 0 && p < 65536) return p;
            }
            catch { }
            return def;
        }

        private static void WriteResult(string msg)
        {
            try { File.WriteAllText(ResultFile, msg, new UTF8Encoding(false)); } catch { }
        }

        private static void WriteState(string text)
        {
            try { File.WriteAllText(StateFile, text, new UTF8Encoding(false)); } catch { }
        }

        private static string ReadState()
        {
            try { if (File.Exists(StateFile)) return File.ReadAllText(StateFile); } catch { }
            return "";
        }

        private static string GetStateValue(string state, string key)
        {
            foreach (string line in state.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int i = line.IndexOf('=');
                if (i > 0 && line.Substring(0, i) == key) return line.Substring(i + 1);
            }
            return "";
        }

        private static string Run(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    var so = new StringBuilder();
                    var se = new StringBuilder();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) so.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) se.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } }
                    return (so.ToString() + se.ToString()).Trim();
                }
            }
            catch (Exception ex) { return "ERR:" + ex.Message; }
        }

        private static bool TorUp(int timeoutMs)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect("127.0.0.1", SocksPort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        private static bool ControlSend(string cmd)
        {
            try
            {
                using (TcpClient c = new TcpClient("127.0.0.1", CtrlPort))
                {
                    NetworkStream s = c.GetStream();
                    StreamWriter w = new StreamWriter(s) { NewLine = "\r\n", AutoFlush = true };
                    StreamReader r = new StreamReader(s);
                    w.WriteLine("AUTHENTICATE \"" + ControlPassword + "\"");
                    if (!r.ReadLine().StartsWith("250")) return false;
                    w.WriteLine(cmd);
                    if (!r.ReadLine().StartsWith("250")) return false;
                    w.WriteLine("QUIT");
                    return true;
                }
            }
            catch { return false; }
        }

        // Port 53 must be free (or be owned by this folder's own tor) before TUN
        // can use it as the system DNS target. Our tor is expected to keep DNS on
        // 127.0.0.1:53 after the first enable (the DNSPort line is never removed).
        private static bool Port53Free()
        {
            string torExe = Path.Combine(DataDir, "tor.exe");
            try
            {
                var pids = new HashSet<int>();
                string tcp = Run("netstat.exe", "-ano");
                string udp = Run("netstat.exe", "-ano -p udp");
                Regex re = new Regex(@":53\s");
                foreach (string line in tcp.Split('\n'))
                    if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) >= 0 && re.IsMatch(line))
                        AddTrailingPid(line, pids);
                foreach (string line in udp.Split('\n'))
                    if (re.IsMatch(line)) AddTrailingPid(line, pids);
                foreach (int pid in pids)
                {
                    if (pid <= 0) continue;
                    try
                    {
                        Process p = Process.GetProcessById(pid);
                        if (p == null || !p.MainModule.FileName.Equals(torExe, StringComparison.OrdinalIgnoreCase))
                            return false;
                    }
                    catch { return false; }
                }
            }
            catch { }
            return true;
        }

        private static void AddTrailingPid(string line, HashSet<int> pids)
        {
            try
            {
                int sp = line.LastIndexOf(' ');
                if (sp > 0)
                {
                    int pid;
                    if (int.TryParse(line.Substring(sp + 1).Trim(), out pid)) pids.Add(pid);
                }
            }
            catch { }
        }

        // Finds the tun2socks adapter and its REAL name. Windows may display it
        // as "TorBoostTun 1" (or worse) when a stale adapter already exists, so
        // every netsh call must use the discovered name, never the fixed one.
        private static bool GetTunAdapter(out int ifIndex, out string name)
        {
            ifIndex = -1;
            name = "";
            string o = Run("netsh.exe", "interface ipv4 show interfaces");
            Regex re = new Regex(@"^\s*(\d+)\s+\d+\s+\d+\s+(\S+(?:\s+\S+)?)\s+(.+)$");
            foreach (string line in o.Split('\n'))
            {
                if (line.IndexOf(TunName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Match m = re.Match(line);
                if (!m.Success) continue;
                ifIndex = int.Parse(m.Groups[1].Value);
                name = m.Groups[3].Value.Trim();
                return true;
            }
            return false;
        }

        private static string IfIpToString(uint v)
        {
            return new IPAddress(BitConverter.GetBytes(v)).ToString();
        }

        private static uint IpToUInt(string ip)
        {
            byte[] b = IPAddress.Parse(ip).GetAddressBytes();
            return BitConverter.ToUInt32(b, 0);
        }

        private static MibIpForwardRow2 MakeRow2(uint dest, byte plen, uint nextHop, int ifIndex, uint metric)
        {
            return new MibIpForwardRow2
            {
                InterfaceLuid = 0,
                InterfaceIndex = (uint)ifIndex,
                DestinationPrefix = new IpAddressPrefix
                {
                    Prefix = new SockaddrInet { si_family = AF_INET, si_addr0 = dest },
                    PrefixLength = plen
                },
                NextHop = new SockaddrInet { si_family = AF_INET, si_addr0 = nextHop },
                ValidLifetime = INFINITE_LIFE,
                PreferredLifetime = INFINITE_LIFE,
                Metric = metric,
                Protocol = MIB_IPPROTO_NETMGMT,
                Loopback = 0,
                AutoconfigureAddress = 0,
                Publish = 1,
                Immortal = 1
            };
        }

        private static uint MaskUint(int plen)
        {
            return plen == 0 ? 0 : (uint)(0xFFFFFFFF << (32 - plen));
        }

        // Legacy iphlpapi route API (MIB_IPFORWARDROW). Some trimmed/older
        // Windows builds lack netioapi.dll, and the old route.exe fallback was
        // far too slow for the several-thousand relay /32 routes (one spawned
        // process per route). CreateIpForwardEntry is present on every Windows
        // but is picky: the route must be MIB_IPROUTE_TYPE_INDIRECT (4) for a
        // gateway route and dwForwardMetric1 must hold the EFFECTIVE metric
        // (interface metric + route metric), exactly like route.exe computes it.
        private static bool LegacyRoute(bool add, uint dest, int plen, uint nextHop, int ifIndex, uint metric)
        {
            var row = new MibIpForwardRow
            {
                dest = dest,
                mask = MaskUint(plen),
                nextHop = nextHop,
                ifIndex = ifIndex,
                metric1 = (uint)(InterfaceMetric(ifIndex) + metric),
                type = 4,  // MIB_IPROUTE_TYPE_INDIRECT (via gateway)
                proto = 3  // MIB_IPROTO_NETMGMT
            };
            uint rc = add ? CreateIpForwardEntry(ref row) : DeleteIpForwardEntry(ref row);
            return rc == NO_ERROR;
        }

        private static readonly Dictionary<int, int> IfMetricCache = new Dictionary<int, int>();

        private static int InterfaceMetric(int ifIndex)
        {
            int m;
            if (IfMetricCache.TryGetValue(ifIndex, out m)) return m;
            m = GetInterfaceMetric(ifIndex);
            IfMetricCache[ifIndex] = m;
            return m;
        }

        private static int GetInterfaceMetric(int ifIndex)
        {
            string o = Run("netsh.exe", "interface ipv4 show interfaces");
            Regex re = new Regex(@"^\s*(\d+)\s+(\d+)\s+\d+\s+\S+\s+(.+)$");
            foreach (string line in o.Split('\n'))
            {
                Match m = re.Match(line);
                if (m.Success && int.Parse(m.Groups[1].Value) == ifIndex)
                {
                    int met;
                    if (int.TryParse(m.Groups[2].Value, out met)) return met;
                }
            }
            return 0;
        }

        private static bool AddRoute(uint dest, int plen, uint nextHop, int ifIndex, uint metric)
        {
            if (UseNetio)
            {
                MibIpForwardRow2 r = MakeRow2(dest, (byte)plen, nextHop, ifIndex, metric);
                return SetIpForwardEntry2(ref r) == NO_ERROR;
            }
            return LegacyRoute(true, dest, plen, nextHop, ifIndex, metric);
        }

        private static bool DeleteRoute(uint dest, int plen, uint nextHop, int ifIndex)
        {
            if (UseNetio)
            {
                MibIpForwardRow2 r = MakeRow2(dest, (byte)plen, nextHop, ifIndex, 0);
                return DeleteIpForwardEntry2(ref r) == NO_ERROR;
            }
            return LegacyRoute(false, dest, plen, nextHop, ifIndex, 0);
        }

        private static bool IsPublicIpv4(string s)
        {
            IPAddress a;
            if (!IPAddress.TryParse(s, out a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = a.GetAddressBytes();
            if (b[0] == 10 || b[0] == 127) return false;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            return true;
        }

        private static HashSet<string> ParseRelayIps()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string f in new[] { Consensus1, Consensus2 })
            {
                if (!File.Exists(f)) continue;
                try
                {
                    foreach (string raw in File.ReadAllLines(f))
                    {
                        if (!raw.StartsWith("r ")) continue;
                        string[] p = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 6 && IsPublicIpv4(p[5])) set.Add(p[5]);
                    }
                }
                catch { }
            }
            try
            {
                if (Directory.Exists(BridgesDir))
                {
                    foreach (string f in Directory.GetFiles(BridgesDir, "*.txt"))
                    {
                        foreach (string line in File.ReadAllLines(f))
                        {
                            foreach (Match m in Regex.Matches(line, @"\b(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\b"))
                                if (IsPublicIpv4(m.Groups[1].Value)) set.Add(m.Groups[1].Value);
                        }
                    }
                }
            }
            catch { }
            return set;
        }

        // Adds "DNSPort 127.0.0.1:53" to torrc once and reloads tor so system DNS
        // (pointed at 127.0.0.1 on the TUN adapter) is served by tor. The line is
        // never removed: removing it requires closing the DNS listener which can
        // crash tor on some Windows builds (libevent WSAENOTSOCK), and leaving tor
        // DNS on 127.0.0.1:53 is harmless when TUN is off.
        private static void EnsureDns53()
        {
            try
            {
                if (!File.Exists(Torrc)) return;
                var lines = new List<string>(File.ReadAllLines(Torrc));
                if (lines.FindIndex(l => l.Trim() == DnsMarker) >= 0) return;
                lines.Add(DnsMarker);
                File.WriteAllLines(Torrc, lines.ToArray(), new UTF8Encoding(false));
                ControlSend("SIGNAL RELOAD");
            }
            catch { }
        }

        private static bool Enable(out Process tun, out int tunIf, out int physIf, out string physGw)
        {
            tun = null;
            tunIf = -1;
            physIf = -1;
            physGw = "";
            WriteResult("enabling...");
            if (!File.Exists(TunExe)) { WriteResult("error: tun2socks.exe not found in " + DataDir); return false; }
            if (!File.Exists(WintunDll)) { WriteResult("error: wintun.dll not found in " + DataDir); return false; }
            if (!TorUp(2000)) { WriteResult("error: tor SOCKS (127.0.0.1:" + SocksPort + ") is not up"); return false; }
            if (GetStateValue(ReadState(), "status") == "on") { WriteResult("error: TUN is already on"); return false; }
            if (!Port53Free()) { WriteResult("error: port 53 is already in use on this machine"); return false; }

            WintunDeleteAdapterByName(TunName);
            KillStaleTun2Socks();

            MibIpForwardRow physRow;
            uint rc = GetBestRoute(0, 0, out physRow);
            if (rc != NO_ERROR) { WriteResult("error: no IPv4 default route (getbestroute " + rc + ")"); return false; }
            physGw = IfIpToString(physRow.nextHop);
            physIf = physRow.ifIndex;

            EnsureDns53();
            Thread.Sleep(500);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = TunExe,
                    Arguments = "-device tun://" + TunName + " -proxy socks5://127.0.0.1:" + SocksPort + " -mtu 1500 -loglevel error",
                    WorkingDirectory = DataDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                tun = new Process();
                tun.StartInfo = psi;
                tun.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                tun.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                tun.Start();
                tun.BeginOutputReadLine();
                tun.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                WriteResult("error: cannot start tun2socks: " + ex.Message);
                return false;
            }

            string tunName = "";
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(500);
                if (tun.HasExited) break;
                if (GetTunAdapter(out tunIf, out tunName)) break;
            }
            if (tunIf <= 0 || tunName.Length == 0)
            {
                try { tun.Kill(); } catch { }
                WintunDeleteAdapterByName(TunName);
                WriteResult("error: wintun adapter was not created (is tun2socks running?)");
                return false;
            }

            Run("netsh.exe", "interface ipv4 set address name=" + tunName + " source=static address=" + TunAddr + " mask=" + TunMask);
            Run("netsh.exe", "interface ipv4 set dnsservers name=" + tunName + " source=static address=127.0.0.1 register=none validate=no");
            Run("netsh.exe", "interface ipv4 set interface " + tunName + " metric=1");

            AddRoute(0, 0, IpToUInt(TunAddr), tunIf, 1);
            MibIpForwardRow check = new MibIpForwardRow();
            bool switched = false;
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(500);
                GetBestRoute(0, 0, out check);
                if (check.ifIndex == tunIf) { switched = true; break; }
            }
            if (!switched)
            {
                try { tun.Kill(); } catch { }
                WintunDeleteAdapterByName(tunName);
                WriteResult("error: default route did not switch to " + tunName +
                            " (still ifIndex " + check.ifIndex + ", gw " + IfIpToString(check.nextHop) + ")");
                return false;
            }

            HashSet<string> relays = ParseRelayIps();
            foreach (string ip in relays)
                AddRoute(IpToUInt(ip), 32, IpToUInt(physGw), physIf, 1);

            WriteState(BuildState(tun.Id, tunIf, physIf, physGw, relays));
            WriteResult("on: " + relays.Count + " relay routes, default route now via " + TunName);
            return true;
        }

        private static string BuildState(int pid, int tunIf, int physIf, string physGw, HashSet<string> relays)
        {
            var sb = new StringBuilder();
            sb.AppendLine("status=on");
            sb.AppendLine("pid=" + pid);
            sb.AppendLine("tunIf=" + tunIf);
            sb.AppendLine("physIf=" + physIf);
            sb.AppendLine("physGw=" + physGw);
            sb.AppendLine("relays=" + string.Join(",", relays));
            return sb.ToString();
        }

        private static void TeardownFromState(string state)
        {
            string pid = GetStateValue(state, "pid");
            string physIf = GetStateValue(state, "physIf");
            string physGw = GetStateValue(state, "physGw");
            string relays = GetStateValue(state, "relays");

            int p;
            if (!string.IsNullOrEmpty(pid) && int.TryParse(pid, out p) && p > 0)
            {
                try { Process.GetProcessById(p).Kill(); } catch { }
            }
            Thread.Sleep(2000);

            int ifidx;
            if (int.TryParse(physIf, out ifidx) && ifidx > 0 && !string.IsNullOrEmpty(physGw) && !string.IsNullOrEmpty(relays))
            {
                foreach (string ip in relays.Split(','))
                {
                    string t = ip.Trim();
                    if (t.Length == 0) continue;
                    DeleteRoute(IpToUInt(t), 32, IpToUInt(physGw), ifidx);
                }
            }

            KillStaleTun2Socks();
            WintunDeleteAdapterByName(TunName);
            WriteState("status=off");
            try { if (File.Exists(StopFile)) File.Delete(StopFile); } catch { }
        }

        private static int RunKeeper()
        {
            Process tun;
            int tunIf, physIf;
            string physGw;
            if (!Enable(out tun, out tunIf, out physIf, out physGw)) return 1;
            if (GetStateValue(ReadState(), "status") != "on")
            {
                try { tun.Kill(); } catch { }
                return 1;
            }

            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (string ip in ParseRelayIps()) known.Add(ip);
            DateTime deadline = DateTime.UtcNow.AddMinutes(2);
            int torDown = 0;

            try
            {
                while (true)
                {
                    Thread.Sleep(4000);
                    if (File.Exists(StopFile)) break;
                    try { tun.Refresh(); } catch { }
                    if (tun.HasExited) break;
                    if (TorUp(1500)) torDown = 0; else torDown++;
                    if (torDown >= 4) break;
                    if (DateTime.UtcNow >= deadline)
                    {
                        deadline = DateTime.UtcNow.AddMinutes(2);
                        var fresh = ParseRelayIps();
                        foreach (string ip in fresh)
                        {
                            if (known.Add(ip))
                                AddRoute(IpToUInt(ip), 32, IpToUInt(physGw), physIf, 1);
                        }
                        WriteState(BuildState(tun.Id, tunIf, physIf, physGw, known));
                    }
                }
            }
            finally
            {
                try { tun.Kill(); } catch { }
                TeardownFromState(ReadState());
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
                TeardownFromState(ReadState());
                WriteResult("off");
                return 0;
            }
            WriteResult("status=" + (GetStateValue(ReadState(), "status") == "on" ? "on" : "off"));
            return 0;
        }
    }
}
