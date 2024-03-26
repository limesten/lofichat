using System.Net;
using System.Net.Sockets;
using System.Text;

class ChatServer
{
    private TcpListener _listener;
    private static List<TcpClient> _clients = new();
    private Dictionary<TcpClient, string> _names = new();
    private Queue<string> _messageQueue = new();
    private Dictionary<TcpClient, DateTime> _lastMessageTimestamps = new();
    private Dictionary<IPAddress, int> _strikeCounts = new();
    private readonly object _clientsLock = new();
    private readonly object _namesLock = new();
    private readonly object _messageQueueLock = new();
    public readonly string ChatName;
    public readonly int Port;
    public bool Running { get; private set; }
    public readonly int BufferSize = 2 * 1024; // 2kB
    public const int StrikeLimit = 10;
    public ChatServer(string name, int port)
    {
        ChatName = name;
        Port = port;
        Running = false;

        _listener = new(IPAddress.Any, port);
    }
    public void Run()
    {
        Console.WriteLine($"Starting the {ChatName} server on {Port}");
        Console.WriteLine("Press Ctrl-C to shut down the server at any time.");

        _listener.Start();
        Running = true;

        while (Running)
        {
            if (_listener.Pending())
                Task.Run(() => HandleNewConnection());

            Task.Run(() => CheckForDisconnects());
            Task.Run(() => CheckForNewMessages());
            Task.Run(() => SendMessages());

            Thread.Sleep(10);
        }

        lock (_clientsLock)
        {
            foreach (TcpClient m in _clients)
            {
                CleanupClient(m);
            }
        }
        _listener.Stop();
        Console.WriteLine("Server is shutting down...");
    }

    private void HandleNewConnection()
    {
        // TODO: Add token that clients need to provide
        TcpClient newClient = _listener.AcceptTcpClient();
        NetworkStream netStream = newClient.GetStream();

        TimeSpan timeout = TimeSpan.FromMinutes(1);

        netStream.ReadTimeout = (int)timeout.TotalMilliseconds;
        newClient.SendBufferSize = BufferSize;
        newClient.ReceiveBufferSize = BufferSize;

        IPEndPoint? IPEndPoint = newClient.Client.RemoteEndPoint as IPEndPoint;
        if (IPEndPoint == null)
        {
            Console.WriteLine("Couldn't get IP address from new client");
            CleanupClient(newClient);
            return;
        }

        if (_strikeCounts.ContainsKey(IPEndPoint.Address))
        {
            if (_strikeCounts[IPEndPoint.Address] >= StrikeLimit)
            {
                Console.WriteLine("Discarding banned IP connection attempt");
                CleanupClient(newClient);
                return;
            }
        }

        Console.WriteLine($"Handling a new client from {IPEndPoint}");

        bool valid = false;
        do
        {
            byte[] msgBuffer = new byte[BufferSize];
            int bytesRead = netStream.Read(msgBuffer, 0, msgBuffer.Length);

            if (bytesRead == 0)
            {
                Console.WriteLine($"Received empty message from {IPEndPoint}");
                string emptyMsgResponse = "[SERVER] Error: received empty message\n";
                WriteToNetStream(netStream, emptyMsgResponse);
                continue;
            }

            string msg = Encoding.UTF8.GetString(msgBuffer, 0, bytesRead);
            if (!msg.StartsWith("name:"))
            {
                Console.WriteLine($"Received message in wrong format from {IPEndPoint}");
                string wrongMsgFormatResponse = "[SERVER] Error: wrong message format\n";
                WriteToNetStream(netStream, wrongMsgFormatResponse);
                continue;
            }

            string name = msg.Substring(msg.IndexOf(":") + 1).Replace("\r\n", "");
            if (name == string.Empty)
            {
                Console.WriteLine($"Received message in correct format but with name missing from {IPEndPoint}");
                string nameMissingResponse = "[SERVER] Error: connection refused due to missing name parameter\n";
                WriteToNetStream(netStream, nameMissingResponse);
                continue;
            }

            if (_names.ContainsValue(name))
            {
                Console.WriteLine($"Received message in correct format but with name missing from {IPEndPoint}");
                string nameTakenResponse = "[SERVER] Error: name is already taken\n";
                WriteToNetStream(netStream, nameTakenResponse);
                continue;
            }

            valid = true;
            lock (_namesLock) _names.Add(newClient, name);
            lock (_clientsLock) _clients.Add(newClient);

            Console.WriteLine($"{IPEndPoint} is a new client with the name {name}");

            lock (_messageQueueLock) _messageQueue.Enqueue($"[SERVER] {name} has joined the chat!\n");

        } while (!valid);
    }

    private void WriteToNetStream(NetworkStream netStream, string msg)
    {
        byte[] msgBuffer = Encoding.UTF8.GetBytes(msg);
        netStream.Write(msgBuffer, 0, msgBuffer.Length);
    }

    private void CheckForNewMessages()
    {
        lock (_clientsLock)
        {
            foreach (TcpClient c in _clients)
            {
                int messageLength = c.Available;
                if (messageLength > 0)
                {
                    byte[] msgBuffer = new byte[messageLength];
                    c.GetStream().Read(msgBuffer, 0, messageLength);

                    IPEndPoint? IPEndPoint = c.Client.RemoteEndPoint as IPEndPoint;
                    if (IPEndPoint == null)
                    {
                        Console.WriteLine("Couldn't get IP address from new client");
                        CleanupClient(c);
                        return;
                    }

                    if (_strikeCounts.ContainsKey(IPEndPoint.Address))
                    {
                        if (_strikeCounts[IPEndPoint.Address] >= StrikeLimit)
                        {
                            Console.WriteLine($"Banning {IPEndPoint.Address}");
                            CleanupClient(c);
                            continue;
                        }
                    }

                    if (_lastMessageTimestamps.ContainsKey(c))
                    {
                        TimeSpan timeSinceLastMessage = DateTime.UtcNow - _lastMessageTimestamps[c];
                        if (!_strikeCounts.ContainsKey(IPEndPoint.Address))
                        {
                            _strikeCounts[IPEndPoint.Address] = 0;
                        }

                        if (timeSinceLastMessage.TotalSeconds < 1)
                        {
                            _strikeCounts[IPEndPoint.Address] += 1;
                            byte[] responseBuffer = Encoding.UTF8.GetBytes("[SERVER] you need to wait 1 second between messages\n");
                            c.GetStream().Write(responseBuffer, 0, responseBuffer.Length);
                            continue;
                        }
                        _strikeCounts[IPEndPoint.Address] -= 1;
                    }

                    string msg = $"{_names[c]}: {Encoding.UTF8.GetString(msgBuffer)}";
                    Console.WriteLine(msg.Replace("\r\n", ""));
                    lock (_messageQueueLock) _messageQueue.Enqueue(msg.Replace("\r\n", ""));
                    _lastMessageTimestamps[c] = DateTime.UtcNow;
                }
            }
        }
    }

    private void SendMessages()
    {
        lock (_messageQueueLock)
        {
            foreach (string msg in _messageQueue)
            {
                byte[] msgBuffer = Encoding.UTF8.GetBytes(msg + "\n");
                lock (_clientsLock)
                {
                    foreach (TcpClient client in _clients)
                    {
                        client.GetStream().Write(msgBuffer, 0, msgBuffer.Length);
                    }
                }
            }
            _messageQueue.Clear();
        }
    }

    private void CheckForDisconnects()
    {
        lock (_clientsLock)
        {
            foreach (TcpClient c in _clients.ToArray())
            {
                if (IsDisconnected(c))
                {
                    string name = _names[c];
                    Console.WriteLine($"{name} has left.");
                    lock (_messageQueueLock) _messageQueue.Enqueue($"[SERVER] {name} has left the chat.");

                    _clients.Remove(c);
                    lock (_namesLock) _names.Remove(c);
                    CleanupClient(c);
                }
            }
        }
    }

    private static void CleanupClient(TcpClient client)
    {
        client.GetStream().Close();
        client.Close();
        _clients.Remove(client);
    }

    // Checks if a socket has disconnected
    // Adapted from -- http://stackoverflow.com/questions/722240/instantly-detect-client-disconnection-from-server-socket
    private static bool IsDisconnected(TcpClient client)
    {
        try
        {
            Socket s = client.Client;
            return s.Poll(10 * 1000, SelectMode.SelectRead) && (s.Available == 0);
        }
        catch (SocketException)
        {
            // We got a socket error, assume it's disconnected
            return true;
        }
    }

    public void Shutdown()
    {
        // TODO: let clients know we're closing
        Running = false;
        Console.WriteLine("Shutting down server");
    }

    public static ChatServer? chat;

    protected static void InterruptHandler(object? sender, ConsoleCancelEventArgs args)
    {
        chat?.Shutdown();
        args.Cancel = true;
    }

    public static void Main(string[] args)
    {
        string name = "LofiChat";//args[0].Trim();
        int port = 6969;//int.Parse(args[1].Trim());
        chat = new(name, port);

        Console.CancelKeyPress += InterruptHandler;
        chat.Run();
    }
}


