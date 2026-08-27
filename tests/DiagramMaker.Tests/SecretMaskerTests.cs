using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class SecretMaskerTests
{
    [Fact]
    public void Mask_RedactsCommonSecrets()
    {
        var input = "password=hunter2\nAuthorization: Bearer abc.def.ghi\napi_key: super-secret";
        var output = new SecretMasker().Mask(input);

        Assert.DoesNotContain("hunter2", output, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def.ghi", output, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", output, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", output, StringComparison.Ordinal);
    }
}
