using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace CodexEnvAdapter;

sealed class DirectProxyOptions
{
    public required string ProxyHost { get; init; }
    public required int ProxyPort { get; init; }
    public string InstallLocation { get; init; } = "";
    public int WatchPid { get; init; }
    public int OnlyPid { get; init; }
    public Action<string>? Log { get; init; }
}

sealed class DirectProxyWorker : IDisposable
{
    const int ChecksumBits = (1 << 21) | (1 << 22) | (1 << 23);

    readonly DirectProxyOptions _options;
    readonly ProcessScope _scope;
    readonly ConcurrentDictionary<ushort, TcpFlow> _flows = new();
    readonly ConcurrentDictionary<ushort, int> _socketPorts = new();
    readonly HashSet<uint> _localIps = new();
    readonly object _sendLock = new();
    readonly ManualResetEventSlim _ready = new(false);
    readonly CancellationTokenSource _cancel = new();
    readonly byte[] _proxyIp;

    TcpListener? _listener;
    IntPtr _network = new(-1);
    IntPtr _sockets = new(-1);
    Thread? _recvThread;
    Thread? _socketThread;
    Thread? _tableThread;
    Task? _acceptTask;
    volatile Dictionary<ushort, int> _tcpPorts = new();
    volatile Dictionary<ushort, int> _udpPorts = new();
    long _tablesAt;
    int _listenPort;
    int _flowsOpened;
    int _quicLogged;
    int _closed;
    Exception? _fault;

    DirectProxyWorker(DirectProxyOptions options)
    {
        _options = options;
        _scope = new ProcessScope(options.InstallLocation, options.WatchPid, options.OnlyPid);
        if (!IPAddress.TryParse(options.ProxyHost, out var proxyIp) || proxyIp.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException("接管直连时，代理地址请使用 IPv4，例如 127.0.0.1:7890。");
        }

        _proxyIp = proxyIp.GetAddressBytes();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    _localIps.Add(BinaryPrimitives.ReadUInt32BigEndian(address.Address.GetAddressBytes()));
                }
            }
        }
    }

    public int FlowsOpened => _flowsOpened;
    public Exception? Fault => _fault;
    public bool Ready => _ready.IsSet;

    public static DirectProxyWorker Start(DirectProxyOptions options)
    {
        var worker = new DirectProxyWorker(options);
        try
        {
            worker.Open();
        }
        catch
        {
            worker.Dispose();
            throw;
        }

        return worker;
    }

    public bool WaitReady(TimeSpan timeout) => _ready.Wait(timeout) && _fault == null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        try { _cancel.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        Release(ref _network);
        Release(ref _sockets);
        if (!ReferenceEquals(_recvThread, Thread.CurrentThread))
        {
            try { _recvThread?.Join(2500); } catch { }
        }

        try { _socketThread?.Join(1500); } catch { }
        try { _tableThread?.Join(1500); } catch { }
        try { _acceptTask?.Wait(1500); } catch { }
        _ready.Set();
    }

    void Open()
    {
        WinDivertNative.EnsureLoaded();
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start(128);
        _listenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var filter =
            "!impostor and ip and ((tcp and ((outbound and !loopback) or tcp.SrcPort == " + _listenPort +
            " or tcp.DstPort == " + _listenPort + ")) or (udp and outbound and !loopback and (udp.DstPort == 53 or udp.DstPort == 443)))";
        _network = WinDivertNative.Open(filter, WinDivertNative.LayerNetwork, 88, 0);
        var openError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        if (WinDivertNative.IsInvalid(_network))
        {
            throw new InvalidOperationException(OpenError("无法接管 Codex 直连", openError));
        }

        WinDivertNative.WinDivertSetParam(_network, WinDivertNative.ParamQueueLength, 8192);
        WinDivertNative.WinDivertSetParam(_network, WinDivertNative.ParamQueueTime, 2000);
        WinDivertNative.WinDivertSetParam(_network, WinDivertNative.ParamQueueSize, 8 * 1024 * 1024);
        _sockets = WinDivertNative.Open("event == CONNECT and tcp", WinDivertNative.LayerSocket, 0, WinDivertNative.FlagSniff | WinDivertNative.FlagRecvOnly);
        RefreshTables(force: true);
        _recvThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "CodexDirectProxy", Priority = ThreadPriority.Highest };
        _recvThread.Start();
        _tableThread = new Thread(TableLoop) { IsBackground = true, Name = "CodexDirectProxyTables" };
        _tableThread.Start();
        if (!WinDivertNative.IsInvalid(_sockets))
        {
            _socketThread = new Thread(SocketLoop) { IsBackground = true, Name = "CodexDirectProxySockets" };
            _socketThread.Start();
        }

        _acceptTask = Task.Run(AcceptLoop);
        _ready.Set();
        Log("直连接管已就绪，监听 " + _listenPort + "，上游 " + _options.ProxyHost + ":" + _options.ProxyPort);
    }

    void ReceiveLoop()
    {
        var packet = new byte[65536];
        var working = new byte[65536];
        var windowStart = Environment.TickCount64;
        var windowCount = 0;
        try
        {
            while (Volatile.Read(ref _closed) == 0)
            {
                var addr = new WinDivertAddress();
                if (!WinDivertNative.WinDivertRecv(_network, packet, (uint)packet.Length, out var length, ref addr) || length == 0)
                {
                    break;
                }

                windowCount++;
                var now = Environment.TickCount64;
                if (now - windowStart > 1000)
                {
                    if (windowCount > 250000)
                    {
                        throw new InvalidOperationException("直连接管流量异常，已停止，避免影响其他软件。");
                    }

                    windowStart = now;
                    windowCount = 0;
                }

                var original = addr;
                var decision = PacketDecision.Passthrough;
                var outLength = length;
                try
                {
                    decision = Classify(packet, length, working, ref outLength, ref addr);
                    if (decision == PacketDecision.Modified)
                    {
                        addr.Bits &= ~unchecked((uint)ChecksumBits);
                        if (!WinDivertNative.WinDivertHelperCalcChecksums(working, outLength, ref addr, 0))
                        {
                            decision = PacketDecision.Passthrough;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("处理数据包失败：" + ex.Message);
                    decision = PacketDecision.Passthrough;
                }

                if (decision == PacketDecision.Drop)
                {
                    continue;
                }

                var sendPacket = decision == PacketDecision.Modified ? working : packet;
                var sendLength = decision == PacketDecision.Modified ? outLength : length;
                var sendAddr = decision == PacketDecision.Modified ? addr : original;
                Send(sendPacket, sendLength, ref sendAddr);
            }
        }
        catch (Exception ex)
        {
            _fault = ex;
            Log(ex.Message);
        }
        finally
        {
            Release(ref _network);
            try { _listener?.Stop(); } catch { }
            _ready.Set();
        }
    }

    PacketDecision Classify(byte[] packet, uint length, byte[] working, ref uint outLength, ref WinDivertAddress addr)
    {
        Buffer.BlockCopy(packet, 0, working, 0, (int)length);
        outLength = length;
        var span = working.AsSpan(0, (int)length);
        if (DirectPacket.TryParseIpv4(span, DirectPacket.Tcp, out _, out var tcp))
        {
            return ClassifyTcp(working, length, tcp, ref addr);
        }

        if (DirectPacket.TryParseIpv4(span, DirectPacket.Udp, out _, out var udp))
        {
            return ClassifyUdp(packet, length, udp, ref addr);
        }

        return PacketDecision.Passthrough;
    }

    PacketDecision ClassifyTcp(byte[] working, uint length, int tcp, ref WinDivertAddress addr)
    {
        var srcPort = BinaryPrimitives.ReadUInt16BigEndian(working.AsSpan(tcp, 2));
        var dstPort = BinaryPrimitives.ReadUInt16BigEndian(working.AsSpan(tcp + 2, 2));
        if (srcPort == _listenPort)
        {
            if (!_flows.TryGetValue(dstPort, out var flow) || !working.AsSpan(16, 4).SequenceEqual(flow.ServerIp))
            {
                return PacketDecision.Passthrough;
            }

            DirectPacket.ReflectFromProxy(working.AsSpan(0, (int)length), tcp, flow.ServerPort);
            addr.SetOutbound(false);
            return PacketDecision.Modified;
        }

        if (!addr.Outbound)
        {
            return PacketDecision.Passthrough;
        }

        var dest = working.AsSpan(16, 4);
        if (!DirectPacket.IsPublic(dest) || IsLocalOrProxy(dest, dstPort))
        {
            return PacketDecision.Passthrough;
        }

        var syn = (working[tcp + 13] & 0x02) != 0;
        if (syn)
        {
            var pid = 0;
            var until = Environment.TickCount64 + 8;
            while (!TryOwner(srcPort, tcp: true, out pid) && Environment.TickCount64 < until)
            {
                Thread.Sleep(1);
            }

            if (pid <= 0 || !_scope.IsTarget(pid))
            {
                return PacketDecision.Passthrough;
            }

            var flow = new TcpFlow(dest.ToArray(), dstPort);
            _flows[srcPort] = flow;
            Interlocked.Increment(ref _flowsOpened);
            Log("接管 TCP " + DirectPacket.ToAddress(flow.ServerIp) + ":" + dstPort);
            DirectPacket.ReflectToProxy(working.AsSpan(0, (int)length), tcp, (ushort)_listenPort);
            addr.SetOutbound(false);
            return PacketDecision.Modified;
        }

        if (!_flows.TryGetValue(srcPort, out var existing)
            || existing.ServerPort != dstPort
            || !dest.SequenceEqual(existing.ServerIp))
        {
            return PacketDecision.Passthrough;
        }

        existing.Seen = Environment.TickCount64;
        DirectPacket.ReflectToProxy(working.AsSpan(0, (int)length), tcp, (ushort)_listenPort);
        addr.SetOutbound(false);
        return PacketDecision.Modified;
    }

    PacketDecision ClassifyUdp(byte[] packet, uint length, int udp, ref WinDivertAddress addr)
    {
        if (!addr.Outbound)
        {
            return PacketDecision.Passthrough;
        }

        var srcPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(udp, 2));
        var dstPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(udp + 2, 2));
        if (dstPort == 443)
        {
            if (TryOwner(srcPort, tcp: false, out var quicPid) && _scope.IsTarget(quicPid))
            {
                if (Interlocked.Exchange(ref _quicLogged, 1) == 0)
                {
                    Log("已拦截 Codex 的 UDP/443，让它改走可代理的 TCP。");
                }

                return PacketDecision.Drop;
            }

            return PacketDecision.Passthrough;
        }

        if (dstPort != 53 || !TryOwner(srcPort, tcp: false, out var pid) || !_scope.IsTarget(pid))
        {
            return PacketDecision.Passthrough;
        }

        var copy = new byte[length];
        Buffer.BlockCopy(packet, 0, copy, 0, (int)length);
        var saved = addr;
        _ = Task.Run(() => AnswerDns(copy, udp, saved));
        return PacketDecision.Drop;
    }

    void AnswerDns(byte[] packet, int udp, WinDivertAddress addr)
    {
        var fallback = addr;
        try
        {
            var header = (packet[0] & 0x0F) * 4;
            var payloadAt = header + 8;
            if (payloadAt >= packet.Length || !DnsMessages.TryParseQuestion(packet.AsSpan(payloadAt), out var question) || DnsMessages.IsLocalName(question.Name))
            {
                Send(packet, (uint)packet.Length, ref fallback);
                return;
            }

            byte[]? payload = question.Type switch
            {
                DnsMessages.TypeAAAA or DnsMessages.TypeHttps => DnsMessages.EmptyNoError(packet.AsSpan(payloadAt), question.End),
                DnsMessages.TypeA => ResolveA(packet.AsSpan(payloadAt, packet.Length - payloadAt)),
                _ => null
            };
            if (payload == null)
            {
                Send(packet, (uint)packet.Length, ref fallback);
                return;
            }

            var reply = DirectPacket.BuildUdpReply(packet, udp, payload);
            addr.SetOutbound(false);
            addr.Bits &= ~unchecked((uint)ChecksumBits);
            if (!WinDivertNative.WinDivertHelperCalcChecksums(reply, (uint)reply.Length, ref addr, 0))
            {
                Send(packet, (uint)packet.Length, ref fallback);
                return;
            }

            Send(reply, (uint)reply.Length, ref addr);
        }
        catch (Exception ex)
        {
            Log("DNS 接管失败，改走原查询：" + ex.Message);
            try { Send(packet, (uint)packet.Length, ref fallback); } catch { }
        }
    }

    byte[]? ResolveA(ReadOnlySpan<byte> query)
    {
        using var body = new ByteArrayContent(query.ToArray());
        body.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        using var response = DnsClient.Value.PostAsync("https://1.1.1.1/dns-query", body).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        if (bytes.Length < 12 || bytes.Length > 1200 || (bytes[2] & 0x80) == 0)
        {
            return null;
        }

        return bytes;
    }

    bool TryOwner(ushort localPort, bool tcp, out int pid)
    {
        var table = tcp ? _tcpPorts : _udpPorts;
        if (table.TryGetValue(localPort, out pid) || _socketPorts.TryGetValue(localPort, out pid))
        {
            return pid > 0;
        }

        pid = 0;
        return false;
    }

    void TableLoop()
    {
        while (Volatile.Read(ref _closed) == 0)
        {
            RefreshTables(force: true);
            Thread.Sleep(5);
        }
    }

    void RefreshTables(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _tablesAt) < 8)
        {
            return;
        }

        Interlocked.Exchange(ref _tablesAt, now);
        try
        {
            _tcpPorts = SocketOwners.Tcp();
            _udpPorts = SocketOwners.Udp();
        }
        catch
        {
            // A missed table leaves the packet on its original path.
        }
    }

    bool IsLocalOrProxy(ReadOnlySpan<byte> dest, ushort destPort)
    {
        var key = BinaryPrimitives.ReadUInt32BigEndian(dest);
        if (_localIps.Contains(key))
        {
            return true;
        }

        return destPort == _options.ProxyPort && dest.SequenceEqual(_proxyIp);
    }

    async Task AcceptLoop()
    {
        while (Volatile.Read(ref _closed) == 0)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_cancel.Token);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => Relay(client));
        }
    }

    async Task Relay(TcpClient app)
    {
        TcpClient? proxy = null;
        try
        {
            var remote = app.Client.RemoteEndPoint as IPEndPoint;
            if (remote == null || !_flows.TryGetValue((ushort)remote.Port, out var flow))
            {
                Log("收到无法对应的连接，已关闭。远端 " + (remote?.ToString() ?? "(空)"));
                return;
            }

            proxy = new TcpClient { NoDelay = true };
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await proxy.ConnectAsync(_options.ProxyHost, _options.ProxyPort, connectCts.Token);
            var upstream = proxy.GetStream();
            var target = DirectPacket.ToAddress(flow.ServerIp) + ":" + flow.ServerPort;
            var request = Encoding.ASCII.GetBytes("CONNECT " + target + " HTTP/1.1\r\nHost: " + target + "\r\n\r\n");
            await upstream.WriteAsync(request);
            if (!await ReadConnectSuccess(upstream))
            {
                Log("代理没有接受 CONNECT " + target);
                return;
            }

            app.NoDelay = true;
            var downstream = app.GetStream();
            var left = Pump(downstream, upstream, _cancel.Token);
            var right = Pump(upstream, downstream, _cancel.Token);
            await Task.WhenAny(left, right);
            try { app.Close(); } catch { }
            try { proxy.Close(); } catch { }
            try { await Task.WhenAll(left, right); } catch { }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log("转发失败：" + ex.Message);
        }
        finally
        {
            try { app.Close(); } catch { }
            try { proxy?.Close(); } catch { }
        }
    }

    static async Task<bool> ReadConnectSuccess(NetworkStream stream)
    {
        var buffer = new byte[1024];
        var used = 0;
        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used));
            if (read == 0)
            {
                return false;
            }

            used += read;
            var text = Encoding.ASCII.GetString(buffer, 0, used);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
            var line = lineEnd > 0 ? text[..lineEnd] : text;
            var parts = line.Split(' ');
            return parts.Length >= 2 && int.TryParse(parts[1], out var status) && status is >= 200 and < 300;
        }

        return false;
    }

    static async Task Pump(Stream from, Stream to, CancellationToken token)
    {
        var buffer = new byte[16384];
        while (true)
        {
            int read;
            try
            {
                read = await from.ReadAsync(buffer, token);
            }
            catch
            {
                return;
            }

            if (read == 0)
            {
                return;
            }

            await to.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    void SocketLoop()
    {
        try
        {
            while (Volatile.Read(ref _closed) == 0)
            {
                var addr = new WinDivertAddress();
                if (!WinDivertNative.WinDivertRecvEvent(_sockets, IntPtr.Zero, 0, out _, ref addr))
                {
                    break;
                }

                if (addr.Event != 4 || addr.SocketLocalPort == 0)
                {
                    continue;
                }

                _socketPorts[addr.SocketLocalPort] = (int)addr.SocketProcessId;
            }
        }
        catch
        {
            // Socket hints are optional. The TCP table still identifies owners.
        }
    }

    void Send(byte[] packet, uint length, ref WinDivertAddress addr)
    {
        if (Volatile.Read(ref _closed) != 0 || WinDivertNative.IsInvalid(_network))
        {
            return;
        }

        lock (_sendLock)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return;
            }

            WinDivertNative.WinDivertSend(_network, packet, length, out _, ref addr);
        }
    }

    void Log(string message) => _options.Log?.Invoke(message);

    Lazy<HttpClient> DnsClient => _dns ??= new Lazy<HttpClient>(() =>
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(AppProxy.ToHttpUrl(_options.ProxyHost + ":" + _options.ProxyPort)),
            UseProxy = true
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
    });

    Lazy<HttpClient>? _dns;

    static void Release(ref IntPtr handle)
    {
        var current = Interlocked.Exchange(ref handle, new IntPtr(-1));
        if (WinDivertNative.IsInvalid(current))
        {
            return;
        }

        try { WinDivertNative.WinDivertShutdown(current, 3); } catch { }
        try { WinDivertNative.WinDivertClose(current); } catch { }
    }

    static string OpenError(string action, int code)
    {
        var reason = code switch
        {
            5 => "需要管理员权限。",
            2 => "找不到 WinDivert 驱动文件。",
            577 => "WinDivert 驱动签名未通过。",
            _ => "WinDivert 错误 " + code + "。"
        };
        return action + "。" + reason;
    }

    enum PacketDecision
    {
        Passthrough,
        Modified,
        Drop
    }

    sealed class TcpFlow(byte[] serverIp, ushort serverPort)
    {
        public byte[] ServerIp { get; } = serverIp;
        public ushort ServerPort { get; } = serverPort;
        public long Seen = Environment.TickCount64;
    }
}
