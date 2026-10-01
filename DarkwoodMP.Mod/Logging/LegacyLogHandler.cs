using System;
using System.Text;

namespace DWMPHorde.Logging
{
    /// <summary>
    /// Interpolated string handler for <see cref="ModRuntime.LegacyInfo(LegacyLogHandler)"/>: when
    /// legacy logging is off the compiler skips every Append call, so nothing is formatted or allocated.
    /// </summary>
    [System.Runtime.CompilerServices.InterpolatedStringHandler]
    public struct LegacyLogHandler
    {
        private StringBuilder _sb;

        public LegacyLogHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            shouldAppend = ModRuntime.LegacyLoggingEnabled;
            _sb = shouldAppend ? new StringBuilder(literalLength + formattedCount * 8) : null;
        }

        internal bool Enabled => _sb != null;

        public void AppendLiteral(string value) => _sb.Append(value);

        public void AppendFormatted<T>(T value) => _sb.Append(value?.ToString());

        public void AppendFormatted<T>(T value, string format)
        {
            if (value is IFormattable f)
                _sb.Append(f.ToString(format, null));
            else
                _sb.Append(value?.ToString());
        }

        public void AppendFormatted<T>(T value, int alignment)
            => AppendAligned(value?.ToString(), alignment);

        public void AppendFormatted<T>(T value, int alignment, string format)
            => AppendAligned(value is IFormattable f ? f.ToString(format, null) : value?.ToString(), alignment);

        public void AppendFormatted(string value) => _sb.Append(value);

        private void AppendAligned(string text, int alignment)
        {
            text = text ?? string.Empty;
            int pad = Math.Abs(alignment) - text.Length;
            if (pad > 0 && alignment > 0) _sb.Append(' ', pad);
            _sb.Append(text);
            if (pad > 0 && alignment < 0) _sb.Append(' ', pad);
        }

        internal string ToStringAndClear()
        {
            string s = _sb?.ToString() ?? string.Empty;
            _sb = null;
            return s;
        }
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>Compiler marker for interpolated string handlers (absent from net471's BCL).</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    internal sealed class InterpolatedStringHandlerAttribute : Attribute
    {
    }
}
