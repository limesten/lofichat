using System.Net;
using System.Net.Sockets;
using System.Text;

class ChatServer
{
    private TcpListener _listener;
    private List<TcpClient> _clients = new();
    private Dictionary<TcpClient, string> _names = new();
    private Queue<string> _messageQueue = new();
    private readonly object _clientsLock = new();
    private readonly object _namesLock = new();
    private readonly object _messageQueueLock = new();
    public readonly string ChatName;
    public readonly int Port;
    public bool Running { get; private set; }
    public readonly int BufferSize = 2 * 1024; // 2kB
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
                Task.Run(() => _handleNewConnection());

            Task.Run(() => _checkForDisconnects());
            Task.Run(() => _checkFoNewMessages());
            Task.Run(() => _sendMessages());

            Thread.Sleep(10);
        }

        lock (_clientsLock)
        {
            foreach (TcpClient m in _clients)
            {
                _cleanupClient(m);
            }
        }
        _listener.Stop();
        Console.WriteLine("Server is shutting down...");
    }

    private void _handleNewConnection()
    {
        TcpClient newClient = _listener.AcceptTcpClient();
        NetworkStream netStream = newClient.GetStream();

        TimeSpan timeout = TimeSpan.FromMinutes(1);

        netStream.ReadTimeout = (int)timeout.TotalMilliseconds;
        newClient.SendBufferSize = BufferSize;
        newClient.ReceiveBufferSize = BufferSize;

        EndPoint? endPoint = newClient.Client.RemoteEndPoint;
        Console.WriteLine($"Handling a new client from {endPoint}");

        string welcomeMsg = "Welcome to the chat, please identify yourself in the format: 'name:{yourName}'\n";
        _writeToNetStream(netStream, welcomeMsg);

        bool valid = false;
        do
        {
            byte[] msgBuffer = new byte[BufferSize];
            int bytesRead = netStream.Read(msgBuffer, 0, msgBuffer.Length);

            if (bytesRead == 0)
            {
                Console.WriteLine($"Received empty message from {endPoint}");
                string emptyMsgResponse = "Received empty message, please identify yourself in the format: 'name:{yourName}'\n";
                _writeToNetStream(netStream, emptyMsgResponse);
                continue;
            }

            string msg = Encoding.UTF8.GetString(msgBuffer, 0, bytesRead);
            if (!msg.StartsWith("name:"))
            {
                Console.WriteLine($"Received message in wrong format from {endPoint}");
                string wrongMsgFormatResponse = "Wrong format received, please identify yourself in the format: 'name:{yourName}'\n";
                _writeToNetStream(netStream, wrongMsgFormatResponse);
                continue;
            }

            string name = msg.Substring(msg.IndexOf(":") + 1).Replace("\r\n", "");
            if (name == string.Empty)
            {
                Console.WriteLine($"Received message in correct format but with name missing from {endPoint}");
                string nameMissingResponse = "Name missing. Please include your name in the format: 'name:{yourName}'\n";
                _writeToNetStream(netStream, nameMissingResponse);
                continue;
            }

            if (_names.ContainsValue(name))
            {
                Console.WriteLine($"Received message in correct format but with name missing from {endPoint}");
                string nameTakenResponse = "Name is already taken, please enter another name.\n";
                _writeToNetStream(netStream, nameTakenResponse);
                continue;
            }

            valid = true;
            lock (_namesLock) _names.Add(newClient, name);
            lock (_clientsLock) _clients.Add(newClient);

            Console.WriteLine($"{endPoint} is a new messenger with the name {name}");

            lock (_messageQueueLock) _messageQueue.Enqueue($"{name} has joined the chat!");

        } while (!valid);
    }

    private void _writeToNetStream(NetworkStream netStream, string msg)
    {
        byte[] msgBuffer = Encoding.UTF8.GetBytes(msg);
        netStream.Write(msgBuffer, 0, msgBuffer.Length);
    }

    private void _checkFoNewMessages()
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

                    string msg = $"{_names[c]}: {Encoding.UTF8.GetString(msgBuffer)}";
                    Console.WriteLine(msg.Replace("\r\n", ""));
                    lock (_messageQueueLock) _messageQueue.Enqueue(msg.Replace("\r\n", ""));
                }
            }
        }
    }

    private void _sendMessages()
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

    private void _checkForDisconnects()
    {
        lock (_clientsLock)
        {
            foreach (TcpClient c in _clients.ToArray())
            {
                if (_isDisconnected(c))
                {
                    string name = _names[c];
                    Console.WriteLine($"{name} has left.");
                    lock (_messageQueueLock) _messageQueue.Enqueue($"{name} has left the chat.");

                    _clients.Remove(c);
                    lock (_namesLock) _names.Remove(c);
                    _cleanupClient(c);
                }
            }
        }
    }

    private static void _cleanupClient(TcpClient client)
    {
        client.GetStream().Close();
        client.Close();
    }

    // Checks if a socket has disconnected
    // Adapted from -- http://stackoverflow.com/questions/722240/instantly-detect-client-disconnection-from-server-socket
    private static bool _isDisconnected(TcpClient client)
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


