using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CodexAccountSwitcher.Core;

/// <summary>DPAPI protection scoped to the signed-in Windows user, with no UI prompts.</summary>
public static class SecretProtection
{
    private const uint CryptProtectUiForbidden = 0x1;
    private const int MaximumPayloadBytes = 128 * 1024 * 1024;
    private static readonly byte[] Header = "CAS-DPAPI-V1\0"u8.ToArray();
    private static readonly byte[] Entropy = SHA256.HashData("CodexAccountSwitcher/vault/v1"u8);
    private static readonly byte[] ZeroBlock = new byte[8192];

    public static byte[] Protect(byte[] plaintext)
    {
        CheckPlatformAndLength(plaintext);
        var inputPin = GCHandle.Alloc(plaintext, GCHandleType.Pinned);
        var entropyPin = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = default(DataBlob);
        try
        {
            var input = new DataBlob { Length = plaintext.Length, Data = inputPin.AddrOfPinnedObject() };
            var entropy = new DataBlob { Length = Entropy.Length, Data = entropyPin.AddrOfPinnedObject() };
            if (!CryptProtectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output) || output.Length <= 0 ||
                output.Length > MaximumPayloadBytes || output.Data == IntPtr.Zero)
                throw new UserFacingException("无法使用当前 Windows 用户加密账号库，操作已取消。");
            var encrypted = new byte[checked(Header.Length + output.Length)];
            Header.CopyTo(encrypted, 0);
            Marshal.Copy(output.Data, encrypted, Header.Length, output.Length);
            return encrypted;
        }
        finally
        {
            ReleaseBlob(output);
            entropyPin.Free();
            inputPin.Free();
        }
    }

    public static byte[] Unprotect(byte[] encrypted)
    {
        CheckPlatformAndLength(encrypted);
        if (encrypted.Length <= Header.Length || !encrypted.AsSpan(0, Header.Length).SequenceEqual(Header))
            throw new UserFacingException("账号库或恢复备份已损坏，或不是本工具创建的加密文件。");
        var inputPin = GCHandle.Alloc(encrypted, GCHandleType.Pinned);
        var entropyPin = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = default(DataBlob);
        var description = IntPtr.Zero;
        byte[]? plaintext = null;
        try
        {
            var input = new DataBlob
            {
                Length = encrypted.Length - Header.Length,
                Data = IntPtr.Add(inputPin.AddrOfPinnedObject(), Header.Length)
            };
            var entropy = new DataBlob { Length = Entropy.Length, Data = entropyPin.AddrOfPinnedObject() };
            if (!CryptUnprotectData(ref input, out description, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output) || output.Length <= 0 ||
                output.Length > MaximumPayloadBytes || output.Data == IntPtr.Zero)
                throw new UserFacingException("无法解密账号库：文件可能已损坏，或属于其他 Windows 用户。");
            plaintext = new byte[output.Length];
            Marshal.Copy(output.Data, plaintext, 0, output.Length);
            return plaintext;
        }
        catch
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            ReleaseBlob(output);
            if (description != IntPtr.Zero) _ = LocalFree(description);
            entropyPin.Free();
            inputPin.Free();
        }
    }

    private static void CheckPlatformAndLength(byte[] data)
    {
        if (!OperatingSystem.IsWindows())
            throw new UserFacingException("账号加密库仅支持 Windows 当前用户环境。");
        if (data is null || data.Length == 0 || data.Length > MaximumPayloadBytes)
            throw new UserFacingException("加密账号文件的大小无效，操作已取消。");
    }

    private static void ReleaseBlob(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero) return;
        try
        {
            if (blob.Length > 0 && blob.Length <= MaximumPayloadBytes)
            {
                for (var offset = 0; offset < blob.Length; offset += ZeroBlock.Length)
                    Marshal.Copy(ZeroBlock, 0, IntPtr.Add(blob.Data, offset),
                        Math.Min(ZeroBlock.Length, blob.Length - offset));
            }
        }
        finally { _ = LocalFree(blob.Data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, IntPtr description,
        ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description,
        ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
