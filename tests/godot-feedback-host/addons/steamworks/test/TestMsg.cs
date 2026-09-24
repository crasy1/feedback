using ProtoBuf;

namespace Godot;

[ProtoContract]
public class TestMsg
{
    [ProtoMember(1)] public Vector2 Position { set; get; }
    [ProtoMember(2)] public float Rotation { set; get; }
}