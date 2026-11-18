using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Booking;

/// <summary>This module's commands in the shell's command entry point (spec 008 FR-008, T020). Its pages check access again.</summary>
internal sealed class BookingCommands : IShellCommands
{
    public IEnumerable<ShellCommand> Commands() =>
    [
        new("book_room", "/booking", "building", Capabilities.BookingRooms, Permissions.BookingBook),
    ];
}
