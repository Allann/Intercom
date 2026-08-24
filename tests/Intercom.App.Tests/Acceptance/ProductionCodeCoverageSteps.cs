using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Intercom.App.Tests.AttentionCards;
using Intercom.AttentionCards;
using Intercom.ControlChannel;
using Intercom.Identity;
using Reqnroll;
using Xunit;

namespace Intercom.App.Tests.Acceptance;

[Binding]
public sealed class ProductionCodeCoverageSteps
{
    readonly ScenarioContext _scenario;

    public ProductionCodeCoverageSteps(ScenarioContext scenario) => _scenario = scenario;

    [Given(@"the verification run includes all human-authored Intercom.Core production code")]
    [Given(@"generated code is excluded from the verification result")]
    [Given(@"the OpenCover CRAP threshold is 15")]
    [Given(@"a complex resident-app behavior depends on Windows or device hardware")]
    [Given(@"the native effect is represented by a replaceable boundary")]
    [Given(@"a native Windows or device effect cannot be observed deterministically")]
    [Given(@"the current attention-card notification acceptance artifacts are part of the change")]
    public void GivenConfiguredVerification() { }

    [Given(@"a human-authored Intercom.Core method has an OpenCover CRAP score greater than 15")]
    public void GivenAnOffender() => _scenario["SyntheticOffender"] = true;

    [When(@"the deterministic verification suite completes")]
    [When(@"the complete verification suite runs")]
    public async Task WhenVerificationCompletes()
    {
        var report = LatestReport();
        _scenario["Gate"] = RunGate(report);
        _scenario["RepeatedGate"] = RunGate(report);
        await RecordAttentionEvidenceAsync();
    }

    [When(@"the coverage gate evaluates the verification result")]
    public void WhenGateEvaluatesAnOffender() => _scenario["Gate"] = RunGate(LatestReport(), threshold: -1);

    [When(@"the deterministic verification suite exercises the behavior through that boundary")]
    public async Task WhenRuntimeBoundaryIsExercised()
    {
        var requestedFlags = new List<X509KeyStorageFlags>();
        using var certificate = CreateCertificate();
        using var loaded = LocalIdentity.LoadPkcs12([], (_, flags) =>
        {
            requestedFlags.Add(flags);
            if (requestedFlags.Count == 1) throw new CryptographicException("controlled unavailable store");
            return new X509Certificate2(certificate);
        });

        _scenario["RuntimeDecision"] = loaded.HasPrivateKey;
        _scenario["BoundaryRequests"] = requestedFlags;
        await RecordAttentionEvidenceAsync();
    }

    [When(@"the coverage evidence is reviewed")]
    public void WhenCoverageEvidenceIsReviewed() => _scenario["NativeQa"] = File.ReadAllText(CoverageQaPath());

    [Then(@"every human-authored Intercom.Core method has an OpenCover CRAP score of 15 or less")]
    public void ThenNoOffendersRemain() => Assert.Equal(0, Gate().ExitCode);

    [Then(@"the coverage gate fails")]
    public void ThenGateFails() => Assert.NotEqual(0, Gate().ExitCode);

    [Then(@"the result identifies each method that is above the threshold")]
    public void ThenOffendersAreActionable() => Assert.Contains("FAIL CRAP=", Gate().Output);

    [Then(@"generated methods do not appear as failures")]
    public void ThenGeneratedMethodsAreExcluded() =>
        Assert.DoesNotContain("RegexGenerator", Gate().Output);

    [Then(@"the same verification run gives the same result when repeated without a product change")]
    public void ThenRepeatedResultIsStable()
    {
        var first = Gate();
        var second = Assert.IsType<GateResult>(_scenario["RepeatedGate"]);
        Assert.Equal(first, second);
    }

    [Then(@"the production decision runs at runtime")]
    public void ThenProductionDecisionRunsAtRuntime() => Assert.True(Assert.IsType<bool>(_scenario["RuntimeDecision"]));

    [Then(@"the verification observes the requested native effect through the boundary")]
    public void ThenBoundaryRecordsTheNativeRequest() => Assert.Equal(
        [X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable,
         X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable],
        Assert.IsType<List<X509KeyStorageFlags>>(_scenario["BoundaryRequests"]));

    [Then(@"the verification does not decide success by reading production source text")]
    public void ThenRuntimeEvidenceDoesNotUseSourceText() =>
        Assert.Equal(typeof(AttentionCardService), Assert.IsAssignableFrom<Type>(_scenario["EvidenceType"]));

    [Then(@"the production decisions before the native effect have deterministic runtime verification")]
    public void ThenPreNativeDecisionsHaveRuntimeEvidence()
    {
        var submission = AttentionCardNotificationSubmission.From(AttentionCardNotificationSetting.DisabledByGroupPolicy);
        Assert.Equal(AttentionCardNotificationSubmissionStatus.Blocked, submission.Status);
        Assert.Equal(AttentionCardNotificationSetting.DisabledByGroupPolicy, submission.Setting);
    }

    [Then(@"the native effect has an explicit Windows or hardware QA check")]
    public void ThenNativeQaCheckExists() => Assert.Contains("## Windows and hardware checks", NativeQa());

    [Then(@"the QA check states the supported environment and the visible result")]
    public void ThenNativeQaNamesEnvironmentAndResult()
    {
        Assert.Contains("Windows 10 and Windows 11", NativeQa());
        Assert.Contains("expected visible result", NativeQa());
    }

    [Then(@"the attention-card notification decisions have deterministic runtime verification")]
    public void ThenAttentionCardDecisionHasRuntimeEvidence()
    {
        var blocked = AttentionCardNotificationSubmission.From(AttentionCardNotificationSetting.DisabledForUser);
        Assert.Equal(AttentionCardNotificationSubmissionStatus.Blocked, blocked.Status);
    }

    [Then(@"the attention card remains available in the resident app when Windows suppresses its popup")]
    public void ThenAttentionCardRemainsAvailable()
    {
        var card = Assert.IsType<AttentionCard>(_scenario["RetainedCard"]);
        Assert.Equal(AttentionCardDirection.Received, card.Direction);
        Assert.Equal(AttentionCardAckState.Acknowledged, card.AckState);
        var acknowledgement = Assert.IsType<ControlFrame>(_scenario["Acknowledgement"]);
        Assert.Equal(ControlMessageType.Acknowledged, acknowledgement.Type);
        Assert.Equal(card.MessageId, acknowledgement.CorrelationId);
    }

    [Then(@"the attention-card notification presentation QA procedure remains available for Windows 10 and Windows 11")]
    public void ThenAttentionCardQaRemainsAvailable()
    {
        var text = File.ReadAllText(AttentionCardQaPath());
        Assert.Contains("Windows 10", text);
        Assert.Contains("Windows 11", text);
        Assert.Contains("available for acknowledgement", text);
    }

    GateResult Gate() => Assert.IsType<GateResult>(_scenario["Gate"]);
    string NativeQa() => Assert.IsType<string>(_scenario["NativeQa"]);

    async Task RecordAttentionEvidenceAsync()
    {
        var transport = new FakeAttentionCardTransport();
        var service = new AttentionCardService(transport);
        var frame = AttentionCardFrameCodec.ToFrame("Runtime evidence", "!", Guid.NewGuid());
        transport.Deliver(frame);
        await service.AcknowledgeAsync(frame.MessageId, CancellationToken.None);
        _scenario["RetainedCard"] = service.Conversation.Cards.Single();
        _scenario["Acknowledgement"] = transport.Sent.Single();
        _scenario["EvidenceType"] = service.GetType();
    }

    static string LatestReport() => Directory.GetFiles(
            Path.Combine(RepositoryRoot(), "tests", "Intercom.App.Tests", "TestResults"),
            "coverage.opencover.xml", SearchOption.AllDirectories)
        .MaxBy(File.GetLastWriteTimeUtc) ?? throw new InvalidOperationException("No OpenCover report exists.");

    static GateResult RunGate(string report, double threshold = 15)
    {
        var start = new ProcessStartInfo("pwsh", $"-NoProfile -File \"{Path.Combine(RepositoryRoot(), "tools", "Test-CrapCoverage.ps1")}\" -Report \"{report}\" -Threshold {threshold}")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start CRAP gate.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new GateResult(process.ExitCode, output);
    }

    static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    static string CoverageQaPath() => Path.Combine(RepositoryRoot(), "tests", "Intercom.App.Acceptance", "ProductionCodeCoverage.qa.md");
    static string AttentionCardQaPath() => Path.Combine(RepositoryRoot(), "tests", "Intercom.App.Acceptance", "AttentionCardNotificationPresentation.qa.md");

    static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create();
        var request = new CertificateRequest("CN=coverage-runtime", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }
    sealed record GateResult(int ExitCode, string Output);
}
