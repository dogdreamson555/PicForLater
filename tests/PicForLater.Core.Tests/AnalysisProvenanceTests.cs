using System.Text.Json;
using PicForLater.Core.Analysis;

namespace PicForLater.Core.Tests;

public sealed class AnalysisProvenanceTests
{
    [Fact]
    public void LegacyJsonWithoutExplicitSemantics_DefaultsToLocalAndUnspecified()
    {
        const string json =
            """
            {
              "providerId": "legacy.provider",
              "modelId": null,
              "modelVersion": null,
              "modelFileHashes": {},
              "schemaVersion": "legacy.v1"
            }
            """;

        var provenance = JsonSerializer.Deserialize<AnalysisProvenance>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(provenance);
        Assert.Equal(AnalysisExecutionLocation.Local, provenance.ExecutionLocation);
        Assert.Equal(AnalysisOutputKind.Unspecified, provenance.OutputKind);
        Assert.Null(provenance.RemoteInputMode);
        Assert.Equal(AnalysisStageOutcome.Completed, provenance.StageOutcome);
    }

    [Fact]
    public void LegacyModelProfileJsonWithoutExecutionFields_DefaultsToLocal()
    {
        const string json =
            """
            {
              "analysisMode": 2,
              "revision": 7,
              "slots": []
            }
            """;

        var snapshot = JsonSerializer.Deserialize<ModelProfileSnapshot>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(snapshot);
        Assert.Equal(AnalysisExecutionBackend.Local, snapshot.ExecutionBackend);
        Assert.Null(snapshot.RemoteInputMode);
        Assert.Null(snapshot.RemoteApiProfile);
        Assert.Equal(7, snapshot.Revision);
    }
}
