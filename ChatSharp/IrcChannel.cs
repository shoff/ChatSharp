namespace ChatSharp
{
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    ///     An IRC channel.
    /// </summary>
    public class IrcChannel
    {
        internal string topic;

        internal IrcChannel(IrcClient client, string name)
        {
            this.Client = client;
            this.Name = name;
            this.Users = new UserPoolView(client.Users.Where(u => u.Channels.Contains(this)));
        }

        private IrcClient Client { get; }

        /// <summary>
        ///     The channel topic. Will send a TOPIC command if set.
        /// </summary>
        public string Topic
        {
            get { return this.topic; }
            set
            {
                this.Client.SetTopic(this.Name, value);
                this.topic = value;
            }
        }

        /// <summary>
        ///     The name, including the prefix (i.e. #), of this channel.
        /// </summary>
        /// <value>The name.</value>
        public string Name { get; internal set; }

        /// <summary>
        ///     The channel mode. May be null if we have not received the mode yet.
        /// </summary>
        public string Mode { get; internal set; }

        /// <summary>
        ///     The users in this channel.
        /// </summary>
        public UserPoolView Users { get; private set; }

        /// <summary>
        ///     Users in this channel, grouped by mode. Users with no special mode are grouped under null.
        /// </summary>
        public Dictionary<char?, UserPoolView> UsersByMode { get; set; }

        /// <summary>
        ///     Invites a user to this channel.
        /// </summary>
        public void Invite(string nick)
        {
            this.Client.InviteUser(this.Name, nick);
        }

        /// <summary>
        ///     Kicks a user from this channel.
        /// </summary>
        public void Kick(string nick)
        {
            this.Client.KickUser(this.Name, nick);
        }

        /// <summary>
        ///     Kicks a user from this channel, giving a reason for the kick.
        /// </summary>
        public void Kick(string nick, string reason)
        {
            this.Client.KickUser(this.Name, nick, reason);
        }

        /// <summary>
        ///     Parts this channel.
        /// </summary>
        public void Part()
        {
            this.Client.PartChannel(this.Name);
        }

        /// <summary>
        ///     Parts this channel, giving a reason for your departure.
        /// </summary>
        public void Part(string reason)
        {
            this.Client.PartChannel(this.Name); // TODO
        }

        /// <summary>
        ///     Sends a PRIVMSG to this channel.
        /// </summary>
        public void SendMessage(string message)
        {
            this.Client.SendMessage(message, this.Name);
        }

        /// <summary>
        ///     Set the channel mode.
        /// </summary>
        public void ChangeMode(string change)
        {
            this.Client.ChangeMode(this.Name, change);
        }
    }
}