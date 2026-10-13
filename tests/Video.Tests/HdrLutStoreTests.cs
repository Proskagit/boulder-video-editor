using System.Text;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Media.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// Phase 18 Step 18.5 (D033 §3 "LUT"): the LUT store writes one file per key with exactly the rule's text, shares one
/// generation between concurrent first uses, writes a damaged or different file again, removes other rule versions'
/// files and leftovers, and lets a failure be retried.
/// </summary>
public sealed class HdrLutStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-lut-store", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private HdrLutStore Store() => new(_folder, NullLogger<HdrLutStore>.Instance);

    private static byte[] Expected(HdrConversion conversion) => new UTF8Encoding(false).GetBytes(HdrLut.Generate(conversion));

    [Fact]
    public async Task One_file_per_key_holding_exactly_the_rules_LUT()
    {
        var store = Store();
        var hlg = await store.GetLutPathAsync(HdrConversion.Hlg());
        var hlgFull = await store.GetLutPathAsync(HdrConversion.Hlg(fullRange: true));   // the range is not part of the key
        var pq = await store.GetLutPathAsync(new HdrConversion(HdrTransfer.Pq, 1500, false));

        Assert.Equal(hlg, hlgFull);
        Assert.Equal(Path.Combine(_folder, HdrLut.FileName(HdrConversion.Hlg())), hlg);
        Assert.Equal(Expected(HdrConversion.Hlg()), File.ReadAllBytes(hlg));
        Assert.Equal(Expected(new HdrConversion(HdrTransfer.Pq, 1500, false)), File.ReadAllBytes(pq));
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public async Task A_relative_folder_gives_a_full_path()
    {
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, _folder);
        var path = await new HdrLutStore(relative, NullLogger<HdrLutStore>.Instance).GetLutPathAsync(HdrConversion.Hlg());
        Assert.True(Path.IsPathFullyQualified(path), path);
        Assert.Equal(Path.Combine(_folder, HdrLut.FileName(HdrConversion.Hlg())), path);
    }

    [Fact]
    public async Task Concurrent_first_uses_share_one_file_and_leave_no_temporary_file()
    {
        var store = Store();
        var paths = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => store.GetLutPathAsync(HdrConversion.Hlg()))));

        Assert.Single(paths.Distinct());
        Assert.Equal(new[] { paths[0] }, Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Two_keys_prepared_at_once_in_a_folder_with_stale_files_both_succeed()
    {
        // The first use cleans up; it must never remove the other key's temporary file (re-review note, 18.5).
        for (var round = 0; round < 5; round++)
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, $"{HdrLut.FilePrefix}{HdrToSdr.RuleVersion + 9}-pq-1000.cube"), "old");
            var store = Store();
            var paths = await Task.WhenAll(
                Task.Run(() => store.GetLutPathAsync(HdrConversion.Hlg())),
                Task.Run(() => store.GetLutPathAsync(new HdrConversion(HdrTransfer.Pq, 1000 + round, false))));
            Assert.All(paths, p => Assert.True(File.Exists(p), p));
        }
    }

    [Fact]
    public async Task A_damaged_file_is_written_again_in_the_next_session()
    {
        var path = await Store().GetLutPathAsync(HdrConversion.Hlg());
        File.WriteAllText(path, "LUT_3D_SIZE 2\n0 0 0\n");   // damaged (or written by another rule)

        Assert.Equal(path, await Store().GetLutPathAsync(HdrConversion.Hlg()));
        Assert.Equal(Expected(HdrConversion.Hlg()), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Other_rule_versions_and_leftover_temporary_files_are_removed_and_other_files_kept()
    {
        Directory.CreateDirectory(_folder);
        var old = Path.Combine(_folder, $"{HdrLut.FilePrefix}{HdrToSdr.RuleVersion + 7}-hlg-1000.cube");
        var leftover = Path.Combine(_folder, $"{HdrLut.FileName(HdrConversion.Hlg())}.{Guid.NewGuid():N}.tmp");
        var unrelated = Path.Combine(_folder, "notes.txt");
        foreach (var file in new[] { old, leftover, unrelated })
            File.WriteAllText(file, "x");

        await Store().GetLutPathAsync(HdrConversion.Hlg());

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task The_previous_rules_LUT_left_by_the_L2_build_is_removed_at_the_first_use()
    {
        Assert.Equal(2, HdrToSdr.RuleVersion);   // S1 (D033 R1)
        Directory.CreateDirectory(_folder);
        var l2 = Path.Combine(_folder, "hdr-r1-hlg-1000.cube");
        File.WriteAllText(l2, "rule 1");

        var path = await Store().GetLutPathAsync(HdrConversion.Hlg());

        Assert.False(File.Exists(l2));
        Assert.EndsWith("hdr-r2-hlg-1000.cube", path);
    }

    [Fact]
    public async Task A_failure_is_thrown_and_retried_on_the_next_request()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_folder)!);
        File.WriteAllText(_folder, "a file where the folder should be");
        var store = Store();

        await Assert.ThrowsAnyAsync<IOException>(() => store.GetLutPathAsync(HdrConversion.Hlg()));

        File.Delete(_folder);
        var path = await store.GetLutPathAsync(HdrConversion.Hlg());   // not the cached failure
        Assert.True(File.Exists(path));
    }
}
