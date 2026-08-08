// start-tor.cs - builds to start-tor.exe (compile with build-start-tor.ps1)
// Portable Tor launcher for Iran. Expected layout next to this exe:
//   start-tor.exe
//   data\tor.exe  data\torrc  data\geoip  data\geoip6  data\transports\...
//   data\data\    <- runtime state (tor.log, cached-*, keys)
// Behavior: start tor with the prepared torrc, wait for "Bootstrapped 100%",
// then enable the Windows system proxy (HTTP 127.0.0.1:8118). Press Enter to
// stop tor and restore the proxy. Flags: --bootstrap-only (test: stop after
// 100%, no proxy change).
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace StartTor
{
    internal static class Program
    {
        private static readonly string AppDir =
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        private static readonly string DataDir = Path.Combine(AppDir, "data");
        private static readonly string TorExe = Path.Combine(DataDir, "tor.exe");
        private static readonly string Torrc = Path.Combine(DataDir, "torrc");
        private static readonly string TorLog = Path.Combine(DataDir, "data", "tor.log");
        private static readonly string ProxyKey =
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        private const int InternetOptionSettingsChanged = 39;
        private const int InternetOptionRefresh = 37;

        private static Process torProc;
        private static bool cleaned;

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr h, int option, IntPtr buffer, int length);

        private static void SetSystemProxy(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(ProxyKey))
                {
                    k.SetValue("ProxyEnable", on ? 1 : 0, RegistryValueKind.DWord);
                    if (on)
                    {
                        k.SetValue("ProxyServer", "127.0.0.1:8118", RegistryValueKind.String);
                        k.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
                    }
                }
            }
            catch { }
            InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }

        private static void Cleanup()
        {
            if (cleaned) return;
            cleaned = true;
            if (torProc != null)
            {
                try { if (!torProc.HasExited) { torProc.Kill(); torProc.WaitForExit(5000); } }
                catch { }
            }
            SetSystemProxy(false);
        }

        private static void WaitForKey()
        {
            try { Console.WriteLine("Press any key to exit..."); Console.ReadKey(true); }
            catch { }
        }

        private static string ReadLogTail(int bytes)
        {
            try
            {
                using (FileStream fs = new FileStream(TorLog, FileMode.Open,
                                                      FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, fs.Length - bytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true))
                        return sr.ReadToEnd();
                }
            }
            catch { return ""; }
        }

        private static int Main(string[] args)
        {
            bool bootstrapOnly = args.Length > 0 &&
                (args[0] == "--bootstrap-only" || args[0] == "-t");

            Console.Title = "Tor Iran Portable";
            Console.WriteLine("Tor Portable for Iran (official tor 0.4.9.11)");
            Console.WriteLine("  launcher: " + AppDir);
            Console.WriteLine("  data dir: " + DataDir);
            Console.WriteLine();

            if (!File.Exists(TorExe) || !File.Exists(Torrc))
            {
                Console.WriteLine("[x] tor.exe / torrc not found. Expected layout:");
                Console.WriteLine("    start-tor.exe");
                Console.WriteLine("    data\\tor.exe   data\\torrc   data\\geoip");
                Console.WriteLine("    data\\data\\    (runtime state)");
                WaitForKey();
                return 1;
            }

            foreach (Process p in Process.GetProcessesByName("tor"))
            {
                try
                {
                    if (p.MainModule.FileName.Equals(TorExe, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("[x] tor is already running from this folder (PID " + p.Id + ").");
                        WaitForKey();
                        return 1;
                    }
                }
                catch { }
            }

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                Cleanup();
                Environment.Exit(0);
            };

            Directory.CreateDirectory(DataDir);
            try { if (File.Exists(TorLog)) File.Delete(TorLog); } catch { }

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = TorExe,
                Arguments = "-f \"torrc\"",
                WorkingDirectory = DataDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try { torProc = Process.Start(psi); }
            catch (Exception ex)
            {
                Console.WriteLine("[x] failed to start tor: " + ex.Message);
                WaitForKey();
                return 1;
            }

            Console.WriteLine("[i] tor started (PID " + torProc.Id + "), bootstrapping...");

            DateTime deadline = DateTime.UtcNow.AddMinutes(10);
            int lastPct = -1;
            bool done = false;
            bool exited = false;

            while (!done && !exited && DateTime.UtcNow < deadline)
            {
                torProc.Refresh();
                if (torProc.HasExited) { exited = true; break; }

                MatchCollection ms = Regex.Matches(ReadLogTail(2000), "Bootstrapped (\\d+)%");
                if (ms.Count > 0)
                {
                    Match m = ms[ms.Count - 1];
                    int pct = int.Parse(m.Groups[1].Value);
                    if (pct >= 100) { done = true; break; }
                    if (pct != lastPct)
                    {
                        Console.WriteLine("    Bootstrapped " + pct + "% ...");
                        lastPct = pct;
                    }
                }
                Thread.Sleep(2000);
            }

            if (exited)
            {
                Console.WriteLine("[x] tor exited with code " + torProc.ExitCode + ".");
                Console.WriteLine("    Last log lines:");
                Console.WriteLine(ReadLogTail(1500));
                Cleanup();
                WaitForKey();
                return 1;
            }

            if (!done)
            {
                Console.WriteLine("[x] bootstrap did not reach 100% in 10 minutes.");
                Console.WriteLine("    Network may block direct Tor - run data\\scripts\\fetch-bridges.ps1.");
                Console.WriteLine("    Last log lines:");
                Console.WriteLine(ReadLogTail(1500));
                Cleanup();
                WaitForKey();
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("Bootstrapped 100% - Tor is UP.");

            if (bootstrapOnly)
            {
                Console.WriteLine("Test mode: stopping tor (no proxy change).");
                Cleanup();
                return 0;
            }

            SetSystemProxy(true);
            Console.WriteLine("System proxy set to 127.0.0.1:8118");
            Console.WriteLine("  SOCKS5 127.0.0.1:9050");
            Console.WriteLine("  HTTP   127.0.0.1:8118");
            Console.WriteLine("  DNS    127.0.0.1:53530");
            Console.WriteLine();
            Console.WriteLine("Press Enter to stop Tor and restore the system proxy...");
            try { Console.ReadLine(); } catch { }

            Cleanup();
            Console.WriteLine("Tor stopped; system proxy restored.");
            WaitForKey();
            return 0;
        }
    }
}
