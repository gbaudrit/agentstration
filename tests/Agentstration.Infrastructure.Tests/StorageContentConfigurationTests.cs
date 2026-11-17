using Agentstration.DataSources;
using Agentstration.Infrastructure;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class StorageContentConfigurationTests
{
    [TestMethod]
    [DataRow("MaximumWriteBytes", "0")]
    [DataRow("MaximumReadBytes", "-1")]
    [DataRow("MaximumObjectBytes", "0")]
    [DataRow("TransferChunkBytes", "0")]
    [DataRow("MaximumPageSize", "0")]
    [DataRow("LocalMaximumObjects", "0")]
    [DataRow("LocalMaximumReceipts", "-1")]
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
    public void ConfigurationHasNoAdditionalHardCodedUpperCeilings()
    {
        var storage = new StorageContentOptions
        {
            MaximumReadBytes = int.MaxValue, MaximumWriteBytes = int.MaxValue, MaximumObjectBytes = int.MaxValue,
            TransferChunkBytes = int.MaxValue, MaximumPageSize = int.MaxValue,
            LocalMaximumStoreBytes = long.MaxValue, LocalMaximumObjects = int.MaxValue,
            LocalMaximumReceipts = int.MaxValue, LocalMaximumPublications = int.MaxValue
        };
        new ServiceCollection().AddAgentstration(new AgentstrationCompositionOptions
        {
            DataDirectory = "unused-valid-configuration", StorageContent = storage,
            DataSourceContent = new() { MaximumValueBytes = int.MaxValue, MaximumHttpResponseBytes = int.MaxValue },
            FunctionContent = new() { MaximumValueBytes = int.MaxValue },
            FlowContent = new() { MaximumValueBytes = int.MaxValue }
        });
        StorageContentOptions.ValidatePolicy(new());
        Assert.AreEqual(storage.MaximumReadBytes, storage.DefaultPolicy().MaximumReadBytes);
        Assert.AreEqual(int.MaxValue, new StorageContentPolicy().MaximumWriteBytes,
            "An omitted consumer limit must inherit the server budget rather than add a hidden 10 MiB ceiling.");
        CollectionAssert.AreEqual(new byte[] { 42 }, Agentstration.Asp.Abstractions.AspValidation.Content("Kg==", int.MaxValue));
    }

    [TestMethod]
    public void InvalidFunctionBudgetFailsDuringComposition()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAgentstration(new AgentstrationCompositionOptions
        {
            DataDirectory = "unused-invalid-configuration", FunctionContent = new() { MaximumValueBytes = 0 }
        }));
    }

    [TestMethod]
    public void InvalidFlowBudgetFailsDuringComposition()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAgentstration(new AgentstrationCompositionOptions
        {
            DataDirectory = "unused-invalid-configuration", FlowContent = new() { MaximumValueBytes = 0 }
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
        Agentstration.Flows.Application.FlowCompositionSchema.ValidateValue(schema, value, maximumValueBytes: size);
        var compositionLimit = Assert.Throws<Agentstration.Flows.FlowValidationException>(() =>
            Agentstration.Flows.Application.FlowCompositionSchema.ValidateValue(schema, value, maximumValueBytes: size - 1));
        Assert.AreEqual("flow_composition_value_too_large", compositionLimit.Code);
    }
}
