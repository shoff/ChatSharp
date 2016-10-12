using ChatSharp.Events;
using ChatSharp.Handlers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Timers;

namespace ChatSharp
{
    /// <summary>
    /// An IRC client.
    /// </summary>
    public sealed partial class IrcClient
    {
        /// <summary>
        /// A raw IRC message handler.
        /// </summary>
        public delegate void MessageHandler(IrcClient client, IrcMessage message);
        private Dictionary<string, MessageHandler> Handlers { get; set; }

        /// <summary>
        /// Sets a custom handler for an IRC message. This applies to the low level IRC protocol,
        /// not for private messages.
        /// </summary>
        public void SetHandler(string message, MessageHandler handler)
        {
#if DEBUG
            // This is the default behavior if 3rd parties want to handle certain messages themselves
            // However, if it happens from our own code, we probably did something wrong
            if (this.Handlers.ContainsKey(message.ToUpper()))
            {
                Console.WriteLine("Warning: {0} handler has been overwritten", message);
            }
#endif
            message = message.ToUpper();
            this.Handlers[message] = handler;
        }

        internal static DateTime DateTimeFromIrcTime(int time)
        {
            return new DateTime(1970, 1, 1).AddSeconds(time);
        }

        private const int ReadBufferLength = 1024;

        private byte[] ReadBuffer { get; set; }
        private int ReadBufferIndex { get; set; }
        private string ServerHostname { get; set; }
        private int ServerPort { get; set; }
        private Timer PingTimer { get; set; }
        private Socket Socket { get; set; }
        private ConcurrentQueue<string> WriteQueue { get; set; }
        private bool IsWriting { get; set; }

        internal RequestManager RequestManager { get; set; }

        internal string ServerNameFromPing { get; set; }

        /// <summary>
        /// The address this client is connected to, or will connect to. Setting this
        /// after the client is connected will not cause a reconnect.
        /// </summary>
        public string ServerAddress
        {
            get
            {
                return this.ServerHostname + ":" + this.ServerPort;
            }
            internal set
            {
                string[] parts = value.Split(':');
                if (parts.Length > 2 || parts.Length == 0)
                {
                    throw new FormatException("Server address is not in correct format ('hostname:port')");
                }
                this.ServerHostname = parts[0];
                if (parts.Length > 1)
                {
                    this.ServerPort = int.Parse(parts[1]);
                }
                else
                {
                    this.ServerPort = 6667;
                }
            }
        }

        /// <summary>
        /// The low level TCP stream for this client.
        /// </summary>
        public Stream NetworkStream { get; set; }
        /// <summary>
        /// If true, SSL will be used to connect.
        /// </summary>
        public bool UseSSL { get; private set; }
        /// <summary>
        /// If true, invalid SSL certificates are ignored.
        /// </summary>
        public bool IgnoreInvalidSSL { get; set; }
        /// <summary>
        /// The character encoding to use for the connection. Defaults to UTF-8.
        /// </summary>
        /// <value>The encoding.</value>
        public Encoding Encoding { get; set; }
        /// <summary>
        /// The user this client is logged in as.
        /// </summary>
        /// <value>The user.</value>
        public IrcUser User { get; set; }
        /// <summary>
        /// The channels this user is joined to.
        /// </summary>
        public ChannelCollection Channels { get; private set; }
        /// <summary>
        /// Settings that control the behavior of ChatSharp.
        /// </summary>
        public ClientSettings Settings { get; set; }
        /// <summary>
        /// Information about the server we are connected to. Servers may not send us this information,
        /// but it's required for ChatSharp to function, so by default this is a guess. Handle
        /// IrcClient.ServerInfoRecieved if you'd like to know when it's populated with real information.
        /// </summary>
        public ServerInfo ServerInfo { get; set; }
        /// <summary>
        /// A string to prepend to all PRIVMSGs sent. Many IRC bots prefix their messages with \u200B, to
        /// indicate to other bots that you are a bot.
        /// </summary>
        public string PrivmsgPrefix { get; set; }
        /// <summary>
        /// A list of users on this network that we are aware of.
        /// </summary>
        public UserPool Users { get; set; }

        /// <summary>
        /// Creates a new IRC client, but will not connect until ConnectAsync is called.
        /// </summary>
        /// <param name="serverAddress">Server address including port in the form of "hostname:port".</param>
        /// <param name="user">The IRC user to connect as.</param>
        /// <param name="useSSL">Connect with SSL if true.</param>
        public IrcClient(string serverAddress, IrcUser user, bool useSSL = false)
        {
            if (serverAddress == null)
            {
                throw new ArgumentNullException("serverAddress");
            }
            if (user == null)
            {
                throw new ArgumentNullException("user");
            }

            this.User = user;
            this.ServerAddress = serverAddress;
            this.Encoding = Encoding.UTF8;
            this.Channels = new ChannelCollection(this);
            this.Settings = new ClientSettings();
            this.Handlers = new Dictionary<string, MessageHandler>();
            MessageHandlers.RegisterDefaultHandlers(this);
            this.RequestManager = new RequestManager();
            this.UseSSL = useSSL;
            this.WriteQueue = new ConcurrentQueue<string>();
            this.ServerInfo = new ServerInfo();
            this.PrivmsgPrefix = "";
            this.Users = new UserPool();
            this.Users.Add(this.User); // Add self to user pool
        }

        /// <summary>
        /// Connects to the IRC server.
        /// </summary>
        public void ConnectAsync()
        {
            if (this.Socket != null && this.Socket.Connected)
            {
                throw new InvalidOperationException("Socket is already connected to server.");
            }
            this.Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            this.ReadBuffer = new byte[ReadBufferLength];
            this.ReadBufferIndex = 0;
            this.PingTimer = new Timer(30000);
            this.PingTimer.Elapsed += (sender, e) => 
            {
                if (!string.IsNullOrEmpty(this.ServerNameFromPing))
                {
                    SendRawMessage("PING :{0}", this.ServerNameFromPing);
                }
            };
            var checkQueue = new Timer(1000);
            checkQueue.Elapsed += (sender, e) =>
            {
                string nextMessage;
                if (this.WriteQueue.Count > 0)
                {
                    while (!this.WriteQueue.TryDequeue(out nextMessage))
                    {
                        ;
                    }
                    SendRawMessage(nextMessage);
                }
            };
            checkQueue.Start();
            this.Socket.BeginConnect(this.ServerHostname, this.ServerPort, ConnectComplete, null);
        }

        /// <summary>
        /// Send a QUIT message and disconnect.
        /// </summary>
        public void Quit()
        {
            Quit(null);
        }

        /// <summary>
        /// Send a QUIT message with a reason and disconnect.
        /// </summary>
        public void Quit(string reason)
        {
            if (reason == null)
            {
                SendRawMessage("QUIT");
            }
            else
            {
                SendRawMessage("QUIT :{0}", reason);
            }
            this.Socket.BeginDisconnect(false, ar =>
            {
                this.Socket.EndDisconnect(ar);
                this.NetworkStream.Dispose();
                this.NetworkStream = null;
            }, null);
            this.PingTimer.Dispose();
        }

        private void ConnectComplete(IAsyncResult result)
        {
            this.Socket.EndConnect(result);

            this.NetworkStream = new NetworkStream(this.Socket);
            if (this.UseSSL)
            {
                if (this.IgnoreInvalidSSL)
                {
                    this.NetworkStream = new SslStream(this.NetworkStream, false, (sender, certificate, chain, policyErrors) => true);
                }
                else
                {
                    this.NetworkStream = new SslStream(this.NetworkStream);
                }
                ((SslStream) this.NetworkStream).AuthenticateAsClient(this.ServerHostname);
            }

            this.NetworkStream.BeginRead(this.ReadBuffer, this.ReadBufferIndex, this.ReadBuffer.Length, DataRecieved, null);
            // Write login info
            if (!string.IsNullOrEmpty(this.User.Password))
            {
                SendRawMessage("PASS {0}", this.User.Password);
            }
            SendRawMessage("NICK {0}", this.User.Nick);
            // hostname, servername are ignored by most IRC servers
            SendRawMessage("USER {0} hostname servername :{1}", this.User.User, this.User.RealName);
            this.PingTimer.Start();
        }

        private void DataRecieved(IAsyncResult result)
        {
            if (this.NetworkStream == null)
            {
                OnNetworkError(new SocketErrorEventArgs(SocketError.NotConnected));
                return;
            }

            int length;
            try
            {
                length = this.NetworkStream.EndRead(result) + this.ReadBufferIndex;
            }
            catch (IOException e)
            {
                var socketException = e.InnerException as SocketException;
                if (socketException != null)
                {
                    OnNetworkError(new SocketErrorEventArgs(socketException.SocketErrorCode));
                }
                else
                {
                    throw;
                }
                return;
            }

            this.ReadBufferIndex = 0;
            while (length > 0)
            {
                int messageLength = Array.IndexOf(this.ReadBuffer, (byte)'\n', 0, length);
                if (messageLength == -1) // Incomplete message
                {
                    this.ReadBufferIndex = length;
                    break;
                }
                messageLength++;
                var message = this.Encoding.GetString(this.ReadBuffer, 0, messageLength - 2); // -2 to remove \r\n
                HandleMessage(message);
                Array.Copy(this.ReadBuffer, messageLength, this.ReadBuffer, 0, length - messageLength);
                length -= messageLength;
            }
            this.NetworkStream.BeginRead(this.ReadBuffer, this.ReadBufferIndex, this.ReadBuffer.Length - this.ReadBufferIndex, DataRecieved, null);
        }

        private void HandleMessage(string rawMessage)
        {
            OnRawMessageRecieved(new RawMessageEventArgs(rawMessage, false));
            var message = new IrcMessage(rawMessage);
            if (this.Handlers.ContainsKey(message.Command.ToUpper()))
            {
                this.Handlers[message.Command.ToUpper()](this, message);
            }
            else
            {
                // TODO: Fire an event or something
            }
        }

        /// <summary>
        /// Send a raw IRC message. Behaves like /quote in most IRC clients.
        /// </summary>
        public void SendRawMessage(string message, params object[] format)
        {
            if (this.NetworkStream == null)
            {
                OnNetworkError(new SocketErrorEventArgs(SocketError.NotConnected));
                return;
            }

            message = string.Format(message, format);
            var data = this.Encoding.GetBytes(message + "\r\n");

            if (!this.IsWriting)
            {
                this.IsWriting = true;
                this.NetworkStream.BeginWrite(data, 0, data.Length, MessageSent, message);
            }
            else
            {
                this.WriteQueue.Enqueue(message);
            }
        }

        /// <summary>
        /// Send a raw IRC message. Behaves like /quote in most IRC clients.
        /// </summary>
        public void SendIrcMessage(IrcMessage message)
        {
            SendRawMessage(message.RawMessage);
        }

        private void MessageSent(IAsyncResult result)
        {
            if (this.NetworkStream == null)
            {
                OnNetworkError(new SocketErrorEventArgs(SocketError.NotConnected));
                this.IsWriting = false;
                return;
            }

            try
            {
                this.NetworkStream.EndWrite(result);
            }
            catch (IOException e)
            {
                var socketException = e.InnerException as SocketException;
                if (socketException != null)
                {
                    OnNetworkError(new SocketErrorEventArgs(socketException.SocketErrorCode));
                }
                else
                {
                    throw;
                }
                return;
            }
            finally
            {
                this.IsWriting = false;
            }

            OnRawMessageSent(new RawMessageEventArgs((string)result.AsyncState, true));

            string nextMessage;
            if (this.WriteQueue.Count > 0)
            {
                while (!this.WriteQueue.TryDequeue(out nextMessage))
                {
                    ;
                }
                SendRawMessage(nextMessage);
            }
        }

        /// <summary>
        /// Raised for socket errors. ChatSharp does not automatically reconnect.
        /// </summary>
        public event EventHandler<SocketErrorEventArgs> NetworkError;
        internal void OnNetworkError(SocketErrorEventArgs e)
        {
            if (this.NetworkError != null)
            {
                this.NetworkError(this, e);
            }
        }
        /// <summary>
        /// Occurs when a raw message is sent.
        /// </summary>
        public event EventHandler<RawMessageEventArgs> RawMessageSent;
        internal void OnRawMessageSent(RawMessageEventArgs e)
        {
            if (this.RawMessageSent != null)
            {
                this.RawMessageSent(this, e);
            }
        }
        /// <summary>
        /// Occurs when a raw message recieved.
        /// </summary>
        public event EventHandler<RawMessageEventArgs> RawMessageRecieved;
        internal void OnRawMessageRecieved(RawMessageEventArgs e)
        {
            if (this.RawMessageRecieved != null)
            {
                this.RawMessageRecieved(this, e);
            }
        }
        /// <summary>
        /// Occurs when a notice recieved.
        /// </summary>
        public event EventHandler<IrcNoticeEventArgs> NoticeRecieved;
        internal void OnNoticeRecieved(IrcNoticeEventArgs e)
        {
            if (this.NoticeRecieved != null)
            {
                this.NoticeRecieved(this, e);
            }
        }
        /// <summary>
        /// Occurs when the server has sent us part of the MOTD.
        /// </summary>
        public event EventHandler<ServerMOTDEventArgs> MOTDPartRecieved;
        internal void OnMOTDPartRecieved(ServerMOTDEventArgs e)
        {
            if (this.MOTDPartRecieved != null)
            {
                this.MOTDPartRecieved(this, e);
            }
        }
        /// <summary>
        /// Occurs when the entire server MOTD has been recieved.
        /// </summary>
        public event EventHandler<ServerMOTDEventArgs> MOTDRecieved;
        internal void OnMOTDRecieved(ServerMOTDEventArgs e)
        {
            if (this.MOTDRecieved != null)
            {
                this.MOTDRecieved(this, e);
            }
        }
        /// <summary>
        /// Occurs when a private message recieved. This can be a channel OR a user message.
        /// </summary>
        public event EventHandler<PrivateMessageEventArgs> PrivateMessageRecieved;
        internal void OnPrivateMessageRecieved(PrivateMessageEventArgs e)
        {
            if (this.PrivateMessageRecieved != null)
            {
                this.PrivateMessageRecieved(this, e);
            }
        }
        /// <summary>
        /// Occurs when a message is recieved in an IRC channel.
        /// </summary>
        public event EventHandler<PrivateMessageEventArgs> ChannelMessageRecieved;
        internal void OnChannelMessageRecieved(PrivateMessageEventArgs e)
        {
            if (this.ChannelMessageRecieved != null)
            {
                this.ChannelMessageRecieved(this, e);
            }
        }
        /// <summary>
        /// Occurs when a message is recieved from a user.
        /// </summary>
        public event EventHandler<PrivateMessageEventArgs> UserMessageRecieved;
        internal void OnUserMessageRecieved(PrivateMessageEventArgs e)
        {
            if (this.UserMessageRecieved != null)
            {
                this.UserMessageRecieved(this, e);
            }
        }
        /// <summary>
        /// Raised if the nick you've chosen is in use. By default, ChatSharp will pick a
        /// random nick to use instead. Set ErronousNickEventArgs.DoNotHandle to prevent this.
        /// </summary>
        public event EventHandler<ErronousNickEventArgs> NickInUse;
        internal void OnNickInUse(ErronousNickEventArgs e)
        {
            if (this.NickInUse != null)
            {
                this.NickInUse(this, e);
            }
        }
        /// <summary>
        /// Occurs when a user or channel mode is changed.
        /// </summary>
        public event EventHandler<ModeChangeEventArgs> ModeChanged;
        internal void OnModeChanged(ModeChangeEventArgs e)
        {
            if (this.ModeChanged != null)
            {
                this.ModeChanged(this, e);
            }
        }
        /// <summary>
        /// Occurs when a user joins a channel.
        /// </summary>
        public event EventHandler<ChannelUserEventArgs> UserJoinedChannel;
        internal void OnUserJoinedChannel(ChannelUserEventArgs e)
        {
            if (this.UserJoinedChannel != null)
            {
                this.UserJoinedChannel(this, e);
            }
        }
        /// <summary>
        /// Occurs when a user parts a channel.
        /// </summary>
        public event EventHandler<ChannelUserEventArgs> UserPartedChannel;
        internal void OnUserPartedChannel(ChannelUserEventArgs e)
        {
            if (this.UserPartedChannel != null)
            {
                this.UserPartedChannel(this, e);
            }
        }
        /// <summary>
        /// Occurs when we have received the list of users present in a channel.
        /// </summary>
        public event EventHandler<ChannelEventArgs> ChannelListRecieved;
        internal void OnChannelListRecieved(ChannelEventArgs e)
        {
            if (this.ChannelListRecieved != null)
            {
                this.ChannelListRecieved(this, e);
            }
        }
        /// <summary>
        /// Occurs when we have received the topic of a channel.
        /// </summary>
        public event EventHandler<ChannelTopicEventArgs> ChannelTopicReceived;
        internal void OnChannelTopicReceived(ChannelTopicEventArgs e)
        {
            if (this.ChannelTopicReceived != null)
            {
                this.ChannelTopicReceived(this, e);
            }
        }
        /// <summary>
        /// Occurs when the IRC connection is established and it is safe to begin interacting with the server.
        /// </summary>
        public event EventHandler<EventArgs> ConnectionComplete;
        internal void OnConnectionComplete(EventArgs e)
        {
            if (this.ConnectionComplete != null)
            {
                this.ConnectionComplete(this, e);
            }
        }
        /// <summary>
        /// Occurs when we receive server info (such as max nick length).
        /// </summary>
        public event EventHandler<SupportsEventArgs> ServerInfoRecieved;
        internal void OnServerInfoRecieved(SupportsEventArgs e)
        {
            if (this.ServerInfoRecieved != null)
            {
                this.ServerInfoRecieved(this, e);
            }
        }
        /// <summary>
        /// Occurs when a user is kicked.
        /// </summary>
        public event EventHandler<KickEventArgs> UserKicked;
        internal void OnUserKicked(KickEventArgs e)
        {
            if (this.UserKicked != null)
            {
                this.UserKicked(this, e);
            }
        }
        /// <summary>
        /// Occurs when a WHOIS response is received.
        /// </summary>
        public event EventHandler<WhoIsReceivedEventArgs> WhoIsReceived;
        internal void OnWhoIsReceived(WhoIsReceivedEventArgs e)
        {
            if (this.WhoIsReceived != null)
            {
                this.WhoIsReceived(this, e);
            }
        }
        /// <summary>
        /// Occurs when a user has changed their nick.
        /// </summary>
        public event EventHandler<NickChangedEventArgs> NickChanged;
        internal void OnNickChanged(NickChangedEventArgs e)
        {
            if (this.NickChanged != null)
            {
                this.NickChanged(this, e);
            }
        }
        /// <summary>
        /// Occurs when a user has quit.
        /// </summary>
        public event EventHandler<UserEventArgs> UserQuit;
        internal void OnUserQuit(UserEventArgs e)
        {
            if (this.UserQuit != null)
            {
                this.UserQuit(this, e);
            }
        }
    }
}
