using System;
using System.Numerics;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Input;
using V12.Core.Interfaces.Renderer;

namespace HelloXR.Components
{
    /// <summary>
    /// "Hello XR": grab the box with a controller trigger, drop it by releasing,
    /// reset it with A, and move the rig with the left thumbstick.
    ///
    /// Tracking itself comes from <c>NovaV2XRService</c>, which writes the
    /// headset and controller poses onto the XR elements every frame and
    /// forwards the controller buttons and axes as <c>xr_&lt;side&gt;_*</c>
    /// input events. This component only consumes those events, so it works the
    /// same on a headset and (for the box reset) on a keyboard.
    /// </summary>
    public sealed class HelloXRController : ComponentBase
    {
        public override string Name => "Hello XR";
        public override string Description => "Grab the box with a controller trigger";

        private const float GrabDistance = 0.45f;
        private const float GrabThreshold = 0.6f;
        private const float ReleaseThreshold = 0.3f;
        private const float MoveSpeed = 2.2f;

        private InputActionMap? _actions;
        private IWorldElement? _owner;
        private IWorldElement? _box;
        private IWorldElement? _leftHand;
        private IWorldElement? _rightHand;

        private Vector3 _boxHome;
        private bool _grabbed;
        private bool _grabWithRight;

        public override void OnAttach(IWorldElement worldElement)
        {
            base.OnAttach(worldElement);
            _owner = worldElement;

            _box = GameRoot.Instance?.FindElement(e => e.Name == "Box");
            if (_box is not null)
                _boxHome = _box.LocalTransform.Position;

            FindHands();
            BindInput();
        }

        /// <summary>
        /// Resolves the rig once the parser has added its children (they do not
        /// exist during OnAttach) and parks the hands in front of the desktop
        /// camera when no headset is driving them.
        /// </summary>
        private void EnsureHands()
        {
            if (_leftHand is not null && _rightHand is not null)
                return;

            FindHands();
            if (_leftHand is null && _rightHand is null)
                return;

            if (GameRoot.Instance?.Registry.Get("XRTrackingService") is null)
            {
                _leftHand?.LocalTransform = new TRS(new Vector3(-0.3f, 1.1f, -0.2f));
                _rightHand?.LocalTransform = new TRS(new Vector3(0.3f, 1.1f, -0.2f));
            }
        }

        public override void OnDetach(IWorldElement worldElement)
        {
            if (_actions is not null && Input() is { } input)
                _actions.Detach(input);
            _actions = null;
            base.OnDetach(worldElement);
        }

        private static InputService? Input() =>
            GameRoot.Instance?.Registry.Get<InputService>();

        private void FindHands()
        {
            if (_owner is null)
                return;

            foreach (var child in _owner.Children)
            {
                if (child.GetComponent<XRRootComponent>() is null)
                    continue;

                foreach (var rigChild in child.Children)
                {
                    if (rigChild.GetComponent<XRHandComponent>() is not { } hand)
                        continue;

                    if (hand.Side == HandSide.Left) _leftHand = rigChild;
                    else _rightHand = rigChild;
                }
            }
        }

        private void BindInput()
        {
            if (Input() is not { } input)
            {
                GameRoot.Log?.Warning("[HelloXR] No InputService; input disabled.");
                return;
            }

            var actions = new InputActionMap { Name = "HelloXR" };

            actions.BindAxis("LeftTrigger", "xr_left_trigger", "");
            actions.BindAxis("RightTrigger", "xr_right_trigger", "");
            actions.BindAxis("MoveX", "xr_left_stick_x", "");
            actions.BindAxis("MoveZ", "xr_left_stick_y", "");

            actions.BindButton("Reset", "xr_right_primary_click");
            actions.AddButton("Reset", InputKey.Space);   // desktop fallback

            actions.Attach(input);
            _actions = actions;
        }

        // ── Frame ───────────────────────────────────────────────────────────

        public override void Update(float deltaTime)
        {
            if (_actions is null)
                return;

            EnsureHands();
            _actions.Update(deltaTime);

            if (_actions.GetButtonDown("Reset"))
                ResetBox();

            MoveRig(deltaTime);
            UpdateGrab();
        }

        private void MoveRig(float deltaTime)
        {
            if (_owner is null)
                return;

            float x = _actions!.GetAxis("MoveX");
            float z = _actions.GetAxis("MoveZ");
            if (Math.Abs(x) < 0.01f && Math.Abs(z) < 0.01f)
                return;

            var lt = _owner.LocalTransform;
            var position = lt.Position;
            position.X = Math.Clamp(position.X + x * MoveSpeed * deltaTime, -5f, 5f);
            position.Z = Math.Clamp(position.Z + z * MoveSpeed * deltaTime, -5f, 5f);
            _owner.LocalTransform = new TRS(position, lt.Rotation, lt.Scale);
        }

        private void UpdateGrab()
        {
            if (_box is null)
                return;

            float left = _actions!.GetAxis("LeftTrigger");
            float right = _actions.GetAxis("RightTrigger");

            if (!_grabbed)
            {
                if (right > GrabThreshold && HandNear(_rightHand))
                {
                    _grabbed = true;
                    _grabWithRight = true;
                }
                else if (left > GrabThreshold && HandNear(_leftHand))
                {
                    _grabbed = true;
                    _grabWithRight = false;
                }
            }

            if (!_grabbed)
                return;

            var hand = _grabWithRight ? _rightHand : _leftHand;
            float trigger = _grabWithRight ? right : left;

            if (hand is null || trigger < ReleaseThreshold)
            {
                _grabbed = false;
                return;
            }

            // Hold the box a little in front of the controller so it does not
            // intersect the hand cube.
            var world = hand.WorldTransform;
            var handPosition = new Vector3(world.M41, world.M42, world.M43);
            var forward = -Vector3.Normalize(new Vector3(world.M31, world.M32, world.M33));

            _box.LocalTransform = new TRS(handPosition + forward * 0.14f);
        }

        private bool HandNear(IWorldElement? hand)
        {
            if (hand is null || _box is null)
                return false;

            var handWorld = hand.WorldTransform;
            var boxWorld = _box.WorldTransform;

            var handPosition = new Vector3(handWorld.M41, handWorld.M42, handWorld.M43);
            var boxPosition = new Vector3(boxWorld.M41, boxWorld.M42, boxWorld.M43);

            return Vector3.Distance(handPosition, boxPosition) <= GrabDistance;
        }

        private void ResetBox()
        {
            if (_box is null)
                return;

            _grabbed = false;
            _box.LocalTransform = new TRS(_boxHome);
        }
    }
}
