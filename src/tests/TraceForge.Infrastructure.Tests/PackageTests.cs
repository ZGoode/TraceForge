using TraceForge.Infrastructure;
using Xunit;

namespace TraceForge.Infrastructure.Tests;

public sealed class PackageTests : TempDirectoryTestBase
{
    [Fact]
    public async Task Project_can_be_created_checked_backed_up_and_restored_without_overwrite()
    {
        var service = new ProjectPackageService();
        var project = Path.Combine(_root, "test.traceproj");
        var backup = Path.Combine(_root, "test.tracebackup");
        var restore = Path.Combine(_root, "restored.traceproj");
        await service.CreateAsync(project, "Test");
        Assert.True((await service.CheckAsync(project)).IsValid);
        await service.CreateBackupAsync(project, backup);
        await service.RestoreBackupAsync(backup, restore);
        Assert.True((await service.CheckAsync(restore)).IsValid);
        await Assert.ThrowsAsync<IOException>(() => service.RestoreBackupAsync(backup, restore));
    }

    [Fact]
    public async Task Managed_attachment_is_portable_and_tampering_is_detected()
    {
        var service = new ProjectPackageService();
        var project = Path.Combine(_root, "attachments.traceproj");
        var source = Path.Combine(_root, "evidence.txt");
        await File.WriteAllTextAsync(source, "measured evidence");
        await service.CreateAsync(project, "Attachments");
        var attachment = await service.ImportManagedAttachmentAsync(project, source);
        Assert.True((await service.CheckAsync(project)).IsValid);
        await File.AppendAllTextAsync(Path.Combine(project, attachment.RelativePath), " altered");
        var report = await service.CheckAsync(project);
        Assert.False(report.IsValid);
        Assert.Contains(report.Findings, f => f.Contains("hash mismatch"));
    }
}
