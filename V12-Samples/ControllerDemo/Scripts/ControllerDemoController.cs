using System;
using System.Collections.Generic;
using System.Numerics;
using V12.Components;
using V12.Components.Renderables;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Input;
using V12.Core.Interfaces.Renderer;

namespace ControllerDemo.Components
{
    /// <summary>
    /// Live controller demo. Every input source is bound through an
    /// <see cref="InputActionMap"/>: the left stick moves the orange cube, the
    /// right stick spins the blue one, the face/shoulder/d-pad buttons light
    /// their tiles, and the triggers raise the two bars.
    ///
    /// Keyboard fallbacks are bound to the same actions with <c>AddButton</c> /
    /// <c>AddAxis</c>, so the demo works without a pad:
    /// WASD move, arrow keys look, Space/E/Q/R face buttons, F/G shoulders,
    /// Backspace/Enter, I/K/J/L d-pad, Z/C triggers.
    /// </summary>
    public sealed class ControllerDemoController : ComponentBase
    {
        public override string Name => "Controller Demo";
        public override string Description => "Live gamepad + keyboard input demo";

        private InputActionMap? _actions;
        private IWorldElement? _owner;
        private IWorldElement? _player;
        private IWorldElement? _look;

        private readonly Dictionary<string, (MaterialComponent Material, Vector3 On)> _buttons = new();
        private readonly List<(IWorldElement Element, float HomeY, MaterialComponent Material, Vector3 On)> _triggers = new();

        private Vector3 _playerHome;
        private Vector3 _lookHome;
        private float _playerX;
        private float _playerZ;
        private float _yaw;
        private float _pitch;

        private const float MoveSpeed = 6f;
        private const float LookSpeed = 2.4f;
        private const float TriggerRise = 2.2f;

        public override void OnAttach(IWorldElement worldElement)
        {
            base.OnAttach(worldElement);
            _owner = worldElement;

            BuildProps();
            BindInput();
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

        // ── Scene props ─────────────────────────────────────────────────────

        private void BuildProps()
        {
            if (_owner is null)
                return;

            _playerHome = new Vector3(-4.4f, 0.8f, 3.0f);
            _lookHome = new Vector3(4.4f, 0.8f, 3.0f);
            _playerX = _playerHome.X;
            _playerZ = _playerHome.Z;

            _player = AddCube("Player", _playerHome, 1.6f, new Vector3(0.95f, 0.55f, 0.16f));
            _look = AddCube("Look", _lookHome, 1.6f, new Vector3(0.18f, 0.55f, 0.9f));

            // Face buttons keep their Xbox colours so the mapping is obvious.
            AddButton("A", -6.6f, new Vector3(0.24f, 0.72f, 0.3f));
            AddButton("B", -5.4f, new Vector3(0.85f, 0.2f, 0.2f));
            AddButton("X", -4.2f, new Vector3(0.2f, 0.42f, 0.9f));
            AddButton("Y", -3.0f, new Vector3(0.9f, 0.78f, 0.18f));

            AddButton("LB", -1.8f, new Vector3(0.8f, 0.8f, 0.86f));
            AddButton("RB", -0.6f, new Vector3(0.8f, 0.8f, 0.86f));

            AddButton("Back", 0.8f, new Vector3(0.55f, 0.58f, 0.64f));
            AddButton("Start", 2.0f, new Vector3(0.55f, 0.58f, 0.64f));

            AddButton("DpadUp", 3.4f, new Vector3(0.95f, 0.62f, 0.2f));
            AddButton("DpadDown", 4.6f, new Vector3(0.95f, 0.62f, 0.2f));
            AddButton("DpadLeft", 5.8f, new Vector3(0.95f, 0.62f, 0.2f));
            AddButton("DpadRight", 7.0f, new Vector3(0.95f, 0.62f, 0.2f));

            AddTrigger("TriggerLeft", -8.6f, new Vector3(0.85f, 0.35f, 0.25f));
            AddTrigger("TriggerRight", 8.6f, new Vector3(0.25f, 0.7f, 0.85f));
        }

        private IWorldElement AddCube(string name, Vector3 position, float size, Vector3 color)
        {
            var element = new Element(name) { LocalTransform = new TRS(position) };

            var mesh = new BoxMesh { Size = new Vector3(size, size, size) };
            element.AddComponent(mesh);
            element.AddComponent(new MeshRenderer { Mesh = mesh });
            element.AddComponent(new MaterialComponent(
                color.X, color.Y, color.Z, 1f, metallic: 0.05f, roughness: 0.55f));

            _owner!.AddChild(element);
            return element;
        }

        private void AddButton(string action, float x, Vector3 onColor)
        {
            var element = AddCube($"Button_{action}", new Vector3(x, 0.45f, -3.4f), 0.9f, onColor * 0.22f);
            if (element.GetComponent<MaterialComponent>() is { } material)
                _buttons[action] = (material, onColor);
        }

        private void AddTrigger(string action, float x, Vector3 onColor)
        {
            const float homeY = 0.12f;
            var element = AddCube($"Trigger_{action}", new Vector3(x, homeY, -3.4f), 0.8f, onColor * 0.22f);
            if (element.GetComponent<MaterialComponent>() is { } material)
                _triggers.Add((element, homeY, material, onColor));
        }

        // ── Input ───────────────────────────────────────────────────────────

        private void BindInput()
        {
            if (Input() is not { } input)
            {
                GameRoot.Log?.Warning("[ControllerDemo] No InputService; input disabled.");
                return;
            }

            var actions = new InputActionMap { Name = "ControllerDemo" };

            // Left stick + WASD. V12 forward is -Z, so the stick's up (-Y)
            // drives negative Z.
            actions.BindAxis("MoveX", GamepadAxis.LeftX);
            actions.AddAxis("MoveX", InputKey.D, InputKey.A);
            actions.BindAxis("MoveZ", GamepadAxis.LeftY);
            actions.AddAxis("MoveZ", InputKey.S, InputKey.W);

            // Right stick + arrow keys.
            actions.BindAxis("LookX", GamepadAxis.RightX);
            actions.AddAxis("LookX", InputKey.Right, InputKey.Left);
            actions.BindAxis("LookY", GamepadAxis.RightY);
            actions.AddAxis("LookY", InputKey.Down, InputKey.Up);

            // Buttons: pad button plus a keyboard fallback on the same action.
            BindPadButton(actions, "A", GamepadButton.A, InputKey.Space);
            BindPadButton(actions, "B", GamepadButton.B, InputKey.E);
            BindPadButton(actions, "X", GamepadButton.X, InputKey.Q);
            BindPadButton(actions, "Y", GamepadButton.Y, InputKey.R);
            BindPadButton(actions, "LB", GamepadButton.LeftShoulder, InputKey.F);
            BindPadButton(actions, "RB", GamepadButton.RightShoulder, InputKey.G);
            BindPadButton(actions, "Back", GamepadButton.Back, InputKey.Back);
            BindPadButton(actions, "Start", GamepadButton.Start, InputKey.Enter);
            BindPadButton(actions, "DpadUp", GamepadButton.DpadUp, InputKey.I);
            BindPadButton(actions, "DpadDown", GamepadButton.DpadDown, InputKey.K);
            BindPadButton(actions, "DpadLeft", GamepadButton.DpadLeft, InputKey.J);
            BindPadButton(actions, "DpadRight", GamepadButton.DpadRight, InputKey.L);

            // Triggers are analog axes; the keyboard fallback reads 0 or 1.
            actions.BindAxis("TriggerLeft", GamepadAxis.TriggerLeft);
            actions.AddAxis("TriggerLeft", InputKey.Z);
            actions.BindAxis("TriggerRight", GamepadAxis.TriggerRight);
            actions.AddAxis("TriggerRight", InputKey.C);

            actions.Attach(input);
            _actions = actions;
        }

        private static void BindPadButton(
            InputActionMap actions, string action, GamepadButton button, InputKey fallback)
        {
            actions.BindButton(action, button);
            actions.AddButton(action, fallback);
        }

        // ── Frame ───────────────────────────────────────────────────────────

        public override void Update(float deltaTime)
        {
            if (_actions is null)
                return;

            _actions.Update(deltaTime);

            MovePlayer(deltaTime);
            SpinLook(deltaTime);
            UpdateButtons();
            UpdateTriggers();
        }

        private void MovePlayer(float deltaTime)
        {
            if (_player is null)
                return;

            float x = _actions!.GetAxis("MoveX");
            float z = _actions.GetAxis("MoveZ");

            _playerX = Math.Clamp(_playerX + x * MoveSpeed * deltaTime, -8f, 8f);
            _playerZ = Math.Clamp(_playerZ + z * MoveSpeed * deltaTime, -5.5f, 5.5f);

            _player.LocalTransform = new TRS(new Vector3(_playerX, _playerHome.Y, _playerZ));
        }

        private void SpinLook(float deltaTime)
        {
            if (_look is null)
                return;

            _yaw -= _actions!.GetAxis("LookX") * LookSpeed * deltaTime;
            _pitch -= _actions.GetAxis("LookY") * LookSpeed * deltaTime;
            _pitch = Math.Clamp(_pitch, -1.2f, 1.2f);

            _look.LocalTransform = new TRS(
                _lookHome,
                Quaternion.CreateFromYawPitchRoll(_yaw, _pitch, 0f));
        }

        private void UpdateButtons()
        {
            foreach (var (action, light) in _buttons)
            {
                bool held = _actions!.GetButton(action);
                var color = held ? light.On : light.On * 0.22f;
                SetColor(light.Material, color);
            }
        }

        private void UpdateTriggers()
        {
            for (int i = 0; i < _triggers.Count; i++)
            {
                var bar = _triggers[i];
                string action = i == 0 ? "TriggerLeft" : "TriggerRight";
                float value = Math.Clamp(_actions!.GetAxis(action), 0f, 1f);

                bar.Element.LocalTransform = new TRS(
                    new Vector3(bar.Element.LocalTransform.Position.X,
                        bar.HomeY + value * TriggerRise,
                        bar.Element.LocalTransform.Position.Z));

                SetColor(bar.Material, bar.On * (0.22f + 0.78f * value));
            }
        }

        private static void SetColor(MaterialComponent material, Vector3 color)
        {
            material.R = color.X;
            material.G = color.Y;
            material.B = color.Z;
        }
    }
}
