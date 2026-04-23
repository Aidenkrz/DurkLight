using Content.Shared.Chat;
using Content.Shared.Radio;

namespace Content.Server.Radio;

[ByRefEvent]
public readonly record struct RadioReceiveEvent(string Message, EntityUid MessageSource, RadioChannelPrototype Channel, EntityUid RadioSource, MsgChatMessage ChatMsg);

/// <summary>
/// Use this event to cancel sending message per receiver
/// </summary>
[ByRefEvent]
public record struct RadioReceiveAttemptEvent(RadioChannelPrototype Channel, EntityUid RadioSource, EntityUid RadioReceiver)
{
    public readonly RadioChannelPrototype Channel = Channel;
    public readonly EntityUid RadioSource = RadioSource;
    public readonly EntityUid RadioReceiver = RadioReceiver;
    public bool Cancelled = false;
}

/// <summary>
/// Use this event to cancel sending message to every receiver
/// </summary>
[ByRefEvent]
public record struct RadioSendAttemptEvent(RadioChannelPrototype Channel, EntityUid RadioSource)
{
    public readonly RadioChannelPrototype Channel = Channel;
    public readonly EntityUid RadioSource = RadioSource;
    public bool Cancelled = false;
}

/// <summary>
/// Raised after a radio message has been sent to all receivers.
/// </summary>
public sealed class RadioSpokeEvent(EntityUid messageSource, string message, RadioChannelPrototype channel, List<EntityUid> receivers) : EntityEventArgs
{
    public EntityUid MessageSource { get; } = messageSource;
    public string Message { get; } = message;
    public RadioChannelPrototype Channel { get; } = channel;
    public List<EntityUid> Receivers { get; } = receivers;
}
