#region

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Disfigure.Net;
using Serilog;

#endregion

namespace Disfigure.Client
{
    public class Client : IDisposable
    {
        private readonly CancellationToken _CancellationToken;

        private readonly CancellationTokenSource _CancellationTokenSource;
        private readonly Dictionary<Guid, Channel> _Channels;
        private readonly Dictionary<Guid, Connection> _ServerConnections;

        public IEnumerable<Connection> ServerConnections => _ServerConnections.Values;
        public IEnumerable<Channel> Channels => _Channels.Values;

        public Client()
        {
            // todo perhaps use a server descriptor or something
            _ServerConnections = new Dictionary<Guid, Connection>();
            _Channels = new Dictionary<Guid, Channel>();

            _CancellationTokenSource = new CancellationTokenSource();
            _CancellationToken = _CancellationTokenSource.Token;
        }

        public async ValueTask<Connection> EstablishConnection(IPEndPoint ipEndPoint, TimeSpan retryDelay)
        {
            const int maximum_retries = 5;

            TcpClient tcpClient = new TcpClient();
            int tries = 0;

            Log.Debug($"Attempting to establish connection to {ipEndPoint}.");

            while (!tcpClient.Connected)
            {
                try
                {
                    await tcpClient.ConnectAsync(ipEndPoint.Address, ipEndPoint.Port);
                }
                catch (SocketException) when (tries >= maximum_retries)
                {
                    Log.Error($"Failed to establish connection to {ipEndPoint}.");
                    throw;
                }
                catch (SocketException exception)
                {
                    tries += 1;

                    Log.Warning($"{exception.Message}. Retrying ({tries}/{maximum_retries})...");

                    await Task.Delay(retryDelay, _CancellationToken);
                }
                catch (Exception exception)
                {

                }
            }

            Guid guid = Guid.NewGuid();

            Log.Debug($"Established connection to {ipEndPoint} with auto-generated GUID {guid}.");

            return FinalizeConnection(guid, tcpClient);
        }

        private Connection FinalizeConnection(Guid guid, TcpClient tcpClient)
        {
            Connection connection = new Connection(guid, tcpClient);
            connection.ChannelIdentityReceived += OnChannelIdentityReceived;
            connection.TextPacketReceived += OnTextPacketReceived;
            _ServerConnections.Add(connection.Guid, connection);

            Log.Debug($"Connection {connection.Guid} finalized.");

            connection.BeginListen(_CancellationToken);
            return connection;
        }

        public static ValueTask WaitOnCompleteIdentity(Connection connection)
        {
            ManualResetEvent manualResetEvent = new ManualResetEvent(connection.CompleteRemoteIdentity);

            ValueTask ManualReset(Connection connectionInternal, Packet packetInternal)
            {
                manualResetEvent.Set();
                return default;
            }

            connection.EndIdentityReceived += ManualReset;
            manualResetEvent.WaitOne();
            connection.EndIdentityReceived -= ManualReset;

            Log.Debug("Server identity information received. Client may now operate freely.");

            return default;
        }

        #region Events

        private unsafe ValueTask OnChannelIdentityReceived(Connection connection, Packet packet)
        {
            Guid guid = new Guid(packet.Content[..sizeof(Guid)]);
            string name = Encoding.Unicode.GetString(packet.Content[sizeof(Guid)..]);

            Channel channel = new Channel(guid, name);
            _Channels.Add(channel.Guid, channel);

            Log.Debug($"Received identity information for channel: #{channel.Name} ({channel.Guid})");
            return default;
        }

        private static ValueTask OnTextPacketReceived(Connection connection, Packet packet)
        {
            Log.Verbose(packet.ToString());
            return default;
        }

        #endregion

        #region Dispose

        private bool _Disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (_Disposed)
            {
                return;
            }

            if (disposing)
            {
                foreach (Connection connection in _ServerConnections.Values)
                {
                    connection?.Dispose();
                }
            }

            _CancellationTokenSource.Cancel();
            _Disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}