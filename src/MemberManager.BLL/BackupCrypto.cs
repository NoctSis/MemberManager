using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MemberManager.Common;

namespace MemberManager.BLL
{
    /// <summary>
    /// 备份文件加密（店主 11-08：「备份文件也要加密」）。
    ///
    /// 解决什么问题：备份就是把整库拷一份，里面有全部会员姓名、手机号、余额、消费记录。
    /// 一旦丢网盘、发微信、U 盘乱放，等于整店客户资料明文外泄。
    /// 所以开启后，备份落地即为密文，只有输入正确密码才能还原。
    ///
    /// 算法（不自己发明密码学，全部用 BCL 标准件）：
    ///   密钥派生 PBKDF2-HMAC-SHA256，100,000 次，16 字节随机盐 → 64 字节（前 32 AES 密钥，后 32 HMAC 密钥）
    ///   正文     AES-256-CBC，PKCS7 填充，16 字节随机 IV
    ///   完整性   HMAC-SHA256（先加密后认证，覆盖头部 + 密文，防篡改/防截断）
    ///   文件头   "MMBAK1" + 迭代次数 + 盐 + IV + 密文长度，共 52 字节，尾随 32 字节 MAC
    ///
    /// 刻意不做的事：
    ///   - 不存密码、也不存"可逆"的东西，只存一个不可逆校验值（用于界面判断输得对不对）。
    ///     代价是**忘密码就等于备份作废**，界面上必须把这句话说在最显眼处。
    ///   - 不用固定密钥。本项目已开源，任何写死在代码里的密钥都等于公开。
    /// </summary>
    public static class BackupCrypto
    {
        /// <summary>加密备份的扩展名。明文备份仍是 .db，两种可共存、界面上分别显示。</summary>
        public const string EncryptedExt = ".dbk";

        public const int MinPasswordLength = 8;
        /// <summary>
        /// PBKDF2 迭代次数。2026-11-08 从 10 万提到 30 万：本项目开源后算法全公开，
        /// 抵挡离线爆破只剩「密码本身 + 迭代次数」两道防线，所以把每猜一次的成本抬高 3 倍。
        /// 实测 32 位进程单次派生约 1.1 秒，备份要派生两次（加密 + 解回验证）≈ 2.2 秒，
        /// 一天一次的自动备份完全无感；提高到 60 万会变成 4 秒以上，手工备份时明显卡顿，故取 30 万。
        /// 迭代次数写在文件头里，老备份按它自己记录的次数解，改了常量不会解不开旧文件。
        /// </summary>
        public const int DefaultIterations = 300000;

        private static readonly byte[] Magic = { (byte)'M', (byte)'M', (byte)'B', (byte)'A', (byte)'K', (byte)'1', 0, 0 };
        private const int SaltLen = 16;
        private const int IvLen = 16;
        private const int MacLen = 32;
        private const int HeaderLen = 8 + 4 + SaltLen + IvLen + 8;   // 52

        /// <summary>看一眼文件头就知道是不是加密备份（不做整文件校验，快）。</summary>
        public static bool IsEncryptedFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                if (new FileInfo(path).Length < HeaderLen + MacLen) return false;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] head = new byte[8];
                    if (fs.Read(head, 0, 8) < 8) return false;
                    for (int i = 0; i < 8; i++) if (head[i] != Magic[i]) return false;
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>加密 src 到 dst（dst 已存在会被覆盖）。密码太短直接拒绝。</summary>
        public static void Encrypt(string srcPath, string dstPath, string password)
        {
            CheckPassword(password);
            if (!File.Exists(srcPath)) throw new IOException("要加密的文件不存在：" + srcPath);

            byte[] salt = RandomBytes(SaltLen);
            byte[] iv = RandomBytes(IvLen);
            byte[] keys = DeriveKeys(password, salt, DefaultIterations);

            string tmp = dstPath + ".part";
            try
            {
                long cipherLen;
                using (var fin = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var fout = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] head = BuildHeader(DefaultIterations, salt, iv, 0);
                    fout.Write(head, 0, HeaderLen);
                    long startCipher = fout.Position;

                    using (var aes = Aes.Create())
                    {
                        aes.KeySize = 256;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;
                        aes.Key = Slice(keys, 0, 32);
                        aes.IV = iv;
                        using (var enc = aes.CreateEncryptor())
                        using (var cs = new CryptoStream(new NonClosingStream(fout), enc, CryptoStreamMode.Write))
                        {
                            fin.CopyTo(cs, 81920);
                            cs.FlushFinalBlock();
                        }
                    }

                    cipherLen = fout.Position - startCipher;
                    // 回头把密文长度补进头部（头部固定 52 字节，长度字段在最后 8 字节）
                    fout.Seek(HeaderLen - 8, SeekOrigin.Begin);
                    byte[] lenBytes = BitConverter.GetBytes(cipherLen);
                    if (!BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                    fout.Write(lenBytes, 0, 8);
                }

                AppendMac(tmp, Slice(keys, 32, 32));

                if (File.Exists(dstPath)) File.Delete(dstPath);
                File.Move(tmp, dstPath);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }

        /// <summary>解密到 dstPath。密码错/文件被改过/文件截断，一律抛中文异常，绝不产出半截文件。</summary>
        public static void Decrypt(string srcPath, string dstPath, string password)
        {
            if (!File.Exists(srcPath)) throw new IOException("备份文件不存在");
            if (string.IsNullOrEmpty(password)) throw new BizException("这个备份是加密的，请输入备份密码");

            byte[] salt, iv;
            int iterations;
            long cipherLen;

            using (var fs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long total = fs.Length;
                if (total < HeaderLen + MacLen) throw new BizException("备份文件不完整，无法恢复");
                byte[] head = new byte[HeaderLen];
                ReadExact(fs, head, HeaderLen);
                for (int i = 0; i < 8; i++)
                    if (head[i] != Magic[i]) throw new BizException("这不是加密备份文件，请用普通备份恢复");

                iterations = BitConverter.ToInt32(head, 8);
                salt = Slice(head, 12, SaltLen);
                iv = Slice(head, 12 + SaltLen, IvLen);
                cipherLen = BitConverter.ToInt64(head, 12 + SaltLen + IvLen);
                if (iterations < 1000 || iterations > 5000000) throw new BizException("备份文件头异常，无法恢复");
                if (cipherLen <= 0 || HeaderLen + cipherLen + MacLen != total)
                    throw new BizException("备份文件被截断或损坏，无法恢复");
            }

            byte[] keys = DeriveKeys(password, salt, iterations);

            using (var fs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] expect = new byte[MacLen];
                fs.Seek(HeaderLen + cipherLen, SeekOrigin.Begin);
                ReadExact(fs, expect, MacLen);

                fs.Seek(0, SeekOrigin.Begin);
                byte[] actual = ComputeMac(fs, HeaderLen + cipherLen, Slice(keys, 32, 32));
                if (!FixedTimeEquals(expect, actual))
                    throw new BizException("备份密码不对（或文件被改动过），无法恢复");
            }

            string tmp = dstPath + ".part";
            try
            {
                using (var fs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var fout = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var aes = Aes.Create())
                {
                    fs.Seek(HeaderLen, SeekOrigin.Begin);
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = Slice(keys, 0, 32);
                    aes.IV = iv;
                    using (var dec = aes.CreateDecryptor())
                    using (var limited = new LimitedStream(fs, cipherLen))
                    using (var cs = new CryptoStream(limited, dec, CryptoStreamMode.Read))
                    {
                        cs.CopyTo(fout, 81920);
                    }
                }
                if (File.Exists(dstPath)) File.Delete(dstPath);
                File.Move(tmp, dstPath);
            }
            catch (BizException) { TryDelete(tmp); throw; }
            catch (CryptographicException)
            {
                TryDelete(tmp);
                throw new BizException("备份密码不对（或文件损坏），无法恢复");
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }

        // ==================== 本机保存密码（DPAPI） ====================

        // 自动备份是无人值守跑的，没人能输密码，所以密码必须能被程序自己取回来。
        // 用 Windows 自带的 DPAPI（CurrentUser 作用域）：只有当前 Windows 用户能解，
        // 换个用户或重装系统就解不开 —— 那时由店主手动输入密码恢复，不影响备份文件本身。
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MemberManager.BackupPwd.v1");

        public static string SealSecret(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        public static string UnsealSecret(string sealedBase64)
        {
            if (string.IsNullOrEmpty(sealedBase64)) return null;
            byte[] data = Convert.FromBase64String(sealedBase64);
            byte[] plain = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }

        // ==================== 密码校验值（不存密码本体） ====================

        /// <summary>生成 "迭代次数:盐:哈希" 的校验串，存进 settings。密码本身永不落库。</summary>
        public static string MakeVerifier(string password)
        {
            CheckPassword(password);
            byte[] salt = RandomBytes(SaltLen);
            byte[] h = DeriveKeys(password, salt, DefaultIterations);
            return DefaultIterations + ":" + Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(Slice(h, 0, 32));
        }

        /// <summary>判断输入的密码是否和校验串吻合。</summary>
        public static bool CheckVerifier(string password, string verifier)
        {
            if (string.IsNullOrEmpty(verifier) || string.IsNullOrEmpty(password)) return false;
            try
            {
                string[] parts = verifier.Split(':');
                // 老格式（迭代次数没写进去）按当时的 10 万次算，保证升级后老密码照样能验证
                string saltB64, hashB64;
                int iterations;
                if (parts.Length == 3)
                {
                    iterations = int.Parse(parts[0]);
                    saltB64 = parts[1];
                    hashB64 = parts[2];
                }
                else if (parts.Length == 2)
                {
                    iterations = 100000;
                    saltB64 = parts[0];
                    hashB64 = parts[1];
                }
                else return false;

                byte[] salt = Convert.FromBase64String(saltB64);
                byte[] expect = Convert.FromBase64String(hashB64);
                byte[] h = DeriveKeys(password, salt, iterations);
                return FixedTimeEquals(expect, Slice(h, 0, 32));
            }
            catch { return false; }
        }

        // ==================== 内部实现 ====================

        private static void CheckPassword(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
                throw new BizException("备份密码至少 " + MinPasswordLength + " 位");

            // 源码全公开之后，算法不再是秘密，能不能扛住离线爆破几乎只取决于密码本身。
            // 六位纯数字在这种场景下等于没加密（几小时就能穷举完），所以直接把最差的几类挡在入口。
            bool allDigit = true, allSame = true;
            for (int i = 0; i < password.Length; i++)
            {
                if (!char.IsDigit(password[i])) allDigit = false;
                if (password[i] != password[0]) allSame = false;
            }
            if (allDigit) throw new BizException("备份密码不能全是数字，请混入字母（例如店铺拼音 + 数字）");
            if (allSame) throw new BizException("备份密码不能是同一个字符重复");

            string low = password.ToLowerInvariant();
            string[] weak = { "password", "12345678", "88888888", "abc12345", "qwerty123", "admin123", "member123" };
            for (int i = 0; i < weak.Length; i++)
                if (low == weak[i]) throw new BizException("这个密码太常见了，换一个");
        }

        private static byte[] BuildHeader(int iterations, byte[] salt, byte[] iv, long cipherLen)
        {
            byte[] head = new byte[HeaderLen];
            Array.Copy(Magic, 0, head, 0, 8);
            byte[] it = BitConverter.GetBytes(iterations);
            byte[] ln = BitConverter.GetBytes(cipherLen);
            if (!BitConverter.IsLittleEndian) { Array.Reverse(it); Array.Reverse(ln); }
            Array.Copy(it, 0, head, 8, 4);
            Array.Copy(salt, 0, head, 12, SaltLen);
            Array.Copy(iv, 0, head, 12 + SaltLen, IvLen);
            Array.Copy(ln, 0, head, 12 + SaltLen + IvLen, 8);
            return head;
        }

        private static byte[] DeriveKeys(string password, byte[] salt, int iterations)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(64);
        }

        private static void AppendMac(string path, byte[] macKey)
        {
            // 必须先读完、关掉读流，再以追加方式打开写：同一个文件同时持有读句柄(FileShare.Read)
            // 和写句柄会直接报「正由另一进程使用」。
            byte[] mac;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                mac = ComputeMac(fs, fs.Length, macKey);

            using (var app = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
                app.Write(mac, 0, MacLen);
        }

        private static byte[] ComputeMac(Stream src, long count, byte[] macKey)
        {
            using (var hmac = new HMACSHA256(macKey))
            {
                byte[] buf = new byte[81920];
                long left = count;
                while (left > 0)
                {
                    int want = (int)Math.Min(buf.Length, left);
                    int n = src.Read(buf, 0, want);
                    if (n <= 0) throw new IOException("读取备份文件时提前结束");
                    hmac.TransformBlock(buf, 0, n, null, 0);
                    left -= n;
                }
                hmac.TransformFinalBlock(new byte[0], 0, 0);
                return hmac.Hash;
            }
        }

        private static byte[] RandomBytes(int n)
        {
            byte[] b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        private static byte[] Slice(byte[] src, int offset, int count)
        {
            byte[] r = new byte[count];
            Array.Copy(src, offset, r, 0, count);
            return r;
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static void ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) throw new BizException("备份文件不完整，无法恢复");
                off += n;
            }
        }

        private static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        /// <summary>CryptoStream 会连底层流一起 Dispose，这里挡一下，让外层的 using 管收尾。</summary>
        private sealed class NonClosingStream : Stream
        {
            private readonly Stream _inner;
            public NonClosingStream(Stream inner) { _inner = inner; }
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { return _inner.Length; } }
            public override long Position { get { return _inner.Position; } set { _inner.Position = value; } }
            public override void Flush() { _inner.Flush(); }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { _inner.Write(buffer, offset, count); }
        }

        /// <summary>只暴露前 N 字节的只读窗口，用于解密时把 MAC 挡在密文之外。</summary>
        private sealed class LimitedStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _limit;
            private long _read;
            public LimitedStream(Stream inner, long limit) { _inner = inner; _limit = limit; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return _limit; } }
            public override long Position { get { return _read; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                long left = _limit - _read;
                if (left <= 0) return 0;
                if (count > left) count = (int)left;
                int n = _inner.Read(buffer, offset, count);
                if (n > 0) _read += n;
                return n;
            }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
