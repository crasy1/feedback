using Godot;

[SceneTree]
[Instantiable]
public partial class Test2dPlayer : CharacterBody2D
{
    public const float Speed = 300.0f;
    public const float RotateSpeed = Mathf.Pi;
    public const float JumpVelocity = -400.0f;

    [Export(PropertyHint.Range, "0,0.2,0.02")]
    public double UpdateRate { set; get; } = 0.1;

    private double LastUpdateTime { set; get; }

    public TestMsg TestMsg { set; get; }

    public override void _EnterTree()
    {
        Log.Info($"peerId:{Multiplayer.GetUniqueId()} ,playername :", Name);
        if (int.TryParse(Name, out var peerId))
        {
            SetMultiplayerAuthority(peerId);
        }
        else
        {
            QueueFree();
        }
    }

    public override void _Ready()
    {
        if (IsMultiplayerAuthority())
        {
            PlayerCamera.MakeCurrent();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (IsMultiplayerAuthority())
        {
            Vector2 velocity = Velocity;

            // Add the gravity.
            if (!IsOnFloor())
            {
                velocity += GetGravity() * (float)delta;
            }

            // Handle Jump.
            if (Input.IsActionJustPressed("ui_accept") && IsOnFloor())
            {
                velocity.Y = JumpVelocity;
            }

            // Get the input direction and handle the movement/deceleration.
            // As good practice, you should replace UI actions with custom gameplay actions.
            Vector2 direction = Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
            if (direction != Vector2.Zero)
            {
                velocity.X = direction.X * Speed;
            }
            else
            {
                velocity.X = Mathf.MoveToward(Velocity.X, 0, Speed);
            }

            Velocity = velocity;
            MoveAndSlide();
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void AsyncPosition(Vector2 position)
    {
        Position = position;
    }
}