﻿#region

using System;
using System.Net;
using System.Threading.Tasks;
using Disfigure.Cryptography;
using Disfigure.Diagnostics;
using Disfigure.Implementations;
using Disfigure.Net;
using Disfigure.Net.Packets;
using Serilog;

#endregion

namespace Disfigure.CLI.Server
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            try
            {
                HostModuleOption hostModuleOption = CLIParser.Parse<HostModuleOption>(args);

                Log.Logger = new LoggerConfiguration().WriteTo.Console().MinimumLevel.Is(hostModuleOption.LogLevel).CreateLogger();

                DiagnosticsProvider.EnableGroup<PacketDiagnosticGroup>();

                using ServerModule module = new ServerModule(new IPEndPoint(hostModuleOption.IPAddress, hostModuleOption.Port));
                module.Connected += Packet.SendEncryptionKeys;
                module.PacketReceived += PacketReceivedCallback;

                module.AcceptConnections(Packet.SerializerAsync, Packet.FactoryAsync);
                Packet.PingPongLoop(module, TimeSpan.FromSeconds(5d), module.CancellationToken);

                while (!module.CancellationToken.IsCancellationRequested)
                {
                    Console.ReadKey();
                }
            }
            finally
            {
                Log.Information("Press any key to exit.");
                Console.ReadKey();
            }
        }

        private static Task PacketReceivedCallback(Connection<Packet> connection, Packet packet)
        {
            switch (packet.Type)
            {
                case PacketType.EncryptionKeys:
                    connection.EncryptionProviderAs<ECDHEncryptionProvider>().AssignRemoteKeys(packet.Content);
                    break;
            }

            return Task.CompletedTask;
        }
    }
}
