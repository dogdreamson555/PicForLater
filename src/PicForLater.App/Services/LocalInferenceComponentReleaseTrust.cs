using PicForLater.Infrastructure.Analysis;

namespace PicForLater.App.Services;

internal static class LocalInferenceComponentReleaseTrust
{
    // RSA 3072 SubjectPublicKeyInfo. The corresponding private key is retained
    // outside the repository and is used only by the component release script.
    private const string RsaPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAtZtsJ/TTtphzFQUHso0e
        hjf6a7hEXMMgCjDMiC5MzlgNByFTpEz2uQcRcI8Irn9Pl70Pwf1mkYmlxR6oKAdO
        8UGOaTaOgoxM68wqNLDOGP3u5jKuiQFjpNbXNl1ZMi9R1l/hvNiV742wllsD3h6i
        xrZ+ygUGLk1HT5WmE3Z5/UhppOisjrPvDo2cDJZd891yUKqh9FkmOGZ+4t37Ig8u
        0CibrswjQbsKO6F4KIEs+QejjxMX9Pzn6kh3eu9n95LYohjITkgxJXJR/pmJNaeW
        b01sP47bSWpNggzq0h0x6uLvufIXJjd1H9FC88f9hUuYDW8f4QsiKnOobunPPyJJ
        3pQ8RU6YOIwG5uKMrusshn+4PA2aR3WVdGWfEkhyIIlOdie7+zmVHksP4nwHNz7x
        T3gghDXlOfBjS9Dq2lEBxAkjk/mjhbMH3ukUnYYfV4qcNZZhM5WJcSU3r/q/Z/q9
        8VBqbodUJmmP/egsQpHNckDx4nZGDhYgfuyJW1YWY3ohAgMBAAE=
        -----END PUBLIC KEY-----
        """;

    public static bool TryCreateSource(
        string architecture,
        out LocalInferenceComponentReleaseSource? source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);
        if (string.IsNullOrWhiteSpace(RsaPublicKeyPem))
        {
            source = null;
            return false;
        }

        var releaseRoot = new Uri(
            "https://github.com/dogdreamson555/PicForLater/releases/latest/download/");
        var manifestName = $"local-inference-{architecture}.release.json";
        source = new LocalInferenceComponentReleaseSource(
            new Uri(releaseRoot, manifestName),
            new Uri(releaseRoot, $"{manifestName}.sig"),
            RsaPublicKeyPem);
        return true;
    }
}
