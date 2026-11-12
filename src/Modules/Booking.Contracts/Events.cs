using TeamBeats.Primitives;

namespace TeamBeats.Modules.Booking.Contracts;

/// <summary>A booking was confirmed (spec 031 section 12): at once, or when its approval came through. Kind is room, desk, parking or equipment.</summary>
public sealed record BookingConfirmed(Guid BookingId, Guid ResourceId, Guid SiteId, string Kind, Guid HolderId, DateTimeOffset StartsAt, DateTimeOffset EndsAt) : IIntegrationEvent;

/// <summary>A booking was cancelled by its holder, by the person who made it, or by a facilities administrator.</summary>
public sealed record BookingCancelled(Guid BookingId, Guid ResourceId, Guid SiteId, string Kind, Guid HolderId, bool ByAdministrator) : IIntegrationEvent;

/// <summary>A booking was released because nobody checked in within the grace period (FR-009).</summary>
public sealed record BookingReleased(Guid BookingId, Guid ResourceId, Guid SiteId, string Kind, Guid HolderId) : IIntegrationEvent;

/// <summary>The holder checked in by QR code, from the booking, or at a kiosk (FR-008).</summary>
public sealed record CheckedIn(Guid BookingId, Guid ResourceId, Guid SiteId, string Kind, Guid HolderId, string Method) : IIntegrationEvent;
