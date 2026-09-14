using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class FolderBrowseDialogTests
{
    [Fact]
    public void BrowseInfo_MarshalsWithoutAStringBuilderField()
    {
        Assert.True(FolderBrowseDialog.NativeBufferRoundTripSucceeds());
    }
}
