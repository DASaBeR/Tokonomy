using System.Xml;
using System.Xml.Linq;

namespace Taskrail.Runtime.Gates;

/// <summary>Extracts failing test identifiers from TRX (dotnet) or JUnit XML (pytest, jest-junit, maven, go-junit).</summary>
public static class TestReportParser
{
    /// <summary>Returns failing test ids, or null when the file is missing or unreadable.</summary>
    public static IReadOnlySet<string>? TryParseFailures(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var doc = XDocument.Load(stream, LoadOptions.None);
            var root = doc.Root;
            if (root is null)
            {
                return null;
            }

            return root.Name.LocalName == "TestRun" ? ParseTrx(root) : ParseJUnit(root);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static HashSet<string> ParseTrx(XElement root)
    {
        var failures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in root.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
        {
            var outcome = (string?)result.Attribute("outcome");
            if (outcome is "Failed" or "Error" or "Timeout" or "Aborted")
            {
                failures.Add((string?)result.Attribute("testName") ?? (string?)result.Attribute("testId") ?? "?");
            }
        }

        return failures;
    }

    private static HashSet<string> ParseJUnit(XElement root)
    {
        var failures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var testcase in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "testcase"))
        {
            var failed = testcase.Elements().Any(e => e.Name.LocalName is "failure" or "error");
            if (failed)
            {
                var cls = (string?)testcase.Attribute("classname");
                var name = (string?)testcase.Attribute("name") ?? "?";
                failures.Add(string.IsNullOrEmpty(cls) ? name : $"{cls}::{name}");
            }
        }

        return failures;
    }
}
