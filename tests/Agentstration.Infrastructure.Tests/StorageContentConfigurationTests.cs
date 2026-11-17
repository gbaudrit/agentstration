using Agentstration.DataSources;
using Agentstration.Infrastructure;
using Agentstration.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class StorageContentConfigurationTests
{
    [TestMethod]
    [DataRow("MaximumWriteBytes", "0")]
    [DataRow("MaximumReadBytes", "16777217")]
    [DataRow("TransferChunkBytes", "65537")]
    [DataRow("LocalMaximumStoreBytes", "1")]
    [DataRow("LocalMaximumPublications", "0")]
    public void InvalidOperatorLimitsFailDuringCompositionBeforeStorageIsOpened(string name, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { [$"{StorageContentOptions.SectionName}:{name}"] = value }).Build();
        var options = configuration.GetSection(StorageContentOptions.SectionName).Get<StorageContentOptions>()!;
        var failure = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAgentstration(new AgentstrationCompositionOptions
        {
            DataDirectory = "unused-invalid-configuration", StorageContent = options
        }));
        Assert.Contains(StorageContentOptions.SectionName, failure.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void InvalidAcquisitionLimitsAlsoFailDuringComposition()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAgentstration(new AgentstrationCompositionOptions
        {
            DataDirectory = "unused-invalid-configuration",
            DataSourceContent = new() { MaximumHttpResponseBytes = 0 }
        }));
    }

    [TestMethod]
    public void SerializedJsonBudgetCountsUtf8AndEscapingInsteadOfCharacterCount()
    {
        var schema = System.Text.Json.JsonSerializer.SerializeToElement(new { type = "string" });
        var value = System.Text.Json.JsonSerializer.SerializeToElement("é\n");
        var size = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value).Length;
        DataSourceDatasetCompositionService.ValidateValue(schema, value, size);
        Assert.Throws<DataSourceValidationException>(() => DataSourceDatasetCompositionService.ValidateValue(schema, value, size - 1));
        DataSourceDatasetCompositionService.ValidateOutput(schema, value, size);
        var outputLimit = Assert.Throws<DataSourceValidationException>(() => DataSourceDatasetCompositionService.ValidateOutput(schema, value, size - 1));
        Assert.AreEqual("data_source_output_too_large", outputLimit.Code);
    }
}
