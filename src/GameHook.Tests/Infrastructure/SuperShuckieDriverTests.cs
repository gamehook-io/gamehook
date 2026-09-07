using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameHook.Domain;
using GameHook.Domain.Interface;
using GameHook.Infrastructure.Drivers;

namespace GameHook.Tests.Infrastructure;

[Platform("Linux")]
public class SuperShuckieDriverTests
{
    private const int HeaderSize = 32;
    private const string SharedMemoryPath = "/dev/shm/EDPS_MemoryData.bin";

    // Fakes the Super Shuckie side of the Poke-A-Byte protocol: acks the Setup packet the driver
    // sends, then lets the test poke bytes directly into the shared memory file the driver reads.
    private sealed class FakeSuperShuckie : IDisposable
    {
        private readonly UdpClient server;
        public int Port { get; }
        public Task<(uint FileOffset, uint GameAddress, uint Length)[]> Setup { get; }

        public FakeSuperShuckie()
        {
            server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
            Setup = Task.Run(async () =>
            {
                var result = await server.ReceiveAsync();
                var blockCount = BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(8, 4));
                var blocks = new (uint, uint, uint)[blockCount];
                for (var i = 0; i < blockCount; i++)
                {
                    var offset = HeaderSize + i * 12;
                    blocks[i] = (
                        BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(offset, 4)),
                        BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(offset + 4, 4)),
                        BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(offset + 8, 4)));
                }

                var ack = new byte[HeaderSize];
                ack[0] = 1;
                ack[4] = 2; // Setup
                ack[5] = 1; // is_response
                await server.SendAsync(ack, ack.Length, result.RemoteEndPoint);

                return blocks;
            });
        }

        public void Dispose() => server.Dispose();
    }

    // Poke-A-Byte's shared memory is a single well-known file, so tests serialize on it to avoid
    // clobbering each other when the test runner parallelizes fixtures.
    private static readonly SemaphoreSlim SharedMemorySerializer = new(1, 1);

    private static async Task<T> WithSharedMemoryFile<T>(byte[] contents, Func<Task<T>> body)
    {
        await SharedMemorySerializer.WaitAsync();
        var created = false;
        try
        {
            if (File.Exists(SharedMemoryPath)) Assert.Ignore("An existing emulator shared-memory file must not be overwritten.");
            await using (var stream = new FileStream(SharedMemoryPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
            {
                created = true;
                await stream.WriteAsync(contents);
            }
            return await body();
        }
        finally
        {
            if (created) File.Delete(SharedMemoryPath);
            SharedMemorySerializer.Release();
        }
    }

    [Test]
    public async Task Read_negotiates_a_block_and_reads_it_from_shared_memory()
    {
        await WithSharedMemoryFile(new byte[] { 0x11, 0x22, 0x33, 0x44 }, async () =>
        {
            using var fake = new FakeSuperShuckie();
            using var driver = new SuperShuckieDriver($"127.0.0.1:{fake.Port}");

            var response = await driver.Read(new IDriver.Request(GameSystem.GB, [
                new IDriver.MemorySegmentRequest("WRAM", 0, 4)
            ]));

            var blocks = await fake.Setup;
            Assert.Multiple(() =>
            {
                Assert.That(blocks, Has.Length.EqualTo(1));
                Assert.That(blocks[0], Is.EqualTo((0u, 0xC000u, 4u)));
                Assert.That(response.Segments[0].Bytes.ToArray(), Is.EqualTo(new byte[] { 0x11, 0x22, 0x33, 0x44 }));
            });
            return true;
        });
    }

    [Test]
    public async Task Read_does_not_resend_setup_when_the_requested_regions_are_unchanged()
    {
        await WithSharedMemoryFile(new byte[] { 0xAA, 0xBB }, async () =>
        {
            using var fake = new FakeSuperShuckie();
            using var driver = new SuperShuckieDriver($"127.0.0.1:{fake.Port}");
            var request = new IDriver.Request(GameSystem.GB, [new IDriver.MemorySegmentRequest("WRAM", 0, 2)]);

            var first = await driver.Read(request);
            await fake.Setup;
            var second = await driver.Read(request);

            Assert.Multiple(() =>
            {
                Assert.That(first.Segments[0].Bytes.ToArray(), Is.EqualTo(new byte[] { 0xAA, 0xBB }));
                Assert.That(second.Segments[0].Bytes.ToArray(), Is.EqualTo(new byte[] { 0xAA, 0xBB }));
            });
            return true;
        });
    }

    [Test]
    public async Task Read_omits_regions_the_driver_does_not_know_how_to_address()
    {
        using var driver = new SuperShuckieDriver(null);

        var response = await driver.Read(new IDriver.Request(
            GameSystem.SNES,
            [new IDriver.MemorySegmentRequest("CGRAM", 0, 1)]));

        Assert.That(response.Segments, Is.Empty);
    }

    [Test]
    public void Read_times_out_when_server_does_not_reply()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var driver = new SuperShuckieDriver($"127.0.0.1:{port}");

        Assert.ThrowsAsync<TimeoutException>(async () => await driver.Read(new IDriver.Request(
            GameSystem.GB, [new IDriver.MemorySegmentRequest("WRAM", 0, 1)])));
    }

    [Test]
    public void Read_times_out_when_nothing_is_listening()
    {
        using var unusedSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var closedPort = ((IPEndPoint)unusedSocket.Client.LocalEndPoint!).Port;
        unusedSocket.Close();

        using var driver = new SuperShuckieDriver($"127.0.0.1:{closedPort}");

        Assert.ThrowsAsync<TimeoutException>(async () => await driver.Read(new IDriver.Request(
            GameSystem.GB,
            [new IDriver.MemorySegmentRequest("WRAM", 0, 1)])));
    }
}
