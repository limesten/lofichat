using System.Net;
using System.Net.Sockets;
using System.Text;
using Terminal.Gui;

class ChatClient
{
    ListView ChatMessages = new();
    public List<string> Messages = new();
    public readonly string ServerAddress;
    public readonly int Port;
    public readonly string Name;
    private TcpClient _client;
    public readonly int BufferSize = 2 * 1024; // 2kB
    public bool Running { get; private set; }
    private NetworkStream? _msgStream = null;
    public ChatClient(string serverAddress, int port, string name)
    {
        ServerAddress = serverAddress;
        Port = port;
        Name = name;

        _client = new TcpClient();
        _client.SendBufferSize = BufferSize;
        _client.ReceiveBufferSize = BufferSize;
        Running = false;
    }
    public void SetupUI()
    {
        Application.Init();

        TextField chatInput = new("")
        {
            X = 0,
            Y = Pos.Bottom(Application.Top) - 5,
            Width = Dim.Fill(),
            Height = 1,
        };

        ChatMessages.X = 0;
        ChatMessages.Y = 0;
        ChatMessages.Width = Dim.Fill();
        ChatMessages.Height = 20;
        ChatMessages.ColorScheme = new Terminal.Gui.ColorScheme()
        {
            Normal = Terminal.Gui.Attribute.Make(Color.White, Color.Black),
            Focus = Terminal.Gui.Attribute.Make(Color.White, Color.Black)
        };
        ChatMessages.SetSource(Messages);

        chatInput.KeyDown += (args) =>
        {
            /*
            if (args.KeyEvent.Key == Key.Enter && chatInput.Text != "")
            {
                test.Add(chatInput.Text);
                chatInput.Text = "";
            }
            */
        };

        Application.Top.Add(chatInput);
        Application.Top.Add(ChatMessages);

        Func<MainLoop, bool> UpdateTimer = (mainLoop) =>
        {
            ChatMessages.SetSource(Messages);
            return true;
        };

        Application.MainLoop.AddTimeout(TimeSpan.FromSeconds(1), UpdateTimer);

        Application.Run();
        Application.Shutdown();
    }
    public void Connect()
    {
        _client.Connect(ServerAddress, Port);
        EndPoint? endPoint = _client.Client.RemoteEndPoint;
        if (endPoint == null)
        {
            return;
        }

        if (_client.Connected)
        {
            Console.WriteLine($"Connected to the server at {endPoint}");
            _msgStream = _client.GetStream();

            byte[] msgBuffer = Encoding.UTF8.GetBytes($"name:{Name}");
            _msgStream.Write(msgBuffer, 0, msgBuffer.Length);

            if (!_isDisconnected(_client))
            {
                Running = true;
                Thread receiveThread = new(ReceiveMessages);
                receiveThread.Start();

            }
            else
            {
                Console.WriteLine("We got rejected by the client :( Name taken?");
                _cleanupNetworkResources();
            }
        }
        else
        {
            _cleanupNetworkResources();
            Console.WriteLine($"Failed to connect to the server at {endPoint}");
        }
    }

    public void SendMessages()
    {
        bool wasRunning = Running;

        while (Running)
        {
            Console.Write($"{Name}> ");
            string? msg = Console.ReadLine();

            if (msg.ToLower() == "quit" || msg.ToLower() == "exit")
            {
                Console.WriteLine("Disconnecting...");
                Running = false;
            }
            else if (msg != string.Empty)
            {
                byte[] msgBuffer = Encoding.UTF8.GetBytes(msg);
                _msgStream.Write(msgBuffer, 0, msgBuffer.Length);
            }

            Thread.Sleep(10);

            if (_isDisconnected(_client))
            {
                Running = false;
                Console.WriteLine("Server has disconnected us :(");
            }
        }

        _cleanupNetworkResources();
        if (wasRunning)
            Console.WriteLine("Disconnected");
    }

    public void ReceiveMessages()
    {
        while (Running)
        {
            int messageLength = _client.Available;
            if (messageLength > 0)
            {
                byte[] msgBuffer = new byte[messageLength];
                _msgStream.Read(msgBuffer, 0, messageLength);

                string msg = Encoding.UTF8.GetString(msgBuffer);
                msg = msg.TrimEnd('\r', '\n');
                Messages.Add(msg);
            }

            Thread.Sleep(10);
        }
    }

    private static bool _isDisconnected(TcpClient client)
    {
        try
        {
            Socket s = client.Client;
            return s.Poll(10 * 1000, SelectMode.SelectRead) && (s.Available == 0);
        }
        catch (SocketException se)
        {
            // We got a socket error, assume it's disconnected
            return true;
        }
    }

    private void _cleanupNetworkResources()
    {
        _msgStream?.Close();
        _msgStream = null;
        _client.Close();
    }

    public static void Main(string[] args)
    {
        ChatClient client = new("70.34.200.185", 6969, "bob");
        client.Connect();
        client.SetupUI();






        /*
        Console.WriteLine("Enter a name to use:");

        string name = "";
        bool valid = false;
        do
        {
            string? input = Console.ReadLine();
            if (input != null && input != "")
            {
                valid = true;
                name = input;
            }
            else
            {
                Console.WriteLine("Please input a valid name and try again.");
            }
        } while (!valid);


        string host = "70.34.200.185";//args[0].Trim();
        int port = 6969;//int.Parse(args[1].Trim());
        ChatClient chatClient = new(host, port, name);
        chatClient.Connect();
        chatClient.SendMessages();
        */
    }
}

