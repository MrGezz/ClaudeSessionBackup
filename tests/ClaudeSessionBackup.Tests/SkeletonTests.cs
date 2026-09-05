using ClaudeSessionBackup.Core.Model;
using Xunit;

namespace ClaudeSessionBackup.Tests;

public class SkeletonTests
{
    [Fact]
    public void KnownStores_DefineSixStores_WithSecretsExcluded()
    {
        var stores = KnownStores.Default(new BackupOptions());
        Assert.Equal(6, stores.Count);
        var config = stores.Single(s => s.Name == KnownStores.CodeConfig);
        Assert.Contains(".credentials.json", config.ExcludeFiles);
        Assert.Contains(".claude.json", config.ExcludeFiles);
        Assert.DoesNotContain(".credentials.json", config.Files);
    }

    [Fact]
    public void IncludeSubagents_TogglesTheTranscriptExclusion()
    {
        var without = KnownStores.Default(new BackupOptions()).Single(s => s.Name == KnownStores.CodeTranscripts);
        var with = KnownStores.Default(new BackupOptions { IncludeSubagents = true }).Single(s => s.Name == KnownStores.CodeTranscripts);
        Assert.Contains("subagents", without.ExcludeDirs);
        Assert.Empty(with.ExcludeDirs);
    }
}
