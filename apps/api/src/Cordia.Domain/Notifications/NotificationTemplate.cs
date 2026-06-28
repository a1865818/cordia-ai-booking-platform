namespace Cordia.Domain.Notifications;

/// <summary>A resolved notification template ready for rendering.</summary>
public sealed record NotificationTemplate(string TemplateVersion, string BodyTemplate);
