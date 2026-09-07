using System.Net;
using System.Net.Sockets;
using System.Text;
using GameHook.Domain;
using GameHook.Domain.Interface;
using GameHook.Infrastructure.Drivers;

namespace GameHook.Tests.Infrastructure;

public class RetroArchDriverTests
{
    private static async Task<(int Port, Task<string> ReceivedCommand)> StartFakeRetroArch(string reply)
    {
        var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;

        var receivedCommand = Task.Run(async () =>
        {
            using (server)
            {
                var result = await server.ReceiveAsync();
                var command = Encoding.ASCII.GetString(result.Buffer);
                var replyBytes = Encoding.ASCII.GetBytes(reply);
                await server.SendAsync(replyBytes, replyBytes.Length, result.RemoteEndPoint);
                return command;
            }
        });

        return (port, receivedCommand);
    }

    [TestCase("READ_CORE_MEMORY c000 01 02 03 04\n")]
    [TestCase("\tREAD_CORE_MEMORY  C000  1  02  03  04  \r\n")]
    public async Task Read_parses_successful_response_and_maps_region_to_real_address(string reply)
    {
        var (port, receivedCommand) = await StartFakeRetroArch(reply);
        using var driver = new RetroArchDriver($"127.0.0.1:{port}");

        var response = await driver.Read(new IDriver.Request(GameSystem.GB, [
            new IDriver.MemorySegmentRequest("WRAM", 0, 4)
        ]));

        Assert.Multiple(async () =>
        {
            Assert.That(response.Segments[0].Bytes.ToArray(), Is.EqualTo(new byte[] { 0x01, 0x02, 0x03, 0x04 }));
            Assert.That(await receivedCommand, Is.EqualTo("READ_CORE_MEMORY c000 4\n"));
        });
    }

    [TestCase("READ_CORE_MEMORY c000\n")]
    [TestCase("READ_CORE_MEMORY c000 gg\n")]
    [TestCase("READ_CORE_MEMORY c000 100\n")]
    public async Task Read_rejects_missing_or_invalid_payload(string reply)
    {
        var (port, receivedCommand) = await StartFakeRetroArch(reply);
        using var driver = new RetroArchDriver($"127.0.0.1:{port}");

        Assert.ThrowsAsync<InvalidDataException>(async () => await driver.Read(new IDriver.Request(
            GameSystem.GB, [new IDriver.MemorySegmentRequest("WRAM", 0, 1)])));
        await receivedCommand;
    }

    [Test]
    public async Task Read_offsets_address_by_the_requested_starting_offset()
    {
        var (port, receivedCommand) = await StartFakeRetroArch("READ_CORE_MEMORY c010 ff\n");
        using var driver = new RetroArchDriver($"127.0.0.1:{port}");

        await driver.Read(new IDriver.Request(GameSystem.GB, [
            new IDriver.MemorySegmentRequest("WRAM", 0x10, 1)
        ]));

        Assert.That(await receivedCommand, Is.EqualTo("READ_CORE_MEMORY c010 1\n"));
    }

    private static async Task<(int Port, Task<List<string>> ReceivedCommands)> StartFakeRetroArch(params string[] replies)
    {
        var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;

        var receivedCommands = Task.Run(async () =>
        {
            using (server)
            {
                var commands = new List<string>();
                foreach (var reply in replies)
                {
                    var result = await server.ReceiveAsync();
                    commands.Add(Encoding.ASCII.GetString(result.Buffer));
                    var replyBytes = Encoding.ASCII.GetBytes(reply);
                    await server.SendAsync(replyBytes, replyBytes.Length, result.RemoteEndPoint);
                }

                return commands;
            }
        });

        return (port, receivedCommands);
    }

    [Test]
    public async Task Read_resumes_from_where_a_truncated_response_left_off()
    {
        // Mirrors cores (e.g. Gambette) that expose a region as adjacent memory-map descriptors:
        // a read spanning both only gets what fits in the first one before the reply is cut short.
        var (port, receivedCommands) = await StartFakeRetroArch(
            "READ_CORE_MEMORY c000 01 02 03\n",
            "READ_CORE_MEMORY c003 04 05\n");
        using var driver = new RetroArchDriver($"127.0.0.1:{port}");

        var response = await driver.Read(new IDriver.Request(GameSystem.GB, [
            new IDriver.MemorySegmentRequest("WRAM", 0, 5)
        ]));

        Assert.Multiple(async () =>
        {
            Assert.That(response.Segments[0].Bytes.ToArray(), Is.EqualTo(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }));
            Assert.That(await receivedCommands, Is.EqualTo(new[]
            {
                "READ_CORE_MEMORY c000 5\n",
                "READ_CORE_MEMORY c003 2\n"
            }));
        });
    }

    [Test]
    public async Task Read_gba_ewram_in_bounded_chunks()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const int length = 0x40000;
        var expected = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
        var serve = Task.Run(async () =>
        {
            var offset = 0;
            while (offset < length)
            {
                var request = await server.ReceiveAsync(timeout.Token);
                var parts = Encoding.ASCII.GetString(request.Buffer).Trim().Split(' ');
                var address = Convert.ToUInt32(parts[1], 16);
                var count = int.Parse(parts[2]);
                Assert.That(address, Is.EqualTo(0x02000000u + (uint)offset));
                Assert.That(count, Is.InRange(1, 4096));
                var payload = string.Join(" ", expected.Skip(offset).Take(count).Select(b => b.ToString("x2")));
                var reply = Encoding.ASCII.GetBytes($"READ_CORE_MEMORY {address:x} {payload}\n");
                await server.SendAsync(reply, request.RemoteEndPoint, timeout.Token);
                offset += count;
            }
        });
        using var driver = new RetroArchDriver($"127.0.0.1:{port}");
        var response = await driver.Read(new IDriver.Request(GameSystem.GBA,
            [new IDriver.MemorySegmentRequest("EWRAM", 0, length)]));
        await serve;
        Assert.That(response.Segments.Single().Bytes.ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public async Task Read_omits_regions_the_driver_does_not_know_how_to_address()
    {
        var driver = new RetroArchDriver(null);

        var response = await driver.Read(new IDriver.Request(
            GameSystem.SNES,
            [new IDriver.MemorySegmentRequest("CGRAM", 0, 1)]));

        Assert.That(response.Segments, Is.Empty);
    }

    [Test]
    public async Task Read_omits_the_segment_when_retroarch_reports_an_error()
    {
        var (port, _) = await StartFakeRetroArch("READ_CORE_MEMORY c000 -1 no descriptor for address\n");
        using var driver = new RetroArchDriver($"127.0.0.1:{port}");

        var response = await driver.Read(new IDriver.Request(
            GameSystem.GB,
            [new IDriver.MemorySegmentRequest("WRAM", 0, 1)]));

        Assert.That(response.Segments, Is.Empty);
    }

    [Test]
    public void Read_times_out_when_server_does_not_reply()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var driver = new RetroArchDriver($"127.0.0.1:{port}");

        Assert.ThrowsAsync<TimeoutException>(async () => await driver.Read(new IDriver.Request(
            GameSystem.GB, [new IDriver.MemorySegmentRequest("WRAM", 0, 1)])));
    }

    [Test]
    public void Read_reports_network_failure_when_nothing_is_listening()
    {
        using var unusedSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var closedPort = ((IPEndPoint)unusedSocket.Client.LocalEndPoint!).Port;
        unusedSocket.Close();

        using var driver = new RetroArchDriver($"127.0.0.1:{closedPort}");

        Assert.That(async () => await driver.Read(new IDriver.Request(
            GameSystem.GB,
            [new IDriver.MemorySegmentRequest("WRAM", 0, 1)])),
            Throws.TypeOf<TimeoutException>().Or.TypeOf<SocketException>());
    }
}
