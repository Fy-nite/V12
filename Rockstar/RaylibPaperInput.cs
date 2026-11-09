using System.Collections.Generic;
using System.Numerics;
using Raylib_cs;
using PPaper = Prowl.PaperUI.Paper;
using PMouse = Prowl.PaperUI.PaperMouseBtn;
using PKey = Prowl.PaperUI.PaperKey;

namespace Rockstar
{
    internal sealed class RaylibPaperInput
    {
        private readonly HashSet<KeyboardKey> _down = new();
        private Vector2 _previousPosition;
        private bool _hasPointer;
        private bool _previousActive = true;
        private bool _previousLeft;
        private bool _previousRight;
        private bool _previousMiddle;
        private bool _previousSide;
        private bool _previousExtra;

        public void Feed(PPaper paper, bool active)
        {
            if (!active)
            {
                foreach (var mapping in MappedKeys)
                    paper.SetKeyState(mapping.Paper, false);

                FeedButton(paper, PMouse.Left, false, _previousPosition, ref _previousLeft);
                FeedButton(paper, PMouse.Right, false, _previousPosition, ref _previousRight);
                FeedButton(paper, PMouse.Middle, false, _previousPosition, ref _previousMiddle);
                FeedButton(paper, PMouse.Button4, false, _previousPosition, ref _previousSide);
                FeedButton(paper, PMouse.Button5, false, _previousPosition, ref _previousExtra);

                _down.Clear();
                _hasPointer = false;
                _previousActive = false;
                return;
            }

            var position = Raylib.GetMousePosition();

            bool moved = !_hasPointer || !_previousActive || position != _previousPosition;
            if (moved)
            {
                paper.SetPointerState(PMouse.Unknown, position.X, position.Y, false, true);
                _hasPointer = true;
            }

            FeedButton(paper, PMouse.Left, Raylib.IsMouseButtonDown(MouseButton.Left), position, ref _previousLeft);
            FeedButton(paper, PMouse.Right, Raylib.IsMouseButtonDown(MouseButton.Right), position, ref _previousRight);
            FeedButton(paper, PMouse.Middle, Raylib.IsMouseButtonDown(MouseButton.Middle), position, ref _previousMiddle);
            FeedButton(paper, PMouse.Button4, Raylib.IsMouseButtonDown(MouseButton.Side), position, ref _previousSide);
            FeedButton(paper, PMouse.Button5, Raylib.IsMouseButtonDown(MouseButton.Extra), position, ref _previousExtra);

            float wheel = Raylib.GetMouseWheelMove();
            if (wheel != 0f)
            {
                paper.SetPointerWheel(wheel);
                paper.SetPointerPosition(position.X, position.Y);
            }

            float wheelX = Raylib.GetMouseWheelMoveV().X;
            if (wheelX != 0f)
                paper.SetPointerWheelX(wheelX);

            foreach (var mapping in MappedKeys)
            {
                bool now = Raylib.IsKeyDown(mapping.Key);
                bool before = _down.Contains(mapping.Key);
                if (now == before) continue;

                paper.SetKeyState(mapping.Paper, now);
                if (now) _down.Add(mapping.Key);
                else _down.Remove(mapping.Key);
            }

            bool wantsText = paper.FocusedElementId != 0 || paper.WantsCaptureKeyboard;
            int codepoint;
            while ((codepoint = Raylib.GetCharPressed()) != 0)
            {
                if (wantsText && codepoint >= 32)
                    paper.AddInputCharacter(char.ConvertFromUtf32(codepoint));
            }

            _previousPosition = position;
            _previousActive = true;
        }

        private static void FeedButton(PPaper paper, PMouse button, bool down, Vector2 position, ref bool previous)
        {
            if (down == previous) return;
            paper.SetPointerState(button, position.X, position.Y, down, false);
            previous = down;
        }

        private static readonly (KeyboardKey Key, PKey Paper)[] MappedKeys = BuildKeyMap();

        private static (KeyboardKey Key, PKey Paper)[] BuildKeyMap()
        {
            var map = new List<(KeyboardKey, PKey)>();

            for (int i = 0; i < 26; i++)
                map.Add(((KeyboardKey)((int)KeyboardKey.A + i), (PKey)((int)PKey.A + i)));

            map.Add((KeyboardKey.Zero, PKey.Num0));
            map.Add((KeyboardKey.One, PKey.Num1));
            map.Add((KeyboardKey.Two, PKey.Num2));
            map.Add((KeyboardKey.Three, PKey.Num3));
            map.Add((KeyboardKey.Four, PKey.Num4));
            map.Add((KeyboardKey.Five, PKey.Num5));
            map.Add((KeyboardKey.Six, PKey.Num6));
            map.Add((KeyboardKey.Seven, PKey.Num7));
            map.Add((KeyboardKey.Eight, PKey.Num8));
            map.Add((KeyboardKey.Nine, PKey.Num9));

            map.Add((KeyboardKey.F1, PKey.F1));
            map.Add((KeyboardKey.F2, PKey.F2));
            map.Add((KeyboardKey.F3, PKey.F3));
            map.Add((KeyboardKey.F4, PKey.F4));
            map.Add((KeyboardKey.F5, PKey.F5));
            map.Add((KeyboardKey.F6, PKey.F6));
            map.Add((KeyboardKey.F7, PKey.F7));
            map.Add((KeyboardKey.F8, PKey.F8));
            map.Add((KeyboardKey.F9, PKey.F9));
            map.Add((KeyboardKey.F10, PKey.F10));
            map.Add((KeyboardKey.F11, PKey.F11));
            map.Add((KeyboardKey.F12, PKey.F12));

            map.Add((KeyboardKey.Enter, PKey.Enter));
            map.Add((KeyboardKey.Escape, PKey.Escape));
            map.Add((KeyboardKey.Backspace, PKey.Backspace));
            map.Add((KeyboardKey.Tab, PKey.Tab));
            map.Add((KeyboardKey.Space, PKey.Space));
            map.Add((KeyboardKey.Minus, PKey.Minus));
            map.Add((KeyboardKey.Equal, PKey.Equals));
            map.Add((KeyboardKey.LeftBracket, PKey.LeftBracket));
            map.Add((KeyboardKey.RightBracket, PKey.RightBracket));
            map.Add((KeyboardKey.Backslash, PKey.Backslash));
            map.Add((KeyboardKey.Semicolon, PKey.Semicolon));
            map.Add((KeyboardKey.Apostrophe, PKey.Apostrophe));
            map.Add((KeyboardKey.Grave, PKey.Grave));
            map.Add((KeyboardKey.Comma, PKey.Comma));
            map.Add((KeyboardKey.Period, PKey.Period));
            map.Add((KeyboardKey.Slash, PKey.Slash));

            map.Add((KeyboardKey.Insert, PKey.Insert));
            map.Add((KeyboardKey.Home, PKey.Home));
            map.Add((KeyboardKey.PageUp, PKey.PageUp));
            map.Add((KeyboardKey.Delete, PKey.Delete));
            map.Add((KeyboardKey.End, PKey.End));
            map.Add((KeyboardKey.PageDown, PKey.PageDown));
            map.Add((KeyboardKey.Right, PKey.Right));
            map.Add((KeyboardKey.Left, PKey.Left));
            map.Add((KeyboardKey.Down, PKey.Down));
            map.Add((KeyboardKey.Up, PKey.Up));

            map.Add((KeyboardKey.LeftControl, PKey.LeftControl));
            map.Add((KeyboardKey.LeftShift, PKey.LeftShift));
            map.Add((KeyboardKey.LeftAlt, PKey.LeftAlt));
            map.Add((KeyboardKey.LeftSuper, PKey.LeftSuper));
            map.Add((KeyboardKey.RightControl, PKey.RightControl));
            map.Add((KeyboardKey.RightShift, PKey.RightShift));
            map.Add((KeyboardKey.RightAlt, PKey.RightAlt));
            map.Add((KeyboardKey.RightSuper, PKey.RightSuper));

            return map.ToArray();
        }
    }
}
