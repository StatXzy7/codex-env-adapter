using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace CodexEnvAdapter;

static class DirectProxySelfTest
{
    public static bool IsRequest(string[] args) =>
        args.Length > 0 && (args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase)
            || args[0].Equals("--direct-connect", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        AttachConsole(-1);
        if (args[0].Equals("--direct-connect", StringComparison.OrdinalIgnoreCase))
        {
            return Connect(args);
        }

        var log = new StringBuilder();
        try
        {
            Logic();
            log.AppendLine("logic ok");
            if (!IsAdmin())
            {
                log.AppendLine("live skipped: not admin");
                Console.WriteLine(log.ToString());
                return 0;
            }

            Live(log);
            log.AppendLine("live ok");
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            File.WriteAllText(Path.Combine(SettingsStore.DirectoryPath, "self-test.log"), log.ToString());
            Console.WriteLine(log.ToString());
            return 0;
        }
        catch (Exception ex)
        {
            log.AppendLine(ex.ToString());
            Console.WriteLine(log.ToString());
            try
            {
                File.WriteAllText(Path.Combine(SettingsStore.DirectoryPath, "self-test.log"), log.ToString());
            }
            catch
            {
                // The console text is the report.
            }

            return 1;
        }
    }

    public static void Logic()
    {
        if (Marshal.SizeOf<WinDivertAddress>() != 80)
        {
            throw new InvalidOperationException("WinDivertAddress size " + Marshal.SizeOf<WinDivertAddress>());
        }

        if (!DirectPacket.IsPublic(new byte[] { 1, 1, 1, 1 })
            || DirectPacket.IsPublic(new byte[] { 127, 0, 0, 1 })
            || DirectPacket.IsPublic(new byte[] { 10, 1, 2, 3 })
            || DirectPacket.IsPublic(new byte[] { 192, 168, 1, 1 })
            || DirectPacket.IsPublic(new byte[] { 172, 16, 0, 1 })
            || DirectPacket.IsPublic(new byte[] { 100, 64, 0, 1 }))
        {
            throw new InvalidOperationException("public address check failed");
        }

        var snap = ProcessSnapshot.Capture();
        if (!snap.Names.ContainsKey(Environment.ProcessId))
        {
            throw new InvalidOperationException("process snapshot missed the current process, count=" + snap.Names.Count);
        }

        var packet = new byte[40];
        packet[0] = 0x45;
        packet[9] = DirectPacket.Tcp;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 40);
        packet[12] = 10;
        packet[15] = 5;
        packet[16] = 1;
        packet[17] = 2;
        packet[18] = 3;
        packet[19] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 50000);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 443);
        DirectPacket.ReflectToProxy(packet, 20, 34010);
        if (packet[12] != 1 || packet[15] != 4 || packet[16] != 10 || packet[19] != 5)
        {
            throw new InvalidOperationException("reflect addresses failed");
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(20, 2)) != 50000
            || BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(22, 2)) != 34010)
        {
            throw new InvalidOperationException("reflect ports failed");
        }

        var reply = new byte[40];
        reply[0] = 0x45;
        reply[9] = DirectPacket.Tcp;
        reply[12] = 10;
        reply[15] = 5;
        reply[16] = 1;
        reply[17] = 2;
        reply[18] = 3;
        reply[19] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(20, 2), 34010);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(22, 2), 50000);
        DirectPacket.ReflectFromProxy(reply, 20, 443);
        if (reply[12] != 1 || reply[15] != 4 || reply[16] != 10 || reply[19] != 5)
        {
            throw new InvalidOperationException("reply addresses failed");
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20, 2)) != 443
            || BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(22, 2)) != 50000)
        {
            throw new InvalidOperationException("reply ports failed");
        }

        var query = SampleQuery("example.com");
        if (!DnsMessages.TryParseQuestion(query, out var question) || question.Type != DnsMessages.TypeA || question.Name != "example.com")
        {
            throw new InvalidOperationException("dns parse failed");
        }

        var empty = DnsMessages.EmptyNoError(query, question.End);
        if (empty.Length != question.End || (empty[2] & 0x80) == 0)
        {
            throw new InvalidOperationException("dns empty answer failed");
        }

        if (!DnsMessages.IsLocalName("localhost") || DnsMessages.IsLocalName("example.com"))
        {
            throw new InvalidOperationException("local name check failed");
        }
    }

    static void Live(StringBuilder log)
    {
        var seen = new ConcurrentBag<string>();
        var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        var proxyTask = Task.Run(() => AcceptProxy(proxy, seen));
        DirectProxyWorker? worker = null;
        try
        {
            worker = DirectProxyWorker.Start(new DirectProxyOptions
            {
                ProxyHost = "127.0.0.1",
                ProxyPort = proxyPort,
                WatchPid = Environment.ProcessId,
                Log = message => log.AppendLine(message)
            });
            if (!worker.WaitReady(TimeSpan.FromSeconds(8)))
            {
                throw new InvalidOperationException(worker.Fault?.Message ?? "worker not ready");
            }

            var child = RunChild(6000);
            if (child != 0)
            {
                throw new InvalidOperationException("redirected child exited " + child + "\n" + log);
            }

            if (seen.Count != 1 || seen.First() != "203.0.113.10:443")
            {
                throw new InvalidOperationException("proxy saw [" + string.Join(", ", seen) + "]");
            }

            var opened = worker.FlowsOpened;
            try
            {
                using var outsider = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
                outsider.ConnectAsync(IPAddress.Parse("203.0.113.10"), 443, cts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                // This process is not in the redirect set, so the blackhole address should not connect.
            }

            if (seen.Count != 1 || worker.FlowsOpened != opened)
            {
                throw new InvalidOperationException("non-target traffic was redirected");
            }
        }
        finally
        {
            worker?.Dispose();
            try { proxy.Stop(); } catch { }
            try { proxyTask.Wait(1000); } catch { }
        }

        var after = RunChild(1500);
        if (after == 0)
        {
            throw new InvalidOperationException("redirect still active after stop");
        }
    }

    static int Connect(string[] args)
    {
        var host = args.Length > 1 ? args[1] : "203.0.113.10";
        var port = args.Length > 2 && int.TryParse(args[2], out var parsed) ? parsed : 443;
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            tcp.ConnectAsync(IPAddress.Parse(host), port, cts.Token).GetAwaiter().GetResult();
            var stream = tcp.GetStream();
            stream.Write("ping"u8);
            var buffer = new byte[4];
            var used = 0;
            while (used < 4)
            {
                var read = stream.Read(buffer, used, 4 - used);
                if (read == 0)
                {
                    return 3;
                }

                used += read;
            }

            return Encoding.ASCII.GetString(buffer) == "pong" ? 0 : 4;
        }
        catch
        {
            return 2;
        }
    }

    static int RunChild(int timeoutMs)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no exe");
        using var process = Process.Start(new ProcessStartInfo(exe, "--direct-connect 203.0.113.10 443")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process == null)
        {
            throw new InvalidOperationException("child did not start");
        }

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return 9;
        }

        return process.ExitCode;
    }

    static async Task AcceptProxy(TcpListener listener, ConcurrentBag<string> seen)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => Serve(client, seen));
        }
    }

    static async Task Serve(TcpClient client, ConcurrentBag<string> seen)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = new List<byte>();
            var one = new byte[1];
            while (header.Count < 2048)
            {
                if (await stream.ReadAsync(one) == 0)
                {
                    return;
                }

                header.Add(one[0]);
                if (header.Count >= 4
                    && header[^4] == '\r'
                    && header[^3] == '\n'
                    && header[^2] == '\r'
                    && header[^1] == '\n')
                {
                    break;
                }
            }

            var text = Encoding.ASCII.GetString(header.ToArray());
            var line = text.Split("\r\n")[0];
            var parts = line.Split(' ');
            var target = parts.Length >= 2 ? parts[1] : "";
            seen.Add(target);
            if (!target.Equals("203.0.113.10:443", StringComparison.Ordinal))
            {
                await stream.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
                return;
            }

            await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());
            var ping = new byte[4];
            var used = 0;
            while (used < 4)
            {
                var read = await stream.ReadAsync(ping.AsMemory(used));
                if (read == 0)
                {
                    return;
                }

                used += read;
            }

            if (Encoding.ASCII.GetString(ping) == "ping")
            {
                await stream.WriteAsync("pong"u8.ToArray());
            }
        }
    }

    static byte[] SampleQuery(string name)
    {
        var labels = name.Split('.');
        var body = new List<byte> { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        foreach (var label in labels)
        {
            body.Add((byte)label.Length);
            body.AddRange(Encoding.ASCII.GetBytes(label));
        }

        body.Add(0);
        body.Add(0);
        body.Add(1);
        body.Add(0);
        body.Add(1);
        return body.ToArray();
    }

    static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
