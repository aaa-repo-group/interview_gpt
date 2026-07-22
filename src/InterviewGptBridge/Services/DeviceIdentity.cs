using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace InterviewGptBridge.Services;

public static class DeviceIdentity
{
    public static string GetStableDeviceHash()
    {
        var raw = string.Join("|", ReadMachineGuid(), Environment.MachineName, Environment.UserName);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }

    private static string ReadMachineGuid()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid",
                null) as string ?? "unknown-machine-guid";
        }
        catch
        {
            return "unknown-machine-guid";
        }
    }
}
