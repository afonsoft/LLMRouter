using System.Security.Cryptography;
using System.Text;

namespace LLMRouter.Core.Security;

/// <summary>
/// Decifra campos <c>enc:v1:</c> gravados pelo OmniRoute em connections/kv.
/// Formato upstream: <c>enc:v1:&lt;iv_hex&gt;:&lt;ciphertext_hex&gt;:&lt;tag_hex&gt;</c>
/// AES-256-GCM, IV 16B, tag 16B; chave = scrypt(N=16384,r=8,p=1, secret, "omniroute-field-encryption-v1", 32).
/// O secret vem de STORAGE_ENCRYPTION_KEY (mesma env var do upstream — zero-config
/// quando o ambiente migra junto). Sem a env, valores cifrados resolvem como null
/// (credencial indisponível) em vez de vazar ciphertext para o upstream.
/// </summary>
public static class OmniCrypto
{
    public const string Prefix = "enc:v1:";
    private const string StaticSalt = "omniroute-field-encryption-v1";

    public static bool IsEncrypted(string? v) =>
        v is not null && v.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Decifra um campo enc:v1; valores sem o prefixo passam direto.
    /// Retorna null quando o valor é cifrado e não dá para decifrar.</summary>
    public static string? MaybeDecrypt(string? v)
    {
        if (!IsEncrypted(v)) return v;
        var key = LoadKey();
        if (key is null) return null;
        var parts = v![Prefix.Length..].Split(':');
        if (parts.Length != 3) return null;
        try
        {
            var iv = Convert.FromHexString(parts[0]);
            var ct = Convert.FromHexString(parts[1]);
            var tag = Convert.FromHexString(parts[2]);
            if (iv.Length != 16 || tag.Length != 16) return null;
            // upstream usa IV de 16 bytes — AesGcm do .NET só aceita nonce de 12.
            var pt = AesGcmCompat.Decrypt(key, iv, ct, tag);
            return pt is null ? null : Encoding.UTF8.GetString(pt);
        }
        catch { return null; }
    }

    private static volatile byte[]? _key;
    private static volatile bool _keyLoaded;

    private static byte[]? LoadKey()
    {
        if (_keyLoaded) return _key;
        var secret = Environment.GetEnvironmentVariable("STORAGE_ENCRYPTION_KEY");
        _key = string.IsNullOrEmpty(secret) ? null : Scrypt(secret, StaticSalt, 32);
        _keyLoaded = true;
        return _key;
    }

    /// <summary>Reseta o cache de chave (testes).</summary>
    public static void ResetKeyCache()
    {
        _key = null;
        _keyLoaded = false;
    }

    /// <summary>scrypt RFC 7914 — parâmetros do Node scryptSync: N=16384, r=8, p=1.</summary>
    public static byte[] Scrypt(string secret, string salt, int dkLen,
        int n = 16384, int r = 8, int p = 1)
    {
        var saltBytes = Encoding.UTF8.GetBytes(salt);
        var passBytes = Encoding.UTF8.GetBytes(secret);
        var b = Rfc2898DeriveBytes.Pbkdf2(passBytes, saltBytes, 1, HashAlgorithmName.SHA256, p * 128 * r);
        var x = new byte[128 * r];
        var v = new byte[n * 128 * r];
        for (var i = 0; i < p; i++)
        {
            Buffer.BlockCopy(b, i * 128 * r, x, 0, 128 * r);
            ROMix(x, v, n, r);
            Buffer.BlockCopy(x, 0, b, i * 128 * r, 128 * r);
        }
        return Rfc2898DeriveBytes.Pbkdf2(passBytes, b, 1, HashAlgorithmName.SHA256, dkLen);
    }

    private static void ROMix(byte[] b, byte[] v, int n, int r)
    {
        var block = 128 * r;
        var x = new byte[block];
        var t = new byte[block];
        Buffer.BlockCopy(b, 0, x, 0, block);
        for (var i = 0; i < n; i++)
        {
            Buffer.BlockCopy(x, 0, v, i * block, block);
            BlockMix(x, t, r);
            (x, t) = (t, x);
        }
        for (var i = 0; i < n; i++)
        {
            var j = (int)(Integerify(x, block - 64) & (ulong)(n - 1));
            XorInto(x, 0, v, j * block, block);
            BlockMix(x, t, r);
            (x, t) = (t, x);
        }
        Buffer.BlockCopy(x, 0, b, 0, block);
    }

    private static ulong Integerify(byte[] b, int off) =>
        BitConverter.ToUInt64(b, off);

    private static void XorInto(byte[] dst, int dstOff, byte[] src, int srcOff, int len)
    {
        for (var i = 0; i < len; i++) dst[dstOff + i] ^= src[srcOff + i];
    }

    // B (2r×64B em x) -> B' (em dst): B'_i = Salsa8(X), saída reordenada
    // pares primeiro, ímpares depois. Buffers não podem se sobrepor.
    private static void BlockMix(byte[] b, byte[] dst, int r)
    {
        var x = new byte[64];
        Buffer.BlockCopy(b, b.Length - 64, x, 0, 64);
        for (var i = 0; i < 2 * r; i++)
        {
            for (var k = 0; k < 64; k++) x[k] ^= b[i * 64 + k];
            Salsa20_8(x);
            var off = (i & 1) == 0 ? (i / 2) * 64 : (r + i / 2) * 64;
            Buffer.BlockCopy(x, 0, dst, off, 64);
        }
    }

    private static void Salsa20_8(byte[] b)
    {
        uint R(uint a, int c) => (a << c) | (a >> (32 - c));
        var x = new uint[16];
        for (var i = 0; i < 16; i++) x[i] = BitConverter.ToUInt32(b, i * 4);
        var o = (uint[])x.Clone();
        for (var i = 8; i > 0; i -= 2)
        {
            x[4] ^= R(x[0] + x[12], 7); x[8] ^= R(x[4] + x[0], 9);
            x[12] ^= R(x[8] + x[4], 13); x[0] ^= R(x[12] + x[8], 18);
            x[9] ^= R(x[5] + x[1], 7); x[13] ^= R(x[9] + x[5], 9);
            x[1] ^= R(x[13] + x[9], 13); x[5] ^= R(x[1] + x[13], 18);
            x[14] ^= R(x[10] + x[6], 7); x[2] ^= R(x[14] + x[10], 9);
            x[6] ^= R(x[2] + x[14], 13); x[10] ^= R(x[6] + x[2], 18);
            x[3] ^= R(x[15] + x[11], 7); x[7] ^= R(x[3] + x[15], 9);
            x[11] ^= R(x[7] + x[3], 13); x[15] ^= R(x[11] + x[7], 18);
            x[1] ^= R(x[0] + x[3], 7); x[2] ^= R(x[1] + x[0], 9);
            x[3] ^= R(x[2] + x[1], 13); x[0] ^= R(x[3] + x[2], 18);
            x[6] ^= R(x[5] + x[4], 7); x[7] ^= R(x[6] + x[5], 9);
            x[4] ^= R(x[7] + x[6], 13); x[5] ^= R(x[4] + x[7], 18);
            x[11] ^= R(x[10] + x[9], 7); x[8] ^= R(x[11] + x[10], 9);
            x[9] ^= R(x[8] + x[11], 13); x[10] ^= R(x[9] + x[8], 18);
            x[12] ^= R(x[15] + x[14], 7); x[13] ^= R(x[12] + x[15], 9);
            x[14] ^= R(x[13] + x[12], 13); x[15] ^= R(x[14] + x[13], 18);
        }
        for (var i = 0; i < 16; i++)
            BitConverter.GetBytes(x[i] + o[i]).CopyTo(b, i * 4);
    }

    /// <summary>
    /// AES-256-GCM com IV de tamanho arbitrário (16B no OmniRoute). O AesGcm
    /// do BCL só aceita nonce de 12B; aqui J0 = GHASH(IV) conforme SP 800-38D,
    /// CTR a partir de inc32(J0), tag = GCTR(J0, GHASH(AAD‖CT‖lengths)).
    /// </summary>
    internal static class AesGcmCompat
    {
        public static byte[]? Decrypt(byte[] key, byte[] iv, byte[] ct, byte[] tag)
        {
            var h = EncryptBlock(key, new byte[16]);
            var j0 = J0(h, iv);
            var stream = Gctr(key, Inc32(j0), ct);
            var pt = new byte[ct.Length];
            for (var i = 0; i < pt.Length; i++) pt[i] = (byte)(ct[i] ^ stream[i]);

            var s = GHash(h, ct);
            var tagCalc = new byte[16];
            var ej0 = EncryptBlock(key, j0);
            for (var i = 0; i < 16; i++) tagCalc[i] = (byte)(s[i] ^ ej0[i]);
            return CryptographicOperations.FixedTimeEquals(tagCalc, tag) ? pt : null;
        }

        private static byte[] J0(byte[] h, byte[] iv)
        {
            if (iv.Length == 12)
            {
                var j = new byte[16];
                Buffer.BlockCopy(iv, 0, j, 0, 12);
                j[15] = 1;
                return j;
            }
            return GHash(h, iv);
        }

        private static byte[] Inc32(byte[] j0)
        {
            var r = (byte[])j0.Clone();
            for (var i = 15; i >= 12 && ++r[i] == 0; i--) { }
            return r;
        }

        private static byte[] Gctr(byte[] key, byte[] icb, byte[] input)
        {
            var outp = new byte[input.Length];
            var cb = (byte[])icb.Clone();
            for (var off = 0; off < input.Length; off += 16)
            {
                var ks = EncryptBlock(key, cb);
                var n = Math.Min(16, input.Length - off);
                for (var i = 0; i < n; i++) outp[off + i] = ks[i];
                cb = Inc32(cb);
            }
            return outp;
        }

        // GHASH com bloco de comprimentos final: (ct ‖ pad ‖ 0‖len(ct)_64)
        private static byte[] GHash(byte[] h, byte[] ct)
        {
            var x = new byte[16];
            var off = 0;
            while (off < ct.Length)
            {
                var blk = new byte[16];
                var n = Math.Min(16, ct.Length - off);
                Buffer.BlockCopy(ct, off, blk, 0, n);
                Xor(x, blk);
                Mul(x, h);
                off += 16;
            }
            var lens = new byte[16]; // aad bits = 0
            var ctBits = (ulong)ct.Length * 8;
            for (var i = 0; i < 8; i++) lens[8 + i] = (byte)(ctBits >> (56 - 8 * i));
            Xor(x, lens);
            Mul(x, h);
            return x;
        }

        private static void Xor(byte[] a, byte[] b)
        {
            for (var i = 0; i < 16; i++) a[i] ^= b[i];
        }

        // x *= h em GF(2^128), polinômio x^128+x^7+x^2+x+1 (R=0xe1)
        private static void Mul(byte[] x, byte[] h)
        {
            var z = new byte[16];
            var v = (byte[])h.Clone();
            for (var i = 0; i < 128; i++)
            {
                if (((x[i / 8] >> (7 - i % 8)) & 1) != 0) Xor(z, v);
                var lsb = (v[15] & 1) != 0;
                for (var k = 15; k > 0; k--)
                    v[k] = (byte)((v[k] >> 1) | ((v[k - 1] & 1) << 7));
                v[0] >>= 1;
                if (lsb) v[0] ^= 0xe1;
            }
            Buffer.BlockCopy(z, 0, x, 0, 16);
        }

        private static byte[] EncryptBlock(byte[] key, byte[] block)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            return aes.EncryptEcb(block, PaddingMode.None);
        }
    }
}
