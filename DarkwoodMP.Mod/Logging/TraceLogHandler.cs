using System;
using System.Text;

namespace DWMPHorde.Logging
{
    /// <summary>
    /// Interpolated string handler for <see cref="ModLog.Trace(LogCat, TraceLogHandler)"/> and the
    /// TraceRate overloads: when Trace is off for the category the compiler skips every Append call,
    /// so nothing is formatted or allocated. Formats like the string overloads (current culture).
    /// </summary>
    [System.Runtime.CompilerServices.InterpolatedStringHandler]
    public struct TraceLogHandler
    {
        private StringBuilder _sb;

        public TraceLogHandler(int literalLength, int formattedCount, LogCat cat, out bool shouldAppend)
        {
            shouldAppend = ModLog.IsEnabled(cat, LogLevel.Trace);
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
    /// <summary>Compiler marker naming the arguments passed to a handler's constructor (absent from net471's BCL).</summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    internal sealed class InterpolatedStringHandlerArgumentAttribute : Attribute
    {
        public InterpolatedStringHandlerArgumentAttribute(string argument) => Arguments = new[] { argument };

        public InterpolatedStringHandlerArgumentAttribute(params string[] arguments) => Arguments = arguments;

        public string[] Arguments { get; }
    }
}
