#region

using System.Net;
using Disfigure.CLI;
using Serilog.Events;
using Xunit;

#endregion

namespace Disfigure.Tests
{
    public class Base
    {
        [Theory]
        [InlineData("-l", "verbose", "127.0.0.1", "8998")]
        public void TestHostModuleOption(params string[] args)
        {
            const string local_host = "127.0.0.1";
            const int port = 8998;

            HostModuleOption hostModuleOption = CLIParser.Parse<HostModuleOption>(args);

            Assert.Equal(LogEventLevel.Verbose, hostModuleOption.LogLevel);
            Assert.Equal(local_host, hostModuleOption.IPAddressUnparsed);
            Assert.Equal(IPAddress.Parse(local_host), hostModuleOption.IPAddress);
            Assert.Equal(port, hostModuleOption.Port);
        }
    }
}
