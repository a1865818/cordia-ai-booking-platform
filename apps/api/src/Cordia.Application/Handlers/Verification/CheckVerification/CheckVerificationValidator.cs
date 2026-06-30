using FluentValidation;

namespace Cordia.Application.Handlers.Verification.CheckVerification;

public sealed class CheckVerificationValidator : AbstractValidator<CheckVerificationQuery>
{
    public CheckVerificationValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.VerificationId).NotEmpty();
    }
}

public sealed class VerifyCodeValidator : AbstractValidator<VerifyCodeCommand>
{
    public VerifyCodeValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.VerificationId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty();
    }
}
