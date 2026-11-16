using System;
using System.Numerics;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Input;
using V12.Core.Interfaces.Renderer;
using V12.Skeletal.Components;

namespace SkinnedCharacter.Components
{
    /// <summary>
    /// Camera orbit and playback controls for the skeletal sample.
    ///
    /// Space pauses/resumes, R restarts, 1-9 select a clip, A/D turn the
    /// character, and right mouse captures the pointer for free orbit.
    /// </summary>
    public sealed class SkinnedCharacterController : ComponentBase
    {
        public override string Name => "Skinned Character Controller";
        public override string Description => "Playback and camera controls for the skeletal sample";

        private InputActionMap? _actions;
        private SkeletalManagerComponent? _manager;
        private SkeletalPlayerComponent? _player;
        private IWorldElement? _camera;

        private float _yaw;
        private float _pitch = 0.05f;
        private float _height = 1.8f;
        private bool _grounded;
        private bool _paused;

        private static readonly InputKey[] ClipKeys =
        {
            InputKey.D1, InputKey.D2, InputKey.D3, InputKey.D4, InputKey.D5,
            InputKey.D6, InputKey.D7, InputKey.D8, InputKey.D9,
        };

        public override void OnAttach(IWorldElement worldElement)
        {
            base.OnAttach(worldElement);
            BindInput();
        }

        public override void OnDetach(IWorldElement worldElement)
        {
            if (_actions is not null && Input() is { } input)
                _actions.Detach(input);
            _actions = null;

            GameRoot.Instance?.MouseCapture?.Release();
            base.OnDetach(worldElement);
        }

        private static InputService? Input() =>
            GameRoot.Instance?.Registry.Get<InputService>();

        private void BindInput()
        {
            if (Input() is not { } input)
            {
                GameRoot.Log?.Warning("[SkinnedCharacter] No InputService; controls disabled.");
                return;
            }

            var actions = new InputActionMap { Name = "SkinnedCharacter" };

            actions.BindButton("Pause", InputKey.Space);
            actions.BindButton("Restart", InputKey.R);
            actions.BindButton("SeekBack", InputKey.OemOpenBrackets);
            actions.BindButton("SeekForward", InputKey.OemCloseBrackets);
            actions.BindButton("Capture", "mouse_right");
            actions.BindAxis("OrbitX", "mouse_dx", "");
            actions.BindAxis("OrbitY", "mouse_dy", "");
            actions.BindAxis("Turn", InputKey.D, InputKey.A);

            for (int i = 0; i < ClipKeys.Length; i++)
                actions.BindButton($"Clip{i + 1}", ClipKeys[i]);

            actions.Attach(input);
            _actions = actions;
        }

        public override void Update(float deltaTime)
        {
            if (_actions is null)
                return;

            _actions.Update(deltaTime);

            ResolveScene();
            UpdatePlayback();
            UpdateCamera(deltaTime);
        }

        private void ResolveScene()
        {
            var world = GameRoot.Instance?.SelectedWorld;
            if (world is null)
                return;

            _manager ??= world.FindElementWithComponentRecursive<SkeletalManagerComponent>()
                ?.GetComponent<SkeletalManagerComponent>();

            _player ??= world.FindElementWithComponentRecursive<SkeletalPlayerComponent>()
                ?.GetComponent<SkeletalPlayerComponent>();

            _camera ??= world.FindElementWithComponentRecursive<CameraComponent>();

            if (_manager is not null && !_grounded)
            {
                _height = _manager.Height > 0.01f ? _manager.Height : 1.8f;

                if (_manager.Owner is { } owner)
                {
                    var home = owner.LocalTransform.Position;
                    owner.LocalTransform = new TRS(new Vector3(
                        home.X,
                        home.Y + _manager.GroundOffset,
                        home.Z));
                }

                _grounded = true;
            }
        }

        private void UpdatePlayback()
        {
            if (_player is null)
                return;

            if (_actions!.GetButtonDown("Pause"))
            {
                _paused = !_paused;
                if (_paused)
                    _player.Pause();
                else
                    _player.Resume();
            }

            if (_actions.GetButtonDown("Restart"))
            {
                _player.Seek(0f);
                if (!_paused)
                    _player.Resume();
            }

            if (_actions.GetButtonDown("SeekBack"))
                _player.Seek(MathF.Max(0f, _player.Time - 0.1f));
            if (_actions.GetButtonDown("SeekForward"))
                _player.Seek(MathF.Min(_player.Duration, _player.Time + 0.1f));

            for (int i = 0; i < ClipKeys.Length; i++)
            {
                if (!_actions.GetButtonDown($"Clip{i + 1}"))
                    continue;

                var clips = _player.Clips;
                if (i >= clips.Count)
                    break;

                _player.Play(clips[i], loop: true);
                if (!_paused)
                    _player.Resume();
                break;
            }
        }

        private void UpdateCamera(float deltaTime)
        {
            if (_camera is null)
                return;

            var mouse = GameRoot.Instance?.MouseCapture;
            if (_actions!.GetButtonDown("Capture") && mouse is not null)
                mouse.SetCaptured(!mouse.IsCaptured);

            bool captured = mouse?.IsCaptured == true;
            if (captured)
            {
                _yaw -= _actions.GetAxis("OrbitX") * 0.006f;
                _pitch = Math.Clamp(_pitch - _actions.GetAxis("OrbitY") * 0.006f, -0.9f, 1.2f);
            }
            else
            {
                _yaw += deltaTime * 0.35f;
            }

            _yaw += _actions.GetAxis("Turn") * deltaTime * 1.8f;

            Vector3 target = _manager?.Owner is { } owner
                ? new Vector3(
                    owner.WorldTransform.M41,
                    owner.WorldTransform.M42 + _height * 0.5f,
                    owner.WorldTransform.M43)
                : new Vector3(0f, _height * 0.5f, 0f);

            var direction = new Vector3(
                MathF.Sin(_yaw) * MathF.Cos(_pitch),
                MathF.Sin(_pitch),
                MathF.Cos(_yaw) * MathF.Cos(_pitch));

            float distance = MathF.Max(2.8f, _height * 1.5f);
            Vector3 position = target + direction * distance;
            var rotation = Quaternion.CreateFromYawPitchRoll(
                MathF.Atan2(direction.X, direction.Z),
                MathF.Asin(-direction.Y),
                0f);

            _camera.LocalTransform = new TRS(position, rotation);
        }
    }
}
