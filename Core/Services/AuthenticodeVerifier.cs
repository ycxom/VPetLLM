using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VPetLLM.Core.Services
{
    /// <summary>
    /// 运行下载来的安装包之前的身份校验。下载可能走第三方代理，运行它等于执行代理给的代码，
    /// 所以信任锚只能是微软 Authenticode：系统验证签名链和文件完整性，这里再确认签名者是 Ollama 本人。
    /// 签名覆盖整个文件，断点续传拼坏的文件也过不了这一关。
    /// </summary>
    public static class AuthenticodeVerifier
    {
        private const string CommonNameOid = "2.5.4.3";
        private const string OrganizationOid = "2.5.4.10";
        private const string RegistrationNumberOid = "2.5.4.5";

        // EV 证书里的公司注册号。重名公司也能申请到 CN=Ollama Inc. 的证书，注册号才是唯一的法人身份
        private const string OllamaName = "Ollama Inc.";
        private const string OllamaRegistrationNumber = "2713355";

        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        /// <summary>校验通过返回 null，否则返回原因（只记日志，不给用户看）。</summary>
        public static string? CheckOllamaPublisher(string path, SafeFileHandle file)
        {
            var status = VerifyTrust(path, file);
            if (status != 0) return $"WinVerifyTrust 0x{status:X8}";

            try
            {
                // X509CertificateLoader 不能读签名文件，这个方法没有替代品
#pragma warning disable SYSLIB0057
                using var embedded = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                using var signer = new X509Certificate2(embedded);
                return IsOllamaPublisher(signer.SubjectName) ? null : $"signer {signer.Subject}";
            }
            catch (CryptographicException ex)
            {
                return ex.Message;
            }
        }

        public static bool IsOllamaPublisher(X500DistinguishedName subject)
        {
            var names = subject.EnumerateRelativeDistinguishedNames().ToList();
            if (names.Any(n => n.HasMultipleElements)) return false;

            return SingleValue(names, CommonNameOid) == OllamaName
                   && SingleValue(names, OrganizationOid) == OllamaName
                   && SingleValue(names, RegistrationNumberOid) == OllamaRegistrationNumber;
        }

        private static string? SingleValue(IEnumerable<X500RelativeDistinguishedName> names, string oid)
        {
            var values = names.Where(n => n.GetSingleElementType().Value == oid).ToList();
            return values.Count == 1 ? values[0].GetSingleElementValue() : null;
        }

        private static int VerifyTrust(string path, SafeFileHandle file)
        {
            var pathPointer = Marshal.StringToCoTaskMemUni(path);
            var fileInfo = new WinTrustFileInfo
            {
                Size = Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
                File = file.DangerousGetHandle()
            };
            var fileInfoPointer = Marshal.AllocCoTaskMem(fileInfo.Size);
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            var data = new WinTrustData
            {
                Size = Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = WtdStateActionVerify,
                // 吊销服务器在部分网络下连不上，开了检查会把正版安装包也拦下来
                ProviderFlags = WtdRevocationCheckNone
            };

            var action = GenericVerifyV2;
            try
            {
                return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
            finally
            {
                data.StateAction = WtdStateActionClose;
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                Marshal.FreeCoTaskMem(fileInfoPointer);
                Marshal.FreeCoTaskMem(pathPointer);
                GC.KeepAlive(file);
            }
        }

        private const uint WtdUiNone = 2;
        private const uint WtdRevokeNone = 0;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;
        private const uint WtdRevocationCheckNone = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            public int Size;
            public IntPtr FilePath;
            public IntPtr File;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public int Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
    }
}
