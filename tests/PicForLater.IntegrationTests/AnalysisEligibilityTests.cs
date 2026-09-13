using PicForLater.App.Services;
using PicForLater.Core.Analysis;

namespace PicForLater.IntegrationTests;

public sealed class AnalysisEligibilityTests
{
    [Fact]
    public void TryGetEligibleMode_RequiresVerifiedCurrentConsent()
    {
        var profile = CreateProfile();

        Assert.True(AnalysisEligibility.TryGetEligibleMode(profile, out var mode));
        Assert.Equal(RemoteInputMode.LocalOcrText, mode);

        Assert.False(AnalysisEligibility.TryGetEligibleMode(
            profile with { LastVerifiedAtUtc = null },
            out _));
        Assert.False(AnalysisEligibility.TryGetEligibleMode(
            profile with { ConsentedDisclosureVersion = "stale" },
            out _));
    }

    [Fact]
    public void ResolveEligibleSelection_PrefersRememberedSelectionThenUniqueProfile()
    {
        var first = CreateProfile("first");
        var second = CreateProfile("second");
        var eligible = new List<(RemoteApiProfile Profile, RemoteInputMode Mode)>
        {
            (first, RemoteInputMode.LocalOcrText),
            (second, RemoteInputMode.LocalOcrText),
        };
        var rememberedState = CreateState(second.ProfileId);

        var remembered = AnalysisEligibility.ResolveEligibleSelection(
            rememberedState,
            eligible);

        Assert.NotNull(remembered);
        Assert.Equal(second.ProfileId, remembered.Value.Profile.ProfileId);

        var unique = AnalysisEligibility.ResolveEligibleSelection(
            CreateState("missing"),
            new List<(RemoteApiProfile Profile, RemoteInputMode Mode)> { eligible[0] });

        Assert.NotNull(unique);
        Assert.Equal(first.ProfileId, unique.Value.Profile.ProfileId);
    }

    [Theory]
    [InlineData(RemoteInputMode.LocalOcrText, false, false)]
    [InlineData(RemoteInputMode.LocalOcrText, true, true)]
    [InlineData(RemoteInputMode.DirectImage, false, true)]
    public void IsInputModeAvailable_RequiresLocalOcrOnlyForTextMode(
        RemoteInputMode mode,
        bool localOcrAvailable,
        bool expected)
    {
        Assert.Equal(
            expected,
            AnalysisEligibility.IsInputModeAvailable(mode, localOcrAvailable));
    }

    private static RemoteAnalysisExecutionState CreateState(string? profileId) =>
        new(
            new AnalysisExecutionSettings(
                AnalysisExecutionBackend.RemoteApi,
                RemoteInputMode.LocalOcrText,
                profileId,
                AnalysisOutputLanguage.ModelDefault,
                Revision: 1,
                UpdatedAtUtc: DateTimeOffset.UtcNow),
            Profile: null);

    private static RemoteApiProfile CreateProfile(string profileId = "profile") => new()
    {
        ProfileId = profileId,
        ProviderId = "provider.test",
        DisplayName = profileId,
        EndpointId = "endpoint.test",
        BaseUri = new Uri("https://example.test/v1/"),
        ModelId = "model.test",
        SupportedInputModes = [RemoteInputMode.LocalOcrText],
        PromptVersion = "prompt.v1",
        OutputSchemaVersion = "schema.v1",
        MaxTextChars = 1_000,
        MaxImageBytes = 1_000,
        MaxOutputTokens = 100,
        TimeoutSeconds = 10,
        PrivacyUrl = new Uri("https://example.test/privacy"),
        TermsUrl = new Uri("https://example.test/terms"),
        RetentionTrainingStatement = "test",
        RetentionTrainingVerifiedAtUtc = DateTimeOffset.UtcNow,
        CredentialReference = "credential.test",
        DisclosureVersion = "disclosure.v1",
        AuthenticationKind = RemoteApiAuthenticationKind.None,
        IsEnabled = true,
        ValidationState = RemoteApiProfileValidationState.Valid,
        LastVerifiedAtUtc = DateTimeOffset.UtcNow,
        ConsentedInputMode = RemoteInputMode.LocalOcrText,
        ConsentedDisclosureVersion = "disclosure.v1",
        ConsentGrantedAtUtc = DateTimeOffset.UtcNow,
    };
}
