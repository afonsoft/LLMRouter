using LLMRouter.Core.Security;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

public class OmniCryptoTests : IDisposable
{
    private const string TestKey = "test-storage-key-123";

    public OmniCryptoTests()
    {
        Environment.SetEnvironmentVariable("STORAGE_ENCRYPTION_KEY", TestKey);
        OmniCrypto.ResetKeyCache();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("STORAGE_ENCRYPTION_KEY", null);
        OmniCrypto.ResetKeyCache();
    }

    [Fact]
    public void Scrypt_matches_node_scryptSync_vector()
    {
        // node: scryptSync("test-storage-key-123","omniroute-field-encryption-v1",32)
        Convert.ToHexStringLower(OmniCrypto.Scrypt(TestKey, "omniroute-field-encryption-v1", 32))
            .ShouldBe("545f1141faaf69085e11e5413e58059421f9a71cd62545e628d5d150ee69f7c2");
    }

    [Fact]
    public void Decrypts_real_omniroute_enc_v1_ciphertext()
    {
        // gerado pelo crypto do OmniRoute (aes-256-gcm + scryptSync) com TestKey
        OmniCrypto.MaybeDecrypt(
                "enc:v1:28ea45299570a33b70ef0112f1150e81:4bf99bef3f267fbcf7f78270a2868f:11146a58d2f8b3a83a9aaf9987fae37c")
            .ShouldBe("sk-live-abc-123");
        OmniCrypto.MaybeDecrypt(
                "enc:v1:3ebe119f1996771755560c06d982f2e6:79eefcca917d5a1d62c1496a74e971:ae3e5f567f5b8e6daff6d168d7f33cec")
            .ShouldBe("oauth-token-xyz");
    }

    [Fact]
    public void Plaintext_passes_through_and_bad_ciphertext_is_null()
    {
        OmniCrypto.MaybeDecrypt("sk-plain").ShouldBe("sk-plain");
        OmniCrypto.MaybeDecrypt(null).ShouldBeNull();
        OmniCrypto.MaybeDecrypt("enc:v1:zz:yy:xx").ShouldBeNull();
        Environment.SetEnvironmentVariable("STORAGE_ENCRYPTION_KEY", null);
        OmniCrypto.ResetKeyCache();
        OmniCrypto.MaybeDecrypt(
            "enc:v1:28ea45299570a33b70ef0112f1150e81:4bf99bef3f267fbcf7f78270a2868f:11146a58d2f8b3a83a9aaf9987fae37c")
            .ShouldBeNull("sem STORAGE_ENCRYPTION_KEY o ciphertext não deve vazar");
    }

    [Fact]
    public void ConnectionSecret_decrypts_and_falls_through()
    {
        var conn = new LLMRouter.Core.Data.ProviderConnection
        {
            Data = """{"apiKey":"enc:v1:28ea45299570a33b70ef0112f1150e81:4bf99bef3f267fbcf7f78270a2868f:11146a58d2f8b3a83a9aaf9987fae37c"}"""
        };
        LLMRouter.Core.Gateway.GatewayEngine.ConnectionSecret(conn).ShouldBe("sk-live-abc-123");

        var bad = new LLMRouter.Core.Data.ProviderConnection
        {
            Data = """{"apiKey":"enc:v1:aa:bb:cc","accessToken":"tok-plain"}"""
        };
        LLMRouter.Core.Gateway.GatewayEngine.ConnectionSecret(bad).ShouldBe("tok-plain");
    }
}
