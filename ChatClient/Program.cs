using System.Net;
using System.Net.Sockets;
using System.Text;
using Terminal.Gui;

class ChatClient
{
    private ListView _chatMessages;
    private Label _connectionStatus;
    private List<string> _messages = new();
    private TcpClient _client;
    public readonly int BufferSize = 2 * 1024; // 2kB
    public bool Running { get; private set; }
    private NetworkStream? _msgStream = null;

    public ChatClient()
    {
        _client = new TcpClient()
        {
            SendBufferSize = BufferSize,
            ReceiveBufferSize = BufferSize,
        };
        Running = false;

        Application.Init();

        Terminal.Gui.ColorScheme blackAndWhite = new Terminal.Gui.ColorScheme()
        {
            Normal = Terminal.Gui.Attribute.Make(Color.White, Color.Black),
            Focus = Terminal.Gui.Attribute.Make(Color.White, Color.Black)
        };

        Label chatInputSymbol = new()
        {
            X = 0,
            Y = Pos.Bottom(Application.Top) - 1,
            Width = 2,
            Height = 1,
            Text = "> "
        };

        TextField chatInput = new("")
        {
            X = 2,
            Y = Pos.Bottom(Application.Top) - 1,
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = blackAndWhite
        };

        View statusBar = new()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
        };

        Label chatName = new()
        {
            LayoutStyle = LayoutStyle.Computed,
            X = 0,
            Y = 0,
            Height = 1,
            Text = "LofiChat"
        };

        _connectionStatus = new()
        {
            LayoutStyle = LayoutStyle.Computed,
            Y = 0,
            Height = 1,
            Text = "Status: Disconnected"
        };
        _connectionStatus.X = Pos.AnchorEnd(_connectionStatus.Text.Length);

        statusBar.Add(chatName, _connectionStatus);

        ProgressBar upperDivider = new()
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = 1
        };

        ProgressBar lowerDivider = new()
        {
            X = 0,
            Y = Pos.Bottom(Application.Top) - 3,
            Width = Dim.Fill(),
            Height = 1
        };

        _chatMessages = new()
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(3),
            ColorScheme = blackAndWhite,
        };

        _messages.Add("Welcome to LofiChat. Enter /help to view available commands.");
        _chatMessages.SetSource(_messages);

        chatInput.KeyDown += async (args) =>
        {
            if (args.KeyEvent.Key == Key.Enter && chatInput.Text != "")
            {
                string? input = chatInput.Text.ToString();
                chatInput.Text = "";

                if (input == null)
                {
                    return;
                }

                if (input.StartsWith("/"))
                {
                    SlashCommandHandler(input);
                }
                else if (_client != null && _client.Connected)
                {
                    await SendMessage(input);
                }
            }
        };

        Application.Top.Add(chatInputSymbol);
        Application.Top.Add(chatInput);
        Application.Top.Add(statusBar);
        Application.Top.Add(upperDivider);
        Application.Top.Add(lowerDivider);
        Application.Top.Add(_chatMessages);
    }

    public void Start()
    {
        Application.Run(); // blocks
        Application.Shutdown(); // cleanup after shutdown
    }

    private void Connect(string serverAddress, int port, string name)
    {
        _client.Connect(serverAddress, port);
        EndPoint? endPoint = _client.Client.RemoteEndPoint;
        if (endPoint == null)
        {
            return;
        }

        if (_client.Connected)
        {
            _msgStream = _client.GetStream();

            byte[] msgBuffer = Encoding.UTF8.GetBytes($"name:{name}");
            _msgStream.Write(msgBuffer, 0, msgBuffer.Length);

            if (!IsDisconnected(_client))
            {
                Running = true;
                Thread receiveThread = new(ReceiveMessages);
                receiveThread.Start();
                _connectionStatus.Text = "Status: Connected";
                _connectionStatus.X = Pos.AnchorEnd(_connectionStatus.Text.Length);
            }
            else
            {
                AddMessage("We got disconnected by the client :(");
                CleanupNetworkResources();
            }
        }
        else
        {
            CleanupNetworkResources();
            AddMessage($"Failed to connect to the server at {endPoint}");
        }
    }

    private void SlashCommandHandler(string command)
    {
        string[] commandParts = command.Split(" ");

        switch (commandParts[0])
        {
            case "/help":
                _messages.Add("/connect [IP_ADDRESS] [PORT] [USERNAME]");
                _messages.Add("/leave - Disconnect from the chat server");
                _messages.Add("CTRL + Q - Exits the program");
                _chatMessages.SetSource(_messages);
                break;
            case "/connect":
                if (commandParts.Length == 4)
                {
                    string addressArg = commandParts[1];
                    string portArg = commandParts[2];
                    string nameArg = commandParts[3];

                    int port;
                    bool success = int.TryParse(portArg, out port);
                    if (!success)
                    {
                        _messages.Add("/connect failed - Invalid port number");
                        _chatMessages.SetSource(_messages);
                        return;
                    }
                    Connect(addressArg, port, nameArg);
                }
                else
                {
                    _messages.Add("/connect failed - Invalid amount of arguments");
                    _chatMessages.SetSource(_messages);
                }
                break;
            case "/leave":
                CleanupNetworkResources();
                break;
            default:
                _messages.Add("Unknown command");
                _chatMessages.SetSource(_messages);
                break;
        }
    }

    private async Task SendMessage(string msg)
    {
        if (msg != string.Empty)
        {
            byte[] msgBuffer = Encoding.UTF8.GetBytes(msg);
            if (_msgStream == null)
            {
                throw new NullReferenceException();
            }
            await _msgStream.WriteAsync(msgBuffer, 0, msgBuffer.Length);
        }
    }

    private void ReceiveMessages()
    {
        while (Running)
        {
            try
            {
                int messageLength = _client.Available;
                if (messageLength > 0)
                {
                    byte[] msgBuffer = new byte[messageLength];
                    if (_msgStream == null)
                    {
                        throw new NullReferenceException();
                    }
                    _msgStream.Read(msgBuffer, 0, messageLength);

                    string msg = Encoding.UTF8.GetString(msgBuffer);
                    msg = msg.TrimEnd('\r', '\n');

                    Application.MainLoop.Invoke(() =>
                    {
                        AddMessage(msg);
                    });
                }

                Thread.Sleep(100);
                if (IsDisconnected(_client))
                {
                    Running = false;
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return;
            }
        }
        CleanupNetworkResources();
    }

    private static bool IsDisconnected(TcpClient client)
    {
        if (client.Client == null) return true;
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
    private void CleanupNetworkResources()
    {
        Running = false;
        _msgStream?.Close();
        _msgStream = null;
        _client.Close();
        _connectionStatus.Text = "Status: Disconnected";
        _connectionStatus.X = Pos.AnchorEnd(_connectionStatus.Text.Length);
    }
    private void AddMessage(string message)
    {
        _messages.Add(message);
        int chatMessagesHeight;
        bool ok = _chatMessages.GetCurrentHeight(out chatMessagesHeight);
        _chatMessages.SetSource(_messages);
        if (ok) _chatMessages.ScrollDown(_messages.Count - chatMessagesHeight);
    }
}

class Program
{
    public static void Main(string[] args)
    {
        ChatClient client = new();
        client.Start();
    }
}