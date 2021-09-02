//*********************************************************
//
// Copyright (c) Microsoft. All rights reserved.
// This code is licensed under the MIT License (MIT).
// THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
// IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
// PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.
//
//*********************************************************

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SDKTemplate
{
    // This scenario connects to the device selected in the "Discover
    // GATT Servers" scenario and communicates with it.
    // Note that this scenario is rather artificial because it communicates
    // with an unknown service with unknown characteristics.
    // In practice, your app will be interested in a specific service with
    // a specific characteristic.
    public sealed partial class Scenario2_Client : Page
    {
        private MainPage rootPage = MainPage.Current;

        private BluetoothLEDevice bluetoothLeDevice = null;
        private GattCharacteristic selectedCharacteristic;

        // Only one registered characteristic at a time.
        private GattCharacteristic registeredCharacteristic;
        private GattPresentationFormat presentationFormat;

        #region Error Codes
        readonly int E_BLUETOOTH_ATT_WRITE_NOT_PERMITTED = unchecked((int)0x80650003);
        readonly int E_BLUETOOTH_ATT_INVALID_PDU = unchecked((int)0x80650004);
        readonly int E_ACCESSDENIED = unchecked((int)0x80070005);
        readonly int E_DEVICE_NOT_AVAILABLE = unchecked((int)0x800710df); // HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_AVAILABLE)
        #endregion

        #region UI Code
        public Scenario2_Client()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            SelectedDeviceRun.Text = rootPage.SelectedBleDeviceName;
            if (string.IsNullOrEmpty(rootPage.SelectedBleDeviceId))
            {
                ConnectButton.IsEnabled = false;
            }
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            var success = await ClearBluetoothLEDeviceAsync();
            if (!success)
            {
                rootPage.NotifyUser("Error: Unable to reset app state", NotifyType.ErrorMessage);
            }
        }
        #endregion

        #region Enumerating Services
        private async Task<bool> ClearBluetoothLEDeviceAsync()
        {
            if (subscribedForNotifications)
            {
                // Need to clear the CCCD from the remote device so we stop receiving notifications
                var result = await registeredCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
                if (result != GattCommunicationStatus.Success)
                {
                    return false;
                }
                else
                {
                    selectedCharacteristic.ValueChanged -= Characteristic_ValueChanged;
                    subscribedForNotifications = false;
                }
            }
            bluetoothLeDevice?.Dispose();
            bluetoothLeDevice = null;
            return true;
        }

        private async void ConnectButton_Click()
        {
            ConnectButton.IsEnabled = false;

            if (!await ClearBluetoothLEDeviceAsync())
            {
                rootPage.NotifyUser("Error: Unable to reset state, try again.", NotifyType.ErrorMessage);
                ConnectButton.IsEnabled = true;
                return;
            }

            try
            {
                // BT_Code: BluetoothLEDevice.FromIdAsync must be called from a UI thread because it may prompt for consent.
                bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(rootPage.SelectedBleDeviceId);

                if (bluetoothLeDevice == null)
                {
                    rootPage.NotifyUser("Failed to connect to device.", NotifyType.ErrorMessage);
                }
            }
            catch (Exception ex) when (ex.HResult == E_DEVICE_NOT_AVAILABLE)
            {
                rootPage.NotifyUser("Bluetooth radio is not on.", NotifyType.ErrorMessage);
            }

            if (bluetoothLeDevice != null)
            {
                // Note: BluetoothLEDevice.GattServices property will return an empty list for unpaired devices. For all uses we recommend using the GetGattServicesAsync method.
                // BT_Code: GetGattServicesAsync returns a list of all the supported services of the device (even if it's not paired to the system).
                // If the services supported by the device are expected to change during BT usage, subscribe to the GattServicesChanged event.
                GattDeviceServicesResult result = await bluetoothLeDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);

                if (result.Status == GattCommunicationStatus.Success)
                {
                    var services = result.Services;
                    rootPage.NotifyUser(String.Format("Found {0} services", services.Count), NotifyType.StatusMessage);
                    foreach (var service in services)
                    {
                        ServiceList.Items.Add(new ComboBoxItem { Content = DisplayHelpers.GetServiceName(service), Tag = service });
                    }
                    ConnectButton.Visibility = Visibility.Collapsed;
                    ServiceList.Visibility = Visibility.Visible;
                }
                else
                {
                    rootPage.NotifyUser("Device unreachable", NotifyType.ErrorMessage);
                }
            }
            ConnectButton.IsEnabled = true;
        }
        #endregion

        #region Enumerating Characteristics
        private async void ServiceList_SelectionChanged()
        {
            var service = (GattDeviceService)((ComboBoxItem)ServiceList.SelectedItem)?.Tag;

            CharacteristicList.Items.Clear();
            RemoveValueChangedHandler();

            IReadOnlyList<GattCharacteristic> characteristics = null;
            try
            {
                // Ensure we have access to the device.
                var accessStatus = await service.RequestAccessAsync();
                if (accessStatus == DeviceAccessStatus.Allowed)
                {
                    // BT_Code: Get all the child characteristics of a service. Use the cache mode to specify uncached characterstics only 
                    // and the new Async functions to get the characteristics of unpaired devices as well. 
                    var result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    if (result.Status == GattCommunicationStatus.Success)
                    {
                        characteristics = result.Characteristics;
                    }
                    else
                    {
                        rootPage.NotifyUser("Error accessing service.", NotifyType.ErrorMessage);

                        // On error, act as if there are no characteristics.
                        characteristics = new List<GattCharacteristic>();
                    }
                }
                else
                {
                    // Not granted access
                    rootPage.NotifyUser("Error accessing service.", NotifyType.ErrorMessage);

                    // On error, act as if there are no characteristics.
                    characteristics = new List<GattCharacteristic>();

                }
            }
            catch (Exception ex)
            {
                rootPage.NotifyUser("Restricted service. Can't read characteristics: " + ex.Message,
                    NotifyType.ErrorMessage);
                // On error, act as if there are no characteristics.
                characteristics = new List<GattCharacteristic>();
            }

            foreach (GattCharacteristic c in characteristics)
            {
                CharacteristicList.Items.Add(new ComboBoxItem { Content = DisplayHelpers.GetCharacteristicName(c), Tag = c });
            }
            CharacteristicList.Visibility = Visibility.Visible;
        }
        #endregion

        private void AddValueChangedHandler()
        {
            ValueChangedSubscribeToggle.Content = "Unsubscribe from value changes";
            if (!subscribedForNotifications)
            {
                registeredCharacteristic = selectedCharacteristic;
                registeredCharacteristic.ValueChanged += Characteristic_ValueChanged;
                subscribedForNotifications = true;
            }
        }

        private void RemoveValueChangedHandler()
        {
            ValueChangedSubscribeToggle.Content = "Subscribe to value changes";
            if (subscribedForNotifications)
            {
                registeredCharacteristic.ValueChanged -= Characteristic_ValueChanged;
                registeredCharacteristic = null;
                subscribedForNotifications = false;
            }
        }

        private async void CharacteristicList_SelectionChanged()
        {
            selectedCharacteristic = (GattCharacteristic)((ComboBoxItem)CharacteristicList.SelectedItem)?.Tag;
            if (selectedCharacteristic == null)
            {
                EnableCharacteristicPanels(GattCharacteristicProperties.None);
                rootPage.NotifyUser("No characteristic selected", NotifyType.ErrorMessage);
                return;
            }

            // Get all the child descriptors of a characteristics. Use the cache mode to specify uncached descriptors only 
            // and the new Async functions to get the descriptors of unpaired devices as well. 
            var result = await selectedCharacteristic.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success)
            {
                rootPage.NotifyUser("Descriptor read failure: " + result.Status.ToString(), NotifyType.ErrorMessage);
            }

            // BT_Code: There's no need to access presentation format unless there's at least one. 
            presentationFormat = null;
            if (selectedCharacteristic.PresentationFormats.Count > 0)
            {

                if (selectedCharacteristic.PresentationFormats.Count.Equals(1))
                {
                    // Get the presentation format since there's only one way of presenting it
                    presentationFormat = selectedCharacteristic.PresentationFormats[0];
                }
                else
                {
                    // It's difficult to figure out how to split up a characteristic and encode its different parts properly.
                    // In this case, we'll just encode the whole thing to a string to make it easy to print out.
                }
            }

            // Enable/disable operations based on the GattCharacteristicProperties.
            EnableCharacteristicPanels(selectedCharacteristic.CharacteristicProperties);
        }

        private void SetVisibility(UIElement element, bool visible)
        {
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EnableCharacteristicPanels(GattCharacteristicProperties properties)
        {
            // BT_Code: Hide the controls which do not apply to this characteristic.
            SetVisibility(CharacteristicReadButton, properties.HasFlag(GattCharacteristicProperties.Read));

            SetVisibility(CharacteristicWritePanel,
                properties.HasFlag(GattCharacteristicProperties.Write) ||
                properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse));
            CharacteristicWriteValue.Text = "";

            SetVisibility(ValueChangedSubscribeToggle, properties.HasFlag(GattCharacteristicProperties.Indicate) ||
                                                       properties.HasFlag(GattCharacteristicProperties.Notify));

        }

        private async void CharacteristicReadButton_Click()
        {
            // BT_Code: Read the actual value from the device by using Uncached.
            GattReadResult result = await selectedCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (result.Status == GattCommunicationStatus.Success)
            {
                string formattedResult = FormatValueByPresentation(result.Value, presentationFormat);
                rootPage.NotifyUser($"Read result: {formattedResult}", NotifyType.StatusMessage);
            }
            else
            {
                rootPage.NotifyUser($"Read failed: {result.Status}", NotifyType.ErrorMessage);
            }
        }

        private async void CharacteristicWriteButton_Click()
        {
            if (!String.IsNullOrEmpty(CharacteristicWriteValue.Text))
            {
                var writeBuffer = CryptographicBuffer.ConvertStringToBinary(CharacteristicWriteValue.Text,
                    BinaryStringEncoding.Utf8);

                var writeSuccessful = await WriteBufferToSelectedCharacteristicAsync(writeBuffer);
            }
            else
            {
                rootPage.NotifyUser("No data to write to device", NotifyType.ErrorMessage);
            }
        }

        private async void CharacteristicWriteButtonInt_Click()
        {
            if (!String.IsNullOrEmpty(CharacteristicWriteValue.Text))
            {
                var isValidValue = true; //Int32.TryParse(CharacteristicWriteValue.Text, out int readValue);

                if (isValidValue)
                {
                    var writer = new DataWriter();

                    /*
                     * writer.ByteOrder = ByteOrder.LittleEndian;
                     * writer.WriteInt32(readValue);
                    */

                    writer.WriteBytes(StringToByte(CharacteristicWriteValue.Text));

                    var writeSuccessful = await WriteBufferToSelectedCharacteristicAsync(writer.DetachBuffer());
                }
                else
                {
                    rootPage.NotifyUser("Data to write has to be an int32", NotifyType.ErrorMessage);
                }
            }
            else
            {
                rootPage.NotifyUser("No data to write to device", NotifyType.ErrorMessage);
            }
        }
        public byte getHex(string srcValue)
        {
            return Convert.ToByte(srcValue, 16);
        }
        private byte[] StringToByte(string str)
        {
            str.Replace(":", " ").Trim();
            byte[] StrByte = Encoding.UTF8.GetBytes(str);
            return StrByte;
        }
        private async Task<bool> WriteBufferToSelectedCharacteristicAsync(IBuffer buffer)
        {
            try
            {
                byte[] data ;
                string sdata;
                CryptographicBuffer.CopyToByteArray(buffer, out data);
                sdata = CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, buffer);
                rootPage.NotifyUser(sdata, NotifyType.StatusMessage);
                
                if (sdata.Contains(":"))
                {
                    string[] Sdata = sdata.Split(new string[] { ":" }, StringSplitOptions.None);
                    byte[] tdata = new byte[Sdata.Length];
                    for (int i = 0; i < Sdata.Length; i++)
                    {
                        tdata[i] = getHex(Sdata[i]);
                    }

                    buffer = tdata.AsBuffer();
                }
                else
                {
                    byte[] tdata = new byte[data.Length];

                    for (int i = 0; i < data.Length; i++)
                    {
                        tdata[i] = (byte)(data[i] - 48);
                    }

                    buffer = tdata.AsBuffer();
                }

                //BT_Code: Writes the value from the buffer to the characteristic.
                var result = await selectedCharacteristic.WriteValueWithResultAsync(buffer);

                if (result.Status == GattCommunicationStatus.Success)
                {
                    //rootPage.NotifyUser("Successfully wrote value to device", NotifyType.StatusMessage);
                    return true;
                }
                else
                {
                    //rootPage.NotifyUser($"Write failed: {result.Status}", NotifyType.ErrorMessage);
                    return false;
                }
            }
            catch (Exception ex) when (ex.HResult == E_BLUETOOTH_ATT_INVALID_PDU)
            {
                rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                return false;
            }
            catch (Exception ex) when (ex.HResult == E_BLUETOOTH_ATT_WRITE_NOT_PERMITTED || ex.HResult == E_ACCESSDENIED)
            {
                // This usually happens when a device reports that it support writing, but it actually doesn't.
                rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                return false;
            }
        }

        #region Write Protocol

        private byte[] CRCcheck(byte[] ByteArray)
        {
            int crc = 0;

            for (int i = 0; i < ByteArray.Length; i++)
            {
                crc += ByteArray[i];
            }

            ByteArray[ByteArray.Length - 1] = (byte)(crc % 256);

            return ByteArray;
        }

        private void DateTimeBufferWriteButtonInt_Click()
        {
            byte[] ByteArray = { 0x01, 0x02, 0x32, 0x30, 0x32, 0x31, 0x30, 0x37, 0x32, 0x38, 0x32, 0x32, 0x33, 0x34, 0x35, 0x35, 0x10, 0x00 };
            cmdBufferWriteButtonInt_Click(CRCcheck(ByteArray));
        }

        private void BLEConnectRequestWriteButtonInt_Click()
        {
            byte[] ByteArray = { 0x13, 0x02, 0x01, 0x16 };
            cmdBufferWriteButtonInt_Click(ByteArray);
        }
        private void SaveDataCheckButtonInt_Click()
        {
            byte[] ByteArray = { 0x12, 0x02, 0x14 };
            cmdBufferWriteButtonInt_Click(ByteArray);
        }
        private void ExitCheckSuccessButtonInt_Click()
        {
            byte[] ByteArray = { 0x14, 0x02, 0x01, 0x17 };
            cmdBufferWriteButtonInt_Click(ByteArray);
        }
        private void ExitCheckFailureButtonInt_Click()
        {
            byte[] ByteArray = { 0x14, 0x02, 0x02, 0x18 };
            cmdBufferWriteButtonInt_Click(ByteArray);
        }
        //private async void WeatherDataBufferWriteButtonInt_Click()
        private void WeatherData5BufferWriteButtonInt_Click()
        {
            byte[] ByteArray =
            {
                0x18,0x02,
                (byte)(DateTime.Now.Year / 1000 + 48),
                (byte)((DateTime.Now.Year / 100) % 10 + 48),
                (byte)((DateTime.Now.Year / 10) % 10 + 48),
                (byte)(DateTime.Now.Year % 10 + 48),
                (byte)(DateTime.Now.Month / 10 + 48),
                (byte)(DateTime.Now.Month % 10 + 48),
                (byte)(DateTime.Now.Day / 10 + 48),
                (byte)(DateTime.Now.Day % 10 + 48),
                (byte)(DateTime.Now.Hour / 10 + 48),
                (byte)(DateTime.Now.Hour % 10 + 48),
                (byte)(DateTime.Now.Minute / 10 + 48),
                (byte)(DateTime.Now.Minute % 10 + 48),
                (byte)(DateTime.Now.Second / 10 + 48),
                (byte)(DateTime.Now.Second % 10 + 48),
                0xC8, 0xA3, 0xB0, 0xE8, 0xB5, 0xBF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                (byte)(DateTime.Now.Day), 0x09, 0x09, 0x01,
                (byte)(DateTime.Now.Day), 0x0C, 0x0C, 0x02,
                (byte)(DateTime.Now.Day), 0x0F, 0x0F, 0x03,
                (byte)(DateTime.Now.Day), 0x12, 0x12, 0x04,
                (byte)(DateTime.Now.Day), 0x15, 0x15, 0x05,
                (byte)(DateTime.Now.Day + 1), 0x00, 0x00, 0x06,
                (byte)(DateTime.Now.Day + 1), 0x03, 0x03, 0x07,
                (byte)(DateTime.Now.Day + 1), 0x06, 0x06, 0x08,
                (byte)(DateTime.Now.Day + 1), 0x09, 0x09, 0x01,
                (byte)(DateTime.Now.Day + 1), 0x0C, 0x0C, 0x02,
                (byte)(DateTime.Now.Day + 1), 0x0F, 0x0F, 0x03,
                (byte)(DateTime.Now.Day + 1), 0x12, 0x12, 0x04,
                (byte)(DateTime.Now.Day + 1), 0x15, 0x15, 0x05,
                (byte)(DateTime.Now.Day + 2), 0x00, 0x00, 0x06,
                0x00,
            };

            cmdBufferWriteButtonInt_Click(CRCcheck(ByteArray));
        }
        private void WeatherData4BufferWriteButtonInt_Click()
        {
            byte[] ByteArray =
            {
                0x18,0x02,
                (byte)(DateTime.Now.Year / 1000 + 48), (byte)((DateTime.Now.Year / 100) % 10 + 48), (byte)((DateTime.Now.Year / 10) % 10 + 48), (byte)(DateTime.Now.Year % 10 + 48),
                (byte)(DateTime.Now.Month / 10 + 48), (byte)(DateTime.Now.Month % 10 + 48), (byte)(DateTime.Now.Day / 10 + 48), (byte)(DateTime.Now.Day % 10 + 48),
                (byte)(DateTime.Now.Hour / 10 + 48), (byte)(DateTime.Now.Hour % 10 + 48), (byte)(DateTime.Now.Minute / 10 + 48), (byte)(DateTime.Now.Minute % 10 + 48),
                (byte)(DateTime.Now.Second / 10 + 48), (byte)(DateTime.Now.Second % 10 + 48),
                0xC3, 0xBB, 0xB7, 0xAE, 0xB8, 0xAE, 0xB5, 0xBF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                (byte)(DateTime.Now.Day + 1), 0x12, 0x00, 0x00,
                (byte)(DateTime.Now.Day + 1), 0x15, 0x01, 0x01,
                0x01, 0x00, 0x04, 0x04,
                0x01, 0x02, 0x05, 0x05,
                0x01, 0x04, 0x06, 0x06,
                0x01, 0x06, 0x07, 0x01,
                0x01, 0x08, 0x08, 0x02,
                0x01, 0x0a, 0x09, 0x03,
                0x01, 0x0c, 0x0A, 0x04,
                0x01, 0x0e, 0x0B, 0x05,
                0x01, 0x10, 0x0C, 0x06,
                0x01, 0x12, 0xF3, 0x00,
                0x01, 0x14, 0x0C, 0x06,
                0x01, 0x16, 0xF3, 0x00,
                0x00,
            };

            cmdBufferWriteButtonInt_Click(CRCcheck(ByteArray));
        }

        private void WeatherData3BufferWriteButtonInt_Click()
        {
            byte[] ByteArray =
            {
                0x18,0x02,
                (byte)(DateTime.Now.Year / 1000 + 48), (byte)((DateTime.Now.Year / 100) % 10 + 48), (byte)((DateTime.Now.Year / 10) % 10 + 48), (byte)(DateTime.Now.Year % 10 + 48),
                (byte)(DateTime.Now.Month / 10 + 48), (byte)(DateTime.Now.Month % 10 + 48), (byte)(DateTime.Now.Day / 10 + 48), (byte)(DateTime.Now.Day % 10 + 48),
                (byte)(DateTime.Now.Hour / 10 + 48), (byte)(DateTime.Now.Hour % 10 + 48), (byte)(DateTime.Now.Minute / 10 + 48), (byte)(DateTime.Now.Minute % 10 + 48),
                (byte)(DateTime.Now.Second / 10 + 48), (byte)(DateTime.Now.Second % 10 + 48),
                0xBE, 0xE7, 0xC0, 0xE7, 0xB5, 0xBF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                (byte)(DateTime.Now.Day+1), 0x02, 0x63, 0x07,
                (byte)(DateTime.Now.Day+1), 0x03, 0x9d, 0x06,
                (byte)(DateTime.Now.Day+1), 0x04, 0xa6, 0x05,
                (byte)(DateTime.Now.Day+1), 0x05, 0xb0, 0x04,
                (byte)(DateTime.Now.Day+1), 0x06, 0xba, 0x03,
                (byte)(DateTime.Now.Day+1), 0x07, 0xc4, 0x02,
                (byte)(DateTime.Now.Day+1), 0x08, 0xce, 0x01,
                (byte)(DateTime.Now.Day+1), 0x09, 0xd8, 0x08,
                (byte)(DateTime.Now.Day+1), 0x0a, 0xe2, 0x07,
                (byte)(DateTime.Now.Day+1), 0x0b, 0xec, 0x06,
                (byte)(DateTime.Now.Day+1), 0x0c, 0xf6, 0x05,
                (byte)(DateTime.Now.Day+1), 0x0d, 0x00, 0x04,
                (byte)(DateTime.Now.Day+1), 0x0e, 0x0a, 0x02,
                (byte)(DateTime.Now.Day+1), 0x0f, 0x14, 0x03,
                0x00,
            };

            cmdBufferWriteButtonInt_Click(CRCcheck(ByteArray));
        }

        private void WeatherData2BufferWriteButtonInt_Click()
        {
            byte[] ByteArray =
            {
                0x18,0x02,
                (byte)(DateTime.Now.Year / 1000 + 48), (byte)((DateTime.Now.Year / 100) % 10 + 48), (byte)((DateTime.Now.Year / 10) % 10 + 48), (byte)(DateTime.Now.Year % 10 + 48),
                (byte)(DateTime.Now.Month / 10 + 48), (byte)(DateTime.Now.Month % 10 + 48), (byte)(DateTime.Now.Day / 10 + 48), (byte)(DateTime.Now.Day % 10 + 48),
                (byte)(DateTime.Now.Hour / 10 + 48), (byte)(DateTime.Now.Hour % 10 + 48), (byte)(DateTime.Now.Minute / 10 + 48), (byte)(DateTime.Now.Minute % 10 + 48),
                (byte)(DateTime.Now.Second / 10 + 48), (byte)(DateTime.Now.Second % 10 + 48),
                0xC0, 0xE5, 0xB5, 0xBF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                (byte)(DateTime.Now.Day), 0x09, 0xff, 0x07,
                (byte)(DateTime.Now.Day), 0x0a, 0xfe, 0x08,
                (byte)(DateTime.Now.Day), 0x0b, 0xfd, 0x01,
                (byte)(DateTime.Now.Day), 0x0c, 0xfc, 0x02,
                (byte)(DateTime.Now.Day), 0x0d, 0xfb, 0x03,
                (byte)(DateTime.Now.Day), 0x0e, 0xfa, 0x04,
                (byte)(DateTime.Now.Day), 0x0f, 0xf9, 0x05,
                (byte)(DateTime.Now.Day), 0x10, 0xf8, 0x06,
                (byte)(DateTime.Now.Day), 0x11, 0xf7, 0x07,
                (byte)(DateTime.Now.Day), 0x12, 0xf6, 0x08,
                (byte)(DateTime.Now.Day), 0x13, 0xf5, 0x01,
                (byte)(DateTime.Now.Day), 0x14, 0xf4, 0x02,
                (byte)(DateTime.Now.Day), 0x15, 0xf3, 0x03,
                (byte)(DateTime.Now.Day), 0x16, 0xf2, 0x04,
                0x00,
            };

            cmdBufferWriteButtonInt_Click(CRCcheck(ByteArray));
        }

        private void WeatherData1BufferWriteButtonInt_Click()
        {
            byte[] ByteArray =
            {
                0x18,0x02,
                (byte)(DateTime.Now.Year / 1000 + 48), (byte)((DateTime.Now.Year / 100) % 10 + 48), (byte)((DateTime.Now.Year / 10) % 10 + 48), (byte)(DateTime.Now.Year % 10 + 48),
                (byte)(DateTime.Now.Month / 10 + 48), (byte)(DateTime.Now.Month % 10 + 48), (byte)(DateTime.Now.Day / 10 + 48), (byte)(DateTime.Now.Day % 10 + 48),
                (byte)(DateTime.Now.Hour / 10 + 48), (byte)(DateTime.Now.Hour % 10 + 48), (byte)(DateTime.Now.Minute / 10 + 48), (byte)(DateTime.Now.Minute % 10 + 48),
                (byte)(DateTime.Now.Second / 10 + 48), (byte)(DateTime.Now.Second % 10 + 48),
                0xB5, 0xBF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x01, 0x00, 0x10, 0x01,
                0x01, 0x01, 0x11, 0x02,
                0x01, 0x02, 0x12, 0x03,
                0x01, 0x03, 0x13, 0x04,
                0x01, 0x04, 0x14, 0x05,
                0x01, 0x05, 0x15, 0x06,
                0x01, 0x06, 0x16, 0x07,
                0x01, 0x07, 0x17, 0x08,
                0x01, 0x08, 0x18, 0x01,
                0x01, 0x09, 0x19, 0x02,
                0x01, 0x0a, 0x1A, 0x03,
                0x01, 0x0b, 0x1B, 0x04,
                0x01, 0x0c, 0x1C, 0x05,
                0x01, 0x0d, 0x1d, 0x06,
                0x00,
            };

            cmdBufferWriteButtonInt_Click(CRCcheck(ByteArray));
        }
        private void AppMessageWriteButtonInt_Click()
        {
            string tempSubject = string.Empty;
            string tempContents = string.Empty;
            byte[] byteSubject;
            byte[] byteContents;

            byte[] ByteArray = {
                0x1A, 0x02,
                (byte)(DateTime.Now.Year / 1000 + 48),
                (byte)((DateTime.Now.Year / 100) % 10 + 48),
                (byte)((DateTime.Now.Year / 10) % 10 + 48),
                (byte)(DateTime.Now.Year % 10 + 48),
                (byte)(DateTime.Now.Month / 10 + 48),
                (byte)(DateTime.Now.Month % 10 + 48),
                (byte)(DateTime.Now.Day / 10 + 48),
                (byte)(DateTime.Now.Day % 10 + 48),
                (byte)(DateTime.Now.Hour / 10 + 48),
                (byte)(DateTime.Now.Hour % 10 + 48),
                (byte)(DateTime.Now.Minute / 10 + 48),
                (byte)(DateTime.Now.Minute % 10 + 48),
                (byte)(DateTime.Now.Second / 10 + 48),
                (byte)(DateTime.Now.Second % 10 + 48),
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // subject
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // subject

                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents1
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents2
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents3
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents4
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents5
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents6
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents7
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents8
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents9
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents10
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents11
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents12
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents13
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents14
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents15
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents16
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents17
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents18
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents19
                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Contents20

                0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, // Padding
            };

            if (!String.IsNullOrEmpty(AppMessageSubject.Text) && !String.IsNullOrEmpty(AppMessageContents.Text))
            {

                //Encoding ksc5601 = Encoding.GetEncoding(51949);
                Encoding ksc5601 = Encoding.GetEncoding(UTF8Encoding.UTF8.CodePage);

                tempSubject = AppMessageSubject.Text;
                byteSubject = ksc5601.GetBytes(tempSubject);

                if (byteSubject.Length > 20)
                {
                    rootPage.NotifyUser($"Error Subject length > 20 ", NotifyType.ErrorMessage);
                }
                else
                {
                    byteSubject.CopyTo(ByteArray, 16);
                    //for (int i = 0; i < byteSubject.Length; i++)
                    //{
                    //    ByteArray[i + 16] = byteSubject[i];
                    //}
                }

                tempContents = AppMessageContents.Text;
                byteContents = ksc5601.GetBytes(tempContents);

                if (byteContents.Length > 200)
                {
                    rootPage.NotifyUser($"Error byteContents length > 200 ", NotifyType.ErrorMessage);
                }
                else
                {
                    byteContents.CopyTo(ByteArray, 36);

                    //for (int i = 0; i < byteContents.Length; i++)
                    //{
                    //    ByteArray[i + 36] = byteContents[i];
                    //}
                }
                
                cmdBufferWriteButtonInt_Click(CRCcheck(ByteArray));
            }
            else
            {
                rootPage.NotifyUser($"Error none message ", NotifyType.ErrorMessage);
            }
        }
        private async void AppMessageKRWriteButtonInt_Click()
        {
            IBuffer buffer = CryptographicBuffer.ConvertStringToBinary("1A:02:32:30:32:31:30:38:31:37:31:31:33:37:30:33:" +
                "C7:D1:B1:DB:20:20:C5:D7:BD:BA:C6:AE:00:00:00:00:00:00:00:00:" +
                "C0:DF:20:20:C1:F6:B3:BB:BD:C3:C1:D2:3F:20:B4:C3:20:20:B0:C7:" +
                "B0:AD:C7:CF:BD:C3:B0:ED:20:20:B8:B8:BB:E7:BF:A1:20:20:C6:F2:" +
                "B0:AD:C7:CF:BD:C3:B1:E6:20:20:B1:E2:BF:F8:20:20:B5:E5:B8:B3:" +
                "B4:CF:B4:D9:2E:40", BinaryStringEncoding.Utf8);

            await WriteBufferToSelectedCharacteristicAsync(buffer);
        }
        private async void AppMessageENWriteButtonInt_Click()
        {
            IBuffer buffer = CryptographicBuffer.ConvertStringToBinary("1A:02:32:30:32:31:30:35:30:33:31:35:35:30:31:39:" +
                "45:6E:67:6C:69:73:68:54:65:73:74:00:00:00:00:00:00:00:00:00:" +
                "41:42:43:44:45:46:47:48:49:4A:4B:4C:4D:4E:4F:50:51:52:53:54:" +
                "55:56:57:58:59:5A:61:62:63:64:65:66:67:68:69:6A:6B:6C:6D:6E:" +
                "6F:70:71:72:73:74:75:76:77:78:79:7A:46", BinaryStringEncoding.Utf8);

            await WriteBufferToSelectedCharacteristicAsync(buffer);
        }
        private async void AppMessageSymbolWriteButtonInt_Click()
        {
            IBuffer buffer = CryptographicBuffer.ConvertStringToBinary("1A:02:32:30:32:31:30:35:30:33:31:35:35:30:31:39:" +
                "C6:AF:B9:AE:54:65:73:74:00:00:00:00:00:00:00:00:00:00:00:00:" +
                "20:21:22:23:24:25:26:27:28:29:2A:2B:2C:2D:2E:2F:3A:3B:3C:3D:" +
                "3E:3F:40:5B:5C:5D:5E:5F:60:7B:7C:7D:7E:A0", BinaryStringEncoding.Utf8);

            await WriteBufferToSelectedCharacteristicAsync(buffer);
        }

        private void DateTimeNowBufferWriteButtonInt_Click()
        {
            byte[] ByteArray =
            {
                0x01,0x02,
                (byte)(DateTime.Now.Year / 1000 + 48),
                (byte)((DateTime.Now.Year / 100) % 10 + 48),
                (byte)((DateTime.Now.Year / 10) % 10 + 48),
                (byte)(DateTime.Now.Year % 10 + 48),
                (byte)(DateTime.Now.Month / 10 + 48),
                (byte)(DateTime.Now.Month % 10 + 48),
                (byte)(DateTime.Now.Day / 10 + 48),
                (byte)(DateTime.Now.Day % 10 + 48),
                (byte)(DateTime.Now.Hour / 10 + 48),
                (byte)(DateTime.Now.Hour % 10 + 48),
                (byte)(DateTime.Now.Minute / 10 + 48),
                (byte)(DateTime.Now.Minute % 10 + 48),
                (byte)(DateTime.Now.Second / 10 + 48),
                (byte)(DateTime.Now.Second % 10 + 48),
                0x10,0x00
            };

            cmdBufferWriteButtonInt_Click(CRCcheck(ByteArray));
        }
        private void IncomingCallBufferWriteButtonInt_Click()
        {
            byte[] ByteArray = { 0x23,0x02, 0x01, 0x03, 0x29 };
            cmdBufferWriteButtonInt_Click(ByteArray);
        }
        private void ShortCutBufferWriteButtonInt_Click()
        {
            byte[] ByteArray = { 0x05, 0x02, 0x32, 0x30, 0x31, 0x39, 0x31, 0x31, 0x31, 0x33, 0x31, 0x39, 0x32, 0x32, 0x30, 0x30, 0x01, 0x11, 0xD9 };
            cmdBufferWriteButtonInt_Click(ByteArray);
        }

        private void UserInfoDataTransferWriteButtonInt_Click()
        {
            byte height = 0xAF;
            byte weight = 0x50;
            byte gender = 0x01;

            
            if (!String.IsNullOrEmpty(UserInfoHeight.Text))
            {
                var isValidHeightValue = Int32.TryParse(UserInfoHeight.Text, out int readValue);
                if (isValidHeightValue)
                {
                    height = (byte)readValue;
                }
            }

            if (!String.IsNullOrEmpty(UserInfoWeight.Text))
            {
                var isValidWeightValue = Int32.TryParse(UserInfoWeight.Text, out int readValue);
                if (isValidWeightValue)
                {
                    weight = (byte)readValue;
                }
            }
            
            if (UserInfoGender.SelectedItem == MALE)
            {
                gender = (byte)0x01;
            }
            else if (UserInfoGender.SelectedItem == FEMALE)
            {
                gender = (byte)0x02;
            }

            byte[] ByteArray = {
                0x06, 0x02,
                (byte)(DateTime.Now.Year / 1000 + 48),
                (byte)((DateTime.Now.Year / 100) % 10 + 48),
                (byte)((DateTime.Now.Year / 10) % 10 + 48),
                (byte)(DateTime.Now.Year % 10 + 48),
                (byte)(DateTime.Now.Month / 10 + 48),
                (byte)(DateTime.Now.Month % 10 + 48),
                (byte)(DateTime.Now.Day / 10 + 48),
                (byte)(DateTime.Now.Day % 10 + 48),
                (byte)(DateTime.Now.Hour / 10 + 48),
                (byte)(DateTime.Now.Hour % 10 + 48),
                (byte)(DateTime.Now.Minute / 10 + 48),
                (byte)(DateTime.Now.Minute % 10 + 48),
                (byte)(DateTime.Now.Second / 10 + 48),
                (byte)(DateTime.Now.Second % 10 + 48),
                height,weight,gender,
                0xF7,0x00
            };

            cmdBufferWriteButtonInt_Click(CRCcheck(ByteArray));
        }

        private void GameRecordsTransferWriteButtonInt_Click()
        {
            byte[] total = { 0x00, 0x64 };
            byte[] Victory = { 0x00, 0x4D };
            byte[] Defeat = { 0x00, 0x17 };
            int t = 0,
                v = 0,
                d = 0;
            
            if (!String.IsNullOrEmpty(TBVictory.Text))
            {
                var isValidHeightValue = Int32.TryParse(TBVictory.Text, out int readValue);
                if (isValidHeightValue)
                {
                    v = readValue;
                }
            }

            if (!String.IsNullOrEmpty(TBDefeat.Text))
            {
                var isValidWeightValue = Int32.TryParse(TBDefeat.Text, out int readValue);
                if (isValidWeightValue)
                {
                    d = readValue;
                }
            }

            if (!String.IsNullOrEmpty(TBVictory.Text) && !String.IsNullOrEmpty(TBDefeat.Text))
            {
                t = v + d;

                if (t < 0xff)
                {
                    total[0] = 0x00;
                    total[1] = (byte)t;
                }
                else
                {
                    total[0] = (byte)(t >> 8);
                    total[1] = (byte)(t & 0xFF);
                }

                if (v < 0xff)
                {
                    Victory[0] = 0x00;
                    Victory[1] = (byte)v;
                }
                else
                {
                    Victory[0] = (byte)(v >> 8);
                    Victory[1] = (byte)(v & 0xFF);
                }

                if (d < 0xff)
                {
                    Defeat[0] = 0x00;
                    Defeat[1] = (byte)d;
                }
                else
                {
                    Defeat[0] = (byte)(d >> 8);
                    Defeat[1] = (byte)(d & 0xFF);
                }
            }

            byte[] ByteArray = {
                0x1B, 0x02,
                (byte)(DateTime.Now.Year / 1000 + 48),
                (byte)((DateTime.Now.Year / 100) % 10 + 48),
                (byte)((DateTime.Now.Year / 10) % 10 + 48),
                (byte)(DateTime.Now.Year % 10 + 48),
                (byte)(DateTime.Now.Month / 10 + 48),
                (byte)(DateTime.Now.Month % 10 + 48),
                (byte)(DateTime.Now.Day / 10 + 48),
                (byte)(DateTime.Now.Day % 10 + 48),
                (byte)(DateTime.Now.Hour / 10 + 48),
                (byte)(DateTime.Now.Hour % 10 + 48),
                (byte)(DateTime.Now.Minute / 10 + 48),
                (byte)(DateTime.Now.Minute % 10 + 48),
                (byte)(DateTime.Now.Second / 10 + 48),
                (byte)(DateTime.Now.Second % 10 + 48),
                total[0],total[1],Victory[0],Victory[1],Defeat[0],Defeat[1],
                0x00
            };

            cmdBufferWriteButtonInt_Click(CRCcheck(ByteArray));
        }
        private void UserInfoDataCheckrWriteButtonInt_Click()
        {
            byte[] ByteArray = { 0x16,0x02,0x18 };
            cmdBufferWriteButtonInt_Click(ByteArray);
        }
        private void CompleteFindMobileButtonInt_Click()
        {
            byte[] ByteArray = { 0x22, 0x02, 0x03, 0x03, 0x2A };
            cmdBufferWriteButtonInt_Click(ByteArray);
        }
        private void StopFindBandButtonInt_Click()
        {
            byte[] ByteArray = { 0x22, 0x02, 0x02, 0x03, 0x29 };
            cmdBufferWriteButtonInt_Click(ByteArray);
        }
        private void FindBandButtonInt_Click()
        {
            byte[] ByteArray = { 0x22, 0x02, 0x01, 0x03, 0x28 };
            cmdBufferWriteButtonInt_Click(ByteArray);
        }

        #endregion

        private async void cmdBufferWriteButtonInt_Click(byte[] ByteArray)
        {
            try
            {
                IBuffer buffer = CryptographicBuffer.CreateFromByteArray(ByteArray);

                // BT_Code: Writes the value from the buffer to the characteristic.
                var result = await selectedCharacteristic.WriteValueWithResultAsync(buffer);

                if (result.Status == GattCommunicationStatus.Success)
                {
                    rootPage.NotifyUser("Successfully wrote value to device", NotifyType.StatusMessage);
                }
                else
                {
                    rootPage.NotifyUser($"Write failed: {result.Status}", NotifyType.ErrorMessage);
                }
            }
            catch (Exception ex) when (ex.HResult == E_BLUETOOTH_ATT_INVALID_PDU)
            {
                rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
            }
            catch (Exception ex) when (ex.HResult == E_BLUETOOTH_ATT_WRITE_NOT_PERMITTED || ex.HResult == E_ACCESSDENIED)
            {
                // This usually happens when a device reports that it support writing, but it actually doesn't.
                rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
            }
        }

        private bool subscribedForNotifications = false;
        private async void ValueChangedSubscribeToggle_Click()
        {
            if (!subscribedForNotifications)
            {
                // initialize status
                GattCommunicationStatus status = GattCommunicationStatus.Unreachable;
                var cccdValue = GattClientCharacteristicConfigurationDescriptorValue.None;
                if (selectedCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
                {
                    cccdValue = GattClientCharacteristicConfigurationDescriptorValue.Indicate;
                }

                else if (selectedCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                {
                    cccdValue = GattClientCharacteristicConfigurationDescriptorValue.Notify;
                }

                try
                {
                    // BT_Code: Must write the CCCD in order for server to send indications.
                    // We receive them in the ValueChanged event handler.
                    status = await selectedCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(cccdValue);

                    if (status == GattCommunicationStatus.Success)
                    {
                        AddValueChangedHandler();
                        rootPage.NotifyUser("Successfully subscribed for value changes", NotifyType.StatusMessage);
                    }
                    else
                    {
                        rootPage.NotifyUser($"Error registering for value changes: {status}", NotifyType.ErrorMessage);
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    // This usually happens when a device reports that it support indicate, but it actually doesn't.
                    rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                }
            }
            else
            {
                try
                {
                    // BT_Code: Must write the CCCD in order for server to send notifications.
                    // We receive them in the ValueChanged event handler.
                    // Note that this sample configures either Indicate or Notify, but not both.
                    var result = await
                            selectedCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                                GattClientCharacteristicConfigurationDescriptorValue.None);
                    if (result == GattCommunicationStatus.Success)
                    {
                        subscribedForNotifications = false;
                        RemoveValueChangedHandler();
                        rootPage.NotifyUser("Successfully un-registered for notifications", NotifyType.StatusMessage);
                    }
                    else
                    {
                        rootPage.NotifyUser($"Error un-registering for notifications: {result}", NotifyType.ErrorMessage);
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    // This usually happens when a device reports that it support notify, but it actually doesn't.
                    rootPage.NotifyUser(ex.Message, NotifyType.ErrorMessage);
                }
            }
        }

        private async void Characteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            // BT_Code: An Indicate or Notify reported that the value has changed.
            // Display the new value with a timestamp.
            var newValue = FormatValueByPresentation(args.CharacteristicValue, presentationFormat);
            var message = $"Value at {DateTime.Now:hh:mm:ss.FFF}: {newValue}";
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                () => CharacteristicLatestValue.Text = message);
        }

        private string FormatValueByPresentation(IBuffer buffer, GattPresentationFormat format)
        {
            // BT_Code: For the purpose of this sample, this function converts only UInt32 and
            // UTF-8 buffers to readable text. It can be extended to support other formats if your app needs them.

            byte[] data;
            CryptographicBuffer.CopyToByteArray(buffer, out data);
            
            if (format != null)
            {
                if (format.FormatType == GattPresentationFormatTypes.UInt32 && data.Length >= 4)
                {
                    return BitConverter.ToInt16(data, 0).ToString();
                }
                else if (format.FormatType == GattPresentationFormatTypes.Utf8)
                {
                    try
                    {
                        return Encoding.UTF8.GetString(data);
                    }
                    catch (ArgumentException)
                    {
                        return "(error: Invalid UTF-8 string)";
                    }
                }
                else
                {
                    // Add support for other format types as needed.
                    return "Unsupported format: " + CryptographicBuffer.EncodeToHexString(buffer);
                }
            }
            else if (data != null)
            {
                // We don't know what format to use. Let's try some well-known profiles, or default back to UTF-8.
                if (selectedCharacteristic.Uuid.Equals(GattCharacteristicUuids.HeartRateMeasurement))
                {
                    try
                    {
                        return "Heart Rate: " + ParseHeartRateValue(data).ToString();
                    }
                    catch (ArgumentException)
                    {
                        return "Heart Rate: (unable to parse)";
                    }
                }
                else if (selectedCharacteristic.Uuid.Equals(GattCharacteristicUuids.BatteryLevel))
                {
                    try
                    {
                        // battery level is encoded as a percentage value in the first byte according to
                        // https://www.bluetooth.com/specifications/gatt/viewer?attributeXmlFile=org.bluetooth.characteristic.battery_level.xml
                        return "Battery Level: " + data[0].ToString() + "%";
                    }
                    catch (ArgumentException)
                    {
                        return "Battery Level: (unable to parse)";
                    }
                }
                // This is our custom calc service Result UUID. Format it like an Int
                else if (selectedCharacteristic.Uuid.Equals(Constants.ResultCharacteristicUuid))
                {
                    return BitConverter.ToInt32(data, 0).ToString();
                }
                // No guarantees on if a characteristic is registered for notifications.
                else if (registeredCharacteristic != null)
                {
                    // This is our custom calc service Result UUID. Format it like an Int
                    if (registeredCharacteristic.Uuid.Equals(Constants.ResultCharacteristicUuid))
                    {
                        return BitConverter.ToInt32(data, 0).ToString();
                    }
                }
                else
                {
                    try
                    {
                        return "Unknown format: " + Encoding.UTF8.GetString(data);
                    }
                    catch (ArgumentException)
                    {
                        return "Unknown format";
                    }
                }
            }
            else
            {
                return "Empty data received";
            }
            return "Unknown format";
        }

        /// <summary>
        /// Process the raw data received from the device into application usable data,
        /// according the the Bluetooth Heart Rate Profile.
        /// https://www.bluetooth.com/specifications/gatt/viewer?attributeXmlFile=org.bluetooth.characteristic.heart_rate_measurement.xml&u=org.bluetooth.characteristic.heart_rate_measurement.xml
        /// This function throws an exception if the data cannot be parsed.
        /// </summary>
        /// <param name="data">Raw data received from the heart rate monitor.</param>
        /// <returns>The heart rate measurement value.</returns>
        private static ushort ParseHeartRateValue(byte[] data)
        {
            // Heart Rate profile defined flag values
            const byte heartRateValueFormat = 0x01;

            byte flags = data[0];
            bool isHeartRateValueSizeLong = ((flags & heartRateValueFormat) != 0);

            if (isHeartRateValueSizeLong)
            {
                return BitConverter.ToUInt16(data, 1);
            }
            else
            {
                return data[1];
            }
        }
                
    }
}
