using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Schemas;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class Rss20SchemaCatalogTests
{
    private const string Channel = "<title>Fixture</title><link>https://example.test/</link><description>Fixture feed</description>";

    [TestMethod]
    public async Task PublishedDefinitionsUseExplicitRssFormatNamesAndResourceVersionOne()
    {
        var input = Manifest("rss-2-0-schemas", "01-rss-2-0-xml.yaml");
        Assert.AreEqual("rss-2-0-xml", input.Metadata.Name);
        Assert.AreEqual("1.0.0", input.Definition.GetProperty("publishVersion").GetString());
        var definition = input.Definition.GetProperty("content");
        Assert.IsTrue((await new BuiltinXmlSchemaFormatHandler().ValidateDefinitionAsync(definition, null, default)).IsValid);
        var output = Manifest("rss-2-0-schemas", "02-rss-2-0-json.yaml");
        Assert.AreEqual("rss-2-0-json", output.Metadata.Name);
        Assert.AreEqual("1.0.0", output.Definition.GetProperty("publishVersion").GetString());
        Assert.IsTrue((await new BuiltinJsonSchemaFormatHandler().ValidateDefinitionAsync(
            output.Definition.GetProperty("content"), null, default)).IsValid);
        var mapping = Manifest("rss-2-0-schemas", "03-rss-2-0-mapping.yaml");
        Assert.AreEqual("rss-2-0-mapping", mapping.Metadata.Name);
        Assert.AreEqual("rss-2-0-xml", mapping.Definition.GetProperty("content").GetProperty("input").GetProperty("name").GetString());
        Assert.AreEqual("rss-2-0-json", mapping.Definition.GetProperty("content").GetProperty("output").GetProperty("name").GetString());
        var exposure = Manifest("rss-2-0-functions", "01-expose.yaml");
        Assert.AreEqual("rss-2-0-xml-to-json", exposure.Metadata.Name);
        Assert.AreEqual("rss-2-0-mapping", exposure.Definition.GetProperty("schema").GetProperty("name").GetString());
        Assert.AreEqual("1.0.0", exposure.Definition.GetProperty("version").GetString());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(101)]
    public async Task EmptyAndRepeatedItemsHaveNoSyntheticSchemaCardinalityCeiling(int count)
    {
        var items = string.Concat(Enumerable.Repeat("<item><description>Article</description></item>", count));
        await AssertValidAsync(Wrap(Channel + items), true);
    }

    [TestMethod]
    [DataRow("<generator>Fixture generator</generator><description>Feed</description><link>https://example.test/</link><title>Fixture</title>")]
    [DataRow("<item><title>First</title></item><description>Feed</description><title>Fixture</title><link>https://example.test/</link>")]
    [DataRow("<link>https://example.test/</link><title>Fixture</title><description>Feed</description>")]
    public async Task ChannelMetadataAndItemsCanAppearInDifferentOrders(string channel)
    {
        await AssertValidAsync(Wrap(channel), true);
    }

    [TestMethod]
    [DataRow("<item><title>Article</title></item>", true)]
    [DataRow("<item><description><![CDATA[<p>HTML stays text</p>]]></description></item>", true)]
    [DataRow("<item><link>https://example.test/article</link><description>Article</description><title>Headline</title></item>", true)]
    [DataRow("<item><guid isPermaLink='false'>opaque:not-a-url</guid><title>Article</title></item>", true)]
    [DataRow("<item><title>Article</title><source url='https://example.test/feed'>Publisher</source></item>", true)]
    [DataRow("<item><title>Article</title><enclosure url='https://example.test/media' length='123' type='audio/mpeg'/></item>", true)]
    [DataRow("<item><category domain='topic'>One</category><title>Article</title><category>Two</category></item>", true)]
    [DataRow("<item/>", false)]
    [DataRow("<item><guid>opaque</guid></item>", false)]
    [DataRow("<item><title>One</title><title>Two</title></item>", false)]
    [DataRow("<item><title>Article</title><guid>one</guid><guid>two</guid></item>", false)]
    [DataRow("<item><title>Article</title><guid isPermaLink='invalid'>opaque</guid></item>", true)]
    [DataRow("<item><title>Article</title><source>Publisher</source></item>", false)]
    [DataRow("<item><title>Article</title><enclosure url='https://example.test/media' type='audio/mpeg'/></item>", false)]
    [DataRow("<item><title>Article</title><enclosure url='https://example.test/media' length='-1' type='audio/mpeg'/></item>", true)]
    [DataRow("<item><title>Article</title><description><p>Unescaped markup</p></description></item>", false)]
    public async Task ItemsValidateRequiredContentOptionalFieldsAndAttributes(string item, bool valid)
    {
        await AssertValidAsync(Wrap(Channel + item), valid);
    }

    [TestMethod]
    public async Task StandardMetadataAndNamespaceExtensionsAreAcceptedWithoutResolution()
    {
        var metadata = """
            <language>fr-FR</language><copyright>Fixture</copyright><managingEditor>editor@example.test</managingEditor>
            <webMaster>webmaster@example.test</webMaster><pubDate>Tue, 17 Nov 2026 11:30:00 +0100</pubDate>
            <lastBuildDate>Tue, 17 Nov 2026 11:30:00 GMT</lastBuildDate><category>News</category><category>Local</category>
            <generator>Fixture</generator><docs>https://www.rssboard.org/rss-specification</docs><ttl>60</ttl><rating>Fixture rating</rating>
            <cloud domain="example.test" port="80" path="/notify" registerProcedure="notify" protocol="http-post"/>
            <image><link>https://example.test/</link><height>100</height><url>https://example.test/logo.png</url><title>Logo</title><width>100</width></image>
            <textInput><link>https://example.test/search</link><name>query</name><description>Search</description><title>Search</title></textInput>
            <skipHours><hour>0</hour><hour>23</hour></skipHours><skipDays><day>Monday</day><day>Sunday</day></skipDays>
            <atom:link xmlns:atom="http://www.w3.org/2005/Atom" href="https://example.test/feed" rel="self"/>
            <item xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:media="http://search.yahoo.com/mrss/">
              <dc:creator>Fixture author</dc:creator><title>Article</title><media:content url="https://example.test/image.png"/>
            </item>
            """;
        await AssertValidAsync(Wrap(Channel + metadata), true);
    }

    [TestMethod]
    [DataRow("<title>Fixture</title><link>https://example.test/</link>")]
    [DataRow("<description>Feed</description><link>https://example.test/</link>")]
    [DataRow("<title>Fixture</title><description>Feed</description>")]
    [DataRow(Channel + "<generator>One</generator><generator>Two</generator>")]
    [DataRow(Channel + "<unknown>Unexpected</unknown>")]
    [DataRow(Channel + "<image><title>Logo</title><url>https://example.test/logo.png</url></image>")]
    public async Task InvalidChannelStructuresAreRejected(string channel)
    {
        await AssertValidAsync(Wrap(channel), false);
    }

    [TestMethod]
    [DataRow("<image><url>https://example.test/logo.png</url><title>Logo</title><link>https://example.test/</link><width>256</width><height>256</height></image>")]
    [DataRow("<image><url>relative</url><title>Logo</title><link>relative</link><width>wide</width><height>-1</height></image>")]
    [DataRow("<ttl>-1</ttl><webMaster>Not an email address</webMaster><pubDate>Not a date</pubDate><lastBuildDate>Also not a date</lastBuildDate>")]
    [DataRow("<cloud domain='example.test' port='any' path='/notify' registerProcedure='notify' protocol='custom'/>")]
    [DataRow("<skipHours><hour>24</hour><hour>1</hour><hour>1</hour></skipHours>")]
    [DataRow("<skipDays><day>NotADay</day><day>Monday</day><day>Monday</day></skipDays>")]
    public async Task PublisherScalarValueDeviationsDoNotBlockStructuralValidation(string metadata)
    {
        await AssertValidAsync(Wrap(Channel + metadata), true);
    }

    [TestMethod]
    public async Task PermissiveValuesDoNotRelaxStructuralCardinalities()
    {
        var hours = string.Concat(Enumerable.Repeat("<hour>any</hour>", 25));
        var days = string.Concat(Enumerable.Repeat("<day>any</day>", 8));
        await AssertValidAsync(Wrap(Channel + "<skipHours>" + hours + "</skipHours>"), false);
        await AssertValidAsync(Wrap(Channel + "<skipDays>" + days + "</skipDays>"), false);
    }

    [TestMethod]
    public async Task RootExtensionsDoNotReplaceTheSingleRequiredChannel()
    {
        var prefix = "<rss version='2.0' xmlns:ext='urn:example:rss-extension'><ext:before/>";
        var suffix = "<ext:after/></rss>";
        await AssertValidAsync(prefix + "<channel>" + Channel + "</channel>" + suffix, true);
        await AssertValidAsync(prefix + suffix, false);
        await AssertValidAsync(prefix + "<channel>" + Channel + "</channel><channel>" + Channel + "</channel>" + suffix, false);
    }

    [TestMethod]
    [DataRow("<rss><channel>" + Channel + "</channel></rss>")]
    [DataRow("<rss version='1.0'><channel>" + Channel + "</channel></rss>")]
    [DataRow("<rss version='2.0'><channel>" + Channel + "</channel><channel>" + Channel + "</channel></rss>")]
    [DataRow("<!DOCTYPE rss [<!ENTITY x SYSTEM 'https://example.test/private'>]><rss version='2.0'><channel>" + Channel + "</channel></rss>")]
    public async Task OnlyTheDeclaredRssVersionAndSafeXmlAreAccepted(string xml)
    {
        await AssertValidAsync(xml, false);
    }

    private static string Wrap(string channel) => "<rss version='2.0'><channel>" + channel + "</channel></rss>";

    private static async Task AssertValidAsync(string xml, bool expected)
    {
        var schema = Manifest("rss-2-0-schemas", "01-rss-2-0-xml.yaml").Definition.GetProperty("content");
        var result = await new BuiltinXmlSchemaFormatHandler().ValidateInstanceAsync(schema,
            JsonSerializer.SerializeToElement(xml), null, default);
        Assert.AreEqual(expected, result.IsValid, string.Join(", ", result.Issues.Select(issue => issue.Code)));
    }

    private static BootstrapResourceDocument Manifest(string profile, string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Agentstration.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        return ResourceManifestSerializer.FromYaml<BootstrapResourceDocument>(
            File.ReadAllText(Path.Combine(root, "deploy", "bootstrap", "profiles", profile, file)));
    }
}
