using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ChatSharp
{
    using Resources;

    /// <summary>
    /// A collection of IRC channels a user is present in.
    /// </summary>
    public class ChannelCollection : IEnumerable<IrcChannel>
    {
        internal ChannelCollection()
        {
            this.Channels = new List<IrcChannel>();
        }

        internal ChannelCollection(IrcClient client) : this()
        {
            this.Client = client;
        }

        private IrcClient Client { get; set; }
        private List<IrcChannel> Channels { get; set; }

        internal void Add(IrcChannel channel)
        {
            if (this.Channels.Any(c => c.Name == channel.Name))
            {
                throw new InvalidOperationException(eng.That_channel_already_exists_in_this_collection);
            }
            this.Channels.Add(channel);
        }

        internal void Remove(IrcChannel channel)
        {
            this.Channels.Remove(channel);
        }

        /// <summary>
        /// Join the specified channel. Only applicable for your own user.
        /// </summary>
        public void Join(string name)
        {
            if (this.Client != null)
            {
                this.Client.JoinChannel(name);
            }
            else
            {
                throw new InvalidOperationException(eng.Cannot_make_other_users_join_channels);
            }
        }

        /// <summary>
        /// Returns true if the channel by the given name, including channel prefix (i.e. '#'), is in this collection.
        /// </summary>
        public bool Contains(string name)
        {
            return this.Channels.Any(c => c.Name == name);
        }

        /// <summary>
        /// Gets the channel at the given index.
        /// </summary>
        public IrcChannel this[int index] => this.Channels[index];

        /// <summary>
        /// Gets the channel by the given channel name, including channel prefix (i.e. '#')
        /// </summary>
        public IrcChannel this[string name]
        {
            get
            {
                var channel = this.Channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (channel == null)
                {
                    throw new KeyNotFoundException();
                }
                return channel;
            }
        }

        internal IrcChannel GetOrAdd(string name)
        {
            if (Contains(name))
            {
                return this[name];
            }
            var channel = new IrcChannel(this.Client, name);
            Add(channel);
            return channel;
        }

        /// <summary>
        /// Gets an for the channels in this collection.
        /// </summary>
        public IEnumerator<IrcChannel> GetEnumerator()
        {
            return this.Channels.GetEnumerator();
        }

        /// <summary>
        /// Gets an for the channels in this collection.
        /// </summary>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
