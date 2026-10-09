using System;

namespace DWMPHorde.Config
{
    /// <summary>
    /// Loader-agnostic config entry. Same <see cref="Value"/> surface as BepInEx ConfigEntry.
    /// </summary>
    public sealed class ModSetting<T>
    {
        private readonly ModConfigStore _store;
        private readonly string _section;
        private readonly string _key;
        private T _value;

        internal ModSetting(ModConfigStore store, string section, string key, T value, T defaultValue)
        {
            _store = store;
            _section = section;
            _key = key;
            _value = value;
            Default = defaultValue;
        }

        /// <summary>The value bound in code (what "Revert to default" puts back).</summary>
        public T Default { get; }

        /// <summary>"Section.Key", unique per setting.</summary>
        public string Id => _section + "." + _key;

        public T Value
        {
            get => _value;
            set
            {
                if (Equals(_value, value))
                    return;
                _value = value;
                _store.NotifyValueChanged(_section, _key, value);
            }
        }
    }
}
