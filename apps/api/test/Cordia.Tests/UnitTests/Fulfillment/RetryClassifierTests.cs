using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;

namespace Cordia.Tests.UnitTests.Fulfillment;

[Trait("Category", "Unit")]
public class RetryClassifierTests
{
    private readonly RetryClassifier _classifier = new();

    [Theory]
    [InlineData(ProviderResultKind.NoCapacity, RetryClassification.NoCapacity, RetryAction.NoCapacity)]
    [InlineData(ProviderResultKind.Rejected, RetryClassification.Validation, RetryAction.Terminal)]
    [InlineData(ProviderResultKind.PaymentActionRequired, RetryClassification.PaymentAction, RetryAction.AwaitUser)]
    [InlineData(ProviderResultKind.AuthError, RetryClassification.Authentication, RetryAction.ManualReview)]
    [InlineData(ProviderResultKind.Transient, RetryClassification.Transient, RetryAction.ScheduleRetry)]
    [InlineData(ProviderResultKind.Ambiguous, RetryClassification.Ambiguous, RetryAction.Reconcile)]
    [InlineData(ProviderResultKind.PermanentError, RetryClassification.PermanentProvider, RetryAction.Terminal)]
    [InlineData(ProviderResultKind.NotFound, RetryClassification.Ambiguous, RetryAction.ManualReview)]
    public void Classify_NonSuccessKind_ReturnsExpectedDecision(
        ProviderResultKind kind,
        RetryClassification expectedClassification,
        RetryAction expectedAction)
    {
        var result = new ProviderResult(kind);

        var decision = _classifier.Classify(result);

        Assert.Equal(expectedClassification, decision.Classification);
        Assert.Equal(expectedAction, decision.Action);
    }

    [Theory]
    [InlineData(ProviderResultKind.Created)]
    [InlineData(ProviderResultKind.AlreadyExists)]
    public void Classify_SuccessKind_ThrowsArgumentException(ProviderResultKind kind)
    {
        var result = new ProviderResult(kind);

        var ex = Assert.Throws<ArgumentException>(() => _classifier.Classify(result));
        Assert.Contains(kind.ToString(), ex.Message);
    }

    [Fact]
    public void ClassifyException_AnyException_ReturnsInternalDefectPoison()
    {
        var decision = _classifier.ClassifyException(new InvalidOperationException("boom"));

        Assert.Equal(RetryClassification.InternalDefect, decision.Classification);
        Assert.Equal(RetryAction.Poison, decision.Action);
    }
}
