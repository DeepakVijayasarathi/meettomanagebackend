using iucs.meettomanage.api.Monitoring;
using Xunit;

namespace iucs.meettomanage.tests;

public sealed class CrashReportSanitizerTests
{
    [Fact]
    public void Scrub_RemovesPersonalDataAndSecrets()
    {
        var text = CrashReportSanitizer.Scrub(
            "Failed for priya.nair@example.com at https://api.meettomanage.cloud/api/x?token=abc phone +91 98765 43210 " +
            "id 3f2b1c4d-1a2b-4c3d-8e9f-0a1b2c3d4e5f jwt eyJhbGciOi.eyJzdWIiOi.sig", 300);

        Assert.DoesNotContain("priya", text);
        Assert.DoesNotContain("token=abc", text);
        Assert.DoesNotContain("98765", text);
        Assert.DoesNotContain("3f2b1c4d", text);
        Assert.DoesNotContain("eyJ", text);
        Assert.Contains("https://api.meettomanage.cloud/api/x", text);
        Assert.Contains("[email]", text);
    }

    [Fact]
    public void Scrub_TruncatesAndHandlesEmpty()
    {
        Assert.Equal(string.Empty, CrashReportSanitizer.Scrub(null, 10));
        Assert.Equal(10, CrashReportSanitizer.Scrub(new string('a', 5) + " " + new string('b', 20), 10).Length);
    }

    [Fact]
    public void Fingerprint_IgnoresLineNumbersButNotFunctions()
    {
        var a = CrashReportSanitizer.Fingerprint("TypeError", "at render (index.bundle:120:33)\nat call (index.bundle:5:1)", "render");
        var rebuilt = CrashReportSanitizer.Fingerprint("TypeError", "at render (index.bundle:998:2)\nat call (index.bundle:7:9)", "render");
        var other = CrashReportSanitizer.Fingerprint("TypeError", "at submit (index.bundle:120:33)", "render");

        Assert.Equal(a, rebuilt);
        Assert.NotEqual(a, other);
        Assert.Equal(32, a.Length);
    }

    [Fact]
    public void StackTop_KeepsOnlyTheFirstFrames()
    {
        var stack = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"at frame{i} (index.bundle:{i}:1)"));
        var top = CrashReportSanitizer.StackTop(stack)!;
        Assert.Equal(6, top.Split('\n').Length);
        Assert.StartsWith("at frame1", top);
        Assert.Null(CrashReportSanitizer.StackTop("  "));
    }
}
