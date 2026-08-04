using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CodexProSafeManager
{
    internal sealed class PrivateLogView : Control
    {
        private const int MaximumLines = 250;
        private readonly List<string> lines = new List<string>();

        internal PrivateLogView()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
            AccessibleName = "Sanitized lifecycle activity";
            AccessibleDescription = "Lifecycle activity is displayed visually; accumulated log text is intentionally unavailable to accessibility automation.";
            TabStop = false;
        }

        internal void AppendLine(string value)
        {
            lines.Add(value ?? String.Empty);
            while (lines.Count > MaximumLines) lines.RemoveAt(0);
            Invalidate();
        }

        internal string RenderedTextForSelfTest
        {
            get { return String.Join(Environment.NewLine, lines.ToArray()); }
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new PrivateLogAccessibleObject(this);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            args.Graphics.Clear(BackColor);
            using (Pen border = new Pen(Color.FromArgb(90, 90, 94)))
                args.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));

            int lineHeight = Math.Max(Font.Height + 2, 14);
            int visibleCount = Math.Max(1, (Height - 8) / lineHeight);
            int start = Math.Max(0, lines.Count - visibleCount);
            int y = 4;
            for (int index = start; index < lines.Count; index++)
            {
                Rectangle bounds = new Rectangle(6, y, Math.Max(0, Width - 12), lineHeight);
                TextRenderer.DrawText(
                    args.Graphics,
                    lines[index],
                    Font,
                    bounds,
                    ForeColor,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                y += lineHeight;
            }
        }

        private sealed class PrivateLogAccessibleObject : ControlAccessibleObject
        {
            internal PrivateLogAccessibleObject(PrivateLogView owner) : base(owner) { }

            public override string Name
            {
                get { return "Sanitized lifecycle activity"; }
                set { }
            }

            public override string Description
            {
                get { return "Accumulated log text is intentionally unavailable to accessibility automation."; }
            }

            public override string Value
            {
                get { return String.Empty; }
                set { }
            }

            public override AccessibleRole Role
            {
                get { return AccessibleRole.Pane; }
            }

            public override int GetChildCount()
            {
                return 0;
            }

            public override AccessibleObject GetChild(int index)
            {
                return null;
            }
        }
    }
}
