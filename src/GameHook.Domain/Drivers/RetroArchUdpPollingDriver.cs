using GameHook.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GameHook.Domain.Drivers
{
    record ReceivedPacket
    {
        public ReceivedPacket(string command, MemoryAddress memoryAddress, byte[] value)
        {
            Command = command;
            MemoryAddress = memoryAddress;
            Value = value;
        }

        public string Command { get; }
        public MemoryAddress MemoryAddress { get; }
        public byte[] Value { get; set; }
    }

    public class RetroArchUdpPollingDriver : IGameHookDriver
    {
        private ILogger<RetroArchUdpPollingDriver> Logger { get; }
        private DriverOptions DriverOptions { get; }
        private UdpClient UdpClient { get; set; }
        private Dictionary<string, ReceivedPacket> Responses { get; set; } = new Dictionary<string, ReceivedPacket>();
        private const int DELAY_BETWEEN_RECEIVE_MS = 2;
        private const int READ_PACKET_TIMEOUT_MS = 75;
        public string ProperName { get; } = "RetroArch";

        void CreateUdpClient()
        {
            // Dispose of the existing UDP client if it exists.
            UdpClient?.Dispose();

            // Create a new one.
            UdpClient = new UdpClient();
            UdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            UdpClient.Connect(IPAddress.Parse(DriverOptions.IpAddress), DriverOptions.Port);
        }

        public RetroArchUdpPollingDriver(ILogger<RetroArchUdpPollingDriver> logger, DriverOptions driverOptions)
        {
            Logger = logger;
            DriverOptions = driverOptions;

            CreateUdpClient();
            UdpClient = UdpClient ?? throw new Exception("Unable to load UDP client.");

            // Wait for messages from the UdpClient.
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        if (UdpClient == null || UdpClient.Client.Connected == false)
                        {
                            Logger.LogDebug("UdpClient is not connected -- reestablishing connection.");

                            CreateUdpClient();
                        }

                        if (UdpClient == null)
                        {
                            throw new Exception("UdpClient is still NULL when waiting for messages.");
                        }

                        var buffer = await UdpClient.ReceiveAsync();
                        ReceivePacket(buffer.Buffer);

                        await Task.Delay(DELAY_BETWEEN_RECEIVE_MS);
                    }
                    catch
                    {
                        // Automatically swallow exceptions here because
                        // they're not useful even if there's an error.

                        // We don't want to spam the user with errors.
                    }
                }
            });
        }

        private string ToRetroArchHexdecimalString(uint value)
        {
            // TODO: This is somewhat of a hack because
            // RetroArch returns the request 00 as 0.

            if (value <= 9) { return $"{value}"; }
            else return $"{value:X2}".ToLower();
        }

        public async Task WriteBytes(MemoryAddress memoryAddress, byte[] values)
        {
            await SendPacket("WRITE_CORE_MEMORY", $"{ToRetroArchHexdecimalString(memoryAddress)} {string.Join(' ', values.Select(x => x.ToHexdecimalString()))}");
        }

        private async Task SendPacket(string command, string argument)
        {
            // We require to store the command to watch for
            // the response.

            // command    READ_CORE_MEMORY d158
            // argument   11

            var outgoingPayload = $"{command} {argument}";
            var datagram = Encoding.ASCII.GetBytes(outgoingPayload);

            if (UdpClient == null)
            {
                CreateUdpClient();
            }

            if (UdpClient == null)
            {
                throw new Exception($"Unable to create UdpClient to SendPacket({command} {argument})");
            }

            _ = await UdpClient.SendAsync(datagram, datagram.Length);
            Logger.LogTrace($"[Outgoing Packet] {outgoingPayload}");
        }

        private async Task<byte[]> ReadMemoryAddress(MemoryAddress memoryAddress, uint length)
        {
            var command = $"READ_CORE_MEMORY {ToRetroArchHexdecimalString(memoryAddress)}";
            await SendPacket(command, $"{length}");

            var responsesKey = $"{command} {length}";
            ReceivedPacket? readCoreMemoryResult = null;

            SpinWait.SpinUntil(() =>
            {
                Responses.TryGetValue(responsesKey, out var result);
                readCoreMemoryResult = result;

                return readCoreMemoryResult != null;
            }, TimeSpan.FromMilliseconds(READ_PACKET_TIMEOUT_MS));

            if (readCoreMemoryResult == null)
            {
                Logger.LogDebug($"A timeout occurred when waiting for ReadMemoryAddress reply from RetroArch. ({responsesKey})");

                throw new DriverTimeoutException(memoryAddress, "RetroArch", null);
            }

            return readCoreMemoryResult.Value;
        }

        private void ReceivePacket(byte[] receiveBytes)
        {
            string receiveString = Encoding.ASCII.GetString(receiveBytes).Replace("\n", string.Empty);
            Logger.LogTrace($"[Incoming Packet] {receiveString}");

            var splitString = receiveString.Split(' ');
            var command = splitString[0];
            var memoryAddressString = splitString[1];
            var valueStringArray = splitString[2..];

            if (valueStringArray[0] == "-1")
            {
                throw new DriverErrorException(receiveString);
            }

            var memoryAddress = Convert.ToUInt32(memoryAddressString, 16);
            var value = valueStringArray.Select(x => Convert.ToByte(x, 16)).ToArray();

            var receiveKey = $"{command} {memoryAddressString} {valueStringArray.Length}";

            Responses[receiveKey] = new ReceivedPacket(command, memoryAddress, value);
            Logger.LogDebug($"[Incoming Packet] Set response {receiveKey}");
        }

        public async Task<IEnumerable<MemoryAddressBlockResult>> ReadBytes(IEnumerable<MemoryAddressBlock> blocks)
        {
            return (await Task.WhenAll(blocks.Select(async x =>
            {
                var data = await ReadMemoryAddress(x.StartingAddress, x.EndingAddress - x.StartingAddress);
                return new MemoryAddressBlockResult(x.Index, x.Name, x.StartingAddress, x.EndingAddress, data);
            }))).ToList();
        }
    }
}