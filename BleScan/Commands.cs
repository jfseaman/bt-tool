// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable MemberCanBeProtected.Global
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace BleScan;

using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;

using Smart.CommandLine.Hosting;

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

public sealed class RootCommandHandler : ICommandHandler
{
    [Option("--active", "-a", Description = "Active scanning")]
    public bool Active { get; set; }

    [Option("--once", "-o", Description = "Scan once")]
    public bool Once { get; set; }

    [Option("--info", "-i", Description = "Show device information")]
    public bool Info { get; set; }

    [Option("--gatt", "-g", Description = "Get gatt services")]
    public bool Gatt { get; set; }

    [Option("--manufacturer", "-m", Description = "Show manufacturer data")]
    public bool Manufacturer { get; set; }

    [Option("--section", "-s", Description = "Show data section")]
    public bool Section { get; set; }

    [Option("--rssi", "-r", Description = "Minimum RSSI threshold (dBm, range: -127 to +20)")]
    public short? Rssi { get; set; }

    [Option("--name", "-n", Description = "Filter by device name")]
    public string? Name { get; set; }

    public async ValueTask ExecuteAsync(CommandContext context)
    {
        // Capture original console color for restoration
        var originalColor = Console.ForegroundColor;

        try
        {
            // Set up Ctrl+C handler to restore color on interruption
            Console.CancelKeyPress += (sender, e) =>
            {
                Console.ForegroundColor = originalColor;
            };

            // Apply default RSSI value if not specified
            var rssiThreshold = Rssi ?? -90;

            // Validate RSSI range
            if (rssiThreshold is < -127 or > 20)
            {
                ConsoleWriteLine(ConsoleColor.Red, $"Error: RSSI value must be between -127 and +20. Provided value: {rssiThreshold}");
                return;
            }

            var set = new HashSet<ulong>();
            using var outputLock = new SemaphoreSlim(1, 1);
            var deviceFound = false;
            var completionSource = new TaskCompletionSource<bool>();

            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = Active ? BluetoothLEScanningMode.Active : BluetoothLEScanningMode.Passive
            };

            watcher.Received += WatcherOnReceived;

#pragma warning disable CA1031
            // ReSharper disable once AsyncVoidMethod
            async void WatcherOnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
            {
                // Filter by RSSI threshold
                if (args.RawSignalStrengthInDBm < rssiThreshold)
                {
                    return;
                }

                if (Once)
                {
                    lock (set)
                    {
                        if (!set.Add(args.BluetoothAddress))
                        {
                            return;
                        }
                    }
                }

                BluetoothLEDevice? device = null;
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    device = await BluetoothLEDevice.FromBluetoothAddressAsync(args.BluetoothAddress);
                    if (device is not null)
                    {
                        break;
                    }

                    await Task.Delay(5000);
                }
                if (device is null)
                {
                    ConsoleWriteLine(ConsoleColor.Red, "(Bluetooth device not found)");
                    return;
                }

                var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
                if (session is not null)
                {
                    session.MaintainConnection = true;
                }

                var cancellationToken = CancellationToken.None;
                var services = Gatt
                    ? await RetryAsync(
                        () => device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken),
                        attempts: 5,
                        delayMs: 200,
                        cancellationToken)
                    : null;
                var name = device.Name ?? "(Unknown)";

                // Filter by device name if specified
                if (!string.IsNullOrEmpty(Name) && !name.Equals(Name, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // Mark that we found a matching device
                if (!string.IsNullOrEmpty(Name))
                {
                    deviceFound = true;
                }

                await outputLock.WaitAsync();
                using var releaser = new SemaphoreReleaser(outputLock);

                ConsoleWrite(ConsoleColor.Cyan, $"{args.Timestamp:HH:mm:ss.fff}");
                ConsoleWrite(Console.ForegroundColor, " [");
                ConsoleWrite(ConsoleColor.DarkCyan, ToAddressString(args.BluetoothAddress));
                ConsoleWrite(Console.ForegroundColor, "] ");
                ConsoleWrite(ConsoleColor.Yellow, "RSSI:");
                ConsoleWrite(Console.ForegroundColor, $"{args.RawSignalStrengthInDBm}");
                ConsoleWrite(Console.ForegroundColor, " ");
                ConsoleWriteLine(ConsoleColor.Magenta, name);

                if (Info && (device is not null))
                {
                    ConsoleWrite(ConsoleColor.Yellow, "DeviceId:");
                    ConsoleWriteLine(Console.ForegroundColor, $" {device.BluetoothDeviceId.Id}");
                    ConsoleWrite(ConsoleColor.Yellow, "AddressType:");
                    ConsoleWriteLine(Console.ForegroundColor, $" {device.BluetoothAddressType}");
                    ConsoleWrite(ConsoleColor.Yellow, "ConnectionStatus:");
                    ConsoleWriteLine(Console.ForegroundColor, $" {device.ConnectionStatus}");
                    ConsoleWrite(ConsoleColor.Yellow, "ProtectionLevel:");
                    ConsoleWriteLine(Console.ForegroundColor, $" {device.DeviceInformation.Pairing.ProtectionLevel}");
                    ConsoleWrite(ConsoleColor.Yellow, "IsPaired:");
                    ConsoleWriteLine(Console.ForegroundColor, $" {device.DeviceInformation.Pairing.IsPaired}");
                    ConsoleWrite(ConsoleColor.Yellow, "CanPair:");
                    ConsoleWriteLine(Console.ForegroundColor, $" {device.DeviceInformation.Pairing.CanPair}");
                }

                if (Gatt && (services is not null))
                {
                    if (services.Status == GattCommunicationStatus.Success)
                    {
                        ConsoleWriteLine(ConsoleColor.Yellow, "GattServices:");
                        foreach (var service in services.Services)
                        {
                            ConsoleWrite(Console.ForegroundColor, $"  {service.Uuid}    ");
                            ConsoleWriteLine(ConsoleColor.Blue, DisplayHelper.GetServiceName(service));
                            var characteristics = await RetryAsync(
                            () => service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken),
                            attempts: 3,
                            delayMs: 150,
                            cancellationToken);

                            if (characteristics.Status != GattCommunicationStatus.Success)
                            {
                                continue;
                            }

                            foreach (var characteristic in characteristics.Characteristics)
                            {
                                ConsoleWrite(Console.ForegroundColor, $"    {characteristic.Uuid}    ");
                                ConsoleWrite(ConsoleColor.Blue, DisplayHelper.GetCharacteristicName(characteristic));
                                ConsoleWrite(Console.ForegroundColor, " [");
                                ConsoleWrite(ConsoleColor.Green, characteristic.CharacteristicProperties.ToString());
                                ConsoleWriteLine(Console.ForegroundColor, "]");
                            }
                        }
                    }
                    else
                    {
                        ConsoleWrite(ConsoleColor.Yellow, "GattServices:");
                        ConsoleWriteLine(ConsoleColor.Red, $" {services.Status}");
                    }
                }

                if (Manufacturer)
                {
                    foreach (var md in args.Advertisement.ManufacturerData)
                    {
                        ConsoleWrite(ConsoleColor.Yellow, "CompanyId:");
                        ConsoleWriteLine(Console.ForegroundColor, $" 0x{md.CompanyId:X4}");
                        ConsoleWriteLine(ConsoleColor.Yellow, "Data:");
                        var array = md.Data.ToArray().AsSpan();
                        for (var start = 0; start < array.Length; start += 16)
                        {
                            ConsoleWriteLine(ConsoleColor.DarkGreen, ToHexString(array.Slice(start, Math.Min(16, array.Length - start))));
                        }
                    }
                }

                if (Section)
                {
                    foreach (var ds in args.Advertisement.DataSections)
                    {
                        ConsoleWrite(ConsoleColor.Yellow, "DataType:");
                        ConsoleWriteLine(Console.ForegroundColor, $" 0x{ds.DataType:X2}");
                        ConsoleWriteLine(ConsoleColor.Yellow, "Data:");
                        var array = ds.Data.ToArray().AsSpan();
                        for (var start = 0; start < array.Length; start += 16)
                        {
                            ConsoleWriteLine(ConsoleColor.DarkGreen, ToHexString(array.Slice(start, Math.Min(16, array.Length - start))));
                        }
                    }
                }

                // Signal completion after all information is displayed (when -n is specified)
                if (!string.IsNullOrEmpty(Name) && deviceFound)
                {
                    completionSource.TrySetResult(true);
                }
            }
#pragma warning restore CA1031

            watcher.Start();

            if (!string.IsNullOrEmpty(Name))
            {
                // When searching for a specific device, wait for it to be found or timeout
                var completedTask = await Task.WhenAny(
                    completionSource.Task,
                    Task.Delay(TimeSpan.FromSeconds(30)));

                watcher.Stop();

                if (completedTask == completionSource.Task)
                {
                    Console.WriteLine("\nDevice found.");
                }
                else
                {
                    ConsoleWriteLine(ConsoleColor.Red, $"Device [{Name}] not found within timeout.");
                }
            }
            else if (Once)
            {
                // Scan for 10 seconds, then stop automatically
                await Task.Delay(TimeSpan.FromSeconds(10));
                watcher.Stop();
                Console.WriteLine("\nScan complete.");
            }
            else
            {
                // Continuous mode - wait for user to press Enter
                Console.ReadLine();
                watcher.Stop();
            }
        }
        finally
        {
            // Always restore the original console color
            Console.ForegroundColor = originalColor;
        }
    }

    private static void ConsoleWrite(ConsoleColor color, string value)
    {
        var backup = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(value);
        Console.ForegroundColor = backup;
    }

    private static void ConsoleWriteLine(ConsoleColor color, string value)
    {
        var backup = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(value);
        Console.ForegroundColor = backup;
    }

    private static unsafe string ToAddressString(ulong address)
    {
        ReadOnlySpan<char> hex = ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'A', 'B', 'C', 'D', 'E', 'F'];
        var span = stackalloc char[18];
        var offset = 0;

        span[offset++] = hex[(int)((address >> 44) & 0xF)];
        span[offset++] = hex[(int)((address >> 40) & 0xF)];
        span[offset++] = ':';
        span[offset++] = hex[(int)((address >> 36) & 0xF)];
        span[offset++] = hex[(int)((address >> 32) & 0xF)];
        span[offset++] = ':';
        span[offset++] = hex[(int)((address >> 28) & 0xF)];
        span[offset++] = hex[(int)((address >> 24) & 0xF)];
        span[offset++] = ':';
        span[offset++] = hex[(int)((address >> 20) & 0xF)];
        span[offset++] = hex[(int)((address >> 16) & 0xF)];
        span[offset++] = ':';
        span[offset++] = hex[(int)((address >> 12) & 0xF)];
        span[offset++] = hex[(int)((address >> 8) & 0xF)];
        span[offset++] = ':';
        span[offset++] = hex[(int)((address >> 4) & 0xF)];
        span[offset] = hex[(int)(address & 0xF)];

        return new string(span);
    }

    private static unsafe string ToHexString(ReadOnlySpan<byte> source)
    {
        ReadOnlySpan<char> hex = ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'A', 'B', 'C', 'D', 'E', 'F'];
        var span = stackalloc char[(source.Length * 3) + 2];
        var offset = 0;

        span[offset++] = ' ';
        foreach (var b in source)
        {
            span[offset++] = ' ';
            span[offset++] = hex[b >> 4];
            span[offset++] = hex[b & 0xF];
        }

        return new string(span);
    }

    private static async Task<T> RetryAsync<T>(
        Func<Task<T>> op,
        int attempts,
        int delayMs,
        CancellationToken cancellationToken)
    {
        Exception? last = null;

        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await op();
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(delayMs * (i + 1), cancellationToken);
            }
        }

        throw last ?? new InvalidOperationException("Operation failed after retries.");
    }

    private sealed class SemaphoreReleaser : IDisposable
    {
        private readonly SemaphoreSlim semaphore;

        public SemaphoreReleaser(SemaphoreSlim semaphore)
        {
            this.semaphore = semaphore;
        }

        public void Dispose()
        {
            semaphore.Release();
        }
    }
}
