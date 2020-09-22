#region

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Disfigure.Cryptography;
using Disfigure.Diagnostics;
using Serilog;

#endregion

namespace Disfigure.Net
{
    public delegate ValueTask ConnectionEventHandler(Connection connection);

    public class Connection : IDisposable, IEquatable<Connection>
    {
        private const double _SERVER_TICKS_PER_SECOND = 20d;
        private static readonly TimeSpan _ServerTickRate = TimeSpan.FromSeconds(1d / _SERVER_TICKS_PER_SECOND);

        private readonly TcpClient _Client;
        private readonly NetworkStream _Stream;
        private readonly PipeWriter _Writer;
        private readonly PipeReader _Reader;
        private readonly EncryptionProvider _EncryptionProvider;
        private readonly Dictionary<PacketType, ManualResetEvent> _PacketResetEvents;

        public Guid Identity { get; }
        public string Name { get; }
        public bool IsOwnerServer { get; }

        public byte[] PublicKey => _EncryptionProvider.PublicKey;
        public EndPoint RemoteEndPoint => _Client.Client.RemoteEndPoint;

        public Connection(TcpClient client, bool isOwnerServer)
        {
            _Client = client;
            _Stream = _Client.GetStream();
            _Writer = PipeWriter.Create(_Stream);
            _Reader = PipeReader.Create(_Stream);
            _EncryptionProvider = new EncryptionProvider();

            Identity = Guid.NewGuid();
            Name = string.Empty;
            IsOwnerServer = isOwnerServer;
            _PacketResetEvents = new Dictionary<PacketType, ManualResetEvent>
            {
                { PacketType.EncryptionKeys, new ManualResetEvent(false) },
                { PacketType.BeginIdentity, new ManualResetEvent(false) },
                { PacketType.EndIdentity, new ManualResetEvent(false) },
            };
        }

        public async ValueTask Finalize(CancellationToken cancellationToken)
        {
            await OnConnected();

            await SendEncryptionKeys(IsOwnerServer, cancellationToken);

            BeginListen(cancellationToken);

            Log.Debug($"Waiting for {nameof(PacketType.EncryptionKeys)} packet from {RemoteEndPoint}.");
            WaitForPacket(PacketType.EncryptionKeys);

            Log.Debug($" <{RemoteEndPoint}> Connection finalized.");
        }

        public void WaitForPacket(PacketType packetType)
        {
            _PacketResetEvents[packetType].WaitOne();
        }


        #region Listening

        private void BeginListen(CancellationToken cancellationToken) =>
            Task.Run(() => BeginListenAsyncInternal(cancellationToken), cancellationToken);

        private async Task BeginListenAsyncInternal(CancellationToken cancellationToken)
        {
            try
            {
                await ReadLoopAsync(cancellationToken);
            }
            catch (IOException ex) when (ex.InnerException is SocketException)
            {
                Log.Debug(ex.ToString());
                Log.Warning($" <{RemoteEndPoint}> Connection forcibly closed.");
            }
            catch (Exception exception)
            {
                Log.Error(exception.ToString());
            }
            finally
            {
                await OnDisconnected();
            }
        }

        private async ValueTask ReadLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                Log.Debug($" <{RemoteEndPoint}> Beginning read loop.");

                Stopwatch stopwatch = new Stopwatch();

                while (!cancellationToken.IsCancellationRequested)
                {
                    ReadResult result = await _Reader.ReadAsync(cancellationToken);
                    ReadOnlySequence<byte> sequence = result.Buffer;

                    stopwatch.Restart();

                    if (!TryReadPacket(sequence, out SequencePosition consumed, out Packet packet))
                    {
                        continue;
                    }

                    stopwatch.Stop();
                    DiagnosticsProvider.CommitData<PacketDiagnosticGroup>(new ConstructionTime(stopwatch.Elapsed));

                    await OnPacketReceived(packet, cancellationToken, stopwatch);
                    _Reader.AdvanceTo(consumed, consumed);
                }
            }
            catch (Exception ex)
            {

            }
        }

        private static bool TryReadPacket(ReadOnlySequence<byte> sequence, [NotNull] out SequencePosition consumed,
            [NotNullWhen(true)] out Packet packet)
        {
            consumed = sequence.Start;
            packet = default;

            if (sequence.Length < sizeof(int))
            {
                return false;
            }

            int packetLength = BitConverter.ToInt32(sequence.Slice(0, sizeof(int)).FirstSpan);

            if (sequence.Length < packetLength)
            {
                return false;
            }
            else
            {
                consumed = sequence.GetPosition(packetLength);
                packet = new Packet(sequence, packetLength);

                return true;
            }
        }

        #endregion


        #region Writing Data

        public async ValueTask WriteAsync(PacketType type, DateTime timestamp, byte[] content, CancellationToken cancellationToken)
        {
            await WriteEncryptedAsync(new Packet(type, PublicKey, timestamp, content), cancellationToken);
            await _Writer.FlushAsync(cancellationToken);
        }

        public async ValueTask WriteAsync(IEnumerable<(PacketType, DateTime, byte[])> packets, CancellationToken cancellationToken)
        {
            foreach ((PacketType type, DateTime timestamp, byte[] content) in packets)
            {
                await WriteEncryptedAsync(new Packet(type, PublicKey, timestamp, content), cancellationToken);
            }

            await _Writer.FlushAsync(cancellationToken);
        }

        private async ValueTask WriteEncryptedAsync(Packet packet, CancellationToken cancellationToken)
        {
            packet.Content = await _EncryptionProvider.Encrypt(packet.Content, cancellationToken);
            byte[] serialized = packet.Serialize();

            await _Writer.WriteAsync(serialized, cancellationToken);

            Log.Verbose($" <{RemoteEndPoint}> OUT: {packet}");
        }

        private async ValueTask SendEncryptionKeys(bool server, CancellationToken cancellationToken)
        {
            Debug.Assert(!_EncryptionProvider.EncryptionNegotiated, "Protocol requires that key exchanges happen ONLY ONCE.");

            Log.Debug($" <{RemoteEndPoint}> Sending encryption keys.");

            Packet packet = new Packet(PacketType.EncryptionKeys, _EncryptionProvider.PublicKey, DateTime.UtcNow,
                server ? _EncryptionProvider.IV : Array.Empty<byte>());
            await _Stream.WriteAsync(packet.Serialize(), cancellationToken);
            await _Stream.FlushAsync(cancellationToken);
        }

        #endregion


        #region Connection Events

        public event ConnectionEventHandler? Connected;
        public event ConnectionEventHandler? Disconnected;

        private async ValueTask OnConnected()
        {
            if (Connected is { })
            {
                await Connected.Invoke(this);
            }
        }

        private async ValueTask OnDisconnected()
        {
            if (Disconnected is { })
            {
                await Disconnected.Invoke(this);
            }
        }

        #endregion


        #region Packet Events

        public event PacketEventHandler? PacketReceived;
        public event PacketEventHandler? TextPacketReceived;
        public event PacketEventHandler? BeginIdentityReceived;
        public event PacketEventHandler? ChannelIdentityReceived;
        public event PacketEventHandler? EndIdentityReceived;
        public event PacketEventHandler? PingReceived;
        public event PacketEventHandler? PongReceived;

        private async ValueTask OnPacketReceived(Packet packet, CancellationToken cancellationToken, Stopwatch stopwatch)
        {
            if (_PacketResetEvents.TryGetValue(packet.Type, out ManualResetEvent? resetEvent))
            {
                resetEvent.Set();
            }

            switch (packet.Type)
            {
                case PacketType.EncryptionKeys:
                    _EncryptionProvider.AssignRemoteKeys(packet.Content, packet.PublicKey);
                    break;
                default:
                    stopwatch.Restart();

                    packet.Content = await _EncryptionProvider.Decrypt(packet.PublicKey, packet.Content, cancellationToken);

                    stopwatch.Stop();
                    DiagnosticsProvider.CommitData<PacketDiagnosticGroup>(new DecryptionTime(stopwatch.Elapsed));

                    await InvokePacketTypeEvent(packet);
                    break;
            }

            if (PacketReceived is { })
            {
                await PacketReceived.Invoke(this, packet);
            }

            Log.Verbose($" <{RemoteEndPoint}> INC: {packet}");
        }

        private async ValueTask InvokePacketTypeEvent(Packet packet)
        {
            switch (packet.Type)
            {
                case PacketType.Ping when PingReceived is { }:
                    await PingReceived.Invoke(this, packet);
                    break;
                case PacketType.Pong when PongReceived is { }:
                    await PongReceived.Invoke(this, packet);
                    break;
                case PacketType.Text when TextPacketReceived is { }:
                    await TextPacketReceived.Invoke(this, packet);
                    break;
                case PacketType.BeginIdentity when BeginIdentityReceived is { }:
                    await BeginIdentityReceived.Invoke(this, packet);
                    break;
                case PacketType.EndIdentity when EndIdentityReceived is { }:
                    await EndIdentityReceived.Invoke(this, packet);
                    break;
                case PacketType.ChannelIdentity when ChannelIdentityReceived is { }:
                    await ChannelIdentityReceived.Invoke(this, packet);
                    break;
            }
        }

        #endregion


        #region IDisposable

        private bool _Disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            _Client.Dispose();
            _Stream.Dispose();

            _Disposed = true;
        }

        public void Dispose()
        {
            if (_Disposed)
            {
                return;
            }

            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion


        #region IEquatable<Connection>

        public bool Equals(Connection? other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return Identity.Equals(other.Identity);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (obj.GetType() != GetType())
            {
                return false;
            }

            return Equals((Connection)obj);
        }

        public override int GetHashCode() => Identity.GetHashCode();

        public static bool operator ==(Connection? left, Connection? right) => Equals(left, right);

        public static bool operator !=(Connection? left, Connection? right) => !Equals(left, right);

        #endregion
    }
}
