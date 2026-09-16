using System.Security.Cryptography;
using Agentstration.Identity.Api.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class InternalDelegationKeysTests
{
    [TestMethod]
    public void RotationKeepsThePreviousPublicKeyWithoutRetainingItsPrivateKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-delegation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var environment = new HostingEnvironment { ContentRootPath = directory };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Data:Directory"] = directory
            }).Build();
            using var initial = new InternalDelegationKeys(configuration, environment,
                Options.Create(new InternalDelegationOptions()));
            var previousId = initial.SigningKey.KeyId;
            var publicPath = Path.Combine(directory, "previous-public.pem");
            File.WriteAllText(publicPath, initial.SigningKey.Rsa.ExportSubjectPublicKeyInfoPem());
            var message = RandomNumberGenerator.GetBytes(32);
            var signature = initial.SigningKey.Rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var nextPath = Path.Combine(directory, "next-private.pem");
            using var rotated = new InternalDelegationKeys(configuration, environment,
                Options.Create(new InternalDelegationOptions
                {
                    SigningKeyFile = nextPath,
                    PreviousPublicKeyFiles = [publicPath]
                }));

            Assert.AreNotEqual(previousId, rotated.SigningKey.KeyId);
            Assert.AreEqual(2, rotated.ValidationKeys.Count);
            var previous = (RsaSecurityKey)rotated.ValidationKeys.Single(value => value.KeyId == previousId);
            Assert.IsTrue(previous.Rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            Assert.ThrowsExactly<CryptographicException>(() => previous.Rsa.ExportPkcs8PrivateKey());
            Assert.ThrowsExactly<InvalidOperationException>(() => new InternalDelegationKeys(
                configuration, environment, Options.Create(new InternalDelegationOptions
                {
                    SigningKeyFile = nextPath,
                    PreviousPublicKeyFiles = [Path.Combine(directory, "internal-delegation-key.pem")]
                })));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
