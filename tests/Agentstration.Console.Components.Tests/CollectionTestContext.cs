using Bunit;

namespace Bunit;

internal static class CollectionTestContext
{
    public static BunitContext WithCollectionPresentation(this BunitContext context)
    {
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/collection-ui.js");
        foreach (var method in new[] { "observeCollection", "releaseCollection", "attachActions", "releaseActions", "attachDisclosure", "releaseDisclosure" })
            module.SetupVoid(method, _ => true).SetVoidResult();
        var preview = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/preview-location.js");
        preview.SetupModule("observePreviewLocation", _ => true).SetupVoid("dispose", _ => true).SetVoidResult();
        return context;
    }
}
