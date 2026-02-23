using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// 中继数据包结构
public class RelayPacket
{
    public string SenderID { get; set; }
    public string TargetID { get; set; }
    public byte[] Data { get; set; }
    public int Channel { get; set; }
}

// 客户端连接类
public class ClientConnection
{
    // 添加状态枚举
    public enum ConnectionState
    {
        Connecting,
        Active,
        Closing,
        Closed
    }

    private ConnectionState state = ConnectionState.Connecting;
    private TcpClient tcpClient;
    private NetworkStream networkStream;
    private RelayServer server;
    private string steamID;
    private bool isRunning = true;
    private DateTime lastActivity = DateTime.Now;
    private DateTime lastHeartbeatResponse = DateTime.Now;
    private object sendLock = new object();

    // 新增：注册时间和连接ID
    private DateTime registrationTime = DateTime.Now;
    private string connectionId = Guid.NewGuid().ToString();

    public DateTime LastHeartbeatResponse
    {
        get { return lastHeartbeatResponse; }
    }

    public DateTime RegistrationTime
    {
        get { return registrationTime; }
    }

    public string ConnectionId
    {
        get { return connectionId; }
    }

    public ConnectionState State
    {
        get { return state; }
    }

    public string SteamID
    {
        get { return steamID; }
    }

    public bool IsConnected
    {
        get { return isRunning && tcpClient != null && tcpClient.Connected && state == ConnectionState.Active; }
    }

    // 构造函数
    public ClientConnection(TcpClient client, RelayServer server)
    {
        this.tcpClient = client;
        this.server = server;
        this.networkStream = client.GetStream();
        this.state = ConnectionState.Connecting;

        // 增加接收缓冲区
        tcpClient.ReceiveBufferSize = 16384;
        tcpClient.SendBufferSize = 16384;
        tcpClient.NoDelay = true; // 禁用Nagle算法

        // 设置超时
        tcpClient.ReceiveTimeout = 300000; // 5分钟超时
        tcpClient.SendTimeout = 30000; // 30秒发送超时

        Console.WriteLine($"[RELAY] New connection {connectionId} created, awaiting registration");

        // 开始接收线程
        Thread receiveThread = new Thread(ReceiveLoop);
        receiveThread.IsBackground = true;
        receiveThread.Start();
    }

    // 激活连接（在成功注册后调用）
    public void Activate(string steamID)
    {
        this.steamID = steamID;
        this.state = ConnectionState.Active;
        this.lastActivity = DateTime.Now;
        this.lastHeartbeatResponse = DateTime.Now;
        Console.WriteLine($"[RELAY] Connection {connectionId} activated for SteamID {steamID}");
    }

    // 更新心跳响应时间
    public void UpdateHeartbeatResponse()
    {
        this.lastHeartbeatResponse = DateTime.Now;
        this.lastActivity = DateTime.Now;
    }

    // 关闭连接
    public void Close()
    {
        if (state == ConnectionState.Closing || state == ConnectionState.Closed)
        {
            return; // 避免重复关闭
        }

        state = ConnectionState.Closing;
        isRunning = false;

        try
        {
            if (networkStream != null)
            {
                networkStream.Close();
                networkStream = null;
            }

            if (tcpClient != null)
            {
                if (tcpClient.Connected)
                {
                    try
                    {
                        tcpClient.Client.Shutdown(SocketShutdown.Both);
                    }
                    catch
                    {
                    }
                }

                tcpClient.Close();
                tcpClient = null;
            }

            state = ConnectionState.Closed;
            Console.WriteLine($"[RELAY] Connection {connectionId} for SteamID {steamID} closed cleanly");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RELAY] Error during connection {connectionId} close: {ex.Message}");
            state = ConnectionState.Closed; // 即使出错也标记为已关闭
        }
    }

    // 发送数据包
    public bool SendPacket(RelayPacket packet)
    {
        if (!IsConnected)
        {
            Console.WriteLine($"[RELAY] Cannot send packet to {steamID}: connection not active");
            return false;
        }

        try
        {
            byte[] serializedPacket = SerializePacket(packet);
            byte[] packetSizeBytes = BitConverter.GetBytes(serializedPacket.Length);

            lock (sendLock)
            {
                // 设置发送超时以防止阻塞
                int originalTimeout = tcpClient.SendTimeout;
                if (originalTimeout != 10000) // 如果不是10秒，设为10秒
                {
                    tcpClient.SendTimeout = 10000;
                }

                try
                {
                    networkStream.Write(packetSizeBytes, 0, 4);
                    networkStream.Write(serializedPacket, 0, serializedPacket.Length);
                    networkStream.Flush();

                    lastActivity = DateTime.Now;
                    return true;
                }
                finally
                {
                    // 恢复原始超时时间
                    if (originalTimeout != 10000)
                    {
                        tcpClient.SendTimeout = originalTimeout;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RELAY] Error sending packet to {steamID}: {ex.Message}");
            return false;
        }
    }

    // 接收循环
    private void ReceiveLoop()
    {
        byte[] sizeBuffer = new byte[4];
        int consecutiveErrors = 0;

        try
        {
            while (isRunning)
            {
                try
                {
                    // 首先读取数据包大小
                    int bytesRead = networkStream.Read(sizeBuffer, 0, 4);
                    if (bytesRead != 4)
                    {
                        consecutiveErrors++;
                        Console.WriteLine(
                            $"[RELAY] Connection {connectionId}: Failed to read packet size, got {bytesRead} bytes");

                        if (consecutiveErrors > 3)
                        {
                            Console.WriteLine(
                                $"[RELAY] Connection {connectionId}: Too many consecutive read errors, closing");
                            break;
                        }

                        Thread.Sleep(100 * consecutiveErrors);
                        continue;
                    }

                    consecutiveErrors = 0; // 成功读取，重置错误计数

                    int packetSize = BitConverter.ToInt32(sizeBuffer, 0);
                    if (packetSize <= 0 || packetSize > 1048576) // 最大1MB
                    {
                        Console.WriteLine($"[RELAY] Connection {connectionId}: Invalid packet size: {packetSize}");
                        continue;
                    }

                    // 读取完整数据包
                    byte[] packetData = new byte[packetSize];
                    int totalBytesRead = 0;
                    while (totalBytesRead < packetSize)
                    {
                        int remainingBytes = packetSize - totalBytesRead;
                        int bytes = networkStream.Read(packetData, totalBytesRead, remainingBytes);

                        if (bytes <= 0)
                        {
                            Console.WriteLine($"[RELAY] Connection {connectionId}: Socket closed during packet read");
                            isRunning = false;
                            break;
                        }

                        totalBytesRead += bytes;
                    }

                    if (totalBytesRead == packetSize)
                    {
                        lastActivity = DateTime.Now;

                        // 反序列化并处理数据包
                        RelayPacket packet = DeserializePacket(packetData);
                        if (packet != null)
                        {
                            ProcessPacket(packet);
                        }
                    }
                }
                catch (IOException ex)
                {
                    consecutiveErrors++;
                    Console.WriteLine($"[RELAY] Connection {connectionId}: IO error: {ex.Message}");

                    if (consecutiveErrors > 3)
                    {
                        Console.WriteLine(
                            $"[RELAY] Connection {connectionId}: Too many consecutive IO errors, closing");
                        break;
                    }

                    Thread.Sleep(100 * consecutiveErrors);
                }
                catch (ObjectDisposedException)
                {
                    Console.WriteLine($"[RELAY] Connection {connectionId}: Connection was closed");
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RELAY] Connection {connectionId}: Unexpected error: {ex.Message}");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RELAY] Connection {connectionId}: Fatal error in receive loop: {ex.Message}");
        }
        finally
        {
            // 确保连接关闭
            Close();

            // 如果已经注册，通知服务器移除此连接
            if (!string.IsNullOrEmpty(steamID))
            {
                server.OnClientDisconnected(steamID, this);
            }
        }
    }

    // 处理接收到的数据包
    private void ProcessPacket(RelayPacket packet)
    {
        // 如果是首次注册
        if (state == ConnectionState.Connecting && packet.TargetID == "SERVER" &&
            System.Text.Encoding.UTF8.GetString(packet.Data) == "REGISTER")
        {
            server.RegisterClient(packet.SenderID, this);
            return;
        }

        // 如果已经注册，但SenderID与注册的ID不匹配
        if (state == ConnectionState.Active && !string.IsNullOrEmpty(steamID) &&
            packet.SenderID != steamID)
        {
            packet.SenderID = steamID; // 强制使用注册的SteamID
        }

        // 转发数据包
        server.ForwardPacket(packet);
    }

    // 序列化数据包
    private byte[] SerializePacket(RelayPacket packet)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                byte[] senderBytes = Encoding.UTF8.GetBytes(packet.SenderID);
                byte[] targetBytes = Encoding.UTF8.GetBytes(packet.TargetID);

                writer.Write((byte)senderBytes.Length);
                writer.Write(senderBytes);
                writer.Write((byte)targetBytes.Length);
                writer.Write(targetBytes);
                writer.Write((byte)packet.Channel);
                writer.Write(packet.Data.Length);
                writer.Write(packet.Data);

                return ms.ToArray();
            }
        }
    }

    // 反序列化数据包
    private RelayPacket DeserializePacket(byte[] data)
    {
        try
        {
            using (MemoryStream ms = new MemoryStream(data))
            {
                using (BinaryReader reader = new BinaryReader(ms))
                {
                    byte senderLengthByte = reader.ReadByte();
                    byte[] senderBytes = reader.ReadBytes(senderLengthByte);
                    string senderId = Encoding.UTF8.GetString(senderBytes);

                    byte targetLengthByte = reader.ReadByte();
                    byte[] targetBytes = reader.ReadBytes(targetLengthByte);
                    string targetId = Encoding.UTF8.GetString(targetBytes);

                    byte channel = reader.ReadByte();
                    int dataLength = reader.ReadInt32();
                    byte[] packetData = reader.ReadBytes(dataLength);

                    return new RelayPacket
                    {
                        SenderID = senderId,
                        TargetID = targetId,
                        Channel = channel,
                        Data = packetData
                    };
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RELAY] Error deserializing packet: {ex.Message}");
            return null;
        }
    }
}

// 主服务器类
public class RelayServer
{
    private TcpListener tcpListener;
    private int port;
    private bool isRunning = false;
    private Dictionary<string, ClientConnection> clientConnections = new Dictionary<string, ClientConnection>();
    private object clientsLock = new object();
    private Thread acceptThread;
    private Thread heartbeatThread;

    // 新增：用于跟踪上次记录的玩家数量
    private int lastRecordedPlayerCount = -1;

    // 新增：日志文件路径
    private readonly string playerCountLogFile =
        @"C:\Users\Administrator\Desktop\PlagueIncAntiCheatingService\WebAPI\public\api\offline.txt";

    public RelayServer(int port)
    {
        this.port = port;
    }

    // 新增：记录玩家数量变化的方法
    private void LogPlayerCountChange()
    {
        lock (clientsLock)
        {
            int currentCount = clientConnections.Count;

            // 只有当玩家数量变化时才记录
            if (currentCount != lastRecordedPlayerCount)
            {
                try
                {
                    // 格式化当前时间和玩家数量
                    string timestamp = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
                    string logEntry = $"[{timestamp}]{currentCount}";

                    // 追加到文件（不覆盖已有内容）
                    File.AppendAllText(playerCountLogFile, logEntry + Environment.NewLine);

                    // 更新上次记录的数量
                    lastRecordedPlayerCount = currentCount;

                    Console.WriteLine($"[RELAY] Player count changed to {currentCount}, logged to file");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RELAY] Error logging player count: {ex.Message}");
                }
            }
        }
    }

    // 启动服务器
    public void Start()
    {
        if (isRunning)
        {
            Console.WriteLine("[RELAY] Server is already running");
            return;
        }

        // 新增：确保日志文件目录存在
        try
        {
            string directory = Path.GetDirectoryName(playerCountLogFile);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RELAY] Error creating log directory: {ex.Message}");
        }

        try
        {
            // 绑定到所有IP地址
            tcpListener = new TcpListener(IPAddress.Any, port);
            tcpListener.Start();

            isRunning = true;
            Console.WriteLine($"[RELAY] Server started on port {port}");

            // 启动客户端接受线程
            acceptThread = new Thread(AcceptClients);
            acceptThread.IsBackground = true;
            acceptThread.Start();

            // 启动心跳监控线程
            heartbeatThread = new Thread(HeartbeatMonitor);
            heartbeatThread.IsBackground = true;
            heartbeatThread.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RELAY] Error starting server: {ex.Message}");
            Stop();
        }
    }

    // 停止服务器
    public void Stop()
    {
        if (!isRunning)
        {
            return;
        }

        isRunning = false;

        try
        {
            // 停止监听
            if (tcpListener != null)
            {
                tcpListener.Stop();
            }

            // 关闭所有客户端连接
            lock (clientsLock)
            {
                foreach (var client in clientConnections.Values)
                {
                    try
                    {
                        client.Close();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[RELAY] Error closing client connection: {ex.Message}");
                    }
                }

                clientConnections.Clear();
            }

            Console.WriteLine("[RELAY] Server stopped");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RELAY] Error stopping server: {ex.Message}");
        }
    }

    // 接受客户端连接
    private void AcceptClients()
    {
        try
        {
            while (isRunning)
            {
                TcpClient client = tcpListener.AcceptTcpClient();
                string clientEndPoint = client.Client.RemoteEndPoint.ToString();
                Console.WriteLine($"[RELAY] New connection from {clientEndPoint}");

                // 创建新的客户端连接，它会自动启动接收线程
                ClientConnection connection = new ClientConnection(client, this);

                // 该连接会在注册成功后添加到clientConnections中
            }
        }
        catch (SocketException ex)
        {
            if (isRunning)
            {
                Console.WriteLine($"[RELAY] Socket error in AcceptClients: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            if (isRunning)
            {
                Console.WriteLine($"[RELAY] Error in AcceptClients: {ex.Message}");
            }
        }
    }

    // 注册客户端
    public void RegisterClient(string steamID, ClientConnection connection)
    {
        if (string.IsNullOrEmpty(steamID))
        {
            Console.WriteLine("[RELAY] Attempted to register client with empty SteamID");
            connection.Close();
            LogPlayerCountChange();
            return;
        }

        lock (clientsLock)
        {
            // 如果已存在相同SteamID的连接
            if (clientConnections.TryGetValue(steamID, out ClientConnection existingConnection))
            {
                // 检查是否是相同的连接实例（可能是重复注册）
                if (existingConnection.ConnectionId == connection.ConnectionId)
                {
                    Console.WriteLine(
                        $"[RELAY] Client {steamID} re-registering with same connection {connection.ConnectionId}");
                    connection.Activate(steamID);
                    return;
                }

                // 如果现有连接已经标记为关闭，直接替换
                if (existingConnection.State == ClientConnection.ConnectionState.Closing ||
                    existingConnection.State == ClientConnection.ConnectionState.Closed)
                {
                    Console.WriteLine($"[RELAY] Replacing closed connection for client {steamID}");
                    // 移除旧连接
                    clientConnections.Remove(steamID);
                }
                else
                {
                    // 如果现有连接仍处于活动状态，关闭它
                    Console.WriteLine(
                        $"[RELAY] Re-registering existing client: {steamID}, closing old connection {existingConnection.ConnectionId}");
                    existingConnection.Close();

                    // 等待一段时间确保旧连接彻底关闭
                    Thread.Sleep(300);

                    // 移除旧连接
                    clientConnections.Remove(steamID);
                }
            }

            // 注册新连接
            connection.Activate(steamID);
            clientConnections[steamID] = connection;
            Console.WriteLine($"[RELAY] Client registered: {steamID} with connection {connection.ConnectionId}");
        }
    }

    // 转发数据包
    public void ForwardPacket(RelayPacket packet)
    {
        if (packet.TargetID == "SERVER")
        {
            // 处理服务器消息（如注册或心跳响应）
            string message = System.Text.Encoding.UTF8.GetString(packet.Data);
            Console.WriteLine($"[RELAY] Received server message from {packet.SenderID}: {message}");

            // 处理心跳响应和注册消息
            lock (clientsLock)
            {
                if (clientConnections.TryGetValue(packet.SenderID, out ClientConnection connection))
                {
                    if (message == "PONG")
                    {
                        connection.UpdateHeartbeatResponse();
                    }
                    else if (message == "REGISTER")
                    {
                        // 更新注册状态
                        Console.WriteLine($"[RELAY] Updated registration for {packet.SenderID}");
                    }
                }
            }

            return;
        }

        // 转发普通数据包
        ClientConnection targetConnection = null;

        lock (clientsLock)
        {
            if (clientConnections.TryGetValue(packet.TargetID, out targetConnection))
            {
                if (!targetConnection.IsConnected)
                {
                    Console.WriteLine($"[RELAY] Target {packet.TargetID} connection exists but not active, removing");
                    RemoveClient(packet.TargetID);
                    return;
                }
            }
            else
            {
                Console.WriteLine(
                    $"[RELAY] Cannot forward packet: Target {packet.TargetID} not found, from {packet.SenderID}");
                return;
            }
        }

        // 在锁外转发以减少锁定时间
        try
        {
            targetConnection.SendPacket(packet);
            Console.WriteLine(
                $"[RELAY] Forwarded packet: {packet.SenderID} -> {packet.TargetID} (Channel: {packet.Channel}, Size: {packet.Data.Length} bytes)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RELAY] Error forwarding to {packet.TargetID}: {ex.Message}");

            // 如果发送失败，移除目标客户端连接
            lock (clientsLock)
            {
                if (clientConnections.TryGetValue(packet.TargetID, out ClientConnection conn) &&
                    conn == targetConnection)
                {
                    RemoveClient(packet.TargetID);
                }
            }
        }
    }

    // 移除客户端
    public void RemoveClient(string steamID)
    {
        lock (clientsLock)
        {
            if (clientConnections.TryGetValue(steamID, out ClientConnection connection))
            {
                Console.WriteLine($"[RELAY] Removing client {steamID}");
                connection.Close();
                clientConnections.Remove(steamID);
            }
        }

        LogPlayerCountChange();
    }

    // 当客户端断开连接时调用
    public void OnClientDisconnected(string steamID, ClientConnection connection)
    {
        lock (clientsLock)
        {
            // 仅当字典中存储的连接与断开的连接相同时才移除
            if (clientConnections.TryGetValue(steamID, out ClientConnection storedConnection) &&
                storedConnection.ConnectionId == connection.ConnectionId)
            {
                Console.WriteLine($"[RELAY] Client disconnected: {steamID}");
                clientConnections.Remove(steamID);
            }
        }

        LogPlayerCountChange();
    }

    // 心跳监控线程
    private void HeartbeatMonitor()
    {
        while (isRunning)
        {
            try
            {
                Thread.Sleep(15000); // 改为15秒检查一次

                lock (clientsLock)
                {
                    List<string> disconnectedClients = new List<string>();
                    Dictionary<string, ClientConnection> clientsToCheck =
                        new Dictionary<string, ClientConnection>(clientConnections);

                    foreach (var kvp in clientsToCheck)
                    {
                        string steamID = kvp.Key;
                        ClientConnection connection = kvp.Value;

                        // 检查连接状态
                        if (!connection.IsConnected)
                        {
                            Console.WriteLine($"[RELAY] Client {steamID} connection no longer active");
                            disconnectedClients.Add(steamID);
                            continue;
                        }

                        // 检查上次活动时间
                        if ((DateTime.Now - connection.LastHeartbeatResponse).TotalMinutes > 2)
                        {
                            Console.WriteLine($"[RELAY] Client {steamID} heartbeat timeout");
                            disconnectedClients.Add(steamID);
                            continue;
                        }

                        try
                        {
                            // 发送心跳包
                            RelayPacket heartbeat = new RelayPacket
                            {
                                SenderID = "SERVER",
                                TargetID = steamID,
                                Channel = 0,
                                Data = System.Text.Encoding.UTF8.GetBytes("PING")
                            };

                            connection.SendPacket(heartbeat);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[RELAY] Failed to send heartbeat to {steamID}: {ex.Message}");
                            disconnectedClients.Add(steamID);
                        }
                    }

                    // 移除断开的客户端
                    foreach (string id in disconnectedClients)
                    {
                        Console.WriteLine($"[RELAY] Heartbeat monitor removing disconnected client: {id}");
                        RemoveClient(id);
                    }

                    Console.WriteLine($"[RELAY] Current client count: {clientConnections.Count}");
                    LogPlayerCountChange();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RELAY] Heartbeat monitor error: {ex.Message}");
            }
        }
    }
}

// 主程序入口点
public class Program
{
    public static void Main(string[] args)
    {
        try
        {
            int port = 27777; // 默认端口

            // 如果命令行提供了端口参数，则使用它
            if (args.Length > 0 && int.TryParse(args[0], out int customPort))
            {
                port = customPort;
            }

            // 创建并启动服务器
            RelayServer server = new RelayServer(port);
            server.Start();

            Console.WriteLine("[RELAY] Server running. Press Ctrl+C to stop...");

            // 等待Ctrl+C信号
            ManualResetEvent quitEvent = new ManualResetEvent(false);
            Console.CancelKeyPress += (sender, eArgs) =>
            {
                quitEvent.Set();
                eArgs.Cancel = true;
            };
            quitEvent.WaitOne();

            // 停止服务器
            server.Stop();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RELAY] Fatal error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}