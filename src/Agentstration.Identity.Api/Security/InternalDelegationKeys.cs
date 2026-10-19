using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Agentstration.Identity.Api.Security;

public sealed class InternalDelegationOptions
{
    public const string SectionName = "Agentstration:InternalDelegation";
    public string? SigningKeyFile { get; set; }
    public List<string> PreviousPublicKeyFiles { get; set; } = [];
    public int LifetimeSeconds { get; set; } = 120;

    public bool Validate() => LifetimeSeconds is >= 30 and <= 300
        && (SigningKeyFile is null || !string.IsNullOrWhiteSpace(SigningKeyFile))
        && PreviousPublicKeyFiles.All(path => !string.IsNullOrWhiteSpace(path));
}

public sealed class InternalDelegationKeys : IDisposable
{
    private readonly List<RSA> keys = [];

    public InternalDelegationKeys(
        IConfiguration configuration,
        IHostEnvironment environment,
        Microsoft.Extensions.Options.IOptions<InternalDelegationOptions> options)
    {
        var settings = options.Value;
        var dataDirectory = configuration["Data:Directory"]
            ?? Path.Combine(environment.ContentRootPath, ".agentstration");
        var signingPath = Resolve(settings.SigningKeyFile
            ?? Path.Combine(dataDirectory, "internal-delegation-key.pem"), environment.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(signingPath)!);
        if (!File.Exists(signingPath))
        {
            using var generated = RSA.Create(3072);
            var temporaryPath = signingPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, generated.ExportPkcs8PrivateKeyPem());
                File.Move(temporaryPath, signingPath);
            }
            catch (IOException) when (File.Exists(signingPath)) { }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        var signer = RSA.Create();
        signer.ImportFromPem(File.ReadAllText(signingPath));
        keys.Add(signer);
        SigningKey = CreateKey(signer);

        var validationKeys = new List<SecurityKey> { SigningKey };
        foreach (var configuredPath in settings.PreviousPublicKeyFiles)
        {
            var material = File.ReadAllText(Resolve(configuredPath, environment.ContentRootPath));
            if (!material.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal)
                || material.Contains("PRIVATE KEY", StringComparison.Ordinal))
                throw new InvalidOperationException("Previous delegation keys must contain public key material only.");
            var previous = RSA.Create();
            previous.ImportFromPem(material);
            keys.Add(previous);
            validationKeys.Add(CreateKey(previous));
        }
        ValidationKeys = validationKeys;
    }

    public RsaSecurityKey SigningKey { get; }
    public IReadOnlyList<SecurityKey> ValidationKeys { get; }

    public IReadOnlyList<object> PublicKeys => keys.Select(key => (object)new
    {
        kty = "RSA",
        use = "sig",
        alg = SecurityAlgorithms.RsaSha256,
        kid = KeyId(key),
        n = Base64Url(key.ExportParameters(false).Modulus!),
        e = Base64Url(key.ExportParameters(false).Exponent!)
    }).ToArray();

    private static RsaSecurityKey CreateKey(RSA rsa) => new(rsa) { KeyId = KeyId(rsa) };

    private static string KeyId(RSA rsa) =>
        Base64Url(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())[..16]);

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Resolve(string path, string contentRoot) =>
        Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path, contentRoot);

    public void Dispose()
    {
        foreach (var key in keys) key.Dispose();
    }
}
