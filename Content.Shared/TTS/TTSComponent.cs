using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype;

namespace Content.Shared.TTS;

[RegisterComponent, NetworkedComponent]
// ReSharper disable once InconsistentNaming
public sealed partial class TTSComponent : Component
{
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("voice", customTypeSerializer: typeof(PrototypeIdSerializer<TTSVoicePrototype>))]
    public string? VoicePrototypeId { get; set; }
}
