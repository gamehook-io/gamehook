using GameHook.Domain.DTOs;
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
        private IContainerForDriver? Container { get; set; }
        private DriverOptions DriverOptions { get; }
        private UdpClient UdpClient { get; set; }
        private Dictionary<string, ReceivedPacket> Responses { get; set; } = new Dictionary<string, ReceivedPacket>();
        private CancellationTokenSource? WatchingAddressesCancellationTokenSource { get; set; }
        private const int DELAY_BETWEEN_RECEIVE_MS = 2;
        private const int DELAY_BETWEEN_WATCHES_MS = 1;
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
                            Logger.LogWarning("UdpClient is not connected -- reestablishing connection.");

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
                    catch (Exception ex)
                    {
                        Container?.OnDriverError(new ProblemDetailsForClientDTO() { Title = "UDP_CONNECTION_ERROR", Detail = "An error has occurred when receiving packets." }, ex);
                    }
                }
            });
        }

        public void AddAddressToWatch(MemoryAddress memoryAddress, int length)
        {
            if (AddressesToWatch.Any(x => x.Address.Equals(memoryAddress) && x.Length == length) == false)
            {
                AddressesToWatch.Add(new WatchMemoryAddress(memoryAddress, length));
            }
        }

        private MemoryAddressRange? GetRangeForAddress(MemoryAddress address, IEnumerable<MemoryAddressRange> ranges)
        {
            foreach (var range in ranges)
            {
                if (address >= range.Address && address <= range.Address + range.Bytes.Length)
                {
                    return range;
                }
            }

            return null;
        }

        public bool StartWatching(IContainerForDriver handler)
        {
            WatchingAddressesCancellationTokenSource = new CancellationTokenSource();
            Container = handler;

            var addressesToWatch = AddressesToWatch.ToList();
            if (addressesToWatch.Any() == false) { return true; }

            // Calculate which platform ranges are required by the mapper.
            var requestRanges = Container.PlatformOptions.Ranges
                                    .Where(x => addressesToWatch.Any(y => y.Address.Between(x.StartingAddress, x.EndingAddress)))
                                    .ToList();

            Logger.LogDebug($"Requested {requestRanges.Count}/{Container.PlatformOptions.Ranges.Count()} ranges of memory.");
            Logger.LogDebug($"Requested ranges: {string.Join(", ", requestRanges.Select(x => x.Name))}");

            var ranSuccessfullyOnce = false;

            Task.Run(async () =>
            {
                var token = WatchingAddressesCancellationTokenSource.Token;

                while (true)
                {
                    Responses.Clear();

                    if (addressesToWatch.Any())
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();

                            var ranges = new List<MemoryAddressRange>();
                            foreach (var range in requestRanges)
                            {
                                token.ThrowIfCancellationRequested();

                                // Read the entire address range into memory.
                                var length = range.EndingAddress - range.StartingAddress;
                                var packet = await ReadMemoryAddress(range.StartingAddress, length);

                                ranges.Add(new MemoryAddressRange(packet.MemoryAddress, packet.Value));
                                AddressNumberOfTimeouts[packet.MemoryAddress] = 0;
                            }

                            await Parallel.ForEachAsync(addressesToWatch, async (watch, cancellationToken) =>
                            {
                                token.ThrowIfCancellationRequested();

                                var range = GetRangeForAddress(watch.Address, ranges);
                                if (range == null)
                                {
                                    // We cannot read from this section of memory, since we did not pull it.

                                    if (ranSuccessfullyOnce == false)
                                    {
                                        Logger.LogWarning($"Cannot access memory address {watch.Address.ToHexdecimalString()} because it outside of the range platform addresses provided. Skipping translation for this property.");
                                    }

                                    return;
                                }

                                var offsetaddress = watch.Address - range.Address;
                                var totalOffset = offsetaddress + watch.Length;
                                var result = range.Bytes.Skip((int)offsetaddress).Take(watch.Length).ToArray();

                                if (watch.OldBytes == null || result.SequenceEqual(watch.OldBytes) == false)
                                {
                                    if (Container != null)
                                    {
                                        await Container.OnDriverMemoryChanged(watch.Address, watch.Length, result);
                                    }

                                    watch.OldBytes = result;
                                }
                            });

                            token.ThrowIfCancellationRequested();

                            ranSuccessfullyOnce = true;
                        }
                        catch (DriverTimeoutException ex)
                        {
                            token.ThrowIfCancellationRequested();

                            AddressNumberOfTimeouts[ex.MemoryAddress] += 1;
                            if (AddressNumberOfTimeouts[ex.MemoryAddress] >= DriverOptions.DriverTimeoutCounter)
                            {
                                await Container.OnDriverMemoryTimeout(ex);
                            }
                        }
                        catch (Exception ex)
                        {
                            token.ThrowIfCancellationRequested();
                            await Container.OnDriverError(new ProblemDetailsForClientDTO() { Title = "DRIVER_ERROR", Detail = "An unknown driver error was encountered when reading RAM." }, ex);
                        }
                    }

                    await Task.Delay(DELAY_BETWEEN_WATCHES_MS);
                }
            }, WatchingAddressesCancellationTokenSource.Token);

            SpinWait.SpinUntil(() => ranSuccessfullyOnce, TimeSpan.FromSeconds(2));

            if (ranSuccessfullyOnce) return true;
            else return false;
        }

        public void StopWatchingAndReset()
        {
            AddressesToWatch.Clear();
            WatchingAddressesCancellationTokenSource?.Cancel();

            Container = null;
            Responses.Clear();
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

        private async Task<ReceivedPacket> ReadMemoryAddress(MemoryAddress memoryAddress, uint length)
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

            return readCoreMemoryResult;
        }

        private void ReceivePacket(byte[] receiveBytes)
        {
            string receiveString = Encoding.ASCII.GetString(receiveBytes).Replace("\n", string.Empty);
            Logger.LogTrace($"[Incoming Packet] {receiveString}");

            try
            {
                var splitString = receiveString.Split(' ');
                var command = splitString[0];
                var memoryAddressString = splitString[1];
                var valueStringArray = splitString[2..];

                if (valueStringArray[0] == "-1")
                {
                    Logger.LogError($"RetroArch sent back an error: {receiveString}");
                    return;
                }

                var memoryAddress = Convert.ToUInt32(memoryAddressString, 16);
                var value = valueStringArray.Select(x => Convert.ToByte(x, 16)).ToArray();

                var receiveKey = $"{command} {memoryAddressString} {valueStringArray.Length}";

                Responses[receiveKey] = new ReceivedPacket(command, memoryAddress, value);
                Logger.LogDebug($"[Incoming Packet] Set response {receiveKey}");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"ReceivePacket error on incoming packet: {receiveString}");
            }
        }
    }
}