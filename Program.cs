using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Bluetest_3
{
    internal class Program
    {
        static DeviceInformation device = null;
        static DeviceWatcher deviceWatcher = null;
        static BluetoothLEDevice bluetoothLeDevice = null;
        static bool isDeviceFound = false;
        static string targetMacAddress = "cc:db:a7:8f:cc:be"; // Update this with the correct MAC address
        static UdpClient udpClient;
        static string udpTargetAddress = "127.0.0.1"; // IP address of Unity machine
        static int udpPort = 12345; // Unity's listening UDP port

        // Sensor characteristic UUID (Update with actual characteristic UUID)
        static string sensorCharacteristicUuid = "b2c3d4e5-6789-2345-6789-abcdef123456";
        static GattCharacteristic sensorCharacteristic = null;
        static bool isConnected = false;

        static async Task Main(string[] args)
        {
            udpClient = new UdpClient();
            udpClient.Connect(udpTargetAddress, udpPort);
            Console.WriteLine($"UDP client initialized. Sending data to {udpTargetAddress}:{udpPort}");

            StartDeviceWatcher();

            while (!isDeviceFound)
            {
                await Task.Delay(200); // Wait for device to be found
            }

            deviceWatcher.Stop();
            Console.WriteLine("Device 'ESP32-Server' discovered. Connecting...");
            await ConnectToDevice();

            while (true) // Keep the program running to receive continuous updates
            {
                await Task.Delay(1000);
                if (!isConnected)
                {
                    Console.WriteLine("Reconnecting to device...");
                    await ConnectToDevice();
                }
            }
        }

        private static void StartDeviceWatcher()
        {
            string[] requestedProperties = { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected" };

            deviceWatcher = DeviceInformation.CreateWatcher(
                BluetoothLEDevice.GetDeviceSelectorFromPairingState(false),
                requestedProperties,
                DeviceInformationKind.AssociationEndpoint);

            deviceWatcher.Added += DeviceWatcher_Added;
            deviceWatcher.Updated += DeviceWatcher_Updated;
            deviceWatcher.Removed += DeviceWatcher_Removed;
            deviceWatcher.EnumerationCompleted += DeviceWatcher_EnumerationCompleted;
            deviceWatcher.Stopped += DeviceWatcher_Stopped;

            deviceWatcher.Start();
            Console.WriteLine("Scanning for devices...");
        }

        private static void DeviceWatcher_Added(DeviceWatcher sender, DeviceInformation args)
        {
            string deviceMacAddress = args.Properties.ContainsKey("System.Devices.Aep.DeviceAddress")
                ? args.Properties["System.Devices.Aep.DeviceAddress"].ToString()
                : string.Empty;

            Console.WriteLine($"Device found: {args.Name} ({deviceMacAddress})");
            if ((args.Name == "ESP32-Server" || deviceMacAddress == targetMacAddress) && device == null)
            {
                device = args;
                isDeviceFound = true;
            }
        }

        private static async Task ConnectToDevice()
        {
            if (device == null)
            {
                Console.WriteLine("ESP32 device not found.");
                return;
            }

            bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(device.Id);
            if (bluetoothLeDevice == null)
            {
                Console.WriteLine("Failed to connect to the device.");
                return;
            }

            Console.WriteLine($"Connected to '{bluetoothLeDevice.Name}'. Retrieving services...");
            isConnected = true;

            bluetoothLeDevice.ConnectionStatusChanged += (sender, args) =>
            {
                if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                {
                    Console.WriteLine("ESP32 Disconnected!");
                    isConnected = false;
                }
            };

            await GetGattServicesAsync(bluetoothLeDevice);
        }

        private static async Task GetGattServicesAsync(BluetoothLEDevice device)
        {
            try
            {
                var result = await device.GetGattServicesAsync();
                if (result.Status == GattCommunicationStatus.Success)
                {
                    Console.WriteLine("Connected successfully.");
                    foreach (var service in result.Services)
                    {
                        Console.WriteLine($"Service UUID: {service.Uuid}");

                        var characteristicsResult = await service.GetCharacteristicsAsync();
                        if (characteristicsResult.Status == GattCommunicationStatus.Success)
                        {
                            foreach (var characteristic in characteristicsResult.Characteristics)
                            {
                                Console.WriteLine($"  Characteristic UUID: {characteristic.Uuid}");

                                if (characteristic.Uuid.ToString().ToLower() == sensorCharacteristicUuid.ToLower())
                                {
                                    sensorCharacteristic = characteristic;
                                    Console.WriteLine("Found sensor characteristic. Enabling notifications...");

                                    if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                                    {
                                        await EnableNotifications(characteristic);
                                    }
                                    else
                                    {
                                        Console.WriteLine("Characteristic does not support Notify.");
                                    }
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("Failed to retrieve characteristics.");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"Failed to retrieve services. Status: {result.Status}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error retrieving services or characteristics: {ex.Message}");
            }
        }

        private static async Task EnableNotifications(GattCharacteristic characteristic)
        {
            try
            {
                GattCommunicationStatus status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);

                if (status == GattCommunicationStatus.Success)
                {
                    characteristic.ValueChanged += Characteristic_ValueChanged;
                    Console.WriteLine("Notifications enabled for characteristic.");
                }
                else
                {
                    Console.WriteLine("Failed to enable notifications.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error enabling notifications: {ex.Message}");
            }
        }

        private static void Characteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            try
            {
                byte[] receivedBytes = args.CharacteristicValue.ToArray();

                if (receivedBytes.Length == 0)
                {
                    Console.WriteLine("Error: No data received from BLE.");
                    return;
                }

                // Convert received bytes to an integer array (assuming 4-byte integers)
                int[] parsedData = new int[receivedBytes.Length / 4];

                for (int i = 0; i < parsedData.Length; i++)
                {
                    parsedData[i] = BitConverter.ToInt32(receivedBytes, i * 4);
                }

                Console.WriteLine($"Received Updated Data: {string.Join(",", parsedData)}");

                SendDataToUnity(parsedData);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing notification: {ex.Message}");
            }
        }

        private static void SendDataToUnity(int[] data)
        {
            try
            {
                byte[] sendData = data.SelectMany(BitConverter.GetBytes).ToArray();
                udpClient.Send(sendData, sendData.Length);
                Console.WriteLine($"Sent to Unity via UDP: {string.Join(",", data)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending data via UDP: {ex.Message}");
            }
        }

        private static void DeviceWatcher_Stopped(DeviceWatcher sender, object args) => Console.WriteLine("Device watcher stopped.");
        private static void DeviceWatcher_EnumerationCompleted(DeviceWatcher sender, object args) => Console.WriteLine("Device enumeration completed.");
        private static void DeviceWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args) => Console.WriteLine($"Device removed: {args.Id}");
        private static void DeviceWatcher_Updated(DeviceWatcher sender, DeviceInformationUpdate args) => Console.WriteLine($"Device updated: {args.Id}");
    }
}
