using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace VPetLLM.Utils.Common
{
    /// <summary>
    /// 数据处理辅助类
    /// </summary>
    internal static class RequestSignatureHelper
    {
        private static Func<ulong>? _0x1f;
        private static Func<Task<int>>? _0x2e;
        private static Func<string>? _0x3d;

        internal static void Init(Func<ulong> p0, Func<Task<int>> p1, Func<string>? p2 = null)
        {
            _0x1f = p0; _0x2e = p1; _0x3d = p2;
        }

        internal static async Task AddSignatureAsync(HttpRequestMessage r)
        {
            try
            {
                var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var v0 = _0x1f?.Invoke() ?? 0;
                var v1 = 0;
                if (_0x2e != null) { try { v1 = await _0x2e(); } catch { } }
                var v2 = _0x4c();

                r.Headers.Add(_0x5a(), _0x6b(v0.ToString(), t));
                r.Headers.Add(_0x7c(), _0x6b(v2, t));
                r.Headers.Add(_0x8d(), _0x6b(v1.ToString(), t));
                r.Headers.Add(_0x9e(), _0xaf(t));
            }
            catch { }
        }

        private static string _0x4c()
        {
            if (_0x3d != null)
            {
                try
                {
                    var r = _0x3d();
                    if (!string.IsNullOrEmpty(r)) return r;
                }
                catch { }
            }
            return _0xbf();
        }

        private static string _0x5a()
        {
            var h = "582d43616368652d546f6b656e";
            return _0xdf(h);
        }

        private static string _0x7c()
        {
            var h = "582d526571756573742d5369676e6174757265";
            return _0xdf(h);
        }

        private static string _0x8d()
        {
            var h = "582d436865636b2d4b6579";
            return _0xdf(h);
        }

        private static string _0x9e()
        {
            var h = "582d54726163652d4964";
            return _0xdf(h);
        }

        private static string _0xbf()
        {
            var h = "33353631393332343135";
            return _0xdf(h);
        }

        private static readonly byte[] _0xcf = _0xef("565065744c4c4d5f");

        private static string _0xdf(string h)
        {
            var b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
            return Encoding.UTF8.GetString(b);
        }

        private static byte[] _0xef(string h)
        {
            var b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
            return b;
        }

        private static long _0xff(long t)
        {
            var d = t.ToString();
            long f = 0;
            for (int i = 0; i < d.Length; i++) f += (d[i] - '0') * (i + 1);
            return f % 60;
        }

        private static long _0x10f(long t) => (t ^ (_0xff(t) * 0x5A5A)) + _0xff(t);

        private static string _0x6b(string p, long t)
        {
            try
            {
                var o = _0x10f(t);
                using var s = SHA256.Create();
                var km = Encoding.UTF8.GetBytes(o.ToString());
                var kc = new byte[km.Length + _0xcf.Length];
                Array.Copy(km, 0, kc, 0, km.Length);
                Array.Copy(_0xcf, 0, kc, km.Length, _0xcf.Length);
                var k = s.ComputeHash(kc);
                using var m = MD5.Create();
                var iv = m.ComputeHash(Encoding.UTF8.GetBytes(t.ToString()));
                using var a = Aes.Create();
                a.Key = k; a.IV = iv; a.Mode = CipherMode.CBC; a.Padding = PaddingMode.PKCS7;
                using var e = a.CreateEncryptor();
                var b = Encoding.UTF8.GetBytes(p);
                return Convert.ToBase64String(e.TransformFinalBlock(b, 0, b.Length));
            }
            catch { return p; }
        }

        private static string _0xaf(long t)
        {
            try
            {
                var r = new byte[16];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(r);
                var c = new byte[20];
                Array.Copy(r, 0, c, 0, 16);
                Array.Copy(BitConverter.GetBytes((int)(t % 10000)), 0, c, 16, 4);
                var x = (byte)(_0x10f(t) & 0xFF);
                for (int i = 0; i < 20; i++) c[i] ^= x;
                return Convert.ToBase64String(c);
            }
            catch { return Convert.ToBase64String(Guid.NewGuid().ToByteArray()); }
        }
    }
}
