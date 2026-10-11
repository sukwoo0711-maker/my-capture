using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.App.Updates;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The update tab only opens the Microsoft Store listing. No test here starts a process: every
/// <see cref="StoreUpdateLink"/> gets a recording opener.
/// </summary>
public sealed class StoreUpdateTests
{
    private static readonly Type[] GitHubUpdateTypes =
    [
        typeof(GitHubUpdateService), typeof(IUpdateService), typeof(UpdateSession), typeof(UpdateInstaller),
        typeof(UpdateTarget), typeof(VerifiedUpdatePackage), typeof(StagedUpdateResult),
    ];

    [Fact]
    public void Links_PointAtTheMyCaptureListing()
    {
        Assert.Equal("ms-windows-store://pdp/?productid=9PB33MC095VR", StoreUpdateLink.StoreAppUri);
        Assert.Equal("https://apps.microsoft.com/detail/9PB33MC095VR", StoreUpdateLink.WebUri);
    }

    [Fact]
    public void Open_TriesTheStoreAppOnceAndStops()
    {
        var opened = new List<string>();
        var link = new StoreUpdateLink(opened.Add);

        Assert.Equal(StoreUpdateLink.StoreAppUri, link.Open(NullLogger.Instance));
        Assert.Equal(new[] { StoreUpdateLink.StoreAppUri }, opened);
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(InvalidOperationException))]
    public void Open_FallsBackToTheWebListing(Type failure)
    {
        var opened = new List<string>();
        var link = new StoreUpdateLink(uri =>
        {
            opened.Add(uri);
            if (uri == StoreUpdateLink.StoreAppUri) throw (Exception)Activator.CreateInstance(failure)!;
        });

        Assert.Equal(StoreUpdateLink.WebUri, link.Open(NullLogger.Instance));
        Assert.Equal(new[] { StoreUpdateLink.StoreAppUri, StoreUpdateLink.WebUri }, opened);
    }

    [Fact]
    public void Open_ReturnsNullWhenNeitherLinkOpens()
    {
        var opened = new List<string>();
        var link = new StoreUpdateLink(uri =>
        {
            opened.Add(uri);
            throw new Win32Exception(2);
        });

        Assert.Null(link.Open(NullLogger.Instance));
        Assert.Equal(new[] { StoreUpdateLink.StoreAppUri, StoreUpdateLink.WebUri }, opened);
    }

    [Fact]
    public void Open_DoesNotHideUnexpectedFailures()
    {
        var link = new StoreUpdateLink(_ => throw new ArgumentException("unexpected"));
        Assert.Throws<ArgumentException>(() => link.Open(NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new StoreUpdateLink(null!));
    }

    [Theory]
    [InlineData(typeof(SettingsWindow))]
    [InlineData(typeof(App))]
    public void SettingsWindowAndApp_HoldNoGitHubUpdater(Type owner)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var offenders = new List<string>();
        foreach (Type type in WithNestedTypes(owner))
        {
            foreach (FieldInfo field in type.GetFields(all))
                if (UsesGitHubUpdater(field.FieldType)) offenders.Add($"{type.Name}.{field.Name}: {field.FieldType}");
            foreach (PropertyInfo property in type.GetProperties(all))
                if (UsesGitHubUpdater(property.PropertyType)) offenders.Add($"{type.Name}.{property.Name}: {property.PropertyType}");
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
        foreach (string removed in new[] { "CanExitForUpdate", "ExitForUpdate" })
            Assert.Null(typeof(SettingsWindow).GetProperty(removed, all));
        foreach (string removed in new[] { "InstallStagedUpdateAsync", "OnUpdateCancel", "ResetUpdateAction" })
            Assert.Null(typeof(SettingsWindow).GetMethod(removed, all));
    }

    private static IEnumerable<Type> WithNestedTypes(Type type)
    {
        yield return type;
        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (Type inner in WithNestedTypes(nested))
                yield return inner;
    }

    private static bool UsesGitHubUpdater(Type type)
    {
        if (type.HasElementType) return UsesGitHubUpdater(type.GetElementType()!);
        if (GitHubUpdateTypes.Contains(type)) return true;
        return type.IsGenericType && type.GetGenericArguments().Any(UsesGitHubUpdater);
    }
}
