using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// 注意：本项目有 VPetLLM.Utils.System 命名空间，在 VPetLLM.Utils.* 里写 "System.xxx" 会被它截胡，
// 所以这个文件只用上面的 using，代码体里不写带 System. 前缀的限定名。
namespace VPetLLM.Utils.Security;

/// <summary>
/// 设置里的密钥（API Key）加密落盘：Windows DPAPI，当前用户作用域。
///
/// 落盘形态是 "dpapi1:" + Base64(密文)。内存里始终是明文，所以设置窗口、请求代码
/// 都不用改——加解密只发生在 JSON 读写那一层（见 <see cref="ProtectedStringConverter"/>），
/// 而 settings 表、provider_nodes 表、plugin_data 表和 JSON 回退存储都经过 Newtonsoft 序列化，
/// 属性上挂一个转换器就全部覆盖。
///
/// 防的是什么：窃密木马批量扫描、拷走 settings.db / 备份文件这类“只搬文件”的攻击——
/// 密文离开这台机器、这个 Windows 账户就解不开。
/// 防不住什么：已经跑在同一账户下、并且专门针对本插件调用 DPAPI 的恶意程序
/// （DPAPI 的设计边界），以及进程内存里的明文。附加熵只是让“通用窃密脚本”解不开，
/// 不是对抗定向攻击的手段。
///
/// 用 P/Invoke 而不是 System.Security.Cryptography.ProtectedData：后者是额外的 DLL，
/// 而创意工坊会对 MOD 内每个 DLL 做哈希校验，少一个依赖少一个出问题的地方。
/// </summary>
public static class SecretProtector
{
    /// <summary>密文前缀；也是区分“已加密”和“旧版明文”的唯一依据</summary>
    public const string Prefix = "dpapi1:";

    private const int CryptProtectUiForbidden = 0x1;

    // 附加熵：绑定到本插件，别的程序拿到密文也不能直接用 CryptUnprotectData(无熵) 解开
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VPetLLM.Main.Settings.Secret.v1");

    /// <summary>
    /// 读设置时是否遇到过非空的明文密钥（已发布版本写进 settings.db 的都是明文）。
    /// 加载完成后据此判断“需要立刻整体重写一次并清理明文残留”，
    /// 否则明文会一直躺在磁盘上直到用户下次点保存，备份里的明文更是要等备份轮换掉。
    /// </summary>
    public static bool SawPlaintext { get; set; }

    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>加密。空串原样返回；已是密文不重复加密</summary>
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || IsProtected(plaintext))
            return plaintext ?? "";

        var cipher = Transform(Encoding.UTF8.GetBytes(plaintext), protect: true);
        return Prefix + Convert.ToBase64String(cipher);
    }

    /// <summary>
    /// 解密。没有前缀视为旧版明文，原样返回并记下 <see cref="SawPlaintext"/>；
    /// 解不开（密文是别的账户/别的机器生成的，比如文档目录被同步到另一台电脑）返回空串并记日志——
    /// 宁可让用户重填一次 key，也不能让整个设置读取失败。
    /// </summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return "";

        if (!IsProtected(stored))
        {
            SawPlaintext = true;
            return stored;
        }

        try
        {
            var cipher = Convert.FromBase64String(stored.Substring(Prefix.Length));
            return Encoding.UTF8.GetString(Transform(cipher, protect: false));
        }
        catch (Exception ex)
        {
            Logger.Log($"SecretProtector: 密钥解密失败（可能是换了机器/账户），需要重新填写: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// 把一段 JSON 里所有名为 ApiKey 的非空明文字符串值换成密文（任意层级，已加密的保持不变）。
    /// 用于不经过 Setting 类的 JSON 文本，比如旧版 VPetLLM.json 的 .backup。
    /// 解析失败原样返回并记日志——那样的文件本来也读不出内容，不去动它。
    /// </summary>
    public static string ProtectJsonSecrets(string json)
    {
        try
        {
            var root = JToken.Parse(json);
            foreach (var property in root.SelectTokens("$..ApiKey").ToList())
            {
                if (property is JValue { Type: JTokenType.String } value
                    && value.Value is string text
                    && text.Length > 0
                    && !IsProtected(text))
                {
                    value.Value = Protect(text);
                }
            }
            return root.ToString(Formatting.Indented);
        }
        catch (Exception ex)
        {
            Logger.Log($"SecretProtector: JSON 解析失败，未处理其中的密钥: {ex.Message}");
            return json;
        }
    }

    /// <summary>用零覆盖文件内容再删除。用于清掉含明文密钥的旧副本</summary>
    public static void ShredFile(string path)
    {
        if (!File.Exists(path))
            return;

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            var zeros = new byte[64 * 1024];
            var remaining = fs.Length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, zeros.Length);
                fs.Write(zeros, 0, count);
                remaining -= count;
            }
            fs.Flush(true);
        }
        File.Delete(path);
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("密钥加密依赖 Windows DPAPI");

        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var inputBlob = new DataBlob { Size = input.Length, Data = inputHandle.AddrOfPinnedObject() };
        var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
        DataBlob outputBlob = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            var ok = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, out description, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var result = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (outputBlob.Data != IntPtr.Zero)
            {
                // 解密输出是明文：用完先清零再释放
                Marshal.Copy(new byte[outputBlob.Size], 0, outputBlob.Data, outputBlob.Size);
                _ = LocalFree(outputBlob.Data);
            }
            if (description != IntPtr.Zero) _ = LocalFree(description);
            entropyHandle.Free();
            inputHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, out IntPtr description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

/// <summary>
/// 标在密钥属性上：写 JSON 时加密、读 JSON 时解密（兼容旧版明文）。
/// null 保持 null、空串保持空串（不产生密文），与改动前的语义一致。
/// </summary>
public sealed class ProtectedStringConverter : JsonConverter<string?>
{
    // 签名里的 JsonSerializer 必须写全名：项目有全局别名把这个名字指向 System.Text.Json 的静态类
    public override string? ReadJson(JsonReader reader, Type objectType, string? existingValue,
        bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;

        return SecretProtector.Unprotect(reader.Value?.ToString());
    }

    public override void WriteJson(JsonWriter writer, string? value, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteValue(SecretProtector.Protect(value));
    }
}
