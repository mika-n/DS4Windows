﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace DS4Windows
{
    // VidPidFeatureSet feature bit-flags (the default in VidPidInfo is zero value = standard DS4 behavior):
    //
    // DefaultDS4 (zero value) = Standard DS4 compatible communication (as it has been in DS4Win app for years)
    // OnlyInputData0x01    = The incoming HID report data structure does NOT send 0x11 packet even in DS4 mode over BT connection. If this flag is set then accept "PC-friendly" 0x01 HID report data in BT just like how DS4 behaves in USB mode.
    // OnlyOutputData0x05   = Outgoing HID report write data structure does NOT support DS4 BT 0x11 data structure. Use only "USB type of" 0x05 data packets even in BT connections.
    // NoOutputData         = Gamepad doesn't support lightbar and rumble data writing at all. DS4Win app does not try to write out anything to gamepad.
    // NoBatteryReading     = Gamepad doesn't send battery readings in the same format than DS4 gamepad (DS4Win app reports always 0% and starts to blink lightbar). Skip reading a battery fields and report fixed 99% battery level to avoid "low battery" LED flashes.
    // NoGyroCalib          = Gamepad doesn't support or need gyro calibration routines. Skip gyro calibration if this flag is set. Some gamepad do have gyro, but don't support calibration or gyro sensors are missing.
    //
    [Flags]
    public enum VidPidFeatureSet : ushort { DefaultDS4 = 0, OnlyInputData0x01 = 1, OnlyOutputData0x05 = 2, NoOutputData = 4, NoBatteryReading = 8, NoGyroCalib = 16 };

    public class VidPidInfo
    {
        public readonly int vid;
        public readonly int pid;
        public readonly string name;
        public readonly VidPidFeatureSet featureSet;
        internal VidPidInfo(int vid, int pid, string name = "Generic DS4", VidPidFeatureSet featureSet = VidPidFeatureSet.DefaultDS4)
        {
            this.vid = vid;
            this.pid = pid;
            this.name = name;
            this.featureSet = featureSet;
        }
    }

    public class RequestElevationArgs : EventArgs
    {
        public const int STATUS_SUCCESS = 0;
        public const int STATUS_INIT_FAILURE = -1;
        private int statusCode = STATUS_INIT_FAILURE;
        private string instanceId;
        public int StatusCode
        {
            get => statusCode;
            set => statusCode = value;
        }
        public string InstanceId { get => instanceId; }

        public RequestElevationArgs(string instanceId)
        {
            this.instanceId = instanceId;
        }
    }

    public delegate void RequestElevationDelegate(RequestElevationArgs args);

    public class CheckVirtualInfo : EventArgs
    {
        private string deviceInstanceId;
        public string DeviceInstanceId
        {
            get => deviceInstanceId;
            set => deviceInstanceId = value;
        }

        private string propertyValue;
        public string PropertyValue { get => propertyValue; set => propertyValue = value; }

        public CheckVirtualInfo() : base()
        {
        }
    }

    public delegate CheckVirtualInfo CheckVirtualDelegate(string deviceInstanceId);

    public class DS4Devices
    {
        // (HID device path, DS4Device)
        private static Dictionary<string, DS4Device> Devices = new Dictionary<string, DS4Device>();
        // (MacAddress, DS4Device)
        private static Dictionary<string, DS4Device> serialDevices = new Dictionary<string, DS4Device>();
        private static HashSet<string> deviceSerials = new HashSet<string>();
        private static HashSet<string> DevicePaths = new HashSet<string>();
        // Keep instance of opened exclusive mode devices not in use (Charging while using BT connection)
        private static List<HidDevice> DisabledDevices = new List<HidDevice>();
        private static Stopwatch sw = new Stopwatch();
        public static event RequestElevationDelegate RequestElevation;
        public static CheckVirtualDelegate checkVirtualFunc = null;
        public static bool isExclusiveMode = false;
        internal const int SONY_VID = 0x054C;
        internal const int RAZER_VID = 0x1532;
        internal const int NACON_VID = 0x146B;
        internal const int HORI_VID = 0x0F0D;

        // https://support.steampowered.com/kb_article.php?ref=5199-TOKV-4426&l=english web site has a list of other PS4 compatible device VID/PID values and brand names. 
        // However, not all those are guaranteed to work with DS4Windows app so support is added case by case when users of DS4Windows app tests non-official DS4 gamepads.

        private static VidPidInfo[] knownDevices =
        {
            new VidPidInfo(SONY_VID, 0xBA0, "Sony WA"),
            new VidPidInfo(SONY_VID, 0x5C4, "DS4 v.1"),
            new VidPidInfo(SONY_VID, 0x09CC, "DS4 v.2"),
            new VidPidInfo(RAZER_VID, 0x1000, "Razer Raiju PS4"),
            new VidPidInfo(NACON_VID, 0x0D01, "Nacon Revol Pro v.1", VidPidFeatureSet.NoGyroCalib), // Nacon Revolution Pro v1 and v2 doesn't support DS4 gyro calibration routines
            new VidPidInfo(NACON_VID, 0x0D02, "Nacon Revol Pro v.2", VidPidFeatureSet.NoGyroCalib),
            new VidPidInfo(HORI_VID, 0x00EE, "Hori PS4 Mini", VidPidFeatureSet.NoOutputData | VidPidFeatureSet.NoBatteryReading | VidPidFeatureSet.NoGyroCalib),  // Hori PS4 Mini Wired Gamepad
            new VidPidInfo(0x7545, 0x0104, "Armor 3 LU Cobra"), // Armor 3 Level Up Cobra
            new VidPidInfo(0x2E95, 0x7725, "Scuf Vantage"), // Scuf Vantage gamepad
            new VidPidInfo(0x11C0, 0x4001, "PS4 Fun"), // PS4 Fun Controller
            new VidPidInfo(RAZER_VID, 0x1007, "Razer Raiju TE"), // Razer Raiju Tournament Edition (wired)
            new VidPidInfo(RAZER_VID, 0x100A, "Razer Raiju TE BT", VidPidFeatureSet.OnlyInputData0x01 | VidPidFeatureSet.OnlyOutputData0x05 | VidPidFeatureSet.NoBatteryReading | VidPidFeatureSet.NoGyroCalib), // Razer Raiju Tournament Edition (BT). Incoming report data is in "ds4 USB format" (32 bytes) in BT. Also, WriteOutput uses "usb" data packet type in BT.
            new VidPidInfo(RAZER_VID, 0x1004, "Razer Raiju UE USB"), // Razer Raiju Ultimate Edition (wired)
            new VidPidInfo(RAZER_VID, 0x1009, "Razer Raiju UE BT", VidPidFeatureSet.OnlyInputData0x01 | VidPidFeatureSet.OnlyOutputData0x05 | VidPidFeatureSet.NoBatteryReading | VidPidFeatureSet.NoGyroCalib), // Razer Raiju Ultimate Edition (BT)
            new VidPidInfo(SONY_VID, 0x05C5, "CronusMax (PS4 Mode)"), // CronusMax (PS4 Output Mode)
            new VidPidInfo(0x0C12, 0x57AB, "Warrior Joypad JS083", VidPidFeatureSet.NoGyroCalib), // Warrior Joypad JS083 (wired). Custom lightbar color doesn't work, but everything else works OK (except touchpad and gyro because the gamepad doesnt have those).
            new VidPidInfo(0x0C12, 0x0E16, "Steel Play MetalTech"), // Steel Play Metaltech P4 (wired)
            new VidPidInfo(NACON_VID, 0x0D08, "Nacon Revol U Pro"), // Nacon Revolution Unlimited Pro
            new VidPidInfo(NACON_VID, 0x0D10, "Nacon Revol Infinite"), // Nacon Revolution Infinite (sometimes known as Revol Unlimited Pro v2?). Touchpad, gyro, rumble, "led indicator" lightbar.
            new VidPidInfo(HORI_VID, 0x0084, "Hori Fighting Cmd"), // Hori Fighting Commander (special kind of gamepad without touchpad or sticks. There is a hardware switch to alter d-pad type between dpad and LS/RS)
            new VidPidInfo(NACON_VID, 0x0D13, "Nacon Revol Pro v.3"),
            new VidPidInfo(HORI_VID, 0x0066, "Horipad FPS Plus", VidPidFeatureSet.NoGyroCalib), // Horipad FPS Plus (wired only. No light bar, rumble and Gyro/Accel sensor. Cannot Hide "HID-compliant vendor-defined device" in USB Composite Device. Other feature works fine.)
            new VidPidInfo(0x9886, 0x0025, "Astro C40", VidPidFeatureSet.NoGyroCalib), // Astro C40 (wired and BT. Works if Astro specific xinput drivers haven't been installed. Uninstall those to use the pad as dinput device)
            new VidPidInfo(0x0E8F, 0x1114, "Gamo2 Divaller", VidPidFeatureSet.NoGyroCalib), // Gamo2 Divaller (wired only. Light bar not controllable. No touchpad, gyro or rumble)
            new VidPidInfo(HORI_VID, 0x0101, "Hori Mini Hatsune Miku FT", VidPidFeatureSet.NoGyroCalib), // Hori Mini Hatsune Miku FT (wired only. No light bar, gyro or rumble)
            new VidPidInfo(HORI_VID, 0x00C9, "Hori Taiko Controller", VidPidFeatureSet.NoGyroCalib), // Hori Taiko Controller (wired only. No light bar, touchpad, gyro, rumble, sticks or triggers)
            new VidPidInfo(0x0C12, 0x1E1C, "SnakeByte Game:Pad 4S", VidPidFeatureSet.NoGyroCalib | VidPidFeatureSet.NoBatteryReading), // SnakeByte Gamepad for PS4 (wired only. No gyro. No light bar). If it doesn't work then try the latest gamepad firmware from https://mysnakebyte.com/

            // Following gamepads don't work for some reason
            new VidPidInfo(NACON_VID, 0x0603, "Nacon Wired Compact", VidPidFeatureSet.NoGyroCalib), // No gyro/lightbar/speaker but has 2xrumble motors. USB only even when inputReport is almost like BT (length 545). https://www.nacongaming.com/it/product/gamepads/gamepads-gaming/compact-controller-ps4/
            new VidPidInfo(HORI_VID, 0x0104, "Hori Onyx PS4", VidPidFeatureSet.OnlyInputData0x01 | VidPidFeatureSet.OnlyOutputData0x05), // Hori Onyx PS4 gamepad (wireless). Doesn't work. MACID cannot be read. USB report 0x11 missing (just 0x01 in PC?)
            new VidPidInfo(HORI_VID, 0x0123, "Hori Wireless Lite BT", VidPidFeatureSet.NoGyroCalib), // Hori Wireless Lite BT. Special gamepad with limited DS4 functionalities (no lightbar/touchpad/rumble/gyro/L2R2 analog triggers).
            new VidPidInfo(HORI_VID, 0x0124, "Hori Wireless Lite USB", VidPidFeatureSet.NoGyroCalib), // Hori Wireless Lite USB (uses different PID in usb connection)        
        };

        public static string devicePathToInstanceId(string devicePath)
        {
            string deviceInstanceId = devicePath;
            deviceInstanceId = deviceInstanceId.Remove(0, deviceInstanceId.LastIndexOf("?\\") + 2);
            deviceInstanceId = deviceInstanceId.Remove(deviceInstanceId.LastIndexOf('{'));
            deviceInstanceId = deviceInstanceId.Replace('#', '\\');
            if (deviceInstanceId.EndsWith("\\"))
            {
                deviceInstanceId = deviceInstanceId.Remove(deviceInstanceId.Length - 1);
            }

            return deviceInstanceId;
        }

        private static bool IsRealDS4(HidDevice hDevice)
        {
            string deviceInstanceId = devicePathToInstanceId(hDevice.DevicePath);
            CheckVirtualInfo info = checkVirtualFunc(deviceInstanceId);
            return string.IsNullOrEmpty(info.PropertyValue);
            //string temp = Global.GetDeviceProperty(deviceInstanceId,
            //    NativeMethods.DEVPKEY_Device_UINumber);
            //return string.IsNullOrEmpty(temp);
        }

        // Enumerates ds4 controllers in the system
        public static void findControllers()
        {
            lock (Devices)
            {
                IEnumerable<HidDevice> hDevices = HidDevices.EnumerateDS4(knownDevices);
                if (checkVirtualFunc != null)
                {
                    hDevices = hDevices.Where(dev => IsRealDS4(dev)).Select(dev => dev);
                }

                //hDevices = from dev in hDevices where IsRealDS4(dev) select dev;
                // Sort Bluetooth first in case USB is also connected on the same controller.
                hDevices = hDevices.OrderBy<HidDevice, ConnectionType>((HidDevice d) => { return DS4Device.HidConnectionType(d); });

                List<HidDevice> tempList = hDevices.ToList();
                purgeHiddenExclusiveDevices();
                tempList.AddRange(DisabledDevices);
                int devCount = tempList.Count();
                string devicePlural = "device" + (devCount == 0 || devCount > 1 ? "s" : "");
                //Log.LogToGui("Found " + devCount + " possible " + devicePlural + ". Examining " + devicePlural + ".", false);

                AppLogger.LogToGui($"DEBUG: findControllers. Found " + devCount + " possible " + devicePlural, false);

                for (int i = 0; i < devCount; i++)
                //foreach (HidDevice hDevice in hDevices)
                {
                    HidDevice hDevice = tempList[i];

                    AppLogger.LogToGui($"DEBUG: findControllers. Idx={i} Potential DS4 gamepad. Description={hDevice.Description}  Path={hDevice.DevicePath}", false);

                    if (hDevice.Description == "HID-compliant vendor-defined device")
                        //continue; // ignore the Nacon Revolution Pro programming interface
                        AppLogger.LogToGui($"DEBUG: findControllers. Idx={i}. Debug version accepts HID-compliant vendor-defined device descriptions even when those are not always real DS4 gamepad devices.", false);
                    else if (DevicePaths.Contains(hDevice.DevicePath))
                        continue; // BT/USB endpoint already open once

                    // DEBUG: Return null metainfo if gamepad type was not identified (Unknown gamepad but using Usage=0x05 flag)
                    //VidPidInfo metainfo = knownDevices.Single(x => x.vid == hDevice.Attributes.VendorId &&
                    //    x.pid == hDevice.Attributes.ProductId);
                    VidPidInfo metainfo = knownDevices.SingleOrDefault(x => x.vid == hDevice.Attributes.VendorId &&
                                            x.pid == hDevice.Attributes.ProductId);

                    if (!hDevice.IsOpen)
                    {
                        AppLogger.LogToGui($"DEBUG: findControllers. idx={i} Opening device VID={hDevice.Attributes.VendorHexId}  PID={hDevice.Attributes.ProductHexId}  isExclusiveMode={isExclusiveMode}", false);

                        hDevice.OpenDevice(isExclusiveMode);
                        if (!hDevice.IsOpen && isExclusiveMode)
                        {
                            try
                            {
                                // Check if running with elevated permissions
                                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                                WindowsPrincipal principal = new WindowsPrincipal(identity);
                                bool elevated = principal.IsInRole(WindowsBuiltInRole.Administrator);

                                if (!elevated)
                                {
                                    // Tell the client to launch routine to re-enable a device
                                    RequestElevationArgs eleArgs = 
                                        new RequestElevationArgs(devicePathToInstanceId(hDevice.DevicePath));
                                    RequestElevation?.Invoke(eleArgs);
                                    if (eleArgs.StatusCode == RequestElevationArgs.STATUS_SUCCESS)
                                    {
                                        hDevice.OpenDevice(isExclusiveMode);
                                    }
                                }
                                else
                                {
                                    reEnableDevice(devicePathToInstanceId(hDevice.DevicePath));
                                    hDevice.OpenDevice(isExclusiveMode);
                                }
                            }
                            catch (Exception) { }
                        }
                        
                        // TODO in exclusive mode, try to hold both open when both are connected
                        if (isExclusiveMode && !hDevice.IsOpen)
                            hDevice.OpenDevice(false);
                    }

                    AppLogger.LogToGui($"DEBUG: findControllers. idx={i} IsOpen={hDevice.IsOpen}", false);

                    if (hDevice.IsOpen)
                    {
                        string serial = hDevice.readSerial();
                        bool validSerial = !serial.Equals(DS4Device.blankSerial);
                        bool newdev = true;

                        AppLogger.LogToGui($"DEBUG: findControllers. idx={i} IsValidSerial={validSerial}  Serial={serial}", false);

                        if (validSerial && deviceSerials.Contains(serial))
                        {
                            // Check if Quick Charge flag is engaged
                            if (serialDevices.TryGetValue(serial, out DS4Device tempDev) && tempDev.ReadyQuickChargeDisconnect)
                            {
                                // Need to disconnect callback here to avoid deadlock
                                tempDev.Removal -= On_Removal;
                                // Call inner removal process here instead
                                InnerRemoveDevice(tempDev);
                                // Disconnect wireless device
                                tempDev.DisconnectWireless();
                            }
                            // happens when the BT endpoint already is open and the USB is plugged into the same host
                            else if (isExclusiveMode && hDevice.IsExclusive &&
                                !DisabledDevices.Contains(hDevice))
                            {
                                // Grab reference to exclusively opened HidDevice so device
                                // stays hidden to other processes
                                DisabledDevices.Add(hDevice);
                                //DevicePaths.Add(hDevice.DevicePath);
                                newdev = false;
                            }
                        }

                        if (newdev)
                        {
                            // DEBUG: Accept unknown device if those were with usage=0x5 flag
                            if (metainfo != null)
                                AppLogger.LogToGui($"DEBUG: findControllers. idx={i} Using device {hDevice.DevicePath}  metainfo.name={metainfo.name}  metainfo.featureSet={metainfo.featureSet}", false);
                            else
                                AppLogger.LogToGui($"DEBUG: findControllers. idx={i} Using device {hDevice.DevicePath}  metainfo.name=UNKNOWN", false);

                            //DS4Device ds4Device = new DS4Device(hDevice, metainfo.name, metainfo.featureSet);
                            DS4Device ds4Device = new DS4Device(hDevice, (metainfo != null ? metainfo.name : "UNKNOWN"), (metainfo != null ? metainfo.featureSet : VidPidFeatureSet.DefaultDS4) /* DEBUG patchfix */);

                            //ds4Device.Removal += On_Removal;
                            if (!ds4Device.ExitOutputThread)
                            {
                                AppLogger.LogToGui($"DEBUG: findControllers. idx={i} OK. DS4Win uses the device as input source", false);

                                Devices.Add(hDevice.DevicePath, ds4Device);
                                DevicePaths.Add(hDevice.DevicePath);
                                deviceSerials.Add(serial);
                                serialDevices.Add(serial, ds4Device);
                            }
                            else
                            {
                                AppLogger.LogToGui($"DEBUG: findControllers. ERROR. idx={i} {hDevice.DevicePath} is in ExitOutputThread state. Failed to read inputData from the controller", false);
                            }
                        }
                    }
                }
            }
        }
        
        // Returns DS4 controllers that were found and are running
        public static IEnumerable<DS4Device> getDS4Controllers()
        {
            lock (Devices)
            {
                DS4Device[] controllers = new DS4Device[Devices.Count];
                Devices.Values.CopyTo(controllers, 0);
                return controllers;
            }
        }

        public static void stopControllers()
        {
            lock (Devices)
            {
                IEnumerable<DS4Device> devices = Devices.Values.ToArray();
                //foreach (DS4Device device in devices)
                //for (int i = 0, devCount = devices.Count(); i < devCount; i++)
                for (var devEnum = devices.GetEnumerator(); devEnum.MoveNext();)
                {
                    DS4Device device = devEnum.Current;
                    //DS4Device device = devices.ElementAt(i);
                    device.StopUpdate();
                    //device.runRemoval();
                    device.HidDevice.CloseDevice();
                }

                Devices.Clear();
                DevicePaths.Clear();
                deviceSerials.Clear();
                DisabledDevices.Clear();
                serialDevices.Clear();
            }
        }

        // Called when devices is disconnected, timed out or has input reading failure
        public static void On_Removal(object sender, EventArgs e)
        {
            DS4Device device = (DS4Device)sender;
            RemoveDevice(device);
        }

        public static void RemoveDevice(DS4Device device)
        {
            lock (Devices)
            {
                InnerRemoveDevice(device);
            }
        }

        private static void InnerRemoveDevice(DS4Device device)
        {
            if (device != null)
            {
                device.HidDevice.CloseDevice();
                Devices.Remove(device.HidDevice.DevicePath);
                DevicePaths.Remove(device.HidDevice.DevicePath);
                deviceSerials.Remove(device.MacAddress);
                serialDevices.Remove(device.MacAddress);
                //purgeHiddenExclusiveDevices();
            }
        }

        public static void UpdateSerial(object sender, EventArgs e)
        {
            lock (Devices)
            {
                DS4Device device = (DS4Device)sender;
                if (device != null)
                {
                    string devPath = device.HidDevice.DevicePath;
                    string serial = device.getMacAddress();
                    if (Devices.ContainsKey(devPath))
                    {
                        deviceSerials.Remove(serial);
                        serialDevices.Remove(serial);
                        device.updateSerial();
                        serial = device.getMacAddress();
                        if (DS4Device.isValidSerial(serial))
                        {
                            deviceSerials.Add(serial);
                            serialDevices.Add(serial, device);
                        }

                        if (device.ShouldRunCalib())
                            device.RefreshCalibration();
                    }
                }
            }
        }

        private static void purgeHiddenExclusiveDevices()
        {
            int disabledDevCount = DisabledDevices.Count;
            if (disabledDevCount > 0)
            {
                List<HidDevice> disabledDevList = new List<HidDevice>();
                for (var devEnum = DisabledDevices.GetEnumerator(); devEnum.MoveNext();)
                //for (int i = 0, arlen = disabledDevCount; i < arlen; i++)
                {
                    //HidDevice tempDev = DisabledDevices.ElementAt(i);
                    HidDevice tempDev = devEnum.Current;
                    if (tempDev != null)
                    {
                        if (tempDev.IsOpen && tempDev.IsConnected)
                        {
                            disabledDevList.Add(tempDev);
                        }
                        else if (tempDev.IsOpen)
                        {
                            if (!tempDev.IsConnected)
                            {
                                try
                                {
                                    tempDev.CloseDevice();
                                }
                                catch { }
                            }

                            if (DevicePaths.Contains(tempDev.DevicePath))
                            {
                                DevicePaths.Remove(tempDev.DevicePath);
                            }
                        }
                    }
                }

                DisabledDevices.Clear();
                DisabledDevices.AddRange(disabledDevList);
            }
        }

        public static void reEnableDevice(string deviceInstanceId)
        {
            bool success;
            Guid hidGuid = new Guid();
            NativeMethods.HidD_GetHidGuid(ref hidGuid);
            IntPtr deviceInfoSet = NativeMethods.SetupDiGetClassDevs(ref hidGuid, deviceInstanceId, 0, NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);
            NativeMethods.SP_DEVINFO_DATA deviceInfoData = new NativeMethods.SP_DEVINFO_DATA();
            deviceInfoData.cbSize = Marshal.SizeOf(deviceInfoData);
            success = NativeMethods.SetupDiEnumDeviceInfo(deviceInfoSet, 0, ref deviceInfoData);
            if (!success)
            {
                throw new Exception("Error getting device info data, error code = " + Marshal.GetLastWin32Error());
            }
            success = NativeMethods.SetupDiEnumDeviceInfo(deviceInfoSet, 1, ref deviceInfoData); // Checks that we have a unique device
            if (success)
            {
                throw new Exception("Can't find unique device");
            }

            NativeMethods.SP_PROPCHANGE_PARAMS propChangeParams = new NativeMethods.SP_PROPCHANGE_PARAMS();
            propChangeParams.classInstallHeader.cbSize = Marshal.SizeOf(propChangeParams.classInstallHeader);
            propChangeParams.classInstallHeader.installFunction = NativeMethods.DIF_PROPERTYCHANGE;
            propChangeParams.stateChange = NativeMethods.DICS_DISABLE;
            propChangeParams.scope = NativeMethods.DICS_FLAG_GLOBAL;
            propChangeParams.hwProfile = 0;
            success = NativeMethods.SetupDiSetClassInstallParams(deviceInfoSet, ref deviceInfoData, ref propChangeParams, Marshal.SizeOf(propChangeParams));
            if (!success)
            {
                throw new Exception("Error setting class install params, error code = " + Marshal.GetLastWin32Error());
            }
            success = NativeMethods.SetupDiCallClassInstaller(NativeMethods.DIF_PROPERTYCHANGE, deviceInfoSet, ref deviceInfoData);
            // TEST: If previous SetupDiCallClassInstaller fails, just continue
            // otherwise device will likely get permanently disabled.
            /*if (!success)
            {
                throw new Exception("Error disabling device, error code = " + Marshal.GetLastWin32Error());
            }
            */

            //System.Threading.Thread.Sleep(50);
            sw.Restart();
            while (sw.ElapsedMilliseconds < 100)
            {
                // Use SpinWait to keep control of current thread. Using Sleep could potentially
                // cause other events to get run out of order
                System.Threading.Thread.SpinWait(100);
            }
            sw.Stop();

            propChangeParams.stateChange = NativeMethods.DICS_ENABLE;
            success = NativeMethods.SetupDiSetClassInstallParams(deviceInfoSet, ref deviceInfoData, ref propChangeParams, Marshal.SizeOf(propChangeParams));
            if (!success)
            {
                throw new Exception("Error setting class install params, error code = " + Marshal.GetLastWin32Error());
            }
            success = NativeMethods.SetupDiCallClassInstaller(NativeMethods.DIF_PROPERTYCHANGE, deviceInfoSet, ref deviceInfoData);
            if (!success)
            {
                throw new Exception("Error enabling device, error code = " + Marshal.GetLastWin32Error());
            }

            //System.Threading.Thread.Sleep(50);
            /*sw.Restart();
            while (sw.ElapsedMilliseconds < 50)
            {
                // Use SpinWait to keep control of current thread. Using Sleep could potentially
                // cause other events to get run out of order
                System.Threading.Thread.SpinWait(100);
            }
            sw.Stop();
            */

            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }
}
