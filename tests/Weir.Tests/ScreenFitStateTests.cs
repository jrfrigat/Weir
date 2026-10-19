using Microsoft.AspNetCore.Components;
using Weir.Admin.Services;
using Xunit;

namespace Weir.Tests;

/// <summary>Guards the screen-fit preference against either ordering of route render and notification.</summary>
public sealed class ScreenFitStateTests
{
    /// <summary>A list page keeps its preference regardless of when the shell receives navigation.</summary>
    /// <param name="pageRendersFirst">Whether the destination sets its preference before the shell resets it.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ListPageKeepsPreferenceAcrossNavigation(bool pageRendersFirst)
    {
        var navigation = new TestNavigation();
        var state = new ScreenFitState(navigation);
        state.Set(true);
        navigation.NavigateTo("audit");

        if (pageRendersFirst)
        {
            state.Set(true);
            state.ResetForNavigation(navigation.Uri);
        }
        else
        {
            state.ResetForNavigation(navigation.Uri);
            state.Set(true);
        }

        Assert.True(state.Enabled);
    }

    /// <summary>An ordinary page loses the old list page's preference and can return to a list later.</summary>
    [Fact]
    public void OrdinaryPageClearsPreferenceAndListCanEnableItAgain()
    {
        var navigation = new TestNavigation();
        var state = new ScreenFitState(navigation);
        state.Set(true);
        navigation.NavigateTo("settings");
        state.ResetForNavigation(navigation.Uri);
        Assert.False(state.Enabled);

        navigation.NavigateTo("endpoints");
        state.Set(true);
        state.ResetForNavigation(navigation.Uri);
        Assert.True(state.Enabled);
    }

    /// <summary>A minimal navigation manager whose URI changes independently of the shell callback.</summary>
    private sealed class TestNavigation : NavigationManager
    {
        /// <summary>Starts on a screen-fit list page.</summary>
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/endpoints");

        /// <summary>Changes the URI so a test controls when navigation reaches the shell.</summary>
        /// <param name="uri">The destination URI.</param>
        /// <param name="forceLoad">Whether a document reload was requested.</param>
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
    }
}
