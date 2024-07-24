using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SharpAdbClient;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Configuration;
using UnixFileMode = System.IO.UnixFileMode;

namespace ADBForwarder;

internal class PortConfiguration(List<int> ports)
{
    public List<int> Ports { get; set; } = ports;
}

internal static class Program
{
    private static readonly AdbClient Client = new();
    private static readonly AdbServer Server = new();
    private static readonly IPEndPoint EndPoint = new(IPAddress.Loopback, AdbClient.AdbServerPort);
    private static PortConfiguration _portConfiguration;

    private static void Main()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("config.json", optional: true);

        IConfiguration config = builder.Build();

        _portConfiguration = config.GetSection("PortConfiguration").Get<PortConfiguration>()
                             ?? new PortConfiguration([9943, 9944]);

        Console.ResetColor();
        var currentDirectory = Path.GetDirectoryName(AppContext.BaseDirectory);
        if (currentDirectory == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Path error!");
            return;
        }

        var adbFolderPath = Path.Combine(currentDirectory, "adb");
        var adbPath = Path.Combine(adbFolderPath, "platform-tools");
        var downloadUri = "https://dl.google.com/android/repository/platform-tools-latest-{0}.zip";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Console.WriteLine("Platform: Linux");

            adbPath = Path.Combine(adbPath, "adb");
            downloadUri = string.Format(downloadUri, "linux");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine("Platform: Windows");

            adbPath = Path.Combine(adbPath, "adb.exe");
            downloadUri = string.Format(downloadUri, "windows");
        }
        else
        {
            Console.WriteLine("Unsupported platform!");
            return;
        }

        var absoluteAdbPath = Path.Combine(currentDirectory, adbPath);
        if (!File.Exists(absoluteAdbPath))
        {
            Console.WriteLine("ADB not found, downloading...");
            DownloadAdb(adbFolderPath, downloadUri).Wait();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Console.WriteLine("Giving adb executable permissions");
                File.SetUnixFileMode(absoluteAdbPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Server.StartServer(absoluteAdbPath, false);

        Client.Connect(EndPoint);

        Console.WriteLine("Adb server started.");
        Console.WriteLine("If your device doesn't show up after connecting the device, " +
                          "please make sure dev mode is enabled on headset and cable or usb port is not damaged.");

        var monitor = new DeviceMonitor(new AdbSocket(EndPoint));
        monitor.DeviceConnected += Monitor_DeviceConnected;
        monitor.DeviceDisconnected += Monitor_DeviceDisconnected;
        monitor.Start();

        while (true)
        {
            // Main thread needs to stay alive, 100ms is acceptable idle time
            Thread.Sleep(100);
        }
    }

    private static void Monitor_DeviceConnected(object sender, DeviceDataEventArgs e)
    {
        // Prevent duplicate ports
        _portConfiguration.Ports = _portConfiguration.Ports.Distinct().ToList();
        if (e.Device.Serial.StartsWith("127.0.0.1"))
        {
            // We don't want to re-forward local device
            return;
        }

        Console.ForegroundColor = ConsoleColor.DarkGreen;
        Console.WriteLine($"Connected device: {DeviceStringFromDeviceData(e.Device)}");
        Forward(e.Device);
    }

    private static void Monitor_DeviceDisconnected(object sender, DeviceDataEventArgs e)
    {
        Console.ForegroundColor = ConsoleColor.DarkRed;
        Console.WriteLine($"Disconnected device: {DeviceStringFromDeviceData(e.Device)}");
    }

    private static void Forward(DeviceData device)
    {
        // DeviceConnected calls without product set yet
        Thread.Sleep(1000);

        foreach (var deviceData in Client.GetDevices().Where(deviceData => device.Serial == deviceData.Serial))
        {
            if (deviceData.State != DeviceState.Online)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Skipped forwarding device: " +
                                  $"{DeviceStringFromDeviceData(deviceData)}," +
                                  $" Error: {deviceData.State}");

                if (deviceData.State == DeviceState.Unauthorized)
                {
                    Console.WriteLine("Unauthorized device, please make sure you enabled developer mode on device, " +
                                      "authorized adb connection on the device and then replug usb cable.");
                    Console.ForegroundColor = ConsoleColor.White;
                    return;
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Console.WriteLine("Permission error, please make sure you have " +
                                      "android-udev-rules installed and reboot pc.");
                    Console.WriteLine("Install udev rules through package manager or visit " +
                                      "https://github.com/M0Rf30/android-udev-rules and follow instructions.");
                    Console.ForegroundColor = ConsoleColor.White;
                    return;
                }
            }

            foreach (var port in _portConfiguration.Ports)
            {
                Client.CreateForward(deviceData, port, port);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Successfully forwarded device: {DeviceStringFromDeviceData(deviceData)}");
            Console.ForegroundColor = ConsoleColor.White;

            return;
        }
    }

    private static string DeviceStringFromDeviceData(DeviceData deviceData)
    {
        return string.IsNullOrWhiteSpace(deviceData.Product)
            ? deviceData.Serial
            : $"{deviceData.Serial} [{deviceData.Product}]";
    }

    private static async Task DownloadAdb(string adbFolderPath, string downloadUri)
    {
        using var client = new HttpClient();
        var fileStream = await client.GetStreamAsync(downloadUri);
        Directory.CreateDirectory(adbFolderPath);
        var zipPath = Path.Combine(adbFolderPath, "adb.zip");
        await using (var outputFileStream = new FileStream(zipPath, FileMode.Create))
        {
            await fileStream.CopyToAsync(outputFileStream);
        }

        Console.WriteLine("Download successful");

        var zip = new FastZip();
        zip.ExtractZip(zipPath, adbFolderPath, null);
        Console.WriteLine("Extraction successful");

        File.Delete(zipPath);
    }
}