using InfuseSync.EntryPoints;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Xunit;

namespace InfuseSync.Tests.EntryPoints;

public sealed class SharedTests
{
    [Fact]
    public void ResolveUpdatedItem_UsesPrimaryVersionWhenOwnerIsMissing()
    {
        var primary = new Movie { Id = Guid.NewGuid() };
        var alternate = new Video
        {
            Id = Guid.NewGuid(),
            PrimaryVersionId = primary.Id.ToString("N")
        };

        var result = Shared.ResolveUpdatedItem(
            alternate,
            id => id == primary.Id ? primary : null,
            (resolvedPrimary, versionId) => resolvedPrimary == primary && versionId == alternate.Id);

        Assert.Same(primary, result);
    }

    [Fact]
    public void ResolveUpdatedItem_UsesOwnerForLocalAlternate()
    {
        var primary = new Movie { Id = Guid.NewGuid() };
        var alternate = new Video
        {
            Id = Guid.NewGuid(),
            OwnerId = primary.Id
        };

        var result = Shared.ResolveUpdatedItem(
            alternate,
            id => id == primary.Id ? primary : null,
            (resolvedPrimary, versionId) => resolvedPrimary == primary && versionId == alternate.Id);

        Assert.Same(primary, result);
    }

    [Fact]
    public void ResolveUpdatedItem_KeepsPrimaryVideo()
    {
        var primary = new Movie { Id = Guid.NewGuid() };

        var result = Shared.ResolveUpdatedItem(
            primary,
            _ => throw new InvalidOperationException("A primary video has no owner to resolve."),
            (_, _) => throw new InvalidOperationException("A primary video needs no relationship check."));

        Assert.Same(primary, result);
    }

    [Fact]
    public void ResolveUpdatedItem_KeepsExtra()
    {
        var primary = new Movie { Id = Guid.NewGuid() };
        var extra = new Video
        {
            Id = Guid.NewGuid(),
            OwnerId = primary.Id,
            ExtraType = ExtraType.Trailer
        };

        var result = Shared.ResolveUpdatedItem(extra, _ => primary, (_, _) => false);

        Assert.Same(extra, result);
    }

    [Fact]
    public void ResolveUpdatedItem_KeepsAdditionalPart()
    {
        var primary = new Movie { Id = Guid.NewGuid() };
        var additionalPart = new Video
        {
            Id = Guid.NewGuid(),
            OwnerId = primary.Id
        };

        var result = Shared.ResolveUpdatedItem(additionalPart, _ => primary, (_, _) => false);

        Assert.Same(additionalPart, result);
    }

    [Fact]
    public void ResolveUpdatedItem_KeepsLinkedAlternate()
    {
        var primary = new Movie { Id = Guid.NewGuid() };
        var linkedAlternate = new Video
        {
            Id = Guid.NewGuid(),
            PrimaryVersionId = primary.Id.ToString("N")
        };

        var result = Shared.ResolveUpdatedItem(linkedAlternate, _ => primary, (_, _) => false);

        Assert.Same(linkedAlternate, result);
    }

    [Fact]
    public void ResolveUpdatedItem_KeepsNonVideoItem()
    {
        var item = new Folder { Id = Guid.NewGuid() };

        var result = Shared.ResolveUpdatedItem(
            item,
            _ => throw new InvalidOperationException("A non-video item has no version to resolve."),
            (_, _) => throw new InvalidOperationException("A non-video item has no version relationship."));

        Assert.Same(item, result);
    }

    [Fact]
    public void ResolveUpdatedItem_FallsBackWhenOwnerIsMissing()
    {
        var alternate = new Video
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid()
        };

        var result = Shared.ResolveUpdatedItem(
            alternate,
            _ => null,
            (_, _) => throw new InvalidOperationException("A missing owner has no relationship to inspect."));

        Assert.Same(alternate, result);
    }

    [Fact]
    public void ResolveUpdatedItem_KeepsItemOwnedByNonVideo()
    {
        var alternate = new Video
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid()
        };

        var result = Shared.ResolveUpdatedItem(
            alternate,
            _ => new Folder(),
            (_, _) => throw new InvalidOperationException("A non-video owner cannot expose local versions."));

        Assert.Same(alternate, result);
    }
}
