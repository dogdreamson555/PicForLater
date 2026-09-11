using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PicForLater.IntegrationTests;

public sealed class LocalizationResourceTests
{
    private static readonly string[] Languages =
    [
        "en-US",
        "zh-CN",
        "zh-TW",
    ];

    [Fact]
    public void ResourceSetsHaveMatchingKeysAndCompatiblePlaceholders()
    {
        var resources = Languages.ToDictionary(
            static language => language,
            LoadResourceValues,
            StringComparer.Ordinal);
        var baseline = resources["en-US"];
        var baselineKeys = baseline.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var language in Languages)
        {
            var values = resources[language];
            Assert.Equal(
                baselineKeys.Count,
                values.Keys.Count);
            Assert.Empty(
                baselineKeys.Except(
                    values.Keys,
                    StringComparer.OrdinalIgnoreCase));
            Assert.Empty(
                values.Keys.Except(
                    baselineKeys,
                    StringComparer.OrdinalIgnoreCase));
            Assert.All(
                values,
                entry => Assert.False(
                    string.IsNullOrWhiteSpace(entry.Value),
                    $"{language}:{entry.Key} has an empty value."));
        }

        foreach (var key in baselineKeys)
        {
            var expected = GetFormatTokens(baseline[key]);
            foreach (var language in Languages.Skip(1))
            {
                Assert.Equal(
                    expected,
                    GetFormatTokens(resources[language][key]));
            }
        }
    }

    [Fact]
    public void ResourceSetsDoNotContainDuplicateKeysIgnoringCase()
    {
        foreach (var language in Languages)
        {
            var document = XDocument.Load(GetResourcePath(language));
            var duplicateKeys = document
                .Root!
                .Elements("data")
                .Select(static element =>
                    (string?)element.Attribute("name"))
                .Where(static key => key is not null)
                .GroupBy(static key => key!, StringComparer.OrdinalIgnoreCase)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
                .ToArray();

            Assert.Empty(duplicateKeys);
        }
    }

    private static Dictionary<string, string> LoadResourceValues(string language)
    {
        var document = XDocument.Load(GetResourcePath(language));
        return document
            .Root!
            .Elements("data")
            .ToDictionary(
                static element => (string)element.Attribute("name")!,
                static element => (string?)element.Element("value") ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string GetResourcePath(string language) => Path.Combine(
        AppContext.BaseDirectory,
        "Resources",
        language,
        "Resources.resw");

    private static string[] GetFormatTokens(string value) =>
        Regex.Matches(value, @"\{\d+[^{}]*\}")
            .Select(static match => match.Value)
            .OrderBy(static token => token, StringComparer.Ordinal)
            .ToArray();
}
