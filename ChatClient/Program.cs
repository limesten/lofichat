using System.Net;
using System.Net.Sockets;
using System.Text;
using Terminal.Gui;

class ChatClient
{
    ListView ChatMessages = new();
    Label? ConnectionStatus;
    public List<string> Messages = new();
    private TcpClient? _client;
    public readonly int BufferSize = 2 * 1024; // 2kB
    public bool Running { get; private set; }
    private NetworkStream? _msgStream = null;

    public void Start()
    {
        Application.Init();

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
        };
        chatInput.ColorScheme = new Terminal.Gui.ColorScheme()
        {
            Normal = Terminal.Gui.Attribute.Make(Color.White, Color.Black),
            Focus = Terminal.Gui.Attribute.Make(Color.White, Color.Black)
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

        ConnectionStatus = new()
        {
            LayoutStyle = LayoutStyle.Computed,
            Y = 0,
            Height = 1,
            Text = "Status: Disconnected"
        };
        ConnectionStatus.X = Pos.AnchorEnd(ConnectionStatus.Text.Length);

        statusBar.Add(chatName, ConnectionStatus);

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

        ChatMessages.X = 0;
        ChatMessages.Y = 2;
        ChatMessages.Width = Dim.Fill();
        ChatMessages.Height = Dim.Fill(3);

        ChatMessages.ColorScheme = new Terminal.Gui.ColorScheme()
        {
            Normal = Terminal.Gui.Attribute.Make(Color.White, Color.Black),
            Focus = Terminal.Gui.Attribute.Make(Color.White, Color.Black)
        };

        Messages.Add("Welcome to LofiChat. Enter /help to view available commands.");
        ChatMessages.SetSource(Messages);

        chatInput.KeyDown += async (args) =>
        {
            if (args.KeyEvent.Key == Key.Enter && chatInput.Text != "")
            {
                string? input = chatInput.Text.ToString();
                chatInput.Text = "";

                if (input.StartsWith("/"))
                {
                    await _slashCommandHandler(input);
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
        Application.Top.Add(ChatMessages);
        Application.Run();
        Application.Shutdown();
    }
    public async Task Connect(string serverAddress, int port, string name)
    {
        _client = new TcpClient();
        _client.SendBufferSize = BufferSize;
        _client.ReceiveBufferSize = BufferSize;
        Running = false;

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

            if (!_isDisconnected(_client))
            {
                Running = true;
                Thread receiveThread = new(ReceiveMessages);
                receiveThread.Start();

                ConnectionStatus.Text = "Status: Connected";
                ConnectionStatus.X = Pos.AnchorEnd(ConnectionStatus.Text.Length);
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

    private async Task _slashCommandHandler(string command)
    {
        string[] commandParts = command.Split(" ");

        switch (commandParts[0])
        {
            case "/help":
                Messages.Add("/connect [IP_ADDRESS] [PORT] [USERNAME]");
                Messages.Add("/leave - Disconnect from the chat server");
                Messages.Add("CTRL + Q - Exits the program");
                ChatMessages.SetSource(Messages);
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
                        Messages.Add("/connect failed - Invalid port number");
                        ChatMessages.SetSource(Messages);
                        return;
                    }
                    await Connect(addressArg, port, nameArg);
                }
                else
                {
                    Messages.Add("/connect failed - Invalid amount of arguments");
                    ChatMessages.SetSource(Messages);
                }
                break;
            case "/leave":
                _cleanupNetworkResources();
                break;
            default:
                Messages.Add("Unknown command");
                ChatMessages.SetSource(Messages);
                break;
        }
    }

    public async Task SendMessage(string msg)
    {
        if (msg != string.Empty)
        {
            byte[] msgBuffer = Encoding.UTF8.GetBytes(msg);
            await _msgStream.WriteAsync(msgBuffer, 0, msgBuffer.Length);
        }
    }

    public void ReceiveMessages()
    {

        while (Running)
        {
            try
            {
                int messageLength = _client.Available;
                if (messageLength > 0)
                {
                    byte[] msgBuffer = new byte[messageLength];
                    _msgStream.Read(msgBuffer, 0, messageLength);

                    string msg = Encoding.UTF8.GetString(msgBuffer);
                    msg = msg.TrimEnd('\r', '\n');
                    Messages.Add(msg);

                    int chatMessagesHeight;
                    bool ok = ChatMessages.GetCurrentHeight(out chatMessagesHeight);
                    if (!ok)
                    {
                        throw new Exception("Failed to get chat message window height.");
                    }

                    Application.MainLoop.Invoke(() =>
                    {
                        ChatMessages.SetSource(Messages);
                        ChatMessages.ScrollDown(Messages.Count - chatMessagesHeight);
                    });
                }

                Thread.Sleep(100);
                if (_isDisconnected(_client))
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
        _cleanupNetworkResources();
    }

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

    private void _cleanupNetworkResources()
    {
        Running = false;
        _msgStream?.Close();
        _msgStream = null;
        _client.Close();
        ConnectionStatus.Text = "Status: Disconnected";
        ConnectionStatus.X = Pos.AnchorEnd(ConnectionStatus.Text.Length);
    }

    public static void Main(string[] args)
    {
        ChatClient client = new();
        client.Start();
    }
}

